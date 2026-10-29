using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>404Launcher 授权服务器客户端：HWID 派生（含设备限制解除）、账号登录、CrcSalt 三层 Fernet 剥层。</summary>
/// <remarks>
/// 原理（逆向自 auth_client.cp312-win_amd64.pyd，见桌面逆向文档）：
/// 1. HWID = sha256("404-launcher-machine-guid-v2|" + MachineGuid).hexdigest().upper()，仅作请求头；
/// 2. 登录 POST {base}/api/login → auth_session.json(token)；
/// 3. 取盐 GET {base}/api/crc-salt（Bearer + X-Client-* 头）→ {layers, payload}；
/// 4. 剥层：for n = layers..1: key = sha256("404-crc-salt-layer-" + n + ":" + token)；payload = Fernet(key).decrypt(payload)；
///    末层明文即 32 位大写十六进制盐值。
/// </remarks>
public static class Auth404Client
{
	public const string DefaultBaseUrl = "https://404.xthdx.wiki";

	public const string DefaultBackupUrl = "http://39.105.154.212:6669";

	public const string AppVersion = "1.3.9";

	/// <summary>HWID 模式：real=官方算法真实值（默认）；spoof=按账号派生的稳定伪装值（解除设备限制且不涨设备数）；random=每次登录随机；fixed=用户固定值。</summary>
	public const string DefaultHwidMode = "real";

	private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15.0);

	// ---------- HWID ----------

	/// <summary>读取 MachineGuid（64 位视图）。</summary>
	public static string ReadMachineGuid()
	{
		try
		{
			using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography");
			return (key?.GetValue("MachineGuid")?.ToString() ?? "").Trim();
		}
		catch
		{
			return "";
		}
	}

	/// <summary>BIOS UUID 回退来源（wmic 不可用时经 PowerShell CIM）。</summary>
	public static string ReadBiosUuid()
	{
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo("powershell.exe")
			{
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			psi.ArgumentList.Add("-NoProfile");
			psi.ArgumentList.Add("-NonInteractive");
			psi.ArgumentList.Add("-Command");
			psi.ArgumentList.Add("(Get-CimInstance Win32_ComputerSystemProduct).UUID");
			using Process proc = Process.Start(psi);
			if (proc == null)
			{
				return "";
			}
			string outp = proc.StandardOutput.ReadToEnd().Trim();
			proc.WaitForExit(8000);
			return outp;
		}
		catch
		{
			return "";
		}
	}

	/// <summary>按模式计算本次会话使用的 HWID（大写十六进制）。</summary>
	public static string ComputeHwid(string mode, string? account = null, string? fixedHwid = null)
	{
		string m = (mode ?? "").Trim().ToLowerInvariant();
		if (m == "fixed")
		{
			string f = (fixedHwid ?? "").Trim().ToUpperInvariant();
			return f;
		}
		if (m == "random")
		{
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("404-launcher-machine-guid-v2|" + Guid.NewGuid().ToString("N")))).ToUpperInvariant();
		}
		if (m == "spoof")
		{
			// 按账号派生的稳定伪装设备：同一账号永远同一 HWID（服务端视为同一台设备），但与真实机器不同 —— 解除真实设备绑定/封禁
			string seed = "mhyunbox-spoof|" + (account ?? "default");
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToUpperInvariant();
		}
		string guid = ReadMachineGuid();
		if (!string.IsNullOrEmpty(guid))
		{
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("404-launcher-machine-guid-v2|" + guid))).ToUpperInvariant();
		}
		string uuid = ReadBiosUuid();
		if (!string.IsNullOrEmpty(uuid))
		{
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("404-launcher-bios-uuid-v2|" + uuid))).ToUpperInvariant();
		}
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("404-launcher-machine-guid-v2|fallback"))).ToUpperInvariant();
	}

	// ---------- 登录 ----------

	/// <summary>
	/// 404 账号登录。返回 (Ok, Message, Token, Hwid)。
	/// 请求体按候选格式依次尝试（{username,password} → {username,password,hwid} → {account,password}）。
	/// </summary>
	public static async Task<(bool Ok, string Message, string Token, string Hwid)> LoginAsync(string baseUrl, string username, string password, string hwidMode, string fixedHwid, CancellationToken ct)
	{
		string hwid = ComputeHwid(hwidMode, username, fixedHwid);
		List<string> errors = new List<string>();
		List<string> urls = BuildAuthUrls(baseUrl, errors);
		if (urls.Count == 0)
		{
			return (Ok: false, Message: "登录失败: " + string.Join(" ｜ ", errors), Token: "", Hwid: hwid);
		}
		List<(string Body, string Tag)> candidates = new List<(string, string)>
		{
			(JsonSerializer.Serialize(new Dictionary<string, string> { ["username"] = username, ["password"] = password }), "user/pass"),
			(JsonSerializer.Serialize(new Dictionary<string, string> { ["username"] = username, ["password"] = password, ["hwid"] = hwid }), "user/pass/hwid"),
			(JsonSerializer.Serialize(new Dictionary<string, string> { ["account"] = username, ["password"] = password }), "account/pass")
		};
		LogHelper.Salt($"[404] 登录开始：主地址={EndpointForLog(urls.Count > 0 ? urls[0] : baseUrl)} 账号={Mask(username)} 密码已隐藏 HWID模式={hwidMode} HWID={Mask(hwid)}");
		foreach (string url in urls)
		{
			// 一个地址复用一个 HttpClient（多个候选格式只换请求体），避免每个候选都新建连接池。
			using HttpClient http = CreateAuthHttpClient(hwid);
			foreach (var (body, tag) in candidates)
			{
				try
				{
					LogHelper.Salt($"[404] 尝试 POST {url}/api/login 格式={tag} 凭据已隐藏");
					using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url + "/api/login");
					req.Content = new StringContent(body, Encoding.UTF8, "application/json");
					using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
					string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
					// 响应里可能回显账号信息，日志与错误文本统一脱敏后再落盘。
					string safeText = Redact(text, password);
					LogHelper.Salt($"[404] 登录响应 HTTP {(int)resp.StatusCode}（{tag}）body长度={text.Length}");
					if (resp.IsSuccessStatusCode)
					{
						string? token = ExtractToken(text);
						if (!string.IsNullOrEmpty(token))
						{
							LogHelper.Salt("[404] 登录成功，token 长度=" + token!.Length);
							return (Ok: true, Message: "登录成功（" + tag + "）", Token: token!, Hwid: hwid);
						}
						errors.Add(url + " (" + tag + ") HTTP " + (int)resp.StatusCode + " 响应无 token 字段: " + Truncate(safeText));
					}
					else
					{
						errors.Add(url + " (" + tag + ") " + DescribeError((int)resp.StatusCode, safeText));
					}
				}
				catch (Exception ex)
				{
					errors.Add(url + " (" + tag + ") " + ex.GetType().Name + ": " + ex.Message);
				}
			}
		}
		LogHelper.Salt("[404] 登录失败汇总: " + string.Join(" ｜ ", errors));
		return (Ok: false, Message: "登录失败: " + string.Join(" ｜ ", errors), Token: "", Hwid: hwid);
	}

	/// <summary>
	/// 把配置的主地址与内置备用地址整理成可安全发送凭据的地址列表。
	/// 安全策略：自定义地址必须是 HTTPS（回环地址除外）；内置备用服务器因上游只有 HTTP，作为受控例外保留。
	/// </summary>
	private static List<string> BuildAuthUrls(string? primary, List<string> errors)
	{
		List<string> urls = new List<string>();
		if (TryNormalizeBaseUrl(primary, out string normalized, out string? error))
		{
			urls.Add(normalized);
		}
		else if (!string.IsNullOrEmpty(error))
		{
			errors.Add(error!);
			LogHelper.Salt("[404] 地址被拒绝: " + error);
		}
		if (TryNormalizeBaseUrl(DefaultBackupUrl, out string backup, out _) && !urls.Exists((string u) => string.Equals(u, backup, StringComparison.OrdinalIgnoreCase)))
		{
			urls.Add(backup);
		}
		return urls;
	}

	private static bool TryNormalizeBaseUrl(string? raw, out string normalized, out string? error)
	{
		normalized = "";
		error = null;
		string value = (string.IsNullOrWhiteSpace(raw) ? DefaultBaseUrl : raw!.Trim());
		if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host))
		{
			error = "地址格式无效（需为 http:// 或 https:// 绝对地址）";
			return false;
		}
		if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
		{
			error = "地址协议不受支持（仅允许 http/https）";
			return false;
		}
		if (!string.IsNullOrEmpty(uri.UserInfo))
		{
			error = "地址中不允许包含用户名/密码";
			return false;
		}
		if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && !string.Equals(uri.Host, "39.105.154.212", StringComparison.OrdinalIgnoreCase))
		{
			error = "安全策略拒绝：账号凭据只能发送到 HTTPS 地址（内置备用服务器除外）";
			return false;
		}
		UriBuilder builder = new UriBuilder(uri)
		{
			Query = string.Empty,
			Fragment = string.Empty
		};
		normalized = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
		if (string.IsNullOrWhiteSpace(normalized))
		{
			error = "地址格式无效";
			return false;
		}
		return true;
	}

	/// <summary>统一的授权请求客户端：禁止自动重定向，避免 POST 凭据被 307/308 重放到其他主机。</summary>
	private static HttpClient CreateAuthHttpClient(string hwid, string? token = null)
	{
		HttpClient http = new HttpClient(new HttpClientHandler
		{
			AllowAutoRedirect = false
		})
		{
			Timeout = HttpTimeout
		};
		http.DefaultRequestHeaders.UserAgent.ParseAdd("404Launcher/" + AppVersion);
		// 登录端点同样有版本门禁：缺 X-Client-Version 会直接 HTTP 426（客户端版本过低），
		// 根本走不到账号校验 —— 表现就是「怎么登都登不上号」。
		// 实测：无此头 426；带上 1.3.9 才会返回 401 用户名或密码错误 / 200 成功。
		http.DefaultRequestHeaders.TryAddWithoutValidation("X-Client-Version", AppVersion);
		http.DefaultRequestHeaders.TryAddWithoutValidation("X-Client-Channel", "desktop");
		http.DefaultRequestHeaders.TryAddWithoutValidation("X-Client-HWID", hwid);
		http.DefaultRequestHeaders.TryAddWithoutValidation("X-Client-HWID-Algorithm", "machine-guid-v2");
		if (!string.IsNullOrEmpty(token))
		{
			http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + token);
		}
		return http;
	}

	/// <summary>日志里只显示协议、主机与端口，避免把地址中的路径/查询参数（可能含 token）写进日志。</summary>
	private static string EndpointForLog(string? url)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host))
		{
			return uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? "" : (":" + uri.Port));
		}
		return "(无效地址)";
	}

	private static string Mask(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "(空)";
		}
		if (value!.Length <= 4)
		{
			return new string('*', value.Length);
		}
		return value.Substring(0, 2) + new string('*', Math.Min(8, value.Length - 4)) + value.Substring(value.Length - 2);
	}

	private static string Redact(string? text, params string?[] secrets)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text ?? "";
		}
		string result = text;
		foreach (string? secret in secrets)
		{
			if (!string.IsNullOrEmpty(secret))
			{
				result = result.Replace(secret!, "***", StringComparison.Ordinal);
			}
		}
		return result;
	}

	private static string? ExtractToken(string json)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;
			foreach (string key in new string[9] { "token", "access_token", "auth_token", "session", "session_token", "Token", "data", "result", "auth" })
			{
				if (root.TryGetProperty(key, out JsonElement v))
				{
					if (v.ValueKind == JsonValueKind.String)
					{
						string s = v.GetString() ?? "";
						if (s.Length >= 8)
						{
							return s;
						}
					}
					else if (v.ValueKind == JsonValueKind.Object)
					{
						foreach (string k2 in new string[2] { "token", "access_token" })
						{
							if (v.TryGetProperty(k2, out JsonElement v2) && v2.ValueKind == JsonValueKind.String && (v2.GetString() ?? "").Length >= 8)
							{
								return v2.GetString();
							}
						}
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	// ---------- CrcSalt ----------

	/// <summary>
	/// 拉取并三层剥层解密 CrcSalt。返回 (Ok, Message, Salt)。
	/// </summary>
	public static async Task<(bool Ok, string Message, string? Salt)> FetchCrcSaltAsync(string baseUrl, string token, string hwid, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return (Ok: false, Message: "未登录（无 token），请先登录 404 账号", Salt: null);
		}
		if (string.IsNullOrWhiteSpace(hwid))
		{
			hwid = ComputeHwid(DefaultHwidMode);
		}
		// 逐个地址、逐条原因都记下来，避免只报最后一个（备用地址）的失败原因而误导排查
		List<string> errors = new List<string>();
		List<string> urls = BuildAuthUrls(baseUrl, errors);
		if (urls.Count == 0)
		{
			return (Ok: false, Message: "404 盐值获取失败: " + string.Join(" ｜ ", errors), Salt: null);
		}
		LogHelper.Salt($"[404] 取盐开始：主地址={EndpointForLog(urls.Count > 0 ? urls[0] : baseUrl)} token长度={token.Length} HWID={Mask(hwid)}");
		foreach (string url in urls)
		{
			try
			{
				LogHelper.Salt($"[404] 尝试 GET {url}/api/crc-salt");
				using HttpClient http = CreateAuthHttpClient(hwid, token);
				using HttpResponseMessage resp = await http.GetAsync(url + "/api/crc-salt", ct).ConfigureAwait(false);
				string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
				string safeText = Redact(text, token);
				LogHelper.Salt($"[404] 取盐响应 HTTP {(int)resp.StatusCode} body长度={text.Length}");
				if (!resp.IsSuccessStatusCode)
				{
					errors.Add(url + " " + DescribeError((int)resp.StatusCode, safeText));
					continue;
				}
				var (ok, msg, salt) = DecodeCrcSaltResponse(text, token);
				LogHelper.Salt($"[404] 剥层结果 ok={ok} msg={msg} salt={(salt == null ? "(null)" : Mask(salt))}");
				if (ok)
				{
					return (Ok: true, Message: msg, Salt: salt);
				}
				errors.Add(url + " " + msg);
			}
			catch (Exception ex)
			{
				errors.Add(url + " " + ex.GetType().Name + ": " + ex.Message);
			}
		}
		return (Ok: false, Message: "404 盐值获取失败: " + string.Join(" ｜ ", errors), Salt: null);
	}

	/// <summary>解析 {algorithm, layers, payload} 并按层号从外向内剥 Fernet。</summary>
	public static (bool Ok, string Message, string? Salt) DecodeCrcSaltResponse(string json, string token)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;
			if (!root.TryGetProperty("payload", out JsonElement payloadEl))
			{
				return (Ok: false, Message: "响应无 payload 字段", Salt: null);
			}
			string payload = payloadEl.GetString() ?? "";
			int layers = 3;
			if (root.TryGetProperty("layers", out JsonElement layersEl) && layersEl.ValueKind == JsonValueKind.Number)
			{
				layers = layersEl.GetInt32();
			}
			if (layers < 1)
			{
				layers = 1;
			}
			if (layers > 8)
			{
				layers = 8;
			}
			LogHelper.Salt($"[404] 开始剥层：声明层数={layers} payload长度={payload.Length}");
			for (int n = layers; n >= 1; n--)
			{
				byte[] key = Fernet.DeriveKey("404-crc-salt-layer-" + n + ":" + token);
				if (!Fernet.TryDecrypt(key, payload, out byte[]? plain) || plain == null)
				{
					LogHelper.Salt($"[404] 第 {n} 层解密失败（token 长度={token.Length}）");
					return (Ok: false, Message: "第 " + n + " 层 Fernet 解密失败（token 不匹配或层数不符）", Salt: null);
				}
				LogHelper.Salt($"[404] 第 {n} 层解密成功，产出 {plain!.Length}B" + ((n == 1) ? "" : "，继续剥内层"));
				if (n == 1)
				{
					string salt = Encoding.UTF8.GetString(plain).Trim().ToUpperInvariant();
					if (salt.Length != 32 || !System.Linq.Enumerable.All(salt, (char c) => Uri.IsHexDigit(c)))
					{
						return (Ok: false, Message: "末层明文不是 32 位十六进制盐值: " + Truncate(salt), Salt: null);
					}
					return (Ok: true, Message: "三层剥层成功", Salt: salt);
				}
				// 外层剥开后的明文就是内层的 Fernet token 字符串本身（已是 base64url 文本），
				// 直接作为下一层入参，不能再做一次 base64 编码。
				payload = Encoding.UTF8.GetString(plain).Trim();
			}
			return (Ok: false, Message: "剥层循环异常", Salt: null);
		}
		catch (Exception ex)
		{
			return (Ok: false, Message: "响应解析失败: " + ex.GetType().Name, Salt: null);
		}
	}

	/// <summary>本机保存的 404 账号（经 SecureStore 加密，与「账号绑定」同一套存储机制）。</summary>
	public sealed class Auth404Account
	{
		public string Username { get; set; } = "";

		public string Password { get; set; } = "";

		public string Token { get; set; } = "";

		public string HwidMode { get; set; } = "";

		public string SavedAt { get; set; } = "";
	}

	private const string AccountStoreName = "auth404_account";

	/// <summary>
	/// 把 404 账号（含密码）长期保存到本机。用 SecureStore 加密落盘，
	/// 与「账号绑定」的 local_accounts 同一套机制，重启程序后仍在。
	/// </summary>
	public static void SaveAccount(string username, string password, string token, string hwidMode = "")
	{
		try
		{
			SecureStore.Save(AccountStoreName, JsonSerializer.Serialize(new Auth404Account
			{
				Username = username,
				Password = password,
				Token = token,
				HwidMode = hwidMode,
				SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
			}));
		}
		catch
		{
		}
	}

	/// <summary>读取本机保存的 404 账号（没有则返回 null）。</summary>
	public static Auth404Account? LoadAccount()
	{
		try
		{
			string? text = SecureStore.Load(AccountStoreName);
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}
			return JsonSerializer.Deserialize<Auth404Account>(text!);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>会话存储名（SecureStore/DPAPI 加密保存，不再明文落盘 token）。</summary>
	private const string SessionStoreName = "auth404_session";

	// 兼容：历史版本曾把 token 明文写到 json，读取时回退一次并在迁移后删除明文文件
	public static string SessionFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MhyunBox", "auth404_session.json");

	public static void SaveSession(string token, string username)
	{
		try
		{
			SecureStore.Save(SessionStoreName, JsonSerializer.Serialize(new Dictionary<string, string> { ["token"] = token, ["username"] = username }));
			try
			{
				if (File.Exists(SessionFilePath))
				{
					File.Delete(SessionFilePath);
				}
			}
			catch
			{
			}
		}
		catch
		{
		}
	}

	public static (string Token, string Username) LoadSession()
	{
		try
		{
			string? text = SecureStore.Load(SessionStoreName);
			if (!string.IsNullOrWhiteSpace(text))
			{
				using JsonDocument doc = JsonDocument.Parse(text);
				return (doc.RootElement.GetProperty("token").GetString() ?? "", doc.RootElement.TryGetProperty("username", out JsonElement u) ? (u.GetString() ?? "") : "");
			}
		}
		catch
		{
		}
		try
		{
			if (File.Exists(SessionFilePath))
			{
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SessionFilePath));
				(string Token, string Username) result = (doc.RootElement.GetProperty("token").GetString() ?? "", doc.RootElement.TryGetProperty("username", out JsonElement u) ? (u.GetString() ?? "") : "");
				// 旧明文会话迁移到加密存储后删除明文文件
				if (!string.IsNullOrWhiteSpace(result.Token))
				{
					SaveSession(result.Token, result.Username);
				}
				return result;
			}
		}
		catch
		{
		}
		return ("", "");
	}

	/// <summary>
	/// 把 404 服务端的错误响应翻译成可直接展示的中文提示。
	/// 实测：/api/crc-salt 缺 X-Client-Version 时会返回 HTTP 426 且带 latest_version 字段
	/// （版本门禁），403 则带 error=账号未授权或订阅已过期。
	/// </summary>
	public static string DescribeError(int statusCode, string body)
	{
		string raw = Truncate(body);
		try
		{
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;
			string err = "";
			string latest = "";
			if (root.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String)
			{
				err = e.GetString() ?? "";
			}
			if (root.TryGetProperty("latest_version", out JsonElement l) && l.ValueKind == JsonValueKind.String)
			{
				latest = l.GetString() ?? "";
			}
			if (statusCode == 426 && latest.Length > 0)
			{
				return "服务端版本门禁：要求客户端版本 ≥ " + latest + "（本程序当前伪装为 " + AppVersion + "）";
			}
			if (err.Length > 0)
			{
				return "服务端返回：" + err;
			}
		}
		catch
		{
		}
		return "HTTP " + statusCode + ": " + raw;
	}

	private static string Truncate(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "(空)";
		}
		return (s.Length > 200) ? (s.Substring(0, 200) + "…") : s;
	}
}
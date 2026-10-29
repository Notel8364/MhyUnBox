using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>
/// 网易「服务器列表入口」自动发现（协议逆向自 Alane2.exe 的 Alane.patched.dll / Codexus.Cipher.dll）：
///   1) POST /item/query/available（master_type_id=2，兜底 11）
///      body: {"available_mc_versions":[],"item_type":1,"length":N,"offset":M,"master_type_id":"2","secondary_type_id":""}
///      响应 entities[]: entity_id(=gameId) / name / online_count / title_image_url / brief_summary
///      headers: user-id / user-token（NeteaseCipher.ComputeHttpRequestToken 签名）、user-agent: WPFLauncher/0.0.0.0
///   2) POST /item-address/get  body: {"item_id":"<gameId>"}
///      响应 entity: ip / port / game_status / announcement / in_whitelist
///   3) 兜底 POST /item-details/get_v2  body: {"item_id":"<gameId>"}
///      响应 entity: server_address / server_port / brief_image_urls / detail_description
///   4) 端口仍无效时按 Alane 同款逻辑回落 25565。
/// 拿到 ip:port 后注册为动态 ServerPreset，即可走既有的代理启动链路。
/// </summary>
public static class NeteaseServerListService
{
	private const string ApiBase = "https://x19apigatewayobt.nie.netease.com";

	private const string ListPath = "/item/query/available";

	private const string AddressPath = "/item-address/get";

	private const string DetailPath = "/item-details/get_v2";

	private const string UserAgent = "WPFLauncher/0.0.0.0";

	private static readonly string[] MasterTypeCandidates = new string[2] { "2", "11" };

	private static readonly JsonSerializerOptions Relaxed = new JsonSerializerOptions
	{
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	// ---------- 封面图下载（BitmapImage 直接加载 CDN URL 常因 UA/防盗链失败，改为手动下载） ----------

	private static readonly HttpClient ImageHttp = new HttpClient(new HttpClientHandler { UseProxy = false })
	{
		Timeout = TimeSpan.FromSeconds(15.0)
	};

	/// <summary>下载服务器封面图（带 WPFLauncher UA 与 Referer，规避 CDN 防盗链）。失败返回 null。</summary>
	public static async Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return null;
		}
		try
		{
			using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
			req.Headers.UserAgent.ParseAdd(UserAgent);
			req.Headers.TryAddWithoutValidation("Referer", "https://mc.163.com/");
			req.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/png,image/jpeg,*/*");
			using HttpResponseMessage resp = await ImageHttp.SendAsync(req, ct).ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
			{
				return null;
			}
			byte[] data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
			return (data.Length > 0) ? data : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	public sealed class NetGameSummary
	{
		[JsonPropertyName("entity_id")]
		public string EntityId { get; set; } = "";

		[JsonPropertyName("name")]
		public string Name { get; set; } = "";

		[JsonPropertyName("online_count")]
		public string OnlineCount { get; set; } = "";

		[JsonPropertyName("title_image_url")]
		public string TitleImageUrl { get; set; } = "";

		[JsonPropertyName("brief_summary")]
		public string BriefSummary { get; set; } = "";
	}

	public sealed class ServerAddressInfo
	{
		public bool Ok;

		public string Ip = "";

		public int Port;

		public int GameStatus;

		public bool InWhitelist;

		public string Announcement = "";

		public string Message = "";

		public string HostPort => (Ok && Port > 0) ? (Ip + ":" + Port) : Ip;
	}

	/// <summary>
	/// 拉取账号可用的网络游戏（服务器）列表，带分页。
	/// 返回按 name 去重后的列表；失败时抛出异常由调用方处理提示。
	/// </summary>
	public static async Task<List<NetGameSummary>> ListServersAsync(string userId, string userToken, int offset, int length, string? keyword = null, CancellationToken ct = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userToken))
		{
			throw new InvalidOperationException("缺少网易游戏会话（EntityId/Token），请先到账号绑定页登录");
		}
		foreach (string masterType in MasterTypeCandidates)
		{
			string body = JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["available_mc_versions"] = Array.Empty<string>(),
				["item_type"] = 1,
				["length"] = length,
				["offset"] = offset,
				["master_type_id"] = masterType,
				["secondary_type_id"] = ""
			}, Relaxed);
			var (status, text) = await PostAsync(ListPath, body, userId, userToken, ct);
			(int code, string? msg) = ParseCode(text);
			LogHelper.Log($"[服务器列表] POST {ListPath} master_type={masterType} offset={offset} length={length} => HTTP {status} code={code} {msg}");
			if (code != 0)
			{
				continue;
			}
			List<NetGameSummary> list = new List<NetGameSummary>();
			try
			{
				using JsonDocument doc = JsonDocument.Parse(text);
				if (doc.RootElement.TryGetProperty("entities", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement el in arr.EnumerateArray())
					{
						NetGameSummary? item = JsonSerializer.Deserialize<NetGameSummary>(el.GetRawText(), Relaxed);
						if (item != null && !string.IsNullOrWhiteSpace(item.EntityId) && !string.IsNullOrWhiteSpace(item.Name))
						{
							list.Add(item);
						}
					}
				}
			}
			catch
			{
			}
			if (list.Count > 0 || offset == 0)
			{
				if (!string.IsNullOrWhiteSpace(keyword))
				{
					string k = keyword.Trim();
					list = list.Where((NetGameSummary s) => s.Name.Contains(k, StringComparison.OrdinalIgnoreCase) || s.EntityId.Contains(k, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(s.BriefSummary) && s.BriefSummary.Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();
				}
				return list;
			}
		}
		throw new InvalidOperationException("服务器列表查询失败（未返回有效数据，请确认账号会话有效后重试）");
	}

	/// <summary>
	/// 解析指定服务器（gameId）的实际进入地址 ip:port。
	/// 顺序：/item-address/get → /item-details/get_v2(server_address/server_port) → 端口回落 25565。
	/// </summary>
	public static async Task<ServerAddressInfo> ResolveAddressAsync(string userId, string userToken, string gameId, CancellationToken ct = default(CancellationToken))
	{
		ServerAddressInfo info = new ServerAddressInfo();
		if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userToken))
		{
			info.Message = "缺少网易游戏会话（EntityId/Token），请先到账号绑定页登录";
			return info;
		}
		if (string.IsNullOrWhiteSpace(gameId) || !long.TryParse(gameId, out _))
		{
			info.Message = "gameId 无效";
			return info;
		}
		try
		{
			// 1) /item-address/get
			string body = "{\"item_id\":\"" + gameId + "\"}";
			var (status, text) = await PostAsync(AddressPath, body, userId, userToken, ct);
			(int code, string? msg) = ParseCode(text);
			LogHelper.Log($"[服务器列表] POST {AddressPath} item_id={gameId} => HTTP {status} code={code} {msg}");
			if (code == 0)
			{
				JsonElement? entity = TryGetEntity(text);
				if (entity.HasValue)
				{
					info.Ip = TryGetString(entity.Value, "ip") ?? "";
					info.Port = TryGetInt(entity.Value, "port");
					info.GameStatus = TryGetInt(entity.Value, "game_status");
					info.InWhitelist = TryGetBool(entity.Value, "in_whitelist") ?? false;
					info.Announcement = TryGetString(entity.Value, "announcement") ?? "";
				}
			}
			// 2) /item-details/get_v2 兜底 server_address/server_port
			if (string.IsNullOrWhiteSpace(info.Ip) || info.Port <= 0)
			{
				var (status2, text2) = await PostAsync(DetailPath, body, userId, userToken, ct);
				(int code2, string? msg2) = ParseCode(text2);
				LogHelper.Log($"[服务器列表] POST {DetailPath} item_id={gameId} => HTTP {status2} code={code2} {msg2}");
				if (code2 == 0)
				{
					JsonElement? detail = TryGetEntity(text2);
					if (detail.HasValue)
					{
						if (string.IsNullOrWhiteSpace(info.Ip))
						{
							info.Ip = TryGetString(detail.Value, "server_address") ?? "";
						}
						if (info.Port <= 0)
						{
							info.Port = TryGetInt(detail.Value, "server_port");
						}
						if (string.IsNullOrWhiteSpace(info.Announcement))
						{
							info.Announcement = TryGetString(detail.Value, "detail_description") ?? "";
						}
					}
				}
			}
			// 3) 端口回落（与 Alane 同款：无效端口一律 25565）
			if (!string.IsNullOrWhiteSpace(info.Ip) && info.Port <= 0)
			{
				info.Port = 25565;
			}
			info.Ok = !string.IsNullOrWhiteSpace(info.Ip) && info.Port > 0;
			if (!info.Ok)
			{
				info.Message = "未能解析到服务器地址（接口未返回 ip/port）";
			}
			return info;
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[服务器列表] 地址解析异常");
			info.Message = "地址解析异常：" + ex.Message;
			return info;
		}
	}

	// ---------- 缓存 ----------

	private static string CachePath => System.IO.Path.Combine(AppContext.BaseDirectory, "MhyunBox_Logs", "server_list_cache.json");

	public static List<NetGameSummary> LoadCache()
	{
		try
		{
			if (File.Exists(CachePath))
			{
				return JsonSerializer.Deserialize<List<NetGameSummary>>(File.ReadAllText(CachePath)) ?? new List<NetGameSummary>();
			}
		}
		catch
		{
		}
		return new List<NetGameSummary>();
	}

	public static void SaveCache(List<NetGameSummary> items)
	{
		try
		{
			string? dir = System.IO.Path.GetDirectoryName(CachePath);
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}
			File.WriteAllText(CachePath, JsonSerializer.Serialize(items, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch
		{
		}
	}

	// ---------- 底层 ----------

	private static async Task<(int Status, string Body)> PostAsync(string path, string body, string userId, string userToken, CancellationToken ct)
	{
		// 服务器列表是登录后立即拉取的，直连不受用户加速器/系统代理影响
		using HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false });
		http.Timeout = TimeSpan.FromSeconds(20.0);
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, ApiBase + path);
		request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
		request.Content = new StringContent(body, Encoding.UTF8, "application/json");
		foreach (KeyValuePair<string, string> item in NeteaseCipher.ComputeHttpRequestToken(path, body, userId, userToken))
		{
			request.Headers.TryAddWithoutValidation(item.Key, item.Value);
		}
		using HttpResponseMessage response = await http.SendAsync(request, ct);
		return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
	}

	private static JsonElement? TryGetEntity(string json)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("entity", out JsonElement el))
			{
				return el.Clone();
			}
		}
		catch
		{
		}
		return null;
	}

	private static (int Code, string? Message) ParseCode(string text)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(text);
			if (doc.RootElement.TryGetProperty("code", out JsonElement value))
			{
				int code = 0;
				if (value.ValueKind == JsonValueKind.Number)
				{
					value.TryGetInt32(out code);
				}
				else if (value.ValueKind == JsonValueKind.String)
				{
					int.TryParse(value.GetString(), out code);
				}
				string? msg = null;
				if (doc.RootElement.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String)
				{
					msg = m.GetString();
				}
				return (Code: code, Message: msg);
			}
		}
		catch
		{
		}
		return (Code: -1, Message: null);
	}

	private static string? TryGetString(JsonElement el, string key)
	{
		try
		{
			if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String)
			{
				return v.GetString();
			}
		}
		catch
		{
		}
		return null;
	}

	private static int TryGetInt(JsonElement el, string key)
	{
		try
		{
			if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out JsonElement v))
			{
				if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
				{
					return n;
				}
				if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int n2))
				{
					return n2;
				}
			}
		}
		catch
		{
		}
		return -1;
	}

	private static bool? TryGetBool(JsonElement el, string key)
	{
		try
		{
			if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out JsonElement v))
			{
				if (v.ValueKind == JsonValueKind.True)
				{
					return true;
				}
				if (v.ValueKind == JsonValueKind.False)
				{
					return false;
				}
				if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool b))
				{
					return b;
				}
			}
		}
		catch
		{
		}
		return null;
	}
}

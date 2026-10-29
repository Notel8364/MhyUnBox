using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>盐值来源类型。优先级：手输 > Alane（首位，永不被覆盖） > 404 > 网易官方 DLL。</summary>
public enum SaltSourceKind
{
	/// <summary>用户手动输入（首页盐值填入框）。</summary>
	Manual,
	/// <summary>Alane 盐值端点（http://191.40.41.220/api/crc_salt.txt，现有默认）。</summary>
	Alane,
	/// <summary>404Launcher 授权服务器（3 层 Fernet）。</summary>
	Auth404,
	/// <summary>网易官方启动器 DLL（控制端口索取 + 版本读取）。</summary>
	Official
}

/// <summary>单个盐值源的运行状态。</summary>
/// <summary>「三源一致性对比」中单个源的结果。</summary>
public sealed class SaltSourceComparison
{
	public SaltSourceKind Kind;

	public string Name = "";

	public string Address = "";

	public bool Ok;

	public string? Salt;

	public string Message = "";

	/// <summary>官方源顺带读到的启动器版本（盐↔版本成对校验）。</summary>
	public string Version = "";

	public string StatusLine => (Ok ? ("✅ " + Name + "：" + CrcSaltSourceManager.Mask(Salt ?? "")) : ("❌ " + Name + "：" + Message));
}

public sealed class SaltSourceState
{
	public SaltSourceKind Kind;

	public string Name = "";

	/// <summary>当前主地址（HTTP 源；官方源为空）。</summary>
	public string Address = "";

	/// <summary>历史可用地址（回退栈，最后一个是最近成功的）。</summary>
	public List<string> AddressHistory = new List<string>();

	/// <summary>本次会话内是否已被标记失败（status=0x11 后跳过）。</summary>
	public bool SessionFailed;

	public bool LastOk;

	public string LastMessage = "";

	public DateTime LastAttemptUtc;

	public string? LastSalt;

	public bool Available => !SessionFailed;

	public string StatusLine
	{
		get
		{
			string state = (SessionFailed ? "已失效(本会话跳过)" : (LastOk ? "可用" : ((LastAttemptUtc == default(DateTime)) ? "未尝试" : "上次失败")));
			return Name + "：" + state + (string.IsNullOrEmpty(LastMessage) ? "" : ("（" + LastMessage + "）"));
		}
	}
}

/// <summary>
/// CrcSalt 盐值源管理器：优先级链 + status=0x11 自动轮换 + 地址回退。
/// 规则：
/// 1. 手输盐值优先（首页填入框，默认 = 最早第一次成功会话的盐值）；
/// 2. Alane 地址请求保持首位且不被其他源覆盖；
/// 3. status=0x11（盐值对不上 / 版本号不对的游戏会话错误）→ 当前源标记失效，转向下一个源；
/// 4. 地址不可用时回退该源之前可用的地址；全部源失败则保持上次可用盐值不清零。
/// </summary>
public static class CrcSaltSourceManager
{
	private static readonly List<SaltSourceState> _sources = new List<SaltSourceState>();

	private static readonly object Gate = new object();

	public static event Action? StateChanged;

	public static IReadOnlyList<SaltSourceState> Sources => _sources;

	/// <summary>当前状态摘要（UI 反馈）。</summary>
	public static string StatusText { get; private set; } = "尚未获取";

	/// <summary>最近一次成功产盐的源。</summary>
	public static SaltSourceKind LastSuccessKind { get; private set; } = SaltSourceKind.Alane;

	static CrcSaltSourceManager()
	{
		_sources.Add(new SaltSourceState
		{
			Kind = SaltSourceKind.Alane,
			Name = "Alane",
			Address = DefaultAlaneAddress
		});
		_sources.Add(new SaltSourceState
		{
			Kind = SaltSourceKind.Auth404,
			Name = "404授权服",
			Address = Auth404Client.DefaultBaseUrl
		});
		_sources.Add(new SaltSourceState
		{
			Kind = SaltSourceKind.Official,
			Name = "网易官方DLL"
		});
	}

	public const string DefaultAlaneAddress = "http://191.40.41.220/api/crc_salt.txt";

	private static SaltSourceState Get(SaltSourceKind kind)
	{
		return _sources.First((SaltSourceState s) => s.Kind == kind);
	}

	// ---------- 地址配置（与设置互通） ----------

	/// <summary>Alane 地址：用户设置优先，否则默认（Alane 首位默认地址不可被覆盖删除）。</summary>
	public static string AlaneAddress
	{
		get
		{
			try
			{
				string v = MhyunBox.Settings.SettingsManager.Instance?.AlaneSaltUrl ?? "";
				return string.IsNullOrWhiteSpace(v) ? DefaultAlaneAddress : v.Trim();
			}
			catch
			{
				return DefaultAlaneAddress;
			}
		}
	}

	public static string Auth404Address
	{
		get
		{
			try
			{
				string v = MhyunBox.Settings.SettingsManager.Instance?.Auth404BaseUrl ?? "";
				return string.IsNullOrWhiteSpace(v) ? Auth404Client.DefaultBaseUrl : v.Trim();
			}
			catch
			{
				return Auth404Client.DefaultBaseUrl;
			}
		}
	}

	/// <summary>用户是否手输了盐值（手输优先，禁用自动覆盖）。</summary>
	public static bool HasManualOverride
	{
		get
		{
			try
			{
				string v = MhyunBox.Settings.SettingsManager.Instance?.CrcSalt ?? "";
				return v.Length == 32 && v.All((char c) => Uri.IsHexDigit(c));
			}
			catch
			{
				return false;
			}
		}
	}

	// ---------- 索取 ----------

	/// <summary>
	/// 按优先级链索取盐值。返回 (盐值, 版本号, 源名)；全部失败返回 (null, null, "")。
	/// </summary>
	public static async Task<(string? Salt, string? Version, string SourceName)> AcquireAsync(CancellationToken ct, Action<string>? log = null)
	{
		try
		{
			MhyunBox.Settings.AppSettings? cfg = MhyunBox.Settings.SettingsManager.Instance;
			Get(SaltSourceKind.Alane).Address = AlaneAddress;
			Get(SaltSourceKind.Auth404).Address = Auth404Address;
			// 0) 手输优先
			string manual = cfg?.CrcSalt ?? "";
			if (manual.Length == 32 && manual.All((char c) => Uri.IsHexDigit(c)))
			{
				SetStatus("使用手输盐值 " + Mask(manual));
				return (manual, cfg?.LastGoodLauncherVersion, "手输");
			}
			// 1) Alane（首位）
			var (salt, msg) = await TryAlaneAsync(ct, log).ConfigureAwait(false);
			if (salt != null)
			{
				MarkSuccess(SaltSourceKind.Alane, salt, msg);
				return (salt, null, "Alane");
			}
			MarkFailure(SaltSourceKind.Alane, msg);
			// 2) 404（需要已登录 token）
			if (!string.IsNullOrWhiteSpace(cfg?.Auth404Token))
			{
				string hwid = cfg?.Auth404FixedHwid ?? "";
				string mode = cfg?.Auth404HwidMode ?? Auth404Client.DefaultHwidMode;
				if (string.IsNullOrEmpty(hwid) && (mode == "spoof" || mode == "random"))
				{
					hwid = Auth404Client.ComputeHwid(mode, cfg?.Auth404Username);
				}
				else if (string.IsNullOrEmpty(hwid))
				{
					hwid = Auth404Client.ComputeHwid("real");
				}
				var (ok2, s2, m2) = await Auth404Client.FetchCrcSaltAsync(Auth404Address, cfg!.Auth404Token!, hwid, ct).ConfigureAwait(false);
				if (ok2 && s2 != null)
				{
					MarkSuccess(SaltSourceKind.Auth404, s2!, m2);
					return (s2, null, "404授权服");
				}
				MarkFailure(SaltSourceKind.Auth404, m2);
			}
			else
			{
				Get(SaltSourceKind.Auth404).LastMessage = "未登录 404 账号";
			}
			// 3) 网易官方 DLL
			OfficialSaltResult official = await OfficialLauncherSaltClient.TryAcquireAsync(ct, log).ConfigureAwait(false);
			if (official.Salt != null)
			{
				MarkSuccess(SaltSourceKind.Official, official.Salt!, official.Message);
				return (official.Salt, official.Version, "网易官方DLL");
			}
			MarkFailure(SaltSourceKind.Official, official.Message);
			// 官方源即使没拿到盐，也可能拿到版本号 —— 供调用方参考
			if (!string.IsNullOrEmpty(official.Version))
			{
				log?.Invoke("[盐值源] 官方源未取得盐值，但取得启动器版本 " + official.Version);
				return (null, official.Version, "网易官方DLL(仅版本)");
			}
			SetStatus("所有盐值源均不可用：" + string.Join("；", _sources.Select((SaltSourceState s) => s.Name + "(" + s.LastMessage + ")")));
			return (null, null, "");
		}
		catch (Exception ex)
		{
			SetStatus("盐值索取异常: " + ex.Message);
			return (null, null, "");
		}
	}

	private static async Task<(string? Salt, string Message)> TryAlaneAsync(CancellationToken ct, Action<string>? log)
	{
		SaltSourceState s = Get(SaltSourceKind.Alane);
		s.LastAttemptUtc = DateTime.UtcNow;
		List<string> candidates = ExpandAddresses(AlaneAddress);
		List<string> errors = new List<string>();
		foreach (string url in candidates)
		{
			var (ok, salt, msg) = await ProbeSaltUrlAsync(url, ct).ConfigureAwait(false);
			if (ok && salt != null)
			{
				RememberAddress(s, url);
				return (salt, "获取成功");
			}
			errors.Add(url + " " + msg);
		}
		string primaryErr = string.Join(" ｜ ", errors);
		// 当前地址不可用 → 逐个回退到本会话记录过可用的旧地址，真正再发一次请求
		foreach (string fallback in s.AddressHistory.AsEnumerable().Reverse())
		{
			if (candidates.Contains(fallback, StringComparer.OrdinalIgnoreCase))
			{
				continue;
			}
			log?.Invoke("[盐值源] Alane 当前地址不可用，回退之前可用地址 " + fallback);
			var (fok, fsalt, fmsg) = await ProbeSaltUrlAsync(fallback, ct).ConfigureAwait(false);
			if (fok && fsalt != null)
			{
				log?.Invoke("[盐值源] 回退地址 " + fallback + " 可用，已采用");
				return (fsalt, "当前地址不可用，已回退 " + fallback + " 并获取成功");
			}
			primaryErr = primaryErr + " ｜ 回退 " + fallback + " 也不可用：" + fmsg;
		}
		return (null, primaryErr);
	}

	// ---------- 地址探测 ----------

	/// <summary>把可能含换行/逗号分隔的地址串拆成地址列表（去除空项与重复项）。</summary>
	public static List<string> ExpandAddresses(string raw)
	{
		List<string> list = new List<string>();
		if (string.IsNullOrWhiteSpace(raw))
		{
			list.Add(DefaultAlaneAddress);
			return list;
		}
		foreach (string s in raw.Replace("\r\n", "\n").Replace(',', '\n').Split('\n'))
		{
			string t = s.Trim();
			if (!string.IsNullOrWhiteSpace(t) && !list.Contains(t))
			{
				list.Add(t);
			}
		}
		if (list.Count == 0)
		{
			list.Add(DefaultAlaneAddress);
		}
		return list;
	}

	/// <summary>从响应文本中提取 32 位十六进制盐值（支持裸文本与 JSON 两种形态）。</summary>
	public static string? ExtractSaltFromText(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		Match m = Regex.Match(text, "(?<![0-9A-Za-z])([0-9A-Fa-f]{32})(?![0-9A-Za-z])");
		if (m.Success)
		{
			return m.Groups[1].Value.ToUpperInvariant();
		}
		Match j = Regex.Match(text, "\"(?:salt|crc_salt|crcSalt)\"\\s*:\\s*\"([0-9A-Fa-f]{32})\"", RegexOptions.IgnoreCase);
		if (j.Success)
		{
			return j.Groups[1].Value.ToUpperInvariant();
		}
		return null;
	}

	/// <summary>探测单个盐值地址是否可用：可用则返回盐值，否则返回具体失败原因（供输入框下方反馈）。</summary>
	public static async Task<(bool Ok, string? Salt, string Message)> ProbeSaltUrlAsync(string url, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return (false, null, "地址为空");
		}
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u) || (u.Scheme != "http" && u.Scheme != "https"))
		{
			return (false, null, "地址格式不正确（须为 http:// 或 https:// 开头的绝对地址）");
		}
		try
		{
			using HttpClient http = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(8.0)
			};
			http.DefaultRequestHeaders.UserAgent.ParseAdd("MhyunBox/7.7.0");
			using HttpResponseMessage resp = await http.GetAsync(u, ct).ConfigureAwait(false);
			string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
			{
				return (false, null, "HTTP " + (int)resp.StatusCode + ((text.Length > 0) ? (": " + Trim(text)) : ""));
			}
			string? salt = ExtractSaltFromText(text);
			if (salt == null)
			{
				return (false, null, "响应中没有 32 位十六进制盐值：" + Trim(text));
			}
			return (true, salt, "可用");
		}
		catch (Exception ex)
		{
			return (false, null, ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static string Trim(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "(空)";
		}
		s = s.Trim();
		return (s.Length > 120) ? (s.Substring(0, 120) + "…") : s;
	}

	/// <summary>校验首页 Alane 地址输入框：返回结果含盐值/失败原因，并在失败时给出已记录的回退地址。</summary>
	public static async Task<(bool Ok, string? Salt, string Message, string? Fallback)> ValidateAlaneAddressAsync(string url, CancellationToken ct)
	{
		var (ok, salt, msg) = await ProbeSaltUrlAsync(url, ct).ConfigureAwait(false);
		if (ok)
		{
			RememberAddress(Get(SaltSourceKind.Alane), url);
			return (true, salt, "地址可用，已获取盐值 " + Mask(salt ?? ""), null);
		}
		string? fallback = Get(SaltSourceKind.Alane).AddressHistory.LastOrDefault((string a) => !string.Equals(a, url, StringComparison.OrdinalIgnoreCase));
		return (false, null, "地址不可用：" + msg, fallback);
	}

	// ---------- 三源一致性对比 ----------

	/// <summary>
	/// 逐个请求三个盐值源并把结果摊开对比。
	/// 只读：不写入任何设置、不改动当前缓存的盐值，纯粹用于诊断「同一时间点三源是否给同一个值」。
	/// </summary>
	public static async Task<List<SaltSourceComparison>> CompareSourcesAsync(CancellationToken ct, Action<string>? log = null)
	{
		List<SaltSourceComparison> rows = new List<SaltSourceComparison>();
		LogHelper.Salt("======== 三源一致性对比开始 ========");
		MhyunBox.Settings.AppSettings? cfg = null;
		try
		{
			cfg = MhyunBox.Settings.SettingsManager.Instance;
		}
		catch
		{
		}
		// 1) Alane
		SaltSourceComparison alane = new SaltSourceComparison
		{
			Kind = SaltSourceKind.Alane,
			Name = "Alane",
			Address = AlaneAddress
		};
		List<string> alaneErrors = new List<string>();
		foreach (string url in ExpandAddresses(AlaneAddress))
		{
			var (ok, salt, msg) = await ProbeSaltUrlAsync(url, ct).ConfigureAwait(false);
			if (ok && salt != null)
			{
				alane.Ok = true;
				alane.Salt = salt;
				alane.Message = "获取成功";
				break;
			}
			alaneErrors.Add(url + " " + msg);
		}
		if (!alane.Ok)
		{
			alane.Message = string.Join(" ｜ ", alaneErrors);
		}
		rows.Add(alane);
		// 2) 404
		SaltSourceComparison a404 = new SaltSourceComparison
		{
			Kind = SaltSourceKind.Auth404,
			Name = "404授权服",
			Address = Auth404Address
		};
		string token = cfg?.Auth404Token ?? "";
		if (string.IsNullOrWhiteSpace(token))
		{
			a404.Message = "未登录 404 账号（需登录且订阅有效才能取盐）";
			LogHelper.Salt("[链路] 404 源：settings 里没有 Auth404Token，视作未登录（先去首页登录 404 账号）");
		}
		else
		{
			string mode = cfg?.Auth404HwidMode ?? Auth404Client.DefaultHwidMode;
			string hwid = Auth404Client.ComputeHwid(mode, cfg?.Auth404Username);
			var (ok, msg, salt) = await Auth404Client.FetchCrcSaltAsync(Auth404Address, token!, hwid, ct).ConfigureAwait(false);
			a404.Ok = ok;
			a404.Salt = salt;
			a404.Message = msg;
		}
		rows.Add(a404);
		// 3) 官方 DLL
		SaltSourceComparison official = new SaltSourceComparison
		{
			Kind = SaltSourceKind.Official,
			Name = "网易官方DLL",
			Address = "控制端口（需游戏进程运行）"
		};
		OfficialSaltResult r = await OfficialLauncherSaltClient.TryAcquireAsync(ct, log).ConfigureAwait(false);
		official.Ok = r.Salt != null;
		official.Salt = r.Salt;
		official.Message = r.Message;
		official.Version = r.Version ?? "";
		rows.Add(official);
		LogHelper.Salt($"[链路] Alane={alane.Salt ?? "失败"} 404={a404.Salt ?? "失败"} 官方={official.Salt ?? "失败"}（官方版本={official.Version}）");
		LogHelper.Salt("======== 三源一致性对比结束 ========");
		return rows;
	}

	/// <summary>给对比结果生成结论：可用源 ≥2 时判定是否一致。</summary>
	public static string BuildComparisonVerdict(List<SaltSourceComparison> rows)
	{
		List<SaltSourceComparison> available = rows.Where((SaltSourceComparison r) => r.Ok && r.Salt != null).ToList();
		if (available.Count == 0)
		{
			return "三个源当前均不可用，无法比较一致性。";
		}
		if (available.Count == 1)
		{
			return "当前只有 1 个源可用（" + available[0].Name + "），无法比较一致性。\n" +
				"另外两个源的前置条件：404 授权服需登录 404 账号且订阅有效；网易官方 DLL 需游戏进程正在运行（会读取其控制端口）。";
		}
		bool allSame = available.All((SaltSourceComparison r) => r.Salt == available[0].Salt);
		if (allSame)
		{
			return "✅ " + available.Count + " 个可用源给出的盐值完全一致：" + Mask(available[0].Salt!);
		}
		return "⚠ " + available.Count + " 个可用源给出的盐值不一致：" +
			string.Join("、", available.Select((SaltSourceComparison r) => r.Name + "=" + Mask(r.Salt!))) +
			"\n说明各源同步时机不同 —— 应以能成功进游戏的那个为准（盐值与启动器版本号成对校验）。";
	}

	// ---------- 状态维护 ----------

	private static void RememberAddress(SaltSourceState s, string address)
	{
		if (string.IsNullOrEmpty(address))
		{
			return;
		}
		if (!s.AddressHistory.Contains(address))
		{
			s.AddressHistory.Add(address);
		}
	}

	/// <summary>status=0x11：当前源标记失效并转向下一源（Alane 永驻首位，仅本会话跳过）。</summary>
	public static void ReportVerifyFailure(string detail)
	{
		lock (Gate)
		{
			SaltSourceKind failed = LastSuccessKind;
			SaltSourceState s = Get(failed);
			s.SessionFailed = true;
			s.LastOk = false;
			s.LastMessage = "status=0x11 盐/版本校验失败";
			SaltSourceKind next = NextAvailable(failed);
			StatusText = "检测到 " + detail + " → " + s.Name + " 已失效，转向 " + ((next == failed) ? "无可用源（保持上次盐值）" : Get(next).Name);
			LogHelper.Log("[盐值源] " + StatusText);
			StateChanged?.Invoke();
		}
	}

	private static SaltSourceKind NextAvailable(SaltSourceKind from)
	{
		SaltSourceKind[] order = new SaltSourceKind[3]
		{
			SaltSourceKind.Alane,
			SaltSourceKind.Auth404,
			SaltSourceKind.Official
		};
		int idx = Array.IndexOf(order, from);
		for (int i = 1; i <= order.Length; i++)
		{
			SaltSourceKind k = order[(idx + i) % order.Length];
			if (Get(k).Available)
			{
				return k;
			}
		}
		return from;
	}

	/// <summary>源成功后调用：记住地址与盐值。</summary>
	public static void MarkSuccess(SaltSourceKind kind, string salt, string message)
	{
		lock (Gate)
		{
			SaltSourceState s = Get(kind);
			s.SessionFailed = false;
			s.LastOk = true;
			s.LastMessage = message;
			s.LastAttemptUtc = DateTime.UtcNow;
			s.LastSalt = salt;
			if (!string.IsNullOrEmpty(s.Address))
			{
				RememberAddress(s, s.Address);
			}
			LastSuccessKind = kind;
			StatusText = "当前盐值来源：" + s.Name + "（" + Mask(salt) + "）";
			StateChanged?.Invoke();
		}
	}

	public static void MarkFailure(SaltSourceKind kind, string message)
	{
		lock (Gate)
		{
			SaltSourceState s = Get(kind);
			s.LastOk = false;
			s.LastMessage = message;
			s.LastAttemptUtc = DateTime.UtcNow;
			StateChanged?.Invoke();
		}
	}

	/// <summary>重置会话级失效标记（用户手动重试时调用）。</summary>
	public static void ResetSessionFlags()
	{
		lock (Gate)
		{
			foreach (SaltSourceState s in _sources)
			{
				s.SessionFailed = false;
			}
			StatusText = "已重置盐值源失效标记";
			StateChanged?.Invoke();
		}
	}

	public static void SetStatus(string text)
	{
		lock (Gate)
		{
			StatusText = text;
			StateChanged?.Invoke();
		}
	}

	public static string Mask(string salt)
	{
		if (string.IsNullOrEmpty(salt) || salt.Length < 8)
		{
			return "(空)";
		}
		return salt.Substring(0, 4) + "***" + salt.Substring(salt.Length - 4);
	}

	/// <summary>UI 状态行（各源一行）。</summary>
	public static string BuildStatusLines()
	{
		List<string> lines = new List<string>();
		foreach (SaltSourceState s in _sources)
		{
			lines.Add("· " + s.StatusLine);
		}
		lines.Add("· 当前：" + StatusText);
		if (HasManualOverride)
		{
			lines.Add("· 手输盐值已启用（自动刷新不会覆盖手输值）");
		}
		return string.Join("\n", lines);
	}
}
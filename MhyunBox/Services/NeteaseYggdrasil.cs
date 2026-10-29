using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto;

namespace MhyunBox.Services;

public static class NeteaseYggdrasil
{
	private static readonly byte[] ChaChaNonce = Encoding.UTF8.GetBytes("163 NetEase\n");

	private static readonly byte[] TokenKey = new byte[16]
	{
		172, 36, 156, 105, 199, 44, 179, 180, 78, 192,
		204, 108, 84, 58, 129, 149
	};

	private static readonly byte[] McVersionSalt = new byte[9] { 1, 0, 4, 128, 210, 62, 247, 17, 1 };

	private static readonly byte[] TcpSalt = new byte[16]
	{
		47, 132, 174, 163, 153, 33, 41, 38, 218, 191,
		149, 163, 171, 175, 55, 224
	};

	private static readonly AsymmetricKeyParameter PublicKey = RsaPkcs1.LoadPublicKey("MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA4HJFrYdVTeoSvH6qsnJElfXuf7FnxxFQdz3gRCs66LDrZfaoGoWt2e/aGIOv8uGHliBWnZP42Ike9Qf5aiYVtQ4mlj2bXZjifHG35LlS1Bq6yCA6k1WevWcrGWOuLzny3jo8Wbdi0lIFMTT2hN98sF2k4YcvyE9zhqxfRNFGVI5kLyxm9CeTKAXGBU5mw3yQWJ8cPRR4866jpGGOhBWlJdilWt2NES9bid8SbhTT55wqumnVO5J5/DaMyTgKIQngH7NyZQljAhdK5I23dzpGop322n2eQ+mTNLuquwU453o1cbyQobgC6vh5/F1QT2INBR2qYCnRzzJ6hrhE5kIMZwIDAQAB");

	private static readonly AsymmetricKeyParameter PrivateKey = RsaPkcs1.LoadPrivateKey("MIIEvAIBADANBgkqhkiG9w0BAQEFAASCBKYwggSiAgEAAoIBAQDobJGIddxcs08xzTIVFanc4/84J1DbxiW7wLokIrap3txzyyMXj+AcDa8jLopLJkMg9rZLzL50Dwp+hmTTgiIcjkM1DVREsRBltzqjVNyiYPL2VGHLn+/eEivjhLNWUMcWrlAoJ5JJBi22oGPLlKDVJpg33JPI5nPZpwufdB0ecn2V0CAeeGyswQyAaqIjoiYOoP3HipjEYGQHp1RsADf4ozGRgv+2HGiSOyhlv00ixnF0nRUTzVh18ka0N3LdLMpAN/YAPkO8tmoWp0asyU3X+nyZFd29povvrRy4rL1lYmo8jdpfpSL+Yk+9+RybjBAhXRx6uDoBxaa2kwE4fyrrAgMBAAECggEAWT42szruHfoLkofDjyz+R/6TZLBT788pdeoOjwl1McyMwTlihA2Oc7cdZFjeaPSMGgAhBwHarx2HXgWkeUIibuyBCcHQdX+3WBb+wPA4t3CaWdMUqecDZzV6/KVbZu0lRKQxyvlGxhtFOjZjmyu6hZ2IHQrpA97Y5N2rLNKcy69W+QYJZappmBfbVgWM0NRmgmpg6siQ0Cm6Ryil3SBAPBVv+EUPiD9jdXbtVq7VwN4YmwUGScp2Fib1oUnqEAja1hfihVnRFQ246nKXhIc/YVrNmwBrxAVwaaPFRka6XjKkSF0WVbpqQLXhbY1fS8FoXGpVhiF6o4rTQJbpQxhQgQKBgQD7XysfPNt9G63gTrkZvjvEk5LKsRG42MAYEkuzxEal9E/AQv0jJrk0f1WO09hCcYQeaOQXhM9mezNQv5jXEnmepXqM3NTw1Di5yh3uvjSXQjdUt+7haNw+QjggBqZxyQZjtadYairSzfmWe7OwJIkCmdgaJKxm4qMExk9kUgApGwKBgQDstBdJHU/KEBqVpsIlu185vFFuaAvxiHjXHqGwytJMQ/5aVqaphIiQCAxaEogPSzPSm28UHVQiZeFO059EKOpSJscW4pV95Dr5BAbHuYecacqnZKbQqb69//Cfpne9tGYXlmP6QnYfPoc4wYfTfPyU2x3KtDhVxtEDutpSNU1ycQKBgBabWn92s66uvJZ9vfvotetZ8ku0XQmoxK3lh1Vlg40NSdbar3Vn2CQ2h3VO7BYdq2oouMq8sQJgdh7+/DnreXChJUJh4ey+yVM8MDD2fjhURjGiUSOIkLYwsmd+8Z0uHRr+jUxQUAWhbJ7yBRkEUCYhu+OuBKtEGrElPKKjFUydAoGAQLj9pQBe0OGWY1U1wRt67k6P9aB9o42tfSTjEXRkDHaLFiibab7TmI6a0gY/Le9iPDREKzvZxY4WDXfQFNMbP1tbFObf+Yxuk6iGMhaI/jvvLdZXxrajcVCKex0JoNWzFMAKlmOV6PUwBFTmzu1eI1XGz6Z3wPycKmjtSY1JoAECgYBBOfaUDMaG1xLzv+q1jPPs2U4lXPK2BXFE5RaliUGC+LIQREXPishII2LYFW3gtXj5QWfIGq6x0d6ca6Bja2vYRDDe5tlT/2VbZahiHpb2PYL/2WgeoHl7sT9Bb/nsKyo85Sv+doop6huy4+aeTiQHgrGR9JYMVBSIx6P8Tt5phA==");

	private static string[] GetConfiguredSaltEndpoints()
	{
		var cfg = MhyunBox.Settings.SettingsManager.Instance;
		// 优先级：AlaneSaltUrl（新，首页输入框） > CrcSaltUrl（旧版兼容） > 内置默认
		var raw = (cfg?.AlaneSaltUrl ?? string.Empty);
		if (string.IsNullOrWhiteSpace(raw))
		{
			raw = cfg?.CrcSaltUrl ?? string.Empty;
		}
		var list = new List<string>();
		if (!string.IsNullOrWhiteSpace(raw))
		{
			foreach (var s in raw.Replace("\r\n", "\n").Replace(',', '\n').Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
			{
				var t = s.Trim();
				if (!string.IsNullOrWhiteSpace(t) && !list.Contains(t))
					list.Add(t);
			}
		}
		// 盐端点只保留一个：http://191.40.41.220/api/crc_salt.txt（Alane2 同源权威盐，nginx 静态文本）。
		// 其余历史端点（404.xthdx.wiki / 39.105.154.212 / alane.club）或已 426 或返回滞后旧盐，
		// 多端点并发采纳会把旧盐当新盐持久化，导致「盐值对不上」(status=0x11)。
		// 注意：Alane 为盐值源链首位，其他源（404 / 官方 DLL）不得覆盖此默认地址。
		if (!list.Contains("http://191.40.41.220/api/crc_salt.txt")) list.Add("http://191.40.41.220/api/crc_salt.txt");
		return list.ToArray();
	}

	/// <summary>供 CrcSaltSourceManager 调用的公共盐值拉取入口（Alane 源）。</summary>
	public static Task<(string? Salt, string? GameVersion)> FetchRemoteSaltPublicAsync(CancellationToken ct)
	{
		return FetchRemoteSaltAsync(ct);
	}
	private static readonly Random Rng = new Random();

	private static readonly object CredentialRefreshLock = new object();

	private static DateTime _lastVersionCheckUtc = DateTime.MinValue;

	private static DateTime _lastSaltRefreshUtc = DateTime.MinValue;

	private static readonly TimeSpan VersionCheckInterval = TimeSpan.FromMinutes(30.0);

	private static readonly TimeSpan SaltRefreshInterval = TimeSpan.FromHours(6.0);

	public static string? LauncherVersion { get; set; } = "1.15.29.18667";

	// patchlist 最新版（仅作 init 失败时的候选，不直接用于签名：服务器校验 (盐↔版本) 配对）
	public static string? PatchlistMaxVersion { get; set; }

	public static string Channel { get; set; } = "netease";

	public static string CrcSalt { get; set; } = "694063DD76E4A3281CE17DA66100C014";

	public static Dictionary<string, string> LastHttpResponse { get; } = new Dictionary<string, string>();

	public static byte[][] HttpKeys { get; } = (from k in "MK6mipwmOUedplb6,OtEylfId6dyhrfdn,VNbhn5mvUaQaeOo9,bIEoQGQYjKd02U0J,fuaJrPwaH2cfXXLP,LEkdyiroouKQ4XN1,jM1h27H4UROu427W,DhReQada7gZybTDk,ZGXfpSTYUvcdKqdY,AZwKf7MWZrJpGR5W,amuvbcHw38TcSyPU,SI4QotspbjhyFdT0,VP4dhjKnDGlSJtbB,UXDZx4KhZywQ2tcn,NIK73ZNvNqzva4kd,WeiW7qU766Q1YQZI".Split(',')
		select Encoding.GetEncoding("us-ascii").GetBytes(k)).ToArray();

	public static async Task RefreshRuntimeCredentialsAsync(CancellationToken ct = default(CancellationToken))
	{
		_ = 1;
		try
		{
			bool flag = false;
			bool needSaltRefresh = false;
			lock (CredentialRefreshLock)
			{
				DateTime utcNow = DateTime.UtcNow;
				if (utcNow - _lastVersionCheckUtc >= VersionCheckInterval)
				{
					flag = true;
				}
				if (utcNow - _lastSaltRefreshUtc >= SaltRefreshInterval)
				{
					needSaltRefresh = true;
				}
			}
			if (!flag && !needSaltRefresh)
			{
				return;
			}
			string patchVersion = null;
			if (flag)
			{
				try
				{
					using HttpClient http = new HttpClient
					{
						Timeout = TimeSpan.FromSeconds(10.0)
					};
					http.DefaultRequestHeaders.UserAgent.ParseAdd("WPFLauncher/0.0.0.0");
					foreach (Match item in Regex.Matches(await http.GetStringAsync("https://x19.update.netease.com/pl/x19_java_patchlist", ct), "\"(\\d+\\.\\d+\\.\\d+\\.\\d+)\""))
					{
						string value = item.Groups[1].Value;
						if (patchVersion == null || CompareVersion(value, patchVersion) > 0)
						{
							patchVersion = value;
						}
					}
				}
				catch (Exception ex)
				{
					LogHelper.Log("[认证-ygg] patchlist 获取失败(使用内置版): " + ex.GetType().Name + ": " + ex.Message);
				}
				lock (CredentialRefreshLock)
				{
					_lastVersionCheckUtc = DateTime.UtcNow;
				}
			}
			bool versionChanged = patchVersion != null && patchVersion != LauncherVersion;
		if (!(versionChanged | needSaltRefresh))
		{
			return;
		}
		// 用户手输盐值优先：定时刷新不覆盖手输值（首页盐值填入框语义）
		bool manualOverride = false;
		try
		{
			manualOverride = CrcSaltSourceManager.HasManualOverride;
		}
		catch
		{
		}
		if (manualOverride)
		{
			LogHelper.Log("[认证-ygg] 检测到手输盐值，定时刷新跳过远端拉取（不覆盖手输值）");
		}
		var (apiSalt, apiVersion) = manualOverride ? ((string?)null, (string?)null) : await FetchRemoteSaltAsync(ct);
			string text2 = patchVersion;
			if (versionChanged && apiVersion != null && CompareVersion(apiVersion, text2 ?? "") > 0)
			{
				text2 = apiVersion;
			}
			if (!versionChanged && apiVersion != null)
			{
				string b = text2 ?? LauncherVersion;
				if (CompareVersion(apiVersion, b) > 0)
				{
					text2 = apiVersion;
				}
			}
			// 服务器校验 (盐↔启动器版本) 配对：优先用上次验证成功的版本，其次 patchlist 最新版
			if (text2 != null)
			{
				PatchlistMaxVersion = text2;
			}
			string? lastGood = null;
			try
			{
				lastGood = MhyunBox.Settings.SettingsManager.Instance?.LastGoodLauncherVersion;
			}
			catch
			{
			}
			string text3 = (!string.IsNullOrWhiteSpace(lastGood)) ? lastGood! : ((PatchlistMaxVersion ?? LauncherVersion) ?? "1.15.29.18667");
			if (text3 != LauncherVersion)
			{
				LogHelper.Log("[认证-ygg] LauncherVersion 更新: " + LauncherVersion + " -> " + text3 + ((lastGood != null) ? "（上次成功版本）" : "（patchlist 最新）"));
				LauncherVersion = text3;
			}
			if (apiSalt != null && apiSalt != CrcSalt)
			{
				LogHelper.Log("[认证-ygg] CrcSalt 动态刷新: " + MaskSalt(CrcSalt) + " -> " + MaskSalt(apiSalt));
				CrcSalt = apiSalt;
				PersistSalt(apiSalt);
			}
			lock (CredentialRefreshLock)
			{
				_lastSaltRefreshUtc = DateTime.UtcNow;
			}
		}
		catch (Exception ex3)
		{
			LogHelper.Log("[认证-ygg] 凭据动态刷新整体失败: " + ex3.GetType().Name + ": " + ex3.Message);
		}
	}

	// init 失败自愈：依次用候选版本重建 init 报文重试；成功即把该版本固定为 LauncherVersion（调用方负责持久化）
	private static async Task<byte> TryInitWithVersionLadderAsync(NetworkStream stream, YggdrasilGameProfile profile, byte[] loginSeed, byte[] signContent, byte lastStatus, CancellationToken ct)
	{
		List<string> list = new List<string>();
		void Add(string? v)
		{
			if (!string.IsNullOrWhiteSpace(v) && !list.Contains(v) && v != LauncherVersion)
			{
				list.Add(v);
			}
		}
		string? lastGood = null;
		try
		{
			lastGood = MhyunBox.Settings.SettingsManager.Instance?.LastGoodLauncherVersion;
		}
		catch
		{
		}
		Add(lastGood);
		Add(PatchlistMaxVersion);
		Add("1.15.29.18667");
		Add("1.15.28.11160");
		foreach (string candidate in list)
		{
			try
			{
				string prev = LauncherVersion ?? "";
				LauncherVersion = candidate;
				byte[] init = BuildInitializeMessage(profile, loginSeed, signContent);
				await stream.WriteAsync(init, ct).ConfigureAwait(continueOnCapturedContext: false);
				await stream.FlushAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				byte[] resp = await ReadFrameAsync(stream, ct).ConfigureAwait(continueOnCapturedContext: false);
				byte status = ((resp.Length != 0) ? resp[0] : byte.MaxValue);
				LogHelper.DebugError(new Exception($"init status=0x{status:X2}"), $"[认证-ygg] init 版本自愈尝试: {prev} -> {candidate} => status=0x{status:X2}");
				if (status == 0)
				{
					LogHelper.Log("[认证-ygg] init 版本自愈成功，采用: " + candidate);
					return 0;
				}
			}
			catch (Exception ex)
			{
				LogHelper.DebugError(ex, "[认证-ygg] init 版本自愈异常: candidate=" + candidate);
				return lastStatus;
			}
		}
		return lastStatus;
	}

	// status=0x11（盐值对不上/版本号不对）时按优先级链切换盐值源重建 init 重试；成功即固定该源
	private static async Task<byte> RotateSaltSourcesForInitAsync(NetworkStream stream, YggdrasilGameProfile profile, byte[] loginSeed, byte[] signContent, byte lastStatus, CancellationToken ct)
	{
		try
		{
			CrcSaltSourceManager.ReportVerifyFailure($"init status=0x11 LauncherVersion={LauncherVersion} salt={CrcSaltSourceManager.Mask(CrcSalt ?? "")}");
		}
		catch (Exception ex)
		{
			LogHelper.Log("[认证-ygg] 盐值源失效标记失败: " + ex.Message);
		}
		for (int attempt = 0; attempt < 3; attempt++)
		{
			string? salt = null;
			string? version = null;
			string source = "";
			try
			{
				var (s, v, src) = await CrcSaltSourceManager.AcquireAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				salt = s;
				version = v;
				source = src;
			}
			catch (Exception ex)
			{
				LogHelper.Log("[认证-ygg] 盐值源链索取异常: " + ex.Message);
			}
			if (string.IsNullOrEmpty(salt))
			{
				LogHelper.Log("[认证-ygg] 盐值源链已耗尽（" + source + "），保持当前盐值 " + MaskSalt(CrcSalt) + " 不再重试");
				break;
			}
			LogHelper.Log($"[认证-ygg] 切换盐值源 [{source}] -> {MaskSalt(salt!)}（版本 {version ?? LauncherVersion ?? ""}）");
			CrcSalt = salt!;
			if (!string.IsNullOrEmpty(version))
			{
				LauncherVersion = version;
			}
			PersistSalt(salt!);
			byte status = await SendInitReadStatusAsync(stream, profile, loginSeed, signContent, ct).ConfigureAwait(continueOnCapturedContext: false);
			LogHelper.Log($"[认证-ygg] 换源后 init 重试: status=0x{status:X2}");
			if (status == 0)
			{
				PersistLastGoodVersion(LauncherVersion ?? "");
				return 0;
			}
			if (status != 0x11)
			{
				return status;
			}
			try
			{
				CrcSaltSourceManager.ReportVerifyFailure($"init status=0x11（源 {source} 也不对）");
			}
			catch
			{
			}
		}
		return lastStatus;
	}

	// 发送 init 报文并读回状态字节（0=通过）
	private static async Task<byte> SendInitReadStatusAsync(NetworkStream stream, YggdrasilGameProfile profile, byte[] loginSeed, byte[] signContent, CancellationToken ct)
	{
		byte[] init = BuildInitializeMessage(profile, loginSeed, signContent);
		await stream.WriteAsync(init, ct).ConfigureAwait(continueOnCapturedContext: false);
		await stream.FlushAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		byte[] resp = await ReadFrameAsync(stream, ct).ConfigureAwait(continueOnCapturedContext: false);
		return (resp.Length != 0) ? resp[0] : byte.MaxValue;
	}

	// 最早第一次成功连接会话的盐值：写入后不再覆盖（首页盐值填入框默认值）
	private static void PersistFirstGoodSalt(string salt)
	{
		if (string.IsNullOrEmpty(salt) || !IsHex32(salt))
		{
			return;
		}
		try
		{
			MhyunBox.Settings.AppSettings? cfg = MhyunBox.Settings.SettingsManager.Instance;
			if (cfg != null && string.IsNullOrEmpty(cfg.LastGoodCrcSalt))
			{
				cfg.LastGoodCrcSalt = salt;
				MhyunBox.Settings.SettingsManager.Save(cfg);
				LogHelper.Log("[认证-ygg] 已记录首次成功会话盐值: " + MaskSalt(salt));
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[认证-ygg] 首次成功盐值记录失败: " + ex.Message);
		}
	}

	// 成功通过 init 后把该版本持久化，之后签名固定用它（配合 (盐↔版本) 服务器配对校验）
	private static void PersistLastGoodVersion(string version)
	{
		if (string.IsNullOrWhiteSpace(version))
		{
			return;
		}
		try
		{
			MhyunBox.Settings.AppSettings? cfg = MhyunBox.Settings.SettingsManager.Instance;
			if (cfg != null && cfg.LastGoodLauncherVersion != version)
			{
				cfg.LastGoodLauncherVersion = version;
				MhyunBox.Settings.SettingsManager.Save(cfg);
				LogHelper.Log("[认证-ygg] 已记住可用启动器版本: " + version);
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[认证-ygg] 可用版本持久化失败: " + ex.Message);
		}
	}

	// init 失败自愈用：强制重拉 patchlist 刷新候选版本（恒写失败原因）
	private static async Task RefreshLauncherVersionAsync(CancellationToken ct)
	{
		try
		{
			using HttpClient http = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(10.0)
			};
			http.DefaultRequestHeaders.UserAgent.ParseAdd("WPFLauncher/0.0.0.0");
			string? best = null;
			foreach (Match item in Regex.Matches(await http.GetStringAsync("https://x19.update.netease.com/pl/x19_java_patchlist", ct), "\"(\\d+\\.\\d+\\.\\d+\\.\\d+)\""))
			{
				string value = item.Groups[1].Value;
				if (best == null || CompareVersion(value, best) > 0)
				{
					best = value;
				}
			}
			if (best != null && best != PatchlistMaxVersion)
			{
				LogHelper.Log("[认证-ygg] patchlist 最新版本记录: " + PatchlistMaxVersion + " -> " + best + "（仅作 init 自愈候选，不直接签名）");
				PatchlistMaxVersion = best;
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[认证-ygg] patchlist 自愈刷新失败(保留版本 " + LauncherVersion + ")");
		}
	}

	private static bool IsHex32(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length != 32)
		{
			return false;
		}
		foreach (char c in value)
		{
			if ((c < '0' || c > '9') && (c < 'a' || c > 'f') && (c < 'A' || c > 'F'))
			{
				return false;
			}
		}
		return true;
	}

	// 统一的远程盐/版本拉取：端点列表 → JSON/纯文本解析 → Alane 回退（供定时刷新与强制刷新共用）
	private static async Task<(string? Salt, string? GameVersion)> FetchRemoteSaltAsync(CancellationToken ct)
	{
		string? apiVersion = null;
		string? apiSalt = null;
		try
		{
			using HttpClient http = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(12.0)
			};
			string? saltRaw = null;
			foreach (string endpoint in GetConfiguredSaltEndpoints())
			{
				try
				{
					saltRaw = await http.GetStringAsync(endpoint, ct);
					if (!string.IsNullOrWhiteSpace(saltRaw))
					{
						break;
					}
				}
				catch (Exception ex2)
				{
					LogHelper.Log("[认证-ygg] crcsalt " + endpoint + " 获取失败: " + ex2.GetType().Name + ": " + ex2.Message);
				}
			}
			if (!string.IsNullOrWhiteSpace(saltRaw))
			{
				string trimmed = saltRaw.Trim();
				string? candidate = null;
				if (trimmed.StartsWith("{"))
				{
					try
					{
						using JsonDocument jsonDocument = JsonDocument.Parse(trimmed);
						JsonElement rootElement = jsonDocument.RootElement;
						if (rootElement.TryGetProperty("salt", out var value2))
						{
							candidate = value2.GetString();
						}
						if (rootElement.TryGetProperty("crcSalt", out var value2b))
						{
							string alt = value2b.GetString();
							if (IsHex32(alt))
							{
								candidate = alt;
							}
						}
						if (rootElement.TryGetProperty("gameVersion", out var value3))
						{
							apiVersion = value3.GetString();
						}
					}
					catch (Exception exJson)
					{
						LogHelper.Log("[认证-ygg] crcsalt(404) JSON 解析失败: " + exJson.GetType().Name);
					}
				}
				else
				{
					candidate = trimmed;
				}
				if (IsHex32(candidate))
				{
					apiSalt = candidate;
				}
				else if (!string.IsNullOrWhiteSpace(candidate))
				{
					LogHelper.Log("[认证-ygg] crcsalt(404) 返回值不是 32 位十六进制，已忽略");
				}
			}
		}
		catch (Exception ex2)
		{
			LogHelper.Log("[认证-ygg] crcsalt(404) API 获取失败(使用内置盐): " + ex2.GetType().Name + ": " + ex2.Message);
		}
		// 注意：alane.club 的 api_get_crcsalt.php 返回的是滞后旧盐（启动器 1.15.29 时代已失效），
		// 191（Alane2 同源）瞬时失败时绝不能拿它兜底，否则会把旧盐当新盐采纳并持久化，
		// 之后一直复用旧盐导致「盐值对不上」。所有端点都失败时保持当前盐，交给验证失败自愈重试。
		return (apiSalt, apiVersion);
	}

	private static DateTime _lastForceSaltRefreshUtc = DateTime.MinValue;

	private static readonly TimeSpan ForceSaltRefreshCooldown = TimeSpan.FromSeconds(60.0);

	// 同验证错误时强制刷新盐（绕过 6h 节流；60s 冷却防风暴）。返回是否实际执行。
	public static async Task<bool> ForceRefreshSaltAsync(CancellationToken ct = default(CancellationToken))
	{
		lock (CredentialRefreshLock)
		{
			if (DateTime.UtcNow - _lastForceSaltRefreshUtc < ForceSaltRefreshCooldown)
			{
				LogHelper.Log("[认证-ygg] 强制刷盐处于冷却期(60s)，跳过");
				return false;
			}
			_lastForceSaltRefreshUtc = DateTime.UtcNow;
		}
		try
		{
			var (apiSalt, _) = await FetchRemoteSaltAsync(ct);
			if (IsHex32(apiSalt) && apiSalt != CrcSalt)
			{
				LogHelper.Log("[认证-ygg] CrcSalt 强制刷新: " + MaskSalt(CrcSalt) + " -> " + MaskSalt(apiSalt!));
				CrcSalt = apiSalt!;
				PersistSalt(apiSalt!);
			}
			lock (CredentialRefreshLock)
			{
				_lastSaltRefreshUtc = DateTime.UtcNow;
			}
			return true;
		}
		catch (Exception ex)
		{
			LogHelper.Log("[认证-ygg] 强制刷盐失败: " + ex.GetType().Name + ": " + ex.Message);
			return false;
		}
	}

	// 同验证错误特征：JoinServer 握手/校验被拒（init 非 0、join 校验失败）。连接类错误不触发刷盐。
	private static bool IsSaltVerifyError(string? message)
	{
		if (string.IsNullOrEmpty(message))
		{
			return false;
		}
		if (message.StartsWith("join type=", StringComparison.Ordinal) || message.StartsWith("init status=", StringComparison.Ordinal))
		{
			return true;
		}
		return message.Contains("验证失败", StringComparison.Ordinal) || message.Contains("InvalidToken", StringComparison.OrdinalIgnoreCase);
	}

	private static string MaskSalt(string salt)
	{
		if (string.IsNullOrEmpty(salt))
		{
			return "(空)";
		}
		if (salt.Length <= 8)
		{
			return "***(" + salt.Length + "位)";
		}
		return salt.Substring(0, 4) + "***" + salt.Substring(salt.Length - 4) + "(" + salt.Length + "位)";
	}

	// 启动后首次使用时，把上次持久化的盐恢复进静态字段（避免重启后用回内置旧盐）
	private static bool _saltFromSettingsLoaded;

	private static void EnsureSaltFromSettings()
	{
		if (_saltFromSettingsLoaded)
		{
			return;
		}
		_saltFromSettingsLoaded = true;
		try
		{
			string? saved = MhyunBox.Settings.SettingsManager.Instance?.CrcSalt;
			if (IsHex32(saved) && saved != CrcSalt)
			{
				LogHelper.Log("[认证-ygg] 从设置恢复持久化 CrcSalt: " + MaskSalt(saved!));
				CrcSalt = saved!;
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[认证-ygg] 恢复持久化 CrcSalt 失败: " + ex.Message);
		}
	}

	// 盐成功刷新后写回设置文件持久化（跨启动生效）
	private static void PersistSalt(string salt)
	{
		if (!IsHex32(salt))
		{
			return;
		}
		try
		{
			MhyunBox.Settings.AppSettings? cfg = MhyunBox.Settings.SettingsManager.Instance;
			if (cfg != null && cfg.CrcSalt != salt)
			{
				cfg.CrcSalt = salt;
				MhyunBox.Settings.SettingsManager.Save(cfg);
				LogHelper.Log("[认证-ygg] CrcSalt 已持久化: " + MaskSalt(salt));
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[认证-ygg] CrcSalt 持久化失败: " + ex.Message);
		}
	}

	private static int CompareVersion(string a, string b)
	{
		int[] array = Array.ConvertAll(a.Split('.'), int.Parse);
		int[] array2 = Array.ConvertAll(b.Split('.'), int.Parse);
		int num = Math.Max(array.Length, array2.Length);
		for (int i = 0; i < num; i++)
		{
			int num2 = ((i < array.Length) ? array[i] : 0);
			int value = ((i < array2.Length) ? array2[i] : 0);
			int num3 = num2.CompareTo(value);
			if (num3 != 0)
			{
				return num3;
			}
		}
		return 0;
	}

	// 公共入口：同验证错误时自动强制刷盐并重试一次（重试仍失败按原结果返回）
	public static async Task<YggdrasilResult> JoinServerAsync(YggdrasilGameProfile profile, string serverId, string? address = null, int? port = null, CancellationToken ct = default(CancellationToken))
	{
		EnsureSaltFromSettings();
		YggdrasilResult result = await JoinServerCoreAsync(profile, serverId, address, port, ct);
		if (!result.Success && IsSaltVerifyError(result.Message))
		{
			LogHelper.Log("[认证-ygg] 检测到同验证错误，自动强制刷盐并重试一次: " + result.Message);
			if (await ForceRefreshSaltAsync(ct))
			{
				result = await JoinServerCoreAsync(profile, serverId, address, port, ct);
			}
		}
		return result;
	}

	private static async Task<YggdrasilResult> JoinServerCoreAsync(YggdrasilGameProfile profile, string serverId, string? address = null, int? port = null, CancellationToken ct = default(CancellationToken))
	{
		_ = 9;
		try
		{
			await RefreshRuntimeCredentialsAsync(ct);
			var (host, port2) = await ResolveAuthServerAsync(address, port);
			using TcpClient client = new TcpClient();
			using (ct.Register(client.Dispose))
			{
			}
			try
			{
				await client.ConnectAsync(host, port2, ct);
			}
			catch (Exception ex) when (!ct.IsCancellationRequested)
			{
				// 认证服务器单台故障时换一台重试一次（借鉴 Alane AwCrcJoin 逐台尝试机制）
				var (host2, port3) = await ResolveAuthServerAsync(address, port);
				if (string.Equals(host2, host, StringComparison.OrdinalIgnoreCase) && port3 == port2)
				{
					throw;
				}
				LogHelper.Log($"[认证-ygg] 认证服务器 {host}:{port2} 连接失败({ex.GetType().Name})，换 {host2}:{port3} 重试");
				host = host2;
				port2 = port3;
				await client.ConnectAsync(host, port2, ct);
			}
			if (!client.Connected)
			{
				return new YggdrasilResult
				{
					Success = false,
					Message = "connect timeout"
				};
			}
			NetworkStream stream = client.GetStream();
			byte[] array = await ReadFrameAsync(stream, ct);
			if (array.Length < 272)
			{
				return new YggdrasilResult
				{
					Success = false,
					Message = $"invalid challenge len={array.Length}"
				};
			}
			byte[] loginSeed = array.AsSpan(0, 16).ToArray();
			byte[] signContent = array.AsSpan(16, 256).ToArray();
			byte[] array2 = BuildInitializeMessage(profile, loginSeed, signContent);
			await stream.WriteAsync(array2, ct);
			await stream.FlushAsync(ct);
			byte[] array3 = await ReadFrameAsync(stream, ct);
			byte b = ((array3.Length != 0) ? array3[0] : byte.MaxValue);
			if (b != 0)
			{
				// 盐/版本自愈属常规链路，诊断日志走调试门控
				LogHelper.DebugError(new Exception($"init status=0x{b:X2}"), $"[认证-ygg] init 失败诊断: LauncherVersion={LauncherVersion} CrcSalt={MaskSalt(CrcSalt)}(len={(CrcSalt ?? "").Length}) GameVersion={profile.GameVersion} BootstrapMd5={profile.BootstrapMd5} DatFileMd5={profile.DatFileMd5}");
				// 自愈：刷新盐 + 按候选版本梯子重建 init（服务器校验 (盐↔版本) 配对）
				await ForceRefreshSaltAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				await RefreshLauncherVersionAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				b = await TryInitWithVersionLadderAsync(stream, profile, loginSeed, signContent, b, ct).ConfigureAwait(continueOnCapturedContext: false);
				// status=0x11：盐值对不上 / 版本号不对的游戏会话错误 → 按优先级链切换盐值源重试（Alane → 404 → 官方 DLL）
				if (b == 0x11)
				{
					b = await RotateSaltSourcesForInitAsync(stream, profile, loginSeed, signContent, b, ct).ConfigureAwait(continueOnCapturedContext: false);
				}
				if (b != 0)
				{
					return new YggdrasilResult
					{
						Success = false,
						Message = $"init status=0x{b:X2}"
					};
				}
			}
			PersistLastGoodVersion(LauncherVersion ?? "");
			// 记录最早第一次成功会话的盐值（首页填入框默认值，只写一次）
			PersistFirstGoodSalt(CrcSalt);
			byte[] authToken = GetAuthToken(profile.UserToken);
			ChaChaPacker chaChaPacker = new ChaChaPacker(authToken.Concat(loginSeed).ToArray(), ChaChaNonce, encrypt: true);
			ChaChaPacker unpacker = new ChaChaPacker(loginSeed.Concat(authToken).ToArray(), ChaChaNonce, encrypt: false);
			byte[] data = BuildJoinMessage(profile, serverId, loginSeed);
			byte[] array4 = chaChaPacker.PackMessage(9, data);
			await stream.WriteAsync(array4, ct);
			await stream.FlushAsync(ct);
			ChaChaPacker chaChaPacker2 = unpacker;
			var (b2, array5) = chaChaPacker2.UnpackMessage(await ReadFrameAsync(stream, ct));
			if (b2 != 9 || array5.Length == 0 || array5[0] != 0)
			{
				// join 失败恒写：init 已过说明盐/版本/MD5 正确，0x10 等拒绝码须靠 join 上下文远程排障。
				// 响应载荷与会话 ID 可能含令牌/会话信息，日志里只保留长度、首字节和指纹。
				string payloadInfo = $"payload长度={array5.Length} payload指纹={SecretRedaction.TokenFingerprint(Convert.ToHexString(array5))}";
				LogHelper.Force($"[认证-join] join 失败: type={b2} first={((array5.Length != 0) ? array5[0].ToString("X2") : "?")} {payloadInfo} GameId={profile.GameId} serverId={SecretRedaction.Fingerprint(serverId)} LauncherVer={LauncherVersion} GameVer={profile.GameVersion} salt长度={(CrcSalt ?? "").Length} ModsJson长度={profile.ModsJson?.Length ?? 0} ModsJson={(profile.ModsJson == "{\"mods\":[]}" ? "空列表兜底" : "正常")} Channel={Channel}");
				return new YggdrasilResult
				{
					Success = false,
					Message = $"join type={b2} first={((array5.Length != 0) ? array5[0].ToString("X2") : "?")} 响应长度={array5.Length}"
				};
			}
			// join 成功：通知盐值源管理器并记录首次成功盐值
			try
			{
				CrcSaltSourceManager.MarkSuccess(CrcSaltSourceManager.LastSuccessKind, CrcSalt ?? "", "join 成功");
			}
			catch
			{
			}
			return new YggdrasilResult
			{
				Success = true,
				Message = "ok"
			};
		}
		catch (Exception ex)
		{
			return new YggdrasilResult
			{
				Success = false,
				Message = ex.GetType().Name + ": " + ex.Message
			};
		}
	}

	public static async Task<(bool Ok, string Message)> StartGameSessionAsync(string entityId, string entityToken, string gameId)
	{
		_ = 3;
		try
		{
			using HttpClient http = new HttpClient
			{
				BaseAddress = new Uri("https://x19obtcore.nie.netease.com:8443"),
				Timeout = TimeSpan.FromSeconds(15.0)
			};
			http.DefaultRequestHeaders.UserAgent.ParseAdd("WPFLauncher/" + LauncherVersion);
			using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "/interconn/web/game-play-v2/login-start"))
			{
				req.Content = new StringContent("{\"strict_mode\":true}", Encoding.UTF8, "application/json");
				foreach (KeyValuePair<string, string> item in ComputeHttpHeaders("/interconn/web/game-play-v2/login-start", "{\"strict_mode\":true}", entityId, entityToken))
				{
					req.Headers.TryAddWithoutValidation(item.Key, item.Value);
				}
				HttpResponseMessage resp = await http.SendAsync(req);
				string value = await resp.Content.ReadAsStringAsync();
				LastHttpResponse["/interconn/web/game-play-v2/login-start"] = value;
				if (!resp.IsSuccessStatusCode)
				{
					return (Ok: false, Message: $"login-start HTTP {(int)resp.StatusCode}: {ExtractBodyError(value)}");
				}
				// HTTP 200 也可能携带业务错误（code!=0，如 login abnormally）：不解析会误判 GameStart 成功
				string? loginStartErr = ExtractBusinessError(value);
				if (loginStartErr != null)
				{
					LogHelper.Log("[网易会话] login-start 业务失败: " + loginStartErr);
					return (Ok: false, Message: "login-start 失败: " + loginStartErr);
				}
			}
			Dictionary<string, object> dictionary = new Dictionary<string, object>();
			dictionary["game_id"] = gameId;
			dictionary["game_type"] = "2";
			dictionary["strict_mode"] = true;
			dictionary["item_list"] = new string[1] { "10000" };
			string text = JsonSerializer.Serialize(dictionary);
			using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "/interconn/web/game-play-v2/start"))
			{
				req.Content = new StringContent(text, Encoding.UTF8, "application/json");
				foreach (KeyValuePair<string, string> item2 in ComputeHttpHeaders("/interconn/web/game-play-v2/start", text, entityId, entityToken))
				{
					req.Headers.TryAddWithoutValidation(item2.Key, item2.Value);
				}
				HttpResponseMessage resp = await http.SendAsync(req);
				string value2 = await resp.Content.ReadAsStringAsync();
				LastHttpResponse["/interconn/web/game-play-v2/start"] = value2;
				if (!resp.IsSuccessStatusCode)
				{
					return (Ok: false, Message: $"start HTTP {(int)resp.StatusCode}: {ExtractBodyError(value2)}");
				}
				string? startErr = ExtractBusinessError(value2);
				if (startErr != null)
				{
					LogHelper.Log("[网易会话] start 业务失败: " + startErr);
					return (Ok: false, Message: "start 失败: " + startErr);
				}
			}
			return (Ok: true, Message: "game session started");
		}
		catch (Exception ex)
		{
			return (Ok: false, Message: ex.GetType().Name + ": " + ex.Message);
		}
	}

	/// <summary>非 2xx 响应体的精简展示：截断避免日志被整段 JSON 淹没。</summary>
	private static string ExtractBodyError(string? body)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return "<空响应体>";
		}
		string? bizErr = ExtractBusinessError(body);
		if (bizErr != null)
		{
			return bizErr;
		}
		string text = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return (text.Length > 200) ? (text.Substring(0, 200) + "…") : text;
	}

	/// <summary>
	/// 解析网易 HTTP 200 响应体中的业务错误（{"code":!=0,"message"/"msg":...}）。
	/// 无 code 字段或 code==0 视为成功返回 null；body 不是 JSON 时也返回 null（保持旧行为）。
	/// </summary>
	private static string? ExtractBusinessError(string? body)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return null;
		}
		try
		{
			using JsonDocument doc = JsonDocument.Parse(body);
			if (doc.RootElement.ValueKind != JsonValueKind.Object)
			{
				return null;
			}
			JsonElement root = doc.RootElement;
			int code = 0;
			if (root.TryGetProperty("code", out JsonElement codeEl))
			{
				if (codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out int ci))
				{
					code = ci;
				}
				else if (codeEl.ValueKind == JsonValueKind.String && int.TryParse(codeEl.GetString(), out int cs))
				{
					code = cs;
				}
			}
			if (code == 0)
			{
				return null;
			}
			foreach (string name in new[] { "message", "msg", "status", "error" })
			{
				if (root.TryGetProperty(name, out JsonElement msgEl) && msgEl.ValueKind == JsonValueKind.String)
				{
					string? msg = msgEl.GetString();
					if (!string.IsNullOrWhiteSpace(msg))
					{
						return msg + " (code=" + code + ")";
					}
				}
			}
			return "code=" + code;
		}
		catch
		{
			return null;
		}
	}

	public static byte[] HttpEncrypt(byte[] bodyIn)
	{
		return NeteaseCipher.HttpEncrypt(bodyIn, HttpKeys, CipherMode.ECB);
	}

	public static byte[]? HttpDecrypt(byte[] body)
	{
		return NeteaseCipher.HttpDecrypt(body, HttpKeys);
	}

	public static async Task<(bool Ok, string? ModsJson, string Message)> FetchGameModsAsync(string entityId, string entityToken, int mcVersionId = 1012002, int gameType = 2)
	{
		_ = 3;
		try
		{
			using HttpClient http = new HttpClient
			{
				BaseAddress = new Uri("https://x19apigatewayobt.nie.netease.com"),
				Timeout = TimeSpan.FromSeconds(15.0)
			};
			http.DefaultRequestHeaders.UserAgent.ParseAdd("WPFLauncher/" + LauncherVersion);
			string text = JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["mc_version_id"] = mcVersionId,
				["game_type"] = gameType
			});
			LogHelper.Log($"[认证-mods] FetchGameMods mcVersionId={mcVersionId} gameType={gameType}");
			using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "/game-auth-item-list/query/search-by-game");
			req.Content = new StringContent(text, Encoding.UTF8, "application/json");
			foreach (KeyValuePair<string, string> item2 in ComputeHttpHeaders("/game-auth-item-list/query/search-by-game", text, entityId, entityToken))
			{
				req.Headers.TryAddWithoutValidation(item2.Key, item2.Value);
			}
			HttpResponseMessage resp = await http.SendAsync(req);
			string text2 = await resp.Content.ReadAsStringAsync();
			LastHttpResponse["/game-auth-item-list/query/search-by-game"] = text2;
			if (!resp.IsSuccessStatusCode)
			{
				return (Ok: false, ModsJson: null, Message: $"mod-list HTTP {(int)resp.StatusCode}: {text2}");
			}
			using JsonDocument doc = JsonDocument.Parse(text2);
			if (!doc.RootElement.TryGetProperty("entity", out var value) || !value.TryGetProperty("iid_list", out var value2) || value2.GetArrayLength() == 0)
			{
				return (Ok: true, ModsJson: null, Message: "mod-list 无 iid_list: " + text2);
			}
			List<ulong> list = new List<ulong>();
			foreach (JsonElement item3 in value2.EnumerateArray())
			{
				list.Add(item3.GetUInt64());
			}
			string text3 = "{\"item_id_list\":" + JsonSerializer.Serialize(list) + "}";
			using HttpRequestMessage req2 = new HttpRequestMessage(HttpMethod.Post, "/user-item-download-v2/get-list");
			req2.Content = new StringContent(text3, Encoding.UTF8, "application/json");
			foreach (KeyValuePair<string, string> item4 in ComputeHttpHeaders("/user-item-download-v2/get-list", text3, entityId, entityToken))
			{
				req2.Headers.TryAddWithoutValidation(item4.Key, item4.Value);
			}
			HttpResponseMessage resp2 = await http.SendAsync(req2);
			string text4 = await resp2.Content.ReadAsStringAsync();
			LastHttpResponse["/user-item-download-v2/get-list"] = text4;
			if (!resp2.IsSuccessStatusCode)
			{
				return (Ok: false, ModsJson: null, Message: $"mod-detail HTTP {(int)resp2.StatusCode}: {text4}");
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(text4);
			if (!jsonDocument.RootElement.TryGetProperty("entities", out var value3))
			{
				return (Ok: true, ModsJson: null, Message: "mod-detail 无 entities: " + text4);
			}
			List<Dictionary<string, string>> list2 = new List<Dictionary<string, string>>();
			int num = 0;
			foreach (JsonElement item5 in value3.EnumerateArray())
			{
				string value4 = item5.GetProperty("item_id").GetString() ?? "";
				int @int = item5.GetProperty("mtypeid").GetInt32();
				if (!item5.TryGetProperty("sub_entities", out var value5))
				{
					continue;
				}
				foreach (JsonElement item6 in value5.EnumerateArray())
				{
					string text5 = (item6.TryGetProperty("jar_md5", out var value6) ? (value6.GetString() ?? "") : "");
					string value7 = $"{value4}@{@int}@0.jar";
					list2.Add(new Dictionary<string, string>
					{
						["modPath"] = value7,
						["name"] = "",
						["id"] = value7,
						["iid"] = value4,
						["md5"] = text5.ToUpperInvariant(),
						["version"] = ""
					});
					num++;
					value6 = default;
					value6 = default;
				}
				value5 = default;
			}
			string item = "{\"mods\":" + JsonSerializer.Serialize(list2) + "}";
			return (Ok: true, ModsJson: item, Message: $"拉起 {num} 个 mod");
		}
		catch (Exception ex)
		{
			return (Ok: false, ModsJson: null, Message: ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static Dictionary<string, string> ComputeHttpHeaders(string requestPath, string sendBody, string userId, string userToken)
	{
		requestPath = (requestPath.StartsWith('/') ? requestPath : ("/" + requestPath));
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.Write(Encoding.UTF8.GetBytes(NetShared.Md5Hex(userToken)));
		memoryStream.Write(Encoding.UTF8.GetBytes(sendBody));
		memoryStream.Write(Encoding.UTF8.GetBytes("0eGsBkhl"));
		memoryStream.Write(Encoding.UTF8.GetBytes(requestPath));
		string text = NetShared.Md5Hex(memoryStream.ToArray());
		string text2 = NetShared.HexToBinary(text);
		text2 = text2.Substring(6) + text2.Substring(0, 6);
		byte[] bytes = Encoding.UTF8.GetBytes(text);
		for (int i = 0; i < text2.Length / 8; i++)
		{
			byte b = 0;
			for (int j = 0; j < 8; j++)
			{
				if (text2[i * 8 + (7 - j)] == '1')
				{
					b |= (byte)(1 << j);
				}
			}
			bytes[i] ^= b;
		}
		string value = (Convert.ToBase64String(bytes, 0, 12) + "1").Replace('+', 'm').Replace('/', 'o');
		return new Dictionary<string, string>
		{
			["user-id"] = userId,
			["user-token"] = value
		};
	}

	private static async Task<(string, int)> ResolveAuthServerAsync(string? address, int? port)
	{
		if (!string.IsNullOrWhiteSpace(address) && port.HasValue && port.GetValueOrDefault() > 0)
		{
			return (address, port.Value);
		}
		using HttpClient http = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(10.0)
		};
		JsonElement[] array = JsonSerializer.Deserialize<JsonElement[]>(await http.GetStringAsync("https://x19.update.netease.com/authserver.list")) ?? Array.Empty<JsonElement>();
		if (array.Length == 0)
		{
			throw new InvalidOperationException("empty authserver.list");
		}
		JsonElement jsonElement = array[Rng.Next(array.Length)];
		string? item = jsonElement.GetProperty("IP").GetString() ?? "45.253.165.190";
		int @int = jsonElement.GetProperty("Port").GetInt32();
		return (item, @int);
	}

	private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
	{
		byte[] prefix = new byte[2];
		await ReadExactlyAsync(stream, prefix, ct);
		short num = BitConverter.ToInt16(prefix, 0);
		if (num < 0)
		{
			throw new InvalidDataException("negative frame length");
		}
		byte[] body = new byte[num];
		await ReadExactlyAsync(stream, body, ct);
		return body;
	}

	private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
	{
		int num;
		for (int offset = 0; offset < buffer.Length; offset += num)
		{
			num = await stream.ReadAsync(buffer.AsMemory(offset), ct);
			if (num == 0)
			{
				throw new EndOfStreamException("connection closed mid-frame");
			}
		}
	}

	public static byte[] GetAuthToken(string userToken)
	{
		return Encoding.ASCII.GetBytes(userToken).Xor(TokenKey);
	}

	public static int GetAuthId(int userId)
	{
		return new Skip32("SaintSteve"u8.ToArray()).Encrypt(userId);
	}

	private static byte[] BuildInitializeMessage(YggdrasilGameProfile profile, byte[] loginSeed, byte[] signContent)
	{
		int authId = GetAuthId(profile.UserId);
		byte[] authToken = GetAuthToken(profile.UserToken);
		byte[] array = AesEcb.Encrypt(loginSeed, authToken);
		byte[] second = BuildSign(profile, authId, array).EncodeSha256();
		byte[] array2 = RsaPkcs1.Rsa(PublicKey, signContent, forEncryption: false);
		if (array2.Length < 51)
		{
			throw new InvalidDataException("sign content too short");
		}
		byte[] first = array2.AsSpan(0, 19).ToArray();
		if (!array2.AsSpan(19, 32).ToArray().SequenceEqual(loginSeed.EncodeSha256()))
		{
			throw new InvalidDataException("checksum validation failed");
		}
		byte[] data = RsaPkcs1.Rsa(PrivateKey, first.Concat(second).ToArray(), forEncryption: true);
		using MemoryStream ms = new MemoryStream();
		ms.WriteInt(authId);
		ms.WriteBytes(array);
		ms.WriteShortString(LauncherVersion ?? "", littleEndian: false);
		ms.WriteByteLengthString(Channel);
		ms.WriteBytes(TcpSalt);
		ms.WriteShortBytes(data);
		ms.WriteByteLengthString(profile.GameVersion);
		ms.WriteBytes(McVersionSalt);
		return ms.ToFrame();
	}

	private static byte[] BuildSign(YggdrasilGameProfile profile, int id, byte[] seed)
	{
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.WriteInt(id);
		memoryStream.WriteBytes(seed);
		memoryStream.WriteBytes(Encoding.UTF8.GetBytes(LauncherVersion ?? ""));
		memoryStream.WriteBytes(Encoding.UTF8.GetBytes(Channel));
		memoryStream.WriteBytes(Encoding.UTF8.GetBytes(CrcSalt));
		memoryStream.WriteBytes(Encoding.UTF8.GetBytes(profile.GameVersion));
		memoryStream.WriteBytes(McVersionSalt);
		return memoryStream.ToArray();
	}

	private static byte[] BuildJoinMessage(YggdrasilGameProfile profile, string serverId, byte[] loginSeed)
	{
		int num = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		byte[] array = BuildHashData(profile, num, loginSeed);
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.WriteLong(long.Parse(profile.GameId));
		memoryStream.WriteString(serverId);
		memoryStream.WriteString(LauncherVersion ?? "");
		memoryStream.WriteString(profile.GameVersion);
		memoryStream.WriteInt(num);
		memoryStream.WriteBytes(array);
		memoryStream.WriteShortString(profile.ModsJson);
		memoryStream.WriteShortString("[]");
		memoryStream.WriteShort(0);
		memoryStream.WriteByteLengthString(Channel);
		LogHelper.Log($"[认证-join] GameId={profile.GameId} serverId={SecretRedaction.Fingerprint(serverId)} LauncherVer={LauncherVersion} GameVer={profile.GameVersion} time={num} hash={SecretRedaction.Fingerprint(Convert.ToHexString(array))} ModsJson前100={profile.ModsJson.Substring(0, Math.Min(100, profile.ModsJson.Length))} BootstrapMd5={profile.BootstrapMd5} DatFileMd5={profile.DatFileMd5} Channel={Channel}");
		return memoryStream.ToArray();
	}

	private static byte[] BuildHashData(YggdrasilGameProfile profile, int time, byte[] loginSeed)
	{
		string text = $"{LauncherVersion}{profile.GameVersion}{time}{CrcSalt}{profile.ModsJson}{profile.BootstrapMd5}{profile.DatFileMd5}[]";
		LogHelper.Log($"[认证-hash] 待签名串长度={text.Length} UserId={profile.UserId} loginSeed长度={loginSeed.Length} CrcSalt长度={(CrcSalt ?? "").Length}");
		return Encoding.UTF8.GetBytes(text).Concat(profile.UserId.ToByteArray()).Concat(loginSeed)
			.ToArray()
			.EncodeSha256();
	}
}

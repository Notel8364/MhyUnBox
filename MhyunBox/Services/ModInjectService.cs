using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using MhyunBox.Settings;

namespace MhyunBox.Services;

/// <summary>
/// 模组注入服务（规则内置代码，不再依赖 mod_features.txt）：
/// - 更多显示/元素之诗模组（MoreShow 系列，仅注入元素之诗客户端）：
///   优先从官网 https://elementsystem.xyz/downloads/ 解析最新版下载并注入，
///   客户端 mods 里的同族旧版（moreshow-*、MoreShow_Extra-*）自动删除；离线回退内置/缓存包。
/// - MhyCPTD（角色端口显示，注入元素之诗与失落世界客户端）：
///   注入 Mods 目录里版本最新的 MhyCPTD-*.jar，同族旧版自动删除；
///   PCL 区「注入 CPTD」关闭时把客户端里的 CPTD 改名 *.jar.disabled（Forge 不加载），
///   重新开启时清理残留并注入最新版。
/// </summary>
public static class ModInjectService
{
	public const string MoreShowDownloadPage = "https://elementsystem.xyz/downloads/";

	private static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(15.0)
	};

	static ModInjectService()
	{
		try
		{
			Http.DefaultRequestHeaders.UserAgent.ParseAdd("MhyUnBox/6.8");
		}
		catch
		{
		}
	}

	public static string ModsDirectory()
	{
		string text = (SettingsManager.Instance.ModSourceDir ?? "").Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			text = Path.Combine(AppContext.BaseDirectory, "Mods");
		}
		return text;
	}

	public static List<ModInjectRule> LoadRules()
	{
		return new List<ModInjectRule>
		{
			// 更多显示：仅注入元素之诗客户端；官网自动更新 + 删旧版
			new ModInjectRule
			{
				Keywords = { "元素芝士", "元素之诗", "1.12.2-Forge" },
				SourceFileName = "moreshow-1.6.4.jar",
				TargetFileName = "moreshow-1.6.4.jar",
				ModId = "moreshow",
				ModVersion = "1.6.4",
				Family = "moreshow",
				Kind = "moreshow",
				DownloadPage = MoreShowDownloadPage
			},
			// MhyCPTD：元素之诗与失落世界客户端均注入；受 PCL 区「注入 CPTD」开关控制
			new ModInjectRule
			{
				Keywords = { "元素芝士", "元素之诗", "失落世界" },
				SourceFileName = "MhyCPTD-1.2.2.jar",
				TargetFileName = "MhyCPTD-1.2.2.jar",
				ModId = "mhcp",
				ModVersion = "1.2.2",
				Family = "mhycptd",
				Kind = "cptd"
			}
		};
	}

	private static string ReadPclVersion(string minecraftDir)
	{
		try
		{
			string path = Path.Combine(minecraftDir, "PCL.ini");
			if (!File.Exists(path))
			{
				return "";
			}
			string[] array = File.ReadAllLines(path);
			string[] array2 = array;
			foreach (string text in array2)
			{
				if (text.StartsWith("Version:"))
				{
					return text.Substring("Version:".Length).Trim();
				}
			}
		}
		catch
		{
		}
		return "";
	}

	public static List<ClientDetection> DetectAll(string? pclExePath)
	{
		List<ClientDetection> list = new List<ClientDetection>();
		if (string.IsNullOrWhiteSpace(pclExePath) || !File.Exists(pclExePath))
		{
			return list;
		}
		string text = Path.Combine(Path.GetDirectoryName(pclExePath) ?? "", ".minecraft");
		if (!Directory.Exists(text))
		{
			return list;
		}
		List<ModInjectRule> rules = LoadRules();
		string text2 = Path.Combine(text, "versions");
		List<string> list2 = new List<string>();
		try
		{
			if (Directory.Exists(text2))
			{
				foreach (DirectoryInfo item in new DirectoryInfo(text2).EnumerateDirectories())
				{
					list2.Add(item.Name);
				}
			}
		}
		catch
		{
		}
		string text3 = ReadPclVersion(text);
		foreach (string candidate in list2)
		{
			// PCL.ini 有当前版本时只注入该版本（与 PCL 启动所选版本一致）
			if (text3.Length > 0 && !candidate.Equals(text3, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			foreach (ModInjectRule rule in rules)
			{
				bool flag = false;
				foreach (string keyword in rule.Keywords)
				{
					if (!string.IsNullOrWhiteSpace(keyword) && candidate.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						flag = true;
						break;
					}
				}
				if (flag)
				{
					list.Add(new ClientDetection
					{
						Matched = true,
						VersionDir = candidate,
						ModsDir = Path.Combine(text2, candidate, "mods"),
						Rule = rule,
						Detail = "识别到客户端（版本: " + candidate + "）"
					});
				}
			}
		}
		return list;
	}

	public static ClientDetection DetectClient(string? pclExePath)
	{
		ClientDetection clientDetection = new ClientDetection();
		if (string.IsNullOrWhiteSpace(pclExePath) || !File.Exists(pclExePath))
		{
			clientDetection.Detail = "未找到 PCL 启动器";
			return clientDetection;
		}
		List<ClientDetection> all = DetectAll(pclExePath);
		ClientDetection clientDetection2 = all.FirstOrDefault((ClientDetection h) => h.Rule != null && h.Rule.Kind == "moreshow") ?? all.FirstOrDefault();
		if (clientDetection2 != null)
		{
			return clientDetection2;
		}
		string text = Path.Combine(Path.GetDirectoryName(pclExePath) ?? "", ".minecraft");
		if (!Directory.Exists(text))
		{
			clientDetection.Detail = "未找到 " + text;
			return clientDetection;
		}
		clientDetection.Detail = "未识别到元素之诗客户端（.minecraft\\versions 下无匹配版本目录）";
		return clientDetection;
	}

	/// <summary>把规则命中的所有客户端全部处理：更多显示官网更新+删旧版；CPTD 注入/按开关禁用。</summary>
	public static (InjectResultKind Kind, string Message) InjectAll(string? pclExePath, bool injectCptd)
	{
		List<ClientDetection> list = DetectAll(pclExePath);
		if (list.Count == 0)
		{
			return (Kind: InjectResultKind.NoMatch, Message: DetectClient(pclExePath).Detail + "，未执行注入");
		}
		List<string> list2 = new List<string>();
		bool flag = false;
		bool flag2 = false;
		foreach (ClientDetection item in list.GroupBy((ClientDetection h) => h.ModsDir + "|" + h.Rule?.Kind).Select((IGrouping<string, ClientDetection> g) => g.First()).ToList())
		{
			try
			{
				if (item.Rule != null && item.Rule.Kind == "cptd" && !injectCptd)
				{
					list2.Add("[" + item.VersionDir + "] " + DisableCptdIn(item.ModsDir));
					continue;
				}
				var (Kind, Message) = InjectRuleInto(item);
				flag |= Kind == InjectResultKind.Failed;
				flag2 |= Kind == InjectResultKind.Injected;
				list2.Add("[" + item.VersionDir + "] " + Message);
			}
			catch (Exception ex)
			{
				flag = true;
				LogHelper.LogError(ex, "[模组注入] 注入异常: " + item.VersionDir);
				list2.Add("[" + item.VersionDir + "] 注入失败: " + ex.Message);
			}
		}
		return (flag ? InjectResultKind.Failed : (flag2 ? InjectResultKind.Injected : InjectResultKind.AlreadyLatest), string.Join("；", list2));
	}

	/// <summary>兼容旧入口：按设置里的 CPTD 开关执行注入。</summary>
	public static (InjectResultKind Kind, string Message) Inject(string? pclExePath)
	{
		bool flag = true;
		try
		{
			flag = SettingsManager.Instance?.PclInjectCptd ?? true;
		}
		catch
		{
		}
		return InjectAll(pclExePath, flag);
	}

	private static (InjectResultKind Kind, string Message) InjectRuleInto(ClientDetection hit)
	{
		ModInjectRule rule = hit.Rule;
		string modsDir = hit.ModsDir;
		Directory.CreateDirectory(modsDir);
		if (rule.Kind == "moreshow")
		{
			return InjectMoreShow(modsDir);
		}
		return InjectCptd(modsDir);
	}

	// ---- 更多显示：主模组(moreshow-*) 与 附属(MoreShow_Extra-*) 分开处理，互不误删 ----

	/// <summary>
	/// 更多显示注入：主模组与附属各自「族内只保留一个文件」。
	/// 源优先取官网最新（官网只有附属时用本地缓存），再与本地内置包按版本取新。
	/// </summary>
	private static (InjectResultKind Kind, string Message) InjectMoreShow(string modsDir)
	{
		List<string> parts = new List<string>();
		bool failed = false;
		bool injected = false;

		// —— 主模组 moreshow-* ——
		try
		{
			string? siteMain = TryFetchFamilyFromSite(IsSiteMainJarName);
			string? localMain = HighestLocalMatch("moreshow-");
			string? mainSrc = PickNewer(siteMain, localMain);
			if (mainSrc == null)
			{
				failed = true;
				parts.Add("主模组源缺失（程序 Mods 目录无 moreshow-*.jar，官网也未取到）");
			}
			else
			{
				var (removed, action, _) = EnsureFamilySingle(modsDir, InMainJarFamily, mainSrc, keepClientNewer: false);
				injected |= action.StartsWith("已注入", StringComparison.Ordinal) || action.StartsWith("已更新", StringComparison.Ordinal);
				parts.Add(action + (removed > 0 ? "，已清理同族文件 " + removed + " 个" : ""));
			}
		}
		catch (Exception ex)
		{
			failed = true;
			LogHelper.LogError(ex, "[模组注入] 更多显示主模组处理失败");
			parts.Add("主模组处理失败: " + ex.Message);
		}

		// —— 附属 MoreShow_Extra-* ——
		try
		{
			string? siteExtra = TryFetchFamilyFromSite(IsSiteExtraJarName);
			string? localExtra = HighestLocalMatch("MoreShow_Extra");
			string? extraSrc = PickNewer(siteExtra, localExtra);
			if (extraSrc == null)
			{
				parts.Add("附属未获取到下载源（官网不可达且无本地缓存），保留现状");
			}
			else
			{
				var (removed, action, _) = EnsureFamilySingle(modsDir, InExtraJarFamily, extraSrc, keepClientNewer: false);
				injected |= action.StartsWith("已注入", StringComparison.Ordinal) || action.StartsWith("已更新", StringComparison.Ordinal);
				string from = (siteExtra != null && string.Equals(Path.GetFileName(extraSrc), Path.GetFileName(siteExtra), StringComparison.OrdinalIgnoreCase)) ? "官网最新" : "本地缓存";
				parts.Add("附属" + action + "（" + from + "）" + (removed > 0 ? "，已清理同族文件 " + removed + " 个" : ""));
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[模组注入] 更多显示附属处理失败");
			parts.Add("附属处理失败: " + ex.Message);
		}

		string text = string.Join("；", parts);
		LogHelper.Log("[模组注入] [更多显示] " + text);
		return (failed ? InjectResultKind.Failed : (injected ? InjectResultKind.Injected : InjectResultKind.AlreadyLatest), text);
	}

	// ---- MhyCPTD：族内只保留最新一个；客户端已存在更新版时不回退覆盖 ----

	private static (InjectResultKind Kind, string Message) InjectCptd(string modsDir)
	{
		string? src = HighestLocalMatch("MhyCPTD-") ?? HighestLocalMatch("CPTD-");
		if (src == null)
		{
			string[] family = SafeFiles(modsDir).Where(f => InCptdJarFamily(Path.GetFileName(f))).ToArray();
			if (family.Length > 0)
			{
				return (InjectResultKind.AlreadyLatest, "未获取到 CPTD 更新源，保留现有模组: " + Path.GetFileName(family[0]));
			}
			return (InjectResultKind.Failed, "CPTD 模组源缺失（程序 Mods 目录无 MhyCPTD-*.jar）");
		}
		try
		{
			var (removed, action, _) = EnsureFamilySingle(modsDir, InCptdJarFamily, src, keepClientNewer: true);
			bool injected = action.StartsWith("已注入", StringComparison.Ordinal) || action.StartsWith("已更新", StringComparison.Ordinal);
			string text = action + (removed > 0 ? "，已清理同族文件 " + removed + " 个" : "");
			LogHelper.Log("[模组注入] [CPTD] " + text);
			return (injected ? InjectResultKind.Injected : InjectResultKind.AlreadyLatest, text);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[模组注入] CPTD 注入失败");
			return (InjectResultKind.Failed, "CPTD 注入失败: " + ex.Message);
		}
	}

	// ---- 族内唯一化：同一 modid 只允许存在一个 jar（含防重复注入） ----

	/// <summary>
	/// 保证 modsDir 里「同族文件」只有一个，且内容与 source 一致。
	/// 关键点：先按文件名忽略大小写判断是否已有同一份，再按长度确认内容；
	/// 不一致就先把整族删干净再复制，复制后还会再兜底清一次同族残留 —— 这样即使用户客户端
	/// 里已有同 modid 的旧包/重命名包/删除失败的包，也不会出现「两份同名模组一起加载」。
	/// </summary>
	/// <returns>(清理数量, 说明文本, 最终文件名)</returns>
	private static (int Removed, string Action, string FinalName) EnsureFamilySingle(
		string modsDir, Func<string, bool> inFamily, string sourcePath, bool keepClientNewer)
	{
		string srcName = Path.GetFileName(sourcePath);
		string srcVer = ExtractVersion(srcName);
		long srcLen = new FileInfo(sourcePath).Length;
		List<string> files = SafeFiles(modsDir)
			.Where(f => inFamily(Path.GetFileName(f)))
			.OrderByDescending(f => VersionTokens(ExtractVersion(Path.GetFileName(f))), VersionKeyComparer.Instance)
			.ToList();
		int removed = 0;

		// 客户端里已有更高版本：保留它（CPTD 专用策略，避免把用户更新的版本降级）
		if (keepClientNewer && files.Count > 0)
		{
			string best = files[0];
			if (CompareVersionKeys(VersionTokens(ExtractVersion(Path.GetFileName(best))), VersionTokens(srcVer)) > 0)
			{
				foreach (string dup in files.Skip(1))
				{
					if (SafeDelete(dup))
					{
						removed++;
					}
				}
				return (removed, "客户端已有更新版 " + Path.GetFileName(best) + "，保留不覆盖", Path.GetFileName(best));
			}
		}

		string? same = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), srcName, StringComparison.OrdinalIgnoreCase));
		bool sameContent = same != null && new FileInfo(same).Length == srcLen;

		if (!sameContent)
		{
			// 先清空同族（含 .disabled、重命名副本、旧版本）
			foreach (string old in files)
			{
				if (SafeDelete(old))
				{
					removed++;
				}
			}
			string dst = Path.Combine(modsDir, srcName);
			File.Copy(sourcePath, dst, overwrite: true);
			// 兜底：删除失败的文件可能还留着，复制后再清一遍同族（排除目标文件本身）
			foreach (string leftover in SafeFiles(modsDir).Where(f =>
				inFamily(Path.GetFileName(f))
				&& !string.Equals(Path.GetFileName(f), srcName, StringComparison.OrdinalIgnoreCase)).ToList())
			{
				if (SafeDelete(leftover))
				{
					removed++;
				}
			}
			return (removed, (files.Count > 0 ? "已更新为 " : "已注入 ") + srcName, srcName);
		}

		// 目标文件已存在且内容一致：只清理同族其它文件（这就是「客户端里已有相同模组」的正解）
		foreach (string dup2 in files.Where(f => !string.Equals(Path.GetFileName(f), srcName, StringComparison.OrdinalIgnoreCase)))
		{
			if (SafeDelete(dup2))
			{
				removed++;
			}
		}
		return (removed, srcName + " 已就绪", srcName);
	}

	private static bool InMainJarFamily(string fileName)
	{
		return fileName.StartsWith("moreshow", StringComparison.OrdinalIgnoreCase)
			&& !fileName.StartsWith("MoreShow_Extra", StringComparison.OrdinalIgnoreCase);
	}

	private static bool InExtraJarFamily(string fileName)
	{
		return fileName.StartsWith("MoreShow_Extra", StringComparison.OrdinalIgnoreCase);
	}

	private static bool InCptdJarFamily(string fileName)
	{
		return fileName.ToLowerInvariant().Contains("cptd");
	}

	private static bool IsSiteMainJarName(string fileName)
	{
		return fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && InMainJarFamily(fileName);
	}

	private static bool IsSiteExtraJarName(string fileName)
	{
		return fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && InExtraJarFamily(fileName);
	}

	/// <summary>两个候选源里取版本更高的那个（任一方为空则取另一方）。</summary>
	private static string? PickNewer(string? a, string? b)
	{
		if (a == null)
		{
			return b;
		}
		if (b == null)
		{
			return a;
		}
		int num = CompareVersionKeys(
			VersionTokens(ExtractVersion(Path.GetFileName(a))),
			VersionTokens(ExtractVersion(Path.GetFileName(b))));
		return (num >= 0) ? a : b;
	}

	// ---- 官网自动更新 ----

	/// <summary>官网目录页的 jar 清单（一次注入会处理多个客户端，10 分钟内复用同一份）。</summary>
	private static readonly object SiteLock = new object();

	private static List<(string Name, string Url)> _siteJars;

	private static DateTime _siteJarsAt = DateTime.MinValue;

	private static readonly TimeSpan SiteCacheTtl = TimeSpan.FromMinutes(10);

	private static List<(string Name, string Url)> GetSiteJars(bool forceRefresh = false)
	{
		lock (SiteLock)
		{
			if (!forceRefresh && _siteJars != null && DateTime.UtcNow - _siteJarsAt < SiteCacheTtl)
			{
				return _siteJars;
			}
			List<(string Name, string Url)> list = new List<(string Name, string Url)>();
			string html = DownloadStringWithRetry(MoreShowDownloadPage);
			if (html == null)
			{
				// 拉页面失败时保留上次结果，避免把已知可用的缓存冲掉
				if (_siteJars != null)
				{
					return _siteJars;
				}
				_siteJars = list;
				_siteJarsAt = DateTime.UtcNow;
				return list;
			}
			foreach (Match item in Regex.Matches(html, "href=\"([^\"]+\\.jar)\"", RegexOptions.IgnoreCase))
			{
				string raw = Uri.UnescapeDataString(item.Groups[1].Value);
				string abs;
				try
				{
					abs = new Uri(new Uri(MoreShowDownloadPage), raw).ToString();
				}
				catch
				{
					continue;
				}
				string name = raw;
				int num = name.LastIndexOf('/');
				if (num >= 0)
				{
					name = name.Substring(num + 1);
				}
				if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
				{
					list.Add((name, abs));
				}
			}
			_siteJars = list;
			_siteJarsAt = DateTime.UtcNow;
			if (list.Count == 0)
			{
				LogHelper.Log("[模组注入] 官网页面未解析到任何 jar 链接: " + MoreShowDownloadPage);
			}
			else
			{
				LogHelper.Log("[模组注入] 官网清单: " + string.Join("、", list.Select(x => x.Name)));
			}
			return list;
		}
	}

	/// <summary>网页抓取带 3 次重试（先失败常见于网络抖动/官网慢，单次失败就放弃会让用户以为「拉不到」）。</summary>
	private static string DownloadStringWithRetry(string url)
	{
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			try
			{
				return Http.GetStringAsync(url).GetAwaiter().GetResult();
			}
			catch (Exception ex)
			{
				if (attempt >= 3)
				{
					LogHelper.Log("[模组注入] 官网页面拉取失败（已重试 " + attempt + " 次）: " + ex.GetType().Name + ": " + ex.Message);
					return null;
				}
				Thread.Sleep(350 * attempt);
			}
		}
		return null;
	}

	/// <summary>
	/// 从官网清单里挑出某族（主模组 / 附属）的最新版，下载到程序 Mods 目录后返回本地路径。
	/// 已缓存且文件非空则直接复用；下载走 .tmp 再改名，失败会清理临时文件。
	/// </summary>
	private static string? TryFetchFamilyFromSite(Func<string, bool> isSiteJarName)
	{
		string tmp = null;
		try
		{
			List<(string Name, string Url)> jars = GetSiteJars();
			if (jars.Count == 0)
			{
				return null;
			}
			var best = jars
				.Where(j => isSiteJarName(j.Name))
				.OrderByDescending(j => VersionTokens(ExtractVersion(j.Name)), VersionKeyComparer.Instance)
				.ThenBy(j => j.Name.Length)
				.FirstOrDefault();
			if (string.IsNullOrEmpty(best.Name))
			{
				return null;
			}
			string dir = ModsDirectory();
			Directory.CreateDirectory(dir);
			string local = Path.Combine(dir, best.Name);
			if (File.Exists(local) && new FileInfo(local).Length > 0)
			{
				return local;
			}
			tmp = local + ".tmp";
			using (var response = Http.GetAsync(best.Url).GetAwaiter().GetResult())
			{
				if (!response.IsSuccessStatusCode)
				{
					LogHelper.Log("[模组注入] 官网下载失败 HTTP " + (int)response.StatusCode + ": " + best.Url);
					return null;
				}
				using FileStream destination = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
				response.Content.CopyToAsync(destination).GetAwaiter().GetResult();
			}
			if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0)
			{
				LogHelper.Log("[模组注入] 官网下载内容为空: " + best.Url);
				return null;
			}
			File.Move(tmp, local, overwrite: true);
			tmp = null;
			LogHelper.Log("[模组注入] 已从官网下载最新: " + best.Name + "（" + (new FileInfo(local).Length / 1024) + " KB）");
			// 清掉程序 Mods 目录里同族的其它缓存包，避免下次选源选到旧包
			foreach (string other in SafeFiles(dir))
			{
				string fn = Path.GetFileName(other);
				if (isSiteJarName(fn) && !string.Equals(fn, best.Name, StringComparison.OrdinalIgnoreCase))
				{
					SafeDelete(other);
				}
			}
			return local;
		}
		catch (Exception ex)
		{
			if (tmp != null)
			{
				SafeDelete(tmp);
			}
			LogHelper.Log("[模组注入] 官网获取最新包失败（回退本地缓存/内置包）: " + ex.GetType().Name + ": " + ex.Message);
			return null;
		}
	}

	// ---- 本地解析与工具 ----

	private static string? HighestLocalMatch(string prefix)
	{
		string text = null;
		List<object> bestKey = null;
		string[] array = SafeFiles(ModsDirectory());
		foreach (string text2 in array)
		{
			string fileName = Path.GetFileName(text2);
			if (fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) || !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			List<object> list = VersionTokens(ExtractVersion(fileName));
			if (text == null || CompareVersionKeys(list, bestKey) > 0)
			{
				text = text2;
				bestKey = list;
			}
		}
		return text;
	}

	private static string DisableCptdIn(string modsDir)
	{
		int num = 0;
		string[] array = SafeFiles(modsDir);
		foreach (string text in array)
		{
			string fileName = Path.GetFileName(text);
			if (!fileName.ToLowerInvariant().Contains("mhycptd") || fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			try
			{
				File.Move(text, text + ".disabled");
				num++;
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[模组注入] 禁用 CPTD 失败: " + text);
			}
		}
		string text2 = (num > 0) ? ("CPTD 模组已禁用（改名 " + num + " 个，游戏不再加载）") : "CPTD 模组未注入";
		LogHelper.Log("[模组注入] " + text2 + "（" + modsDir + "）");
		return text2;
	}

	private static string[] SafeFiles(string dir)
	{
		try
		{
			return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).ToArray() : Array.Empty<string>();
		}
		catch
		{
			return Array.Empty<string>();
		}
	}

	private static bool SafeDelete(string file)
	{
		try
		{
			File.Delete(file);
			return true;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[模组注入] 删除旧版失败: " + file);
			return false;
		}
	}

	private static string ExtractVersion(string fileName)
	{
		string text = fileName ?? "";
		if (text.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(0, text.Length - 4);
		}
		int num = text.LastIndexOf('-');
		return (num >= 0) ? text.Substring(num + 1) : text;
	}

	private static List<object> VersionTokens(string version)
	{
		List<object> list = new List<object>();
		if (string.IsNullOrWhiteSpace(version))
		{
			return list;
		}
		foreach (Match item in Regex.Matches(version, "\\d+|[^\\d]+"))
		{
			string value = item.Value;
			list.Add(char.IsDigit(value[0]) ? ((object)long.Parse(value)) : ((object)value.ToLowerInvariant()));
		}
		return list;
	}

	private static int CompareVersionKeys(List<object> a, List<object> b)
	{
		int count = Math.Max(a.Count, b.Count);
		for (int i = 0; i < count; i++)
		{
			object obj = ((i < a.Count) ? a[i] : null);
			object obj2 = ((i < b.Count) ? b[i] : null);
			if (obj == null && obj2 == null)
			{
				return 0;
			}
			if (obj == null)
			{
				return -1;
			}
			if (obj2 == null)
			{
				return 1;
			}
			if (obj is long num && obj2 is long num2)
			{
				if (num != num2)
				{
					return num.CompareTo(num2);
				}
			}
			else if (obj is string text && obj2 is string text2)
			{
				int num3 = string.CompareOrdinal(text, text2);
				if (num3 != 0)
				{
					return num3;
				}
			}
			else
			{
				if (!(obj is long))
				{
					return -1;
				}
				return 1;
			}
		}
		return 0;
	}

	private sealed class VersionKeyComparer : IComparer<List<object>>
	{
		public static readonly VersionKeyComparer Instance = new VersionKeyComparer();

		public int Compare(List<object>? x, List<object>? y)
		{
			return CompareVersionKeys(x ?? new List<object>(), y ?? new List<object>());
		}
	}

	public static List<(string ModId, string Version, string TargetFileName)> DeclaredMods()
	{
		List<(string, string, string)> list = new List<(string, string, string)>();
		foreach (ModInjectRule item in LoadRules())
		{
			string version = item.ModVersion;
			if (item.Kind == "cptd")
			{
				string text = HighestLocalMatch("MhyCPTD-");
				if (text != null)
				{
					version = ExtractVersion(Path.GetFileName(text));
				}
			}
			list.Add((item.ModId, version, item.TargetFileName));
		}
		return list;
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
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
			Http.DefaultRequestHeaders.UserAgent.ParseAdd("MhyunBox/6.8");
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

	private static (InjectResultKind Kind, string Message) InjectMoreShow(string modsDir)
	{
		List<string> parts = new List<string>();
		bool failed = false;
		bool injected = false;
		// —— 主模组：目标 1.6.4，先删客户端里低于该版本的旧版，再补/保 1.6.4 ——
		try
		{
			string mainSrc = HighestLocalMatch("moreshow-"); // 内置主模组（Mods 目录随程序分发）
			string mainSrcVer = (mainSrc != null) ? ExtractVersion(Path.GetFileName(mainSrc)) : "1.6.4";
			List<string> clientMains = SafeFiles(modsDir).Where(delegate (string f)
			{
				string fileName = Path.GetFileName(f);
				return fileName.StartsWith("moreshow", StringComparison.OrdinalIgnoreCase)
					&& !fileName.StartsWith("MoreShow_Extra", StringComparison.OrdinalIgnoreCase);
			}).ToList();
			// 先删：客户端里版本低于目标的旧主模组（含 .disabled 残留）
			int removedOld = 0;
			foreach (string old in clientMains.ToList())
			{
				string fn = Path.GetFileName(old);
				if (fn.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
					|| CompareVersionKeys(VersionTokens(ExtractVersion(fn)), VersionTokens(mainSrcVer)) < 0)
				{
					if (SafeDelete(old))
					{
						removedOld++;
					}
					clientMains.Remove(old);
				}
			}
			string mainMsg;
			if (clientMains.Count > 0)
			{
				string existing = clientMains[0];
				if (mainSrc != null && string.Equals(Path.GetFileName(existing), Path.GetFileName(mainSrc), StringComparison.OrdinalIgnoreCase)
					&& new FileInfo(existing).Length != new FileInfo(mainSrc).Length)
				{
					// 同版本但程序内置包内容已更新（如加图标/修补丁）：静默覆盖
					File.Copy(mainSrc, Path.Combine(modsDir, Path.GetFileName(mainSrc)), overwrite: true);
					injected = true;
					mainMsg = "已更新主模组: " + Path.GetFileName(mainSrc) + "（内容刷新）";
				}
				else
				{
					mainMsg = "主模组已是 " + ExtractVersion(Path.GetFileName(existing)) + "，保留";
				}
			}
			else if (mainSrc != null)
			{
				string dst = Path.Combine(modsDir, Path.GetFileName(mainSrc));
				File.Copy(mainSrc, dst, overwrite: true);
				injected = true;
				mainMsg = "已注入主模组: " + Path.GetFileName(mainSrc);
			}
			else
			{
				failed = true;
				mainMsg = "主模组源缺失（程序 Mods 目录无 moreshow-*.jar）";
			}
			parts.Add((removedOld > 0 ? "已删除旧版更多显示 " + removedOld + " 个，" : "") + mainMsg);
		}
		catch (Exception ex)
		{
			failed = true;
			LogHelper.LogError(ex, "[模组注入] 更多显示主模组处理失败");
			parts.Add("主模组处理失败: " + ex.Message);
		}
		// —— 附属：官网最新优先（下载地址兜底），删客户端旧附属后注入 ——
		try
		{
			string siteLatest = TryGetLatestMoreShowFromSite();
			string extraSrc = siteLatest ?? HighestLocalMatch("MoreShow_Extra");
			if (extraSrc != null)
			{
				string extraName = Path.GetFileName(extraSrc);
				string dst = Path.Combine(modsDir, extraName);
				List<string> clientExtras = SafeFiles(modsDir).Where(delegate (string f)
				{
					string fileName = Path.GetFileName(f);
					return fileName.StartsWith("MoreShow_Extra", StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(fileName, extraName, StringComparison.OrdinalIgnoreCase);
				}).ToList();
				int removed = 0;
				foreach (string old in clientExtras)
				{
					if (SafeDelete(old))
					{
						removed++;
					}
				}
				bool copiedExtra = false;
				if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(extraSrc).Length)
				{
					File.Copy(extraSrc, dst, overwrite: true);
					injected = true;
					copiedExtra = true;
				}
				parts.Add((copiedExtra ? "附属已更新: " : "附属已就绪: ") + extraName + "（" + ((siteLatest != null) ? "官网最新" : "本地缓存") + "）" + (removed > 0 ? "，已删除旧附属 " + removed + " 个" : ""));
			}
			else
			{
				parts.Add("附属未获取到下载源（官网不可达且无缓存），保留现状");
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

	// ---- MhyCPTD：先删客户端里低于源版本的旧版（MhyCPTD/CPTD/mhycptd 全家族），再注入最新 ----

	private static (InjectResultKind Kind, string Message) InjectCptd(string modsDir)
	{
		// 重新启用注入：清理 .disabled 残留
		foreach (string item in SafeFiles(modsDir))
		{
			string fileName = Path.GetFileName(item);
			if (fileName.ToLowerInvariant().Contains("cptd") && fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
			{
				SafeDelete(item);
			}
		}
		string src = HighestLocalMatch("MhyCPTD-");
		if (src == null)
		{
			src = HighestLocalMatch("CPTD-");
		}
		if (src == null)
		{
			string[] family = SafeFiles(modsDir).Where((string f) => Path.GetFileName(f).ToLowerInvariant().Contains("cptd")).ToArray();
			if (family.Length > 0)
			{
				return (InjectResultKind.AlreadyLatest, "未获取到 CPTD 更新源，保留现有模组: " + Path.GetFileName(family[0]));
			}
			return (InjectResultKind.Failed, "CPTD 模组源缺失（程序 Mods 目录无 MhyCPTD-*.jar）");
		}
		string srcName = Path.GetFileName(src);
		string srcVer = ExtractVersion(srcName);
		// 先删：客户端里版本 ≤ 源版本的旧版/同名重复（含 .disabled 残留）；比源版本新的保留且不覆盖
		int removedOld = 0;
		bool clientNewer = false;
		List<string> keepNewer = new List<string>();
		foreach (string item2 in SafeFiles(modsDir))
		{
			string fileName2 = Path.GetFileName(item2);
			if (!fileName2.ToLowerInvariant().Contains("cptd"))
			{
				continue;
			}
			if (string.Equals(fileName2, srcName, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			int cmp = CompareVersionKeys(VersionTokens(ExtractVersion(fileName2)), VersionTokens(srcVer));
			if (cmp > 0)
			{
				clientNewer = true;
				keepNewer.Add(fileName2);
				continue;
			}
			if (SafeDelete(item2))
			{
				removedOld++;
			}
		}
		string dst2 = Path.Combine(modsDir, srcName);
		bool copied = false;
		if (!clientNewer && (!File.Exists(dst2) || new FileInfo(dst2).Length != new FileInfo(src).Length))
		{
			File.Copy(src, dst2, overwrite: true);
			copied = true;
		}
		string text = (clientNewer ? "客户端已有更新版 CPTD（" + string.Join("、", keepNewer) + "），保留不覆盖" : (copied ? "已注入: " + srcName : "CPTD 已是最新: " + srcName)) + (removedOld > 0 ? "，已删除旧版 " + removedOld + " 个" : "");
		LogHelper.Log("[模组注入] [CPTD] " + text);
		return ((copied || clientNewer) ? InjectResultKind.Injected : InjectResultKind.AlreadyLatest, text);
	}

	// ---- 更多显示官网自动更新 ----

	private static string? TryGetLatestMoreShowFromSite()
	{
		try
		{
			string text = Http.GetStringAsync(MoreShowDownloadPage).GetAwaiter().GetResult();
			List<string> list = new List<string>();
			foreach (Match item in Regex.Matches(text, "href=\"([^\"]+\\.jar)\"", RegexOptions.IgnoreCase))
			{
				string text2 = Uri.UnescapeDataString(item.Groups[1].Value);
				int num = text2.LastIndexOf('/');
				if (num >= 0)
				{
					text2 = text2.Substring(num + 1);
				}
				if (text2.StartsWith("MoreShow_Extra", StringComparison.OrdinalIgnoreCase) && text2.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
				{
					list.Add(text2);
				}
			}
			if (list.Count == 0)
			{
				LogHelper.Log("[模组注入] 官网未解析到 MoreShow_Extra jar");
				return null;
			}
			string text3 = list.OrderByDescending((string n) => VersionTokens(ExtractVersion(n)), VersionKeyComparer.Instance).ThenBy((string n) => n.Length).First();
			string text4 = Path.Combine(ModsDirectory(), text3);
			if (!File.Exists(text4) || new FileInfo(text4).Length == 0)
			{
				Directory.CreateDirectory(ModsDirectory());
				string text5 = text4 + ".tmp";
				using (var response = Http.GetAsync(MoreShowDownloadPage + Uri.EscapeDataString(text3)).GetAwaiter().GetResult())
				using (FileStream destination = new FileStream(text5, FileMode.Create))
				{
					response.Content.CopyToAsync(destination).GetAwaiter().GetResult();
				}
				File.Move(text5, text4, overwrite: true);
				LogHelper.Log("[模组注入] 已从官网下载最新: " + text3);
				string[] array = SafeFiles(ModsDirectory());
				foreach (string text6 in array)
				{
					string fileName = Path.GetFileName(text6);
					if (fileName.StartsWith("MoreShow_Extra", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !string.Equals(fileName, text3, StringComparison.OrdinalIgnoreCase))
					{
						SafeDelete(text6);
					}
				}
			}
			return text4;
		}
		catch (Exception ex)
		{
			LogHelper.Log("[模组注入] 官网获取最新 MoreShow_Extra 失败（回退本地缓存/内置包）: " + ex.GetType().Name + ": " + ex.Message);
			return null;
		}
	}

	// ---- 本地解析与工具 ----

	private static string[] FamilyFiles(string modsDir, ModInjectRule rule)
	{
		return SafeFiles(modsDir).Where((string f) =>
		{
			string fileName = Path.GetFileName(f);
			return !string.IsNullOrWhiteSpace(rule.Family) && fileName.ToLowerInvariant().Contains(rule.Family);
		}).ToArray();
	}

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

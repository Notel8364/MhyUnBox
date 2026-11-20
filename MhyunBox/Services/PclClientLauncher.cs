using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MhyunBox.Services;

/// <summary>PCL / 通用 .minecraft 客户端检测结果。</summary>
public sealed class PclClientInfo
{
	public string McRoot = "";

	/// <summary>选中版本目录名（versions\&lt;name&gt;\）。</summary>
	public string VersionName = "";

	/// <summary>选中的版本 JSON 全路径。</summary>
	public string VersionJson = "";

	/// <summary>工作目录（版本隔离时为 versions\&lt;name&gt;，否则 .minecraft）。</summary>
	public string WorkingDir = "";
}

/// <summary>
/// PCL / 通用 .minecraft 客户端启动适配（2026-11-20）。
///
/// OfficialClientLauncher 只认网易官方 1.20.1（versions\1.20\1.20.json 的官方启动器格式）。
/// 当用户填的是 PCL 管理的客户端目录（versions\&lt;版本名&gt;\&lt;版本名&gt;.json 标准格式，如 1.12.2-Forge）时，
/// 由此处按标准版本 JSON（含 inheritsFrom 继承链、libraries 规则、natives 解包）拼命令行启动。
/// 进服走 --server/--port（1.12.2 原生支持），认证交给服务器认证模组，不需要网易 -DToken/userProperties。
/// </summary>
public static class PclClientLauncher
{
	/// <summary>检测给定目录是否为可启动的 PCL / 通用 .minecraft 客户端。给定路径可以是 .minecraft 本身、PCL 根目录或 versions\&lt;name&gt; 版本目录。</summary>
	public static PclClientInfo? Detect(string? dir)
	{
		try
		{
			string path = (dir ?? "").Trim();
			if (path.Length == 0 || !Directory.Exists(path))
			{
				return null;
			}
			path = path.TrimEnd('\\');
			// 形态 1：给的就是版本目录（versions 下）——同名 .json 优先；PCL 也允许目录名 ≠ json 名，
			// 此时只要目录里有 json/jar 也按版本目录处理（如 元素芝士？启动！ 内的 (10.24PCL).json）。
			string dirName = Path.GetFileName(path) ?? "";
			DirectoryInfo? versions = Directory.GetParent(path);
			DirectoryInfo? mc = versions?.Parent;
			if (mc != null && string.Equals(versions!.Name, "versions", StringComparison.OrdinalIgnoreCase)
				&& (File.Exists(Path.Combine(path, dirName + ".json")) || Directory.GetFiles(path, "*.json").Length > 0))
			{
				return PickVersion(mc.FullName, path);
			}
			// 形态 2：给的就是 .minecraft（直接含 versions）
			if (Directory.Exists(Path.Combine(path, "versions")))
			{
				return PickVersion(path, null);
			}
			// 形态 3：PCL 根目录（下一级含 .minecraft）
			string inner = Path.Combine(path, ".minecraft");
			if (Directory.Exists(Path.Combine(inner, "versions")))
			{
				return PickVersion(inner, null);
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>从 versions 下选一个最合适的版本：优先 1.12.2（元素之诗/AdventureWorld 目标版本），其次 Forge，最后取最近修改的。</summary>
	private static PclClientInfo PickVersion(string mcRoot, string? forcedVersionDir)
	{
		try
		{
			string versionsDir = Path.Combine(mcRoot, "versions");
			string? forcedJson = null;
			if (forcedVersionDir != null && Directory.Exists(forcedVersionDir))
			{
				string dirName = Path.GetFileName(forcedVersionDir) ?? "";
				string sameName = Path.Combine(forcedVersionDir, dirName + ".json");
				if (dirName.Length > 0 && File.Exists(sameName))
				{
					forcedJson = sameName;
				}
				else
				{
					// PCL 允许「版本目录名 ≠ json 名」：在版本目录里选「有同名 jar」的最新 json
					// （如 元素芝士？启动！ 目录内的 元素芝士？启动！(10.24PCL).json）
					string[] jsons = Directory.GetFiles(forcedVersionDir, "*.json");
					forcedJson = jsons
						.Where((string j) => File.Exists(Path.Combine(forcedVersionDir, Path.GetFileNameWithoutExtension(j) + ".jar")))
						.OrderByDescending(File.GetLastWriteTimeUtc)
						.FirstOrDefault()
						?? jsons.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
				}
			}
			string best;
			if (forcedJson != null)
			{
				best = Path.GetDirectoryName(forcedJson)!;
			}
			else
			{
				List<string> candidates = Directory.GetDirectories(versionsDir)
					.Where((string d) => File.Exists(Path.Combine(d, Path.GetFileName(d) + ".json")))
					.ToList();
				if (candidates.Count == 0)
				{
					// PCL 允许版本目录名与 json 名不同：按 json 兜底扫一遍
					candidates = Directory.GetDirectories(versionsDir)
						.Where((string d) => Directory.GetFiles(d, "*.json").Length > 0 && Directory.GetFiles(d, "*.jar").Length > 0)
						.ToList();
					if (candidates.Count == 0)
					{
						return null!;
					}
				}
				best = candidates
					.OrderByDescending((string d) =>
					{
						string n = Path.GetFileName(d);
						if (n.Contains("1.12.2", StringComparison.OrdinalIgnoreCase)) return 3;   // 元素之诗目标版本
						if (n.Contains("forge", StringComparison.OrdinalIgnoreCase)) return 2;
						return 0;
					})
					.ThenByDescending((string d) => Directory.GetLastWriteTimeUtc(d))
					.First();
				string name0 = Path.GetFileName(best);
				forcedJson = Path.Combine(best, name0 + ".json");
				if (!File.Exists(forcedJson))
				{
					forcedJson = Directory.GetFiles(best, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).First();
				}
			}
			string json = forcedJson;
			string name = Path.GetFileNameWithoutExtension(json);
			// 版本隔离：版本目录里已有 mods/config 就把工作目录设为版本目录（对齐 PCL 隔离行为）
			string work = (Directory.Exists(Path.Combine(best, "mods")) || Directory.Exists(Path.Combine(best, "config"))) ? best : mcRoot;
			return new PclClientInfo { McRoot = mcRoot, VersionName = name, VersionJson = json, WorkingDir = work };
		}
		catch
		{
			return null!;
		}
	}

	/// <summary>用 PCL 客户端启动并连服。</summary>
	public static OfficialClientLaunchResult Launch(PclClientInfo info, string targetHost, int targetPort, string loginName, string uuid, long uid, string? javaHome = null, bool consoleJava = false, string? memoryArg = null)
	{
		OfficialClientLaunchResult result = new OfficialClientLaunchResult { Root = info.McRoot };
		try
		{
			string nativesDir = Path.Combine(Path.GetDirectoryName(info.VersionJson)!, "natives");
			// 1) 合并继承链（child → base），收集 libraries / mainClass / 游戏参数
			List<JsonElement> chain = new List<JsonElement>();
			List<string> libNames = new List<string>();
			string versionDir = Path.GetDirectoryName(info.VersionJson)!;
			string cursor = info.VersionJson;
			HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			while (true)
			{
				if (visited.Contains(cursor) || !File.Exists(cursor))
				{
					break;
				}
				visited.Add(cursor);
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(cursor));
				JsonElement clone = doc.RootElement.Clone();
				chain.Add(clone);
				if (clone.TryGetProperty("inheritsFrom", out JsonElement inh))
				{
					cursor = Path.Combine(versionDir, inh.GetString() + ".json");
					continue;
				}
				break;
			}
			if (chain.Count == 0)
			{
				result.Message = "版本 JSON 解析失败：" + info.VersionJson;
				return result;
			}
			JsonElement child = chain[0];
			string mainClass = child.TryGetProperty("mainClass", out JsonElement mcEl) ? (mcEl.GetString() ?? "") : "";
			if (mainClass.Length == 0)
			{
				result.Message = "版本 JSON 缺少 mainClass";
				return result;
			}
			// 游戏参数：优先子版本 minecraftArguments（1.12.2），否则父链 arguments.game
			string gameArgs = child.TryGetProperty("minecraftArguments", out JsonElement maEl) ? (maEl.GetString() ?? "") : "";
			if (gameArgs.Length == 0)
			{
				// 1.13+ arguments 结构
				foreach (JsonElement el in chain)
				{
					if (el.TryGetProperty("arguments", out JsonElement argsEl) && argsEl.TryGetProperty("game", out JsonElement gameEl) && gameEl.ValueKind == JsonValueKind.Array)
					{
						StringBuilder sb = new StringBuilder();
						foreach (JsonElement g in gameEl.EnumerateArray())
						{
							if (g.ValueKind == JsonValueKind.String)
							{
								sb.Append(g.GetString()).Append(' ');
							}
						}
						gameArgs = sb.ToString().TrimEnd();
						break;
					}
				}
			}
			if (gameArgs.Length == 0)
			{
				gameArgs = "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} --assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid} --accessToken ${auth_access_token} --userType ${user_type}";
			}
			// jar：子版本自身同名 jar 优先，否则继承链里第一个存在的
			string clientJar = Path.Combine(versionDir, Path.GetFileNameWithoutExtension(info.VersionJson) + ".jar");
			if (!File.Exists(clientJar))
			{
				foreach (JsonElement el in chain)
				{
					string id = el.TryGetProperty("id", out JsonElement idEl) ? (idEl.GetString() ?? "") : "";
					if (id.Length > 0)
					{
						string cand = Path.Combine(Path.Combine(info.McRoot, "versions", id), id + ".jar");
						if (File.Exists(cand))
						{
							clientJar = cand;
							break;
						}
					}
				}
			}
			if (!File.Exists(clientJar))
			{
				result.Message = "未找到版本 jar（" + versionDir + "）";
				return result;
			}

			// 2) libraries（windows 规则过滤）+ natives 解包
			Directory.CreateDirectory(nativesDir);
			List<string> classpath = new List<string> { clientJar };
			foreach (JsonElement el in chain)
			{
				if (!el.TryGetProperty("libraries", out JsonElement libsEl) || libsEl.ValueKind != JsonValueKind.Array)
				{
					continue;
				}
				foreach (JsonElement lib in libsEl.EnumerateArray())
				{
					try
					{
						if (!RulesAllowWindows(lib))
						{
							continue;
						}
						string path = ResolveLibPath(info.McRoot, lib, out bool isNative);
						if (path.Length == 0 || !File.Exists(path))
						{
							continue;
						}
						if (isNative)
						{
							ExtractNatives(path, nativesDir);
						}
						else if (!classpath.Contains(path, StringComparer.OrdinalIgnoreCase))
						{
							classpath.Add(path);
						}
					}
					catch
					{
					}
				}
			}

			// 3) Java：显式指定 → 客户端自带 jre → 本机自动搜索（按 PCL 原则：版本必须满足游戏需求，
			//    1.12.2 / Forge 1.12.2 → 恰好 Java 8；1.17 → 16+；1.18~1.20.4 → 17+；1.20.5+ → 21+）
			(int javaMin, int javaMax) = JavaRangeFor(chain, info.VersionName);
			string? javaExe = ResolveJava(info.McRoot, versionDir, javaHome, javaMin, javaMax);
			if (javaExe == null)
			{
				result.Message = $"未找到满足需求（Java {javaMin}~{(javaMax == int.MaxValue ? "+" : javaMax.ToString())}）的 Java；可在启动卡 JRE 目录里指定";
				return result;
			}
			if (!consoleJava)
			{
				string javaw = javaExe.Replace("\\java.exe", "\\javaw.exe");
				if (File.Exists(javaw))
				{
					javaExe = javaw;
				}
			}

			// 4) 参数组装
			string assetsRoot = Path.Combine(info.McRoot, "assets");
			string assetsIndex = child.TryGetProperty("assets", out JsonElement assetsEl) ? (assetsEl.GetString() ?? "legacy") : "legacy";
			string gameAssets = assetsIndex.Equals("legacy", StringComparison.OrdinalIgnoreCase)
				? Path.Combine(assetsRoot, "virtual", "legacy")
				: assetsRoot;
			var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["auth_player_name"] = loginName,
				["auth_uuid"] = uuid,
				["auth_access_token"] = "0",
				["auth_session"] = "",
				["auth_xuid"] = "",
				["clientid"] = "",
				["user_type"] = "legacy",
				["user_properties"] = "{}",
				["version_name"] = info.VersionName,
				["version_type"] = "MhyunBox",
				["assets_index_name"] = assetsIndex,
				["assets_root"] = assetsRoot,
				["game_assets"] = gameAssets,
				["game_directory"] = info.WorkingDir,
				["launcher_name"] = "MhyunBox",
				["launcher_version"] = AppVersion.Display.TrimStart('v'),
				["native_directory"] = nativesDir,
				["classpath"] = string.Join(";", classpath),
				["classpath_separator"] = ";"
			};
			string game = Regex.Replace(gameArgs, @"\$\{([a-zA-Z0-9_]+)\}", (Match m) => map.TryGetValue(m.Groups[1].Value, out string? v) ? v : "");
			bool hasTarget = targetPort > 0 && targetHost.Length > 0;
			if (hasTarget)
			{
				game += " --server " + targetHost + " --port " + targetPort;
			}

			string cp = string.Join(";", classpath);
			// 2026-11-20 帧率优化（对齐 PCL 快速启动 GC 参数）：G1 分代调优降低 GC 停顿与卡顿，
			// 减少 1.12.2 / Forge 环境下掉帧；内存大小仍由 memoryArg（内存优化选项）控制。
			string jvm = string.Join(" ", new[]
			{
				memoryArg ?? "-Xmx2000M",
				"-XX:+UseG1GC",
				"-XX:-UseAdaptiveSizePolicy",
				"-XX:-OmitStackTraceInFastThrow",
				"-XX:+UnlockExperimentalVMOptions",
				"-XX:G1NewSizePercent=23",
				"-XX:G1ReservePercent=20",
				"-XX:MaxGCPauseMillis=40",
				"-XX:G1HeapRegionSize=16M",
				"-Djava.library.path=\"" + nativesDir + "\"",
				"-Dfml.ignoreInvalidMinecraftCertificates=true",
				"-Dfml.ignorePatchDiscrepancies=true",
				"-Dlog4j2.formatMsgNoLookups=true",
				"-cp \"" + cp + "\"",
				mainClass
			});
			string fullArgs = jvm + " " + game;
			result.CommandLine = fullArgs;
			LogHelper.Log($"[PCL客户端] 启动 root={info.McRoot} 版本={info.VersionName} java={javaExe} 工作目录={info.WorkingDir} 目标={(hasTarget ? (targetHost + ":" + targetPort) : "主菜单")} 登录名={loginName}");
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = javaExe,
				Arguments = fullArgs,
				WorkingDirectory = info.WorkingDir,
				UseShellExecute = false,
				CreateNoWindow = false
			};
			Process p = new Process { StartInfo = psi, EnableRaisingEvents = true };
			p.Exited += delegate
			{
				try
				{
					LogHelper.Log("[PCL客户端] 已退出（pid " + p.Id + "，退出码 " + p.ExitCode + "）");
				}
				catch
				{
				}
			};
			if (!p.Start())
			{
				result.Message = "进程启动失败（java 未能创建）";
				return result;
			}
			result.Ok = true;
			result.Pid = p.Id;
			result.Message = "已启动 PCL 客户端 " + info.VersionName + "（pid " + p.Id + "）" + (hasTarget ? ("，目标 " + targetHost + ":" + targetPort) : "（未自动进服）");
			LogHelper.Log("[PCL客户端] " + result.Message);
			return result;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[PCL客户端] 启动失败");
			result.Message = "启动失败：" + ex.Message;
			return result;
		}
	}

	/// <summary>
	/// Java 选择（参考 PCL ModJava/JavaUtils 原理）：显式指定 → 客户端自带 JRE → 常见目录自动搜索；
	/// 候选按「版本落在游戏需求区间内 → 距离目标主版本最近 → 优先级」挑选，并拒绝明显不满足区间的 Java。
	/// </summary>
	private static string? ResolveJava(string mcRoot, string versionDir, string? javaHome, int minMajor, int maxMajor)
	{
		int recommended = Math.Max(8, Math.Min(minMajor, maxMajor));
		List<(int Priority, string Exe, int Major)> candidates = CollectCandidates(mcRoot, versionDir, javaHome);
		var best = candidates
			.Where((Func<(int Priority, string Exe, int Major), bool>)(c => c.Major >= 5))
			.OrderBy((Func<(int Priority, string Exe, int Major), (int, int, int)>)(c =>
			{
				bool inRange = c.Major >= minMajor && c.Major <= maxMajor;
				return (inRange ? 0 : 1, Math.Abs(c.Major - recommended), c.Priority);
			}))
			.FirstOrDefault();
		if (best.Exe != null)
		{
			LogHelper.Log($"[PCL客户端] Java 选择：{best.Exe}（主版本 {best.Major}，需求 {minMajor}~{maxMajor}）");
			return best.Exe;
		}
		return OfficialClientLauncher.FindSystemJava();
	}

	/// <summary>
	/// 收集全部可用 Java 候选（对齐 PCL CandidateFolders 的搜索面）：
	/// 显式指定 → 客户端自带 JRE → 常见安装目录 / 运行时目录 / 环境变量；
	/// 名字看不出版本的候选会实际运行 java -version 探测主版本。
	/// </summary>
	private static List<(int Priority, string Exe, int Major)> CollectCandidates(string? mcRoot, string? versionDir, string? javaHome)
	{
		// (优先级, javaw 全路径, 探测到的主版本) ；major == -1 表示未知
		List<(int Priority, string Exe, int Major)> candidates = new List<(int, string, int)>();
		void AddHome(string? home, int priority)
		{
			if (string.IsNullOrWhiteSpace(home)) return;
			string dir = home.Trim().TrimEnd('\\');
			if (dir.Length == 0 || !Directory.Exists(dir)) return;
			string[] names = { "javaw.exe", "java.exe" };
			string? found = null;
			foreach (string name in names)
			{
				string cand = Path.Combine(dir, "bin", name);
				if (File.Exists(cand)) { found = cand; break; }
			}
			// PCL / 官方包有时把 exe 直接放在 jre 根
			found ??= names.Select((string n) => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
			if (found == null) return;
			int major = JavaMajorFromName(dir);
			candidates.Add((priority, found, major));
		}
		try
		{
			// 0) 显式指定（启动卡 JRE 目录 / 设置里的 JRE 目录）
			string explicitHome = (javaHome ?? "").Trim();
			if (explicitHome.Length == 0)
			{
				explicitHome = (Settings.SettingsManager.Instance.OfficialClientJavaHome ?? "").Trim();
			}
			if (explicitHome.Length > 0)
			{
				// 显式指定的可能是 jre 根或其下的 bin；目录名一般带版本号，必要时探测
				string dir0 = explicitHome.TrimEnd('\\');
				string? exe0 = new[] { "javaw.exe", "java.exe" }
					.Select((string n) => Path.Combine(dir0, "bin", n))
					.FirstOrDefault(File.Exists)
					?? new[] { "javaw.exe", "java.exe" }.Select((string n) => Path.Combine(dir0, n)).FirstOrDefault(File.Exists)
					?? (Directory.Exists(Path.Combine(dir0, "bin")) ? null : Directory.GetFiles(dir0, "java*.exe", SearchOption.AllDirectories).FirstOrDefault());
				if (exe0 != null && File.Exists(exe0))
				{
					int major0 = JavaMajorFromName(dir0);
					if (major0 < 0) major0 = ProbeJavaMajor(exe0);
					candidates.Add((0, exe0, major0));
				}
			}
			// 1) 客户端自带 JRE（.minecraft\jre\*、versions\<版本>\jre\*）
			if (mcRoot != null)
			{
				foreach (string bundledRoot in new[] { Path.Combine(mcRoot, "jre"), Path.Combine(mcRoot, "runtime"), Path.Combine(versionDir ?? "", "jre") })
				{
					if (bundledRoot.Length == 0 || !Directory.Exists(bundledRoot)) continue;
					foreach (string sub in Directory.GetDirectories(bundledRoot))
					{
						AddHome(sub, 1);
					}
					AddHome(bundledRoot, 1);
				}
			}
			// 2) 本机常见安装位置（对齐 PCL CandidateFolders 的常用子集）
			foreach (string baseDir in new[]
			{
				"C:\\Program Files\\Java", "C:\\Program Files\\Eclipse Adoptium", "C:\\Program Files\\Amazon Corretto",
				"C:\\Program Files\\Zulu", "C:\\Program Files (x86)\\Java",
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks")
			})
			{
				try
				{
					if (!Directory.Exists(baseDir)) continue;
					foreach (string sub in Directory.GetDirectories(baseDir))
					{
						AddHome(sub, 2);
					}
				}
				catch { }
			}
			try
			{
				string msRoot = "C:\\Program Files\\Microsoft";
				if (Directory.Exists(msRoot))
				{
					foreach (string sub in Directory.GetDirectories(msRoot, "jdk-*"))
					{
						AddHome(sub, 2);
					}
				}
			}
			catch { }
			foreach (string envName in new[] { "JDK_HOME", "JAVA_HOME" })
			{
				try
				{
					string? env = Environment.GetEnvironmentVariable(envName);
					if (!string.IsNullOrWhiteSpace(env)) AddHome(env, 2);
				}
				catch { }
			}
		}
		catch
		{
		}
		// 名字看不出版本的候选先探测一次真实版本（java -version），之后统一评分
		for (int i = 0; i < candidates.Count; i++)
		{
			if (candidates[i].Major < 0)
			{
				int probed = ProbeJavaMajor(candidates[i].Exe);
				candidates[i] = (candidates[i].Priority, candidates[i].Exe, probed);
			}
		}
		return candidates;
	}

	/// <summary>
	/// 列出本机所有找到的 JRE/JDK（2026-11-20 新增，对齐 PCL 的 Java 列表行为）。
	/// 返回 (JRE 根目录, java 可执行文件, 主版本)，按主版本升序、同名去重；供「客户端配置与启动」下拉展示。
	/// </summary>
	public static List<(string Home, string Exe, int Major)> EnumerateJavas()
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<(string Home, string Exe, int Major)> list = new List<(string, string, int)>();
		foreach (var c in CollectCandidates(null, null, null)
			.OrderBy((Func<(int Priority, string Exe, int Major), int>)(c => c.Priority))
			.ThenBy((Func<(int Priority, string Exe, int Major), int>)(c => c.Major)))
		{
			try
			{
				if (string.IsNullOrEmpty(c.Exe) || c.Major < 5 || !seen.Add(c.Exe))
				{
					continue;
				}
				string dir = Path.GetDirectoryName(c.Exe) ?? "";
				// exe 在 <home>\bin\ 下时上一级才是 JRE 根
				string home = string.Equals(Path.GetFileName(dir), "bin", StringComparison.OrdinalIgnoreCase)
					? Path.GetDirectoryName(dir) ?? dir
					: dir;
				list.Add((home, c.Exe, c.Major));
			}
			catch
			{
			}
		}
		return list.OrderBy((Func<(string Home, string Exe, int Major), int>)(j => j.Major)).ToList();
	}

	/// <summary>由游戏版本 JSON 继承链 / 版本名推导 Java 主版本需求区间（对齐 PCL GetJavaRequirement 的常用分支）。</summary>
	private static (int Min, int Max) JavaRangeFor(List<JsonElement> chain, string versionName)
	{
		try
		{
			// 版本 JSON 自带要求：javaVersion.majorVersion（1.12.2 vanilla = 8）或顶层 java_version（1.13+ 格式）
			foreach (JsonElement el in chain)
			{
				if (el.TryGetProperty("javaVersion", out JsonElement jv) && jv.TryGetProperty("majorVersion", out JsonElement mv) && mv.TryGetInt32(out int code) && code >= 5)
				{
					return code <= 8 ? (8, 8) : (code, int.MaxValue);   // j8 恰好 8：Forge 1.12.2 在 Java 9+ 无法启动
				}
				if (el.TryGetProperty("java_version", out JsonElement jv2) && jv2.TryGetInt32(out int code2) && code2 >= 5)
				{
					return code2 <= 8 ? (8, 8) : (code2, int.MaxValue);
				}
			}
		}
		catch
		{
		}
		string n = versionName ?? "";
		bool Has(params string[] keys) => keys.Any((string k) => n.Contains(k, StringComparison.OrdinalIgnoreCase));
		if (Has("1.21", "1.20.5", "1.20.6")) return (21, int.MaxValue);
		if (Has("1.18", "1.19", "1.20")) return (17, int.MaxValue);
		if (Has("1.17")) return (16, int.MaxValue);
		if (Has("1.13", "1.14", "1.15", "1.16")) return (8, int.MaxValue);
		if (Has("1.7", "1.8", "1.9", "1.10", "1.11", "1.12")) return (8, 8);
		return (8, int.MaxValue);
	}

	/// <summary>从 JRE 目录名猜测主版本（jdk-17.0.9 → 17；jre1.8.0_401 → 8；看不出返回 -1）。</summary>
	private static int JavaMajorFromName(string dir)
	{
		try
		{
			string n = Path.GetFileName(dir.TrimEnd('\\'));
			Match m = Regex.Match(n, @"(?<![0-9.])1[._]([0-9]+)(?![0-9])");
			if (m.Success && int.TryParse(m.Groups[1].Value, out int v1) && v1 >= 5 && v1 <= 30)
			{
				return v1;
			}
			m = Regex.Match(n, "[0-9]+");
			if (m.Success && int.TryParse(m.Value, out int v2) && v2 >= 5 && v2 <= 30)
			{
				return v2;
			}
		}
		catch
		{
		}
		return -1;
	}

	/// <summary>实际运行 java -version 解析主版本（对齐 PCL Java.CheckAsync 的版本探测方式）。</summary>
	private static int ProbeJavaMajor(string javaExe)
	{
		try
		{
			using Process p = new Process();
			p.StartInfo = new ProcessStartInfo
			{
				FileName = javaExe,
				Arguments = "-version",
				UseShellExecute = false,
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				CreateNoWindow = true
			};
			p.Start();
			string err = p.StandardError.ReadToEnd();
			if (!p.WaitForExit(8000))
			{
				try { p.Kill(); } catch { }
				return -1;
			}
			Match m = Regex.Match(err, "version \"(\\d+)(?:\\.(\\d+))?(?:[^\"]*)\"");
			if (m.Success)
			{
				int a = int.Parse(m.Groups[1].Value);
				if (a == 1 && m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out int b))
				{
					return b;
				}
				return a;
			}
		}
		catch
		{
		}
		return -1;
	}

	/// <summary>评估版本 JSON 的 rules：Windows 可用返回 true。</summary>
	private static bool RulesAllowWindows(JsonElement lib)
	{
		try
		{
			if (!lib.TryGetProperty("rules", out JsonElement rules) || rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() == 0)
			{
				return true;
			}
			bool allowed = false;
			foreach (JsonElement rule in rules.EnumerateArray())
			{
				string action = rule.TryGetProperty("action", out JsonElement actEl) ? (actEl.GetString() ?? "") : "";
				bool osMatch = true;
				if (rule.TryGetProperty("os", out JsonElement osEl))
				{
					string osName = osEl.TryGetProperty("name", out JsonElement nameEl) ? (nameEl.GetString() ?? "") : "";
					osMatch = string.Equals(osName, "windows", StringComparison.OrdinalIgnoreCase);
				}
				if (osMatch)
				{
					allowed = action.Equals("allow", StringComparison.OrdinalIgnoreCase);
				}
			}
			return allowed;
		}
		catch
		{
			return true;
		}
	}

	/// <summary>解析库/原生 jar 的本地绝对路径（mcRoot = .minecraft）。isNative=true 表示 windows natives 分类包（解包用，不进 classpath）。</summary>
	private static string ResolveLibPath(string mcRoot, JsonElement lib, out bool isNative)
	{
		isNative = false;
		try
		{
			// 新格式：downloads.artifact.path 相对 libraries 根
			if (lib.TryGetProperty("downloads", out JsonElement dl) && dl.TryGetProperty("artifact", out JsonElement art) && art.TryGetProperty("path", out JsonElement pathEl))
			{
				string rel = (pathEl.GetString() ?? "").Replace('/', Path.DirectorySeparatorChar);
				if (rel.Length > 0)
				{
					string abs = Path.IsPathRooted(rel) ? rel : Path.Combine(mcRoot, "libraries", rel);
					if (File.Exists(abs))
					{
						return abs;
					}
				}
			}
		}
		catch
		{
		}
		try
		{
			// 旧格式：name("group:artifact:ver") + natives.classifiers/natives.windows
			bool hasNativeKey = false;
			string? classifier = null;
			if (lib.TryGetProperty("natives", out JsonElement nat))
			{
				if (nat.ValueKind == JsonValueKind.Object && nat.TryGetProperty("windows", out JsonElement winEl))
				{
					hasNativeKey = true;
					classifier = winEl.GetString() ?? "natives-windows";
				}
				else if (nat.ValueKind == JsonValueKind.String && nat.GetString() == "windows")
				{
					hasNativeKey = true;
					classifier = "natives-windows";
				}
			}
			if (hasNativeKey)
			{
				isNative = true;
				string name = lib.TryGetProperty("name", out JsonElement nameEl) ? (nameEl.GetString() ?? "") : "";
				return AbsFromMaven(mcRoot, MavenRelative(name, classifier));
			}
			if (lib.TryGetProperty("name", out JsonElement n2))
			{
				return AbsFromMaven(mcRoot, MavenRelative(n2.GetString() ?? "", null));
			}
		}
		catch
		{
		}
		return "";
	}

	private static string AbsFromMaven(string mcRoot, string rel)
	{
		return (rel.Length == 0) ? "" : Path.Combine(mcRoot, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
	}

	/// <summary>由 maven 坐标转相对路径（需要版本根补全，调用方用 LibAbsPath 解析）。</summary>
	private static string MavenRelative(string coord, string? classifier)
	{
		try
		{
			string[] parts = coord.Split(':');
			if (parts.Length < 3)
			{
				return "";
			}
			string group = parts[0].Replace('.', '/');
			string artifact = parts[1];
			string version = parts[2];
			string ext = (parts.Length > 3 && parts[3].Length > 0) ? parts[3] : "jar";
			string file = (classifier != null && classifier.Length > 0)
				? artifact + "-" + version + "-" + classifier + "." + ext
				: artifact + "-" + version + "." + ext;
			return group + "/" + artifact + "/" + version + "/" + file;
		}
		catch
		{
			return "";
		}
	}

	private static void ExtractNatives(string jarPath, string destDir)
	{
		try
		{
			using ZipArchive zip = ZipFile.OpenRead(jarPath);
			foreach (ZipArchiveEntry entry in zip.Entries)
			{
				if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains('/'))
				{
					continue;
				}
				string dest = Path.Combine(destDir, entry.FullName);
				using Stream src = entry.Open();
				using FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write);
				src.CopyTo(fs);
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[PCL客户端] natives 解包失败 " + Path.GetFileName(jarPath) + " => " + ex.Message);
		}
	}
}

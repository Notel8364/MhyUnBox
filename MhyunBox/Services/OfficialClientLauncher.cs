using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MhyunBox.Services.BjdProxy;
using MhyunBox.Settings;

namespace MhyunBox.Services;

/// <summary>官方客户端（网易我的世界 1.20.1 / MCLDownload）脱盒启动结果。</summary>
public sealed class OfficialClientLaunchResult
{
	public bool Ok;

	public string Message = "";

	public int Pid;

	public string Root = "";

	/// <summary>完整命令行（已脱敏，仅用于日志排查）。</summary>
	public string CommandLine = "";
}

/// <summary>
/// 官方客户端「脱盒」启动器。
///
/// 原理（2026-11-19 实测确认）：
/// 1. 官方客户端本体 = C:\MCLDownload\Game\.minecraft 下的原版 1.20.1 jar + Forge 47.3.0 + 网易官方 mod；
/// 2. 官方启动器只是把完整 java 命令行拼好（含 --server/--port、--userProperties、-DToken 等）后再拉起 java；
/// 3. 把 --userPropertiesEx 的 GameType 从 2 改成其它值，客户端就不会向启动器索取网易 session 认证
///    （authlibWrapper.joinServerWrapper 直接 return），因此可以完全脱离网易盒子启动；
/// 4. 认证改由 MhyunBox 现有认证代理完成：客户端 --server 指向 127.0.0.1:&lt;代理端口&gt;，
///    代理侧用指定账号/角色做网易云认证并中转游戏流量。
///
/// 这样官方客户端（含官方 mod / 反作弊 native）可以在没有启动器的情况下进服。
/// </summary>
public static class OfficialClientLauncher
{
	private static readonly object Gate = new object();

	// 允许同时多开多个官方客户端，每个进程都记在列表里。
	private static readonly List<Process> _processes = new List<Process>();

	private static int LivePids(out List<int> pids)
	{
		pids = new List<int>();
		lock (Gate)
		{
			for (int i = _processes.Count - 1; i >= 0; i--)
			{
				Process p = _processes[i];
				try
				{
					if (p.HasExited)
					{
						_processes.RemoveAt(i);
						continue;
					}
					pids.Add(p.Id);
				}
				catch
				{
					_processes.RemoveAt(i);
				}
			}
			return pids.Count;
		}
	}

	/// <summary>是否已有由本程序拉起的官方客户端（多开时任一个存活即为 true）。</summary>
	public static bool IsRunning
	{
		get
		{
			return LivePids(out _) > 0;
		}
	}

	/// <summary>本程序拉起的存活客户端数量（多开）。</summary>
	public static int RunningCount
	{
		get
		{
			return LivePids(out _);
		}
	}

	public static int RunningPid
	{
		get
		{
			LivePids(out List<int> pids);
			return (pids.Count == 0) ? 0 : pids[pids.Count - 1];
		}
	}

	/// <summary>所有存活客户端 pid（用于界面显示多开状态）。</summary>
	public static IReadOnlyList<int> RunningPids
	{
		get
		{
			LivePids(out List<int> pids);
			return pids;
		}
	}

	/// <summary>查找官方客户端 .minecraft 根目录（含 versions\1.20\1.20.json 才算）。</summary>
	public static string? FindClientRoot(string? preferred = null)
	{
		List<string> candidates = new List<string>();
		if (!string.IsNullOrWhiteSpace(preferred))
		{
			candidates.Add(preferred.Trim());
		}
		string configured = SettingsManager.Instance.OfficialClientRoot ?? "";
		if (!string.IsNullOrWhiteSpace(configured))
		{
			candidates.Add(configured.Trim());
		}
		foreach (char drive in new char[4] { 'C', 'D', 'E', 'F' })
		{
			candidates.Add(drive + ":\\MCLDownload\\Game\\.minecraft");
		}
		foreach (string item in candidates)
		{
			try
			{
				string root = item.TrimEnd('\\');
				if (File.Exists(Path.Combine(root, "versions", "1.20", "1.20.json")))
				{
					return root;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	/// <summary>查找官方客户端自带的魔改 JRE（ext\jre-*\jdk*\bin\javaw.exe）。</summary>
	/// <summary>查找随客户端一起打包的 JRE（&lt;root&gt;\jre\jdk*\bin\javaw.exe）。</summary>
	public static string? FindBundledJava(string mcRoot)
	{
		try
		{
			string jreDir = Path.Combine(mcRoot, "jre");
			if (!Directory.Exists(jreDir)) return null;
			string[] hits = Directory.GetFiles(jreDir, "javaw.exe", SearchOption.AllDirectories);
			if (hits.Length == 0) hits = Directory.GetFiles(jreDir, "java.exe", SearchOption.AllDirectories);
			if (hits.Length == 0) return null;
			return hits.OrderByDescending((string h) => h.Contains("\\bin\\")).FirstOrDefault();
		}
		catch
		{
			return null;
		}
	}
	/// <summary>查找本机安装的 Java（JAVA_HOME / Program Files / PATH）。</summary>
	public static string? FindSystemJava()
	{
		List<string> homes = new List<string>();
		try
		{
			string env = Environment.GetEnvironmentVariable("JAVA_HOME") ?? "";
			if (env.Length != 0) homes.Add(env);
		}
		catch
		{
		}
		foreach (string baseDir in new string[3] { "C:\\Program Files\\Java", "C:\\Program Files\\Eclipse Adoptium", "C:\\Program Files\\Microsoft" })
		{
			try
			{
				if (!Directory.Exists(baseDir)) continue;
				foreach (string d in Directory.GetDirectories(baseDir))
				{
					if (d.Contains("jdk", StringComparison.OrdinalIgnoreCase) || d.Contains("jre", StringComparison.OrdinalIgnoreCase)) homes.Add(d);
				}
			}
			catch
			{
			}
		}
		try
		{
			string? path = Environment.GetEnvironmentVariable("PATH");
			if (!string.IsNullOrWhiteSpace(path))
			{
				foreach (string p in path.Split(';'))
				{
					if (p.Length == 0) continue;
					if (File.Exists(Path.Combine(p, "javaw.exe"))) homes.Add(Path.GetDirectoryName(p.TrimEnd('\\')) ?? p);
				}
			}
		}
		catch
		{
		}
		foreach (string home in homes)
		{
			foreach (string name in new string[2] { "javaw.exe", "java.exe" })
			{
				try
				{
					string cand = Path.Combine(home, "bin", name);
					if (File.Exists(cand)) return cand;
				}
				catch
				{
				}
			}
		}
		return null;
	}
	public static string? FindJavaExe(string mcRoot)
	{
		try
		{
			DirectoryInfo? game = Directory.GetParent(mcRoot.TrimEnd('\\'));
			DirectoryInfo? install = game?.Parent;
			if (install != null)
			{
				string ext = Path.Combine(install.FullName, "ext");
				if (Directory.Exists(ext))
				{
					string? jre = Directory.EnumerateDirectories(ext, "jre-*").OrderByDescending((string d) => d).FirstOrDefault();
					if (jre != null)
					{
						string[] names = new string[2] { "javaw.exe", "java.exe" };
						foreach (string name in names)
						{
							string[] hits = Directory.GetFiles(jre, name, SearchOption.AllDirectories);
							if (hits.Length != 0)
							{
								return hits.OrderByDescending((string h) => h.Contains("\\bin\\")).FirstOrDefault();
							}
						}
					}
				}
			}
		}
		catch
		{
		}
		// 退路：同目录下找 jre/jdk
		try
		{
			string[] hits2 = Directory.GetFiles(mcRoot, "javaw.exe", SearchOption.AllDirectories);
			if (hits2.Length != 0)
			{
				return hits2.FirstOrDefault((string h) => h.Contains("\\bin\\"));
			}
		}
		catch
		{
		}
		return null;
	}

	private static string Uuid32(string name)
	{
		byte[] hash = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
		hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
		hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	private static bool LooksLikeUuid32(string s)
	{
		return !string.IsNullOrWhiteSpace(s) && Regex.IsMatch(s.Trim(), "^[0-9a-fA-F]{32}$");
	}

	/// <summary>
	/// 启动官方客户端并让它连接本地代理端口。
	/// </summary>
	/// <param name="proxyPort">MhyunBox 认证代理的监听端口（客户端连 127.0.0.1:该端口）。</param>
	/// <param name="loginName">登录名（留空则用账号昵称）。</param>
	/// <param name="authAccountIndex">认证账号索引（用于取 UUID/uid；-1 表示按登录名匹配）。</param>
	/// <param name="clientRoot">官方客户端 .minecraft 根目录（留空自动查找）。</param>
	/// <param name="consoleJava">true 用 java.exe（带控制台，便于排查），默认 javaw.exe。</param>
	/// <param name="directAddress">非布吉岛预设的直连地址（host:port）；给出时优先于代理端口，留空且 proxyPort=0 时只拉起客户端停在主菜单。</param>
	public static OfficialClientLaunchResult Launch(int proxyPort, string? loginName = null, int authAccountIndex = -1, string? clientRoot = null, bool consoleJava = false, Action<string>? log = null, int gameType = 1, int launcherPort = 0, long uid = 0, string? token = null, string? javaHome = null, string? directAddress = null, string? memoryArg = null, string? extraJvmArgs = null)
	{
		OfficialClientLaunchResult result = new OfficialClientLaunchResult();
		try
		{
			// 多开：不再限制同时只能有一个客户端，各自连自己的目标地址。
			string direct = (directAddress ?? "").Trim();
			string targetHost = "";
			int targetPort = 0;
			if (direct.Length > 0)
			{
				if (!TryParseAddress(direct, out targetHost, out targetPort))
				{
					result.Message = "直连地址格式不正确（应为 host:port）：" + direct;
					return result;
				}
			}
			else if (proxyPort > 0)
			{
				if (proxyPort > 65535)
				{
					result.Message = "代理端口不合法：" + proxyPort;
					return result;
				}
				targetHost = "127.0.0.1";
				targetPort = proxyPort;
			}
			// 目标为空 = 只拉起客户端停在主菜单（非布吉岛预设且没保存直连地址时）
			bool hasTarget = targetPort > 0 && targetHost.Length > 0;
			string explicitRoot = (clientRoot ?? "").Trim().TrimEnd('\\');
			// 2026-11-20 修复「填了 PCL 1.12.2 客户端却仍启动官方 1.20.1」：
			// 用户显式填写的客户端路径拥有最高优先级——它本身是官方 1.20 格式才走官方启动，
			// 否则按 PCL / 标准客户端识别并用该路径启动；两者都不是就报错，绝不全局乱搜到别的客户端。
			if (explicitRoot.Length > 0 && launcherPort <= 0)
			{
				bool explicitIsOfficial = File.Exists(Path.Combine(explicitRoot, "versions", "1.20", "1.20.json"));
				if (!explicitIsOfficial)
				{
					PclClientInfo? explicitPcl = PclClientLauncher.Detect(explicitRoot);
					if (explicitPcl != null)
					{
						return LaunchViaPcl(explicitPcl, targetHost, targetPort, loginName, authAccountIndex, uid, javaHome, consoleJava, memoryArg, extraJvmArgs);
					}
					result.Message = "指定的客户端目录不是可启动的客户端（需要 versions\\<版本>\\<版本>.json 或 versions\\1.20\\1.20.json）：" + explicitRoot;
					LogHelper.Log("[启动] " + result.Message);
					return result;
				}
			}
			string? root = FindClientRoot(clientRoot);
			if (root == null && launcherPort <= 0)
			{
				// 官方 1.20.1 客户端未找到：回退 PCL / 通用 .minecraft 适配（1.12.2-Forge 等，元素之诗走 PCL 客户端）。
				// launcherPort>0 = 布吉岛假启动器路径，必须用官方客户端（网易 session），不走 PCL。
				PclClientInfo? pcl = PclClientLauncher.Detect(explicitRoot) ?? PclClientLauncher.Detect(SettingsManager.Instance.OfficialClientRoot ?? "");
				if (pcl != null)
				{
					return LaunchViaPcl(pcl, targetHost, targetPort, loginName, authAccountIndex, uid, javaHome, consoleJava, memoryArg, extraJvmArgs);
				}
				result.Message = "未找到官方客户端（需要 versions\\1.20\\1.20.json）或 PCL 客户端（versions\\<版本>\\<版本>.json）。可在设置里指定 .minecraft 目录";
				return result;
			}
			result.Root = root;
			string versionJson = Path.Combine(root, "versions", "1.20", "1.20.json");
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(versionJson));
			string jvmArgs = doc.RootElement.TryGetProperty("jvm_arguments", out JsonElement jvmEl) ? (jvmEl.GetString() ?? "") : "";
			string gameArgs = doc.RootElement.TryGetProperty("parameter_arguments", out JsonElement gameEl) ? (gameEl.GetString() ?? "") : "";
			if (jvmArgs.Length == 0 || gameArgs.Length == 0)
			{
				result.Message = "版本 JSON 缺少 jvm_arguments / parameter_arguments，不是官方启动器生成的版本文件";
				return result;
			}

			// 账号信息（只用于客户端本地显示 + userProperties，认证由代理完成）
			AccountStore.LocalAccount? account = null;
			IReadOnlyList<AccountStore.LocalAccount> all = AccountStore.All;
			if (authAccountIndex >= 0 && authAccountIndex < all.Count)
			{
				account = all[authAccountIndex];
			}
			string name = (loginName ?? "").Trim();
			if (name.Length == 0)
			{
				name = !string.IsNullOrWhiteSpace(account?.Nickname) ? account!.Nickname : "Player";
			}
			if (account == null)
			{
				account = all.FirstOrDefault((AccountStore.LocalAccount a) => string.Equals(a.Nickname, name, StringComparison.OrdinalIgnoreCase))
					?? all.FirstOrDefault((AccountStore.LocalAccount a) => a.RoleNames != null && a.RoleNames.Contains(name))
					?? all.FirstOrDefault();
			}
			// uid 未显式给出时，尝试从账号 entityId（数字）推导
			if (uid <= 0 && account != null && long.TryParse(account.EntityId, out long parsedUid) && parsedUid > 0)
			{
				uid = parsedUid;
			}
			string uuid = (account != null && LooksLikeUuid32(account.EntityId)) ? account.EntityId.Trim().ToUpperInvariant() : Uuid32(name).ToUpperInvariant();

			string? javaExe = null;
			string effectiveJavaHome = (javaHome ?? "").Trim();
			if (effectiveJavaHome.Length == 0)
			{
				effectiveJavaHome = (SettingsManager.Instance.OfficialClientJavaHome ?? "").Trim();
			}
			if (effectiveJavaHome.Length > 0)
			{
				foreach (string javaName in new string[2] { "javaw.exe", "java.exe" })
				{
					string cand = Path.Combine(effectiveJavaHome, "bin", javaName);
					if (File.Exists(cand))
					{
						javaExe = cand;
						break;
					}
				}
				if (javaExe == null)
				{
					string[] hits = Directory.GetFiles(effectiveJavaHome, "javaw.exe", SearchOption.AllDirectories);
					if (hits.Length != 0) javaExe = hits.OrderByDescending((string h) => h.Contains("\\bin\\")).FirstOrDefault();
				}
			}
			// 包内自带 JRE（<客户端目录>\jre\jdk*\bin\javaw.exe）最高优先级：与官方环境完全一致
			if (effectiveJavaHome.Length == 0 && javaExe == null)
			{
				javaExe = FindBundledJava(root);
				if (javaExe != null)
				{
					LogHelper.Log("[官方客户端] 使用包内自带 JRE：" + javaExe);
				}
			}
			// 指定的 JRE 不可用时回退到自动查找（本机/包内/系统 Java）
			javaExe ??= FindJavaExe(root);
			if (javaExe == null)
			{
				string? sysJava = FindSystemJava();
				if (sysJava != null)
				{
					javaExe = sysJava;
					LogHelper.Log("[官方客户端] 未在客户端目录找到 JRE，改用本机 Java：" + sysJava);
				}
			}
			if (javaExe == null)
			{
				result.Message = "未找到可用的 Java（请在「JRE 目录」里选择一个 JDK/JRE，或留空自动查找）";
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
			string natives = Path.Combine(root, "versions", "1.20", "natives");
			string runtime = Path.Combine(natives, "runtime");
			string filterPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Netease\\MCLauncher\\config\\fltmp";
			if (!Directory.Exists(filterPath))
			{
				filterPath = "";
			}

			// 1) JVM 参数：版本 JSON 模板 + 我们的覆盖（-D 重复时后出现者生效，必须插在主类之前）
			int lastSpace = jvmArgs.LastIndexOf(' ');
			string jvmPrefix = (lastSpace > 0) ? jvmArgs.Substring(0, lastSpace) : jvmArgs;
			string mainClass = (lastSpace > 0) ? jvmArgs.Substring(lastSpace + 1) : "";
			string ourJvm = string.Join(" ", new string[9]
			{
				memoryArg ?? "-Xmx2000M",
				"-Xmn128M",
				"-Djava.library.path=\"" + natives + "\"",
				"-Druntime_path=\"" + runtime + "\"",
				"-DlauncherControlPort=" + ((launcherPort > 0) ? launcherPort : 9),
				"-DlauncherGameId=" + BjdGate.GameId,
				"-DuserId=" + uid,
				"-DToken=" + (string.IsNullOrWhiteSpace(token) ? new string('0', 64) : token),
				"-DServer=RELEASE"
			});
			if (!string.IsNullOrWhiteSpace(extraJvmArgs))
			{
				ourJvm += " " + extraJvmArgs.Trim();
			}
			string finalJvm = jvmPrefix + " " + ourJvm;
			if (mainClass.Length != 0)
			{
				finalJvm += " " + mainClass;
			}

			// 2) 游戏参数：模板占位符替换 + 脱盒参数
			// 多开时每个目标用独立的 quickPlay 文件，避免两个客户端同时改写同一个文件。
			string quickPlayRel = Path.Combine("quickPlay", "java", "mhyunbox_quickplay_" + (hasTarget ? targetPort : 0) + ".json");
			string quickPlayAbs = Path.Combine(root, quickPlayRel);
			if (hasTarget && gameType < 2)
			{
				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(quickPlayAbs)!);
					string qp = "[{\"type\":\"multiplayer\",\"id\":\"" + targetHost + ":" + targetPort + "\",\"name\":\"MhyunBox\"," + "\"gamemode\":\"survival\"}]";
					File.WriteAllText(quickPlayAbs, qp, Encoding.UTF8);
				}
				catch
				{
				}
			}

			string game = gameArgs;
			game = game.Replace("${auth_player_name}", name);
			game = game.Replace("${auth_uuid}", uuid);
			game = game.Replace("${auth_access_token}", "0");
			game = Regex.Replace(game, "--quickPlayPath\\s+\\S+", "--quickPlayPath " + quickPlayRel);

			string userPropsJson = "{\"uid\":[" + uid + ",0],\"gameid\":[1,0],\"launcherport\":[" + ((launcherPort > 0) ? launcherPort : 9) + ",0],\"filterkey\":[\"nwxlygdrfanmnuwokuwuaynlwtpzfcrg\",\"0\"],\"filterpath\":[\"" + EscapeJson(filterPath) + "\",\"0\"],\"timedelta\":[0,0],\"launchversion\":[\"1.15.34.21425\",\"0\"]}";
			string userPropsEx = "{GameType:" + gameType + ",isFilter:true,channel:a50_sdk_cn,timedelta:0,launcherVersion:1.15.34.21425}";
			game += " --userProperties \"" + userPropsJson.Replace("\"", "\\\"") + "\"";
			game += " --userPropertiesEx " + userPropsEx;
			if (hasTarget)
			{
				game += " --server " + targetHost + " --port " + targetPort;
			}
			if (hasTarget && gameType < 2)
			{
				game += " --quickPlayMultiplayer " + targetHost + ":" + targetPort;
			}
			else
			{
				// NetGame（官方）模式下不允许走多人菜单/quickPlay；没有直连地址时也不自动进服。
				game = Regex.Replace(game, "--quickPlayPath\\s+\\S+", "");
			}

			string fullArgs = finalJvm + " " + game;
			string sanitized = Regex.Replace(fullArgs, "-DToken=[0-9a-fA-F]{8,}", "-DToken=***");
			result.CommandLine = sanitized;

			LogHelper.Log("[官方客户端] 脱盒启动：root=" + root + "，java=" + javaExe + "，登录名=" + name + "，uuid=" + uuid + "，目标=" + (hasTarget ? (targetHost + ":" + targetPort) : "主菜单"));
			log?.Invoke("官方客户端脱盒启动中…");
			if (SettingsManager.Instance.DebugMode)
			{
				LogHelper.Debug("[官方客户端] 完整命令行：" + sanitized);
			}

			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = javaExe,
				Arguments = fullArgs,
				WorkingDirectory = root,
				UseShellExecute = false,
				CreateNoWindow = false
			};
			Process p = new Process
			{
				StartInfo = psi,
				EnableRaisingEvents = true
			};
			p.Exited += delegate
			{
				try
				{
					LogHelper.Log("[官方客户端] 已退出（pid " + p.Id + "，退出码 " + p.ExitCode + "）");
				}
				catch
				{
				}
				lock (Gate)
				{
					_processes.Remove(p);
				}
			};
			bool started = p.Start();
			if (!started)
			{
				result.Message = "进程启动失败（java 未能创建）";
				return result;
			}
			lock (Gate)
			{
				_processes.Add(p);
			}
			result.Ok = true;
			result.Pid = p.Id;
			result.Message = "已启动官方客户端（pid " + p.Id + "）" + (hasTarget ? ("，目标 " + targetHost + ":" + targetPort) : "（未自动进服）") + "（登录名 " + name + "）";
			LogHelper.Log("[官方客户端] " + result.Message);
			log?.Invoke(result.Message);
			return result;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[官方客户端] 脱盒启动失败");
			result.Message = "启动失败：" + ex.Message;
			return result;
		}
	}

	/// <summary>经 PCL / 通用客户端启动：解析账号（登录名 / uuid / uid）后调 PclClientLauncher.Launch。
	/// 对齐 PCL 启动原理：由启动方拼好完整 java 命令行（classpath/natives/游戏参数）后直接拉起 java。</summary>
	private static OfficialClientLaunchResult LaunchViaPcl(PclClientInfo pcl, string targetHost, int targetPort, string? loginName, int authAccountIndex, long uid, string? javaHome, bool consoleJava, string? memoryArg = null, string? extraJvmArgs = null)
	{
		OfficialClientLaunchResult result = new OfficialClientLaunchResult();
		AccountStore.LocalAccount? pclAcc = null;
		IReadOnlyList<AccountStore.LocalAccount> pclAll = AccountStore.All;
		string pclName = (loginName ?? "").Trim();
		if (authAccountIndex >= 0 && authAccountIndex < pclAll.Count)
		{
			pclAcc = pclAll[authAccountIndex];
		}
		if (pclName.Length == 0)
		{
			pclName = !string.IsNullOrWhiteSpace(pclAcc?.Nickname) ? pclAcc!.Nickname : "Player";
		}
		long pclUid = uid;
		if (pclAcc == null)
		{
			pclAcc = pclAll.FirstOrDefault((AccountStore.LocalAccount a) => string.Equals(a.Nickname, pclName, StringComparison.OrdinalIgnoreCase))
				?? pclAll.FirstOrDefault((AccountStore.LocalAccount a) => a.RoleNames != null && a.RoleNames.Contains(pclName))
				?? pclAll.FirstOrDefault();
		}
		if (pclUid <= 0 && pclAcc != null && long.TryParse(pclAcc.EntityId, out long pclParsed) && pclParsed > 0)
		{
			pclUid = pclParsed;
		}
		string pclUuid = (pclAcc != null && LooksLikeUuid32(pclAcc.EntityId)) ? pclAcc.EntityId.Trim().ToUpperInvariant() : Uuid32(pclName).ToUpperInvariant();
		OfficialClientLaunchResult pr = PclClientLauncher.Launch(pcl, targetHost, targetPort, pclName, pclUuid, pclUid, javaHome, consoleJava, memoryArg, extraJvmArgs);
		result.Ok = pr.Ok;
		result.Message = pr.Message;
		result.Pid = pr.Pid;
		result.Root = pr.Root;
		result.CommandLine = pr.CommandLine;
		return result;
	}

	/// <summary>解析 host:port（也接受只写端口的形式，如 ":25565"）。</summary>
	private static bool TryParseAddress(string text, out string host, out int port)
	{
		host = "";
		port = 0;
		try
		{
			string s = (text ?? "").Trim();
			int idx = s.LastIndexOf(':');
			if (idx < 0)
			{
				return false;
			}
			host = s.Substring(0, idx).Trim();
			string portText = s.Substring(idx + 1).Trim();
			if (!int.TryParse(portText, out port) || port < 1 || port > 65535)
			{
				return false;
			}
			if (host.Length == 0)
			{
				host = "127.0.0.1";
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static string EscapeJson(string s)
	{
		return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}

	/// <summary>关闭由本程序拉起的全部官方客户端（多开时全部关闭）。</summary>
	public static void Stop()
	{
		List<Process> all;
		lock (Gate)
		{
			all = _processes.ToList();
			_processes.Clear();
		}
		foreach (Process p in all)
		{
			try
			{
				if (!p.HasExited)
				{
					LogHelper.Log("[官方客户端] 正在关闭（pid " + p.Id + "）");
					p.Kill(entireProcessTree: true);
					p.WaitForExit(8000);
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[官方客户端] 关闭失败");
			}
		}
	}
}

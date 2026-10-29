using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Settings;

namespace MhyunBox.Services;

/// <summary>扫盘找到的网易官方启动器安装。</summary>
public sealed class OfficialLauncherInfo
{
	public string LauncherExe = "";

	public string Directory = "";

	/// <summary>versionInfo 首行（启动器版本号）。</summary>
	public string Version = "";

	/// <summary>mcl.common.dll 存在（ComputeDynamicToken / HttpEncrypt 载体，WinLicense 壳）。</summary>
	public bool HasMclCommon;

	/// <summary>api-ms-win-crt-utility-l1-1-1.dll 存在（游戏 JNI 桥，WinLicense 壳）。</summary>
	public bool HasAuthBridgeDll;

	public bool IsRunning;

	public override string ToString()
	{
		return $"{LauncherExe} (版本 {Version}{(HasMclCommon ? ", mcl.common" : "")}{(HasAuthBridgeDll ? ", authdll" : "")})";
	}
}

/// <summary>运行中的游戏/启动器进程（命令行提取）。</summary>
public sealed class RunningGameInfo
{
	public int Pid;

	public string ProcessName = "";

	/// <summary>-DlauncherControlPort（JVM 参数里的控制端口）。</summary>
	public int ControlPort;

	/// <summary>
	/// userProperties.launcherport —— **游戏 NetworkSocket 实际连接的就是这个端口**
	/// （`new Socket("127.0.0.1", GameState.launcherport)`）。实测与 -DlauncherControlPort
	/// 可能不是同一个值，取盐握手应优先用它。
	/// </summary>
	public int LauncherPort;

	/// <summary>
	/// NetworkSocket 登录消息用的 short gameid（userProperties.gameid）。
	/// 注意：实测游戏常直接传 `[0,0]`，**0 是合法值**，不能用值是否为 0 判断有没有解析到。
	/// </summary>
	public int GameIdShort;

	/// <summary>gameid 是否真的从命令行里解析出来了（区别于"解析到但值就是 0"）。</summary>
	public bool HasGameId;

	/// <summary>-DlauncherGameId（64 位）。</summary>
	public long LauncherGameId;

	/// <summary>-DuserId。</summary>
	public int UserId;

	/// <summary>-DToken（32 字节登录态会话凭证）。</summary>
	public string Token = "";

	public string CommandLine = "";
}

/// <summary>扫描结果汇总。</summary>
public sealed class LauncherScanResult
{
	public List<OfficialLauncherInfo> Launchers = new List<OfficialLauncherInfo>();

	public List<RunningGameInfo> RunningGames = new List<RunningGameInfo>();

	/// <summary>x19 patchlist 最新启动器版本（主动更新检查）。</summary>
	public string PatchlistVersion = "";

	public string Summary = "";

	public OfficialLauncherInfo? FirstLauncher => Launchers.FirstOrDefault();

	public RunningGameInfo? FirstGameWithPort => RunningGames.FirstOrDefault((RunningGameInfo g) => g.LauncherPort > 0);
}

/// <summary>
/// 我的世界启动器扫盘 / 进程 / 控制端口 / 版本探测。
/// 路径来源：常见安装目录候选 + 注册表卸载项 + 固定盘浅层并行枚举；
/// 进程来源：PowerShell CIM 读取 java/javaw/WPFLauncher 命令行（提取 launcherControlPort / gameid / Token）。
/// </summary>
public static class LauncherScanner
{
	private static readonly TimeSpan ProcessQueryTimeout = TimeSpan.FromSeconds(10.0);

	private static readonly TimeSpan PatchlistTimeout = TimeSpan.FromSeconds(10.0);

	/// <summary>常见安装目录候选（{d} 为盘符）。</summary>
	private static readonly string[] DirCandidates = new string[16]
	{
		"{d}\\FeverApps\\MC", "{d}\\FeverApps\\MCLauncher", "{d}\\MCLDownload", "{d}\\网易\\我的世界",
		"{d}\\Netease\\MCLauncher", "{d}\\Program Files\\Netease\\MCLauncher", "{d}\\Program Files (x86)\\Netease\\MCLauncher",
		"{d}\\Program Files\\网易\\我的世界启动器", "{d}\\Program Files (x86)\\网易\\我的世界启动器",
		"{d}\\Games\\FeverApps\\MC", "{d}\\Games\\Netease\\MCLauncher", "{d}\\MCLauncher",
		"{d}\\FeverApps", "{d}\\MCL", "{d}\\Minecraft\\Netease", "{d}\\我的世界"
	};

	/// <summary>
	/// **只扫进程**（不扫盘、不拉 patchlist）——用于守候轮询，单次开销很小。
	/// 守候要在游戏进程出现的第一时间就抢连控制端口，不能每次都做全量扫盘。
	/// </summary>
	public static async Task<LauncherScanResult> ScanProcessesOnlyAsync(CancellationToken ct, Action<string>? log = null)
	{
		LauncherScanResult result = new LauncherScanResult();
		try
		{
			await ScanProcessesAsync(result, ct, log).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			log?.Invoke("[扫盘] 进程扫描异常: " + ex.Message);
		}
		return result;
	}

	public static async Task<LauncherScanResult> ScanAsync(CancellationToken ct, Action<string>? log = null)
	{
		LauncherScanResult result = new LauncherScanResult();
		try
		{
			await ScanProcessesAsync(result, ct, log).ConfigureAwait(false);
			ScanDirectories(result, log);
			await FetchPatchlistVersionAsync(result, ct, log).ConfigureAwait(false);
			StringBuilder sb = new StringBuilder();
			sb.Append("启动器安装 ").Append(result.Launchers.Count).Append(" 处：");
			foreach (OfficialLauncherInfo launcher in result.Launchers)
			{
				sb.Append("\n  · ").Append(launcher);
			}
			if (result.Launchers.Count == 0)
			{
				sb.Append("（未找到 WPFLauncher.exe）");
			}
			sb.Append("\n运行中游戏/启动器 ").Append(result.RunningGames.Count).Append(" 个");
			foreach (RunningGameInfo runningGame in result.RunningGames)
			{
				sb.Append("\n  · pid=").Append(runningGame.Pid).Append(' ').Append(runningGame.ProcessName)
					.Append(" 控制端口=").Append(runningGame.LauncherPort)
					.Append(" gameid=").Append(runningGame.GameIdShort)
					.Append(" userId=").Append(runningGame.UserId);
			}
			if (!string.IsNullOrEmpty(result.PatchlistVersion))
			{
				sb.Append("\nx19 patchlist 最新启动器版本：").Append(result.PatchlistVersion);
			}
			result.Summary = sb.ToString();
			log?.Invoke("[扫盘] " + result.Summary.Replace("\n", " | "));
		}
		catch (Exception ex)
		{
			result.Summary = "扫描异常: " + ex.Message;
			log?.Invoke("[扫盘] 异常: " + ex.Message);
		}
		return result;
	}

	// ---------- 进程扫描（PowerShell CIM） ----------

	private static async Task ScanProcessesAsync(LauncherScanResult result, CancellationToken ct, Action<string>? log)
	{
		List<(int Pid, string Name, string Cmd)> procs = await QueryProcessesAsync(ct).ConfigureAwait(false);
		foreach (var (pid, name, cmd) in procs)
		{
			if (string.IsNullOrEmpty(cmd))
			{
				continue;
			}
			bool isGame = name.Contains("java", StringComparison.OrdinalIgnoreCase);
			GameCommandInfo parsed = ParseGameCommandLine(cmd);
			int controlPort = parsed.ControlPort;
			int gameIdShort = parsed.GameId;
			bool hasGameId = parsed.HasGameId;
			int port = parsed.EffectivePort;
			long launcherGameId = parsed.LauncherGameId;
			int userId = parsed.UserId;
			string token = parsed.Token;
			bool relevant = isGame || name.Contains("wpf", StringComparison.OrdinalIgnoreCase);
			if (!relevant || (port == 0 && !isGame))
			{
				continue;
			}
			result.RunningGames.Add(new RunningGameInfo
			{
				Pid = pid,
				ProcessName = name,
				ControlPort = controlPort,
				LauncherPort = port,
				GameIdShort = gameIdShort,
				HasGameId = hasGameId,
				LauncherGameId = launcherGameId,
				UserId = userId,
				Token = token,
				CommandLine = cmd
			});
		}
		// 启动器进程也在运行时标记到 Launchers（进程在 = 正在监听控制端口的实例）
		log?.Invoke($"[扫盘] 进程扫描完成：{result.RunningGames.Count} 个相关进程");
	}

	/// <summary>从一条 javaw 命令行里解析出的游戏侧要素。</summary>
	public readonly struct GameCommandInfo
	{
		/// <summary>-DlauncherControlPort。</summary>
		public readonly int ControlPort;

		/// <summary>userProperties.launcherport（游戏 NetworkSocket 实际连的端口）。</summary>
		public readonly int GamePort;

		/// <summary>取盐要连的端口：游戏端口优先，拿不到才用控制端口。</summary>
		public readonly int EffectivePort;

		/// <summary>userProperties.gameid（short）。</summary>
		public readonly int GameId;

		/// <summary>gameid 是否真的解析出来了（区别于"解析到但值就是 0"）。</summary>
		public readonly bool HasGameId;

		public readonly long LauncherGameId;

		public readonly int UserId;

		public readonly string Token;

		public GameCommandInfo(int controlPort, int gamePort, int gameId, bool hasGameId, long launcherGameId, int userId, string token)
		{
			ControlPort = controlPort;
			GamePort = gamePort;
			EffectivePort = (gamePort > 0) ? gamePort : controlPort;
			GameId = gameId;
			HasGameId = hasGameId;
			LauncherGameId = launcherGameId;
			UserId = userId;
			Token = token;
		}
	}

	/// <summary>
	/// 解析 javaw 命令行，提取取盐需要的端口 / gameid / userId / Token。
	///
	/// 实测样本（2026-10-18 pid=14928）：
	/// <code>
	/// -DlauncherControlPort=12557 -DlauncherGameId=4634308743419409922 -DuserId=156937529
	/// --userProperties "{\"uid\":[156937529,0],\"gameid\":[0,0],\"launcherport\":[12303,0],...}"
	/// </code>
	/// 两个要点：
	/// 1. userProperties 里的值是**数组**（`[0,0]`），正则必须能吃下 `[`；
	/// 2. **gameid 的合法值就是 0**，所以判定"有没有解析到"要靠 HasGameId，不能靠 GameId != 0；
	/// 3. 游戏连的是 `launcherport`(12303)，与 `-DlauncherControlPort`(12557) 不是同一个，取盐要用前者。
	/// </summary>
	public static GameCommandInfo ParseGameCommandLine(string cmd)
	{
		int controlPort = 0;
		Match m = Regex.Match(cmd, "-DlauncherControlPort=(\\d+)");
		if (m.Success)
		{
			int.TryParse(m.Groups[1].Value, out controlPort);
		}
		// 关键：WMI/CIM 返回的命令行里 userProperties 的引号是**转义过的**，逐字符是
		//   \  "  g a m e i d  \  "  :  [  0  ,  0  ]
		// 也就是 \"gameid\":[0,0]（不是 "gameid":0）。所以必须按带反斜杠的形态先找，
		// 找不到再退回未转义形态。值本身可能是 0，判定"解析到没有"要看 hasGameId。
		int gameId = 0;
		bool hasGameId = false;
		Match m2 = Regex.Match(cmd, @"\\""gameid\\""\s*:\s*\[?\s*(\d+)");
		if (!m2.Success)
		{
			m2 = Regex.Match(cmd, @"""gameid""\s*:\s*\[?\s*(\d+)");
		}
		if (m2.Success)
		{
			int.TryParse(m2.Groups[1].Value, out gameId);
			hasGameId = true;
		}
		int gamePort = 0;
		Match m2b = Regex.Match(cmd, @"\\""launcherport\\""\s*:\s*\[?\s*(\d+)");
		if (!m2b.Success)
		{
			m2b = Regex.Match(cmd, @"""launcherport""\s*:\s*\[?\s*(\d+)");
		}
		if (m2b.Success)
		{
			int.TryParse(m2b.Groups[1].Value, out gamePort);
		}
		long launcherGameId = 0L;
		Match m4 = Regex.Match(cmd, "-DlauncherGameId=(\\d+)");
		if (m4.Success)
		{
			long.TryParse(m4.Groups[1].Value, out launcherGameId);
		}
		int userId = 0;
		Match m5 = Regex.Match(cmd, "-DuserId=(\\d+)");
		if (m5.Success)
		{
			int.TryParse(m5.Groups[1].Value, out userId);
		}
		string token = "";
		Match m6 = Regex.Match(cmd, "-DToken=([0-9A-Fa-f]{16,64})");
		if (m6.Success)
		{
			token = m6.Groups[1].Value;
		}
		return new GameCommandInfo(controlPort, gamePort, gameId, hasGameId, launcherGameId, userId, token);
	}

	private static async Task<List<(int, string, string)>> QueryProcessesAsync(CancellationToken ct)
	{
		List<(int, string, string)> list = new List<(int, string, string)>();
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo("powershell.exe")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			psi.ArgumentList.Add("-NoProfile");
			psi.ArgumentList.Add("-NonInteractive");
			psi.ArgumentList.Add("-WindowStyle");
			psi.ArgumentList.Add("Hidden");
			psi.ArgumentList.Add("-Command");
			psi.ArgumentList.Add("Get-CimInstance Win32_Process -Filter \"Name='javaw.exe' OR Name='java.exe' OR Name='WPFLauncher.exe'\" | Select-Object ProcessId,Name,CommandLine | ConvertTo-Json -Compress -Depth 2");
			using Process proc = Process.Start(psi);
			if (proc == null)
			{
				return list;
			}
			string output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
			if (!proc.WaitForExit((int)ProcessQueryTimeout.TotalMilliseconds))
			{
				try
				{
					proc.Kill();
				}
				catch
				{
				}
				return list;
			}
			output = output.Trim();
			if (string.IsNullOrEmpty(output))
			{
				return list;
			}
			JsonElement root = JsonSerializer.Deserialize<JsonElement>(output);
			if (root.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in root.EnumerateArray())
				{
					int pid = item.TryGetProperty("ProcessId", out var p) ? p.GetInt32() : 0;
					string name = item.TryGetProperty("Name", out var n) ? (n.GetString() ?? "") : "";
					string cmd = item.TryGetProperty("CommandLine", out var c) ? (c.GetString() ?? "") : "";
					list.Add((pid, name, cmd));
				}
			}
			else if (root.ValueKind == JsonValueKind.Object)
			{
				int pid2 = root.TryGetProperty("ProcessId", out var p2) ? p2.GetInt32() : 0;
				string name2 = root.TryGetProperty("Name", out var n2) ? (n2.GetString() ?? "") : "";
				string cmd2 = root.TryGetProperty("CommandLine", out var c2) ? (c2.GetString() ?? "") : "";
				list.Add((pid2, name2, cmd2));
			}
		}
		catch
		{
			// PowerShell 不可用等情况：静默降级为仅目录扫描
		}
		return list;
	}

	// ---------- 目录扫描 ----------

	private static void ScanDirectories(LauncherScanResult result, Action<string>? log)
	{
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<string> candidates = new List<string>();
		// 每个固定盘符都展开候选目录
		List<string> roots = new List<string> { "C:" };
		try
		{
			foreach (DriveInfo d in DriveInfo.GetDrives())
			{
				if (d.IsReady && d.DriveType == DriveType.Fixed)
				{
					string r = d.RootDirectory.FullName.TrimEnd('\\');
					if (!roots.Contains(r, StringComparer.OrdinalIgnoreCase))
					{
						roots.Add(r);
					}
				}
			}
		}
		catch
		{
		}
		foreach (string tpl in DirCandidates)
		{
			foreach (string root in roots)
			{
				candidates.Add(tpl.Replace("{d}", root));
			}
		}
		candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Netease", "MCLauncher"));
		candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Netease", "MCLauncher"));
		candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Netease", "MCLauncher"));
		foreach (string dir in EnumerateRegistryUninstallPaths())
		{
			if (!string.IsNullOrWhiteSpace(dir))
			{
				candidates.Add(dir);
			}
		}
		// 记住上次成功路径，优先复扫
		try
		{
			string remembered = MhyunBox.Settings.SettingsManager.Instance?.OfficialLauncherPath ?? "";
			if (!string.IsNullOrWhiteSpace(remembered) && !candidates.Contains(remembered))
			{
				candidates.Insert(0, remembered);
			}
		}
		catch
		{
		}
		foreach (string dir2 in candidates)
		{
			if (string.IsNullOrWhiteSpace(dir2) || seen.Contains(dir2) || !Directory.Exists(dir2))
			{
				continue;
			}
			seen.Add(dir2);
			TryAddLauncher(result, dir2);
			if (Directory.Exists(Path.Combine(dir2, "MC")))
			{
				TryAddLauncher(result, Path.Combine(dir2, "MC"));
			}
		}
		// 固定盘浅层并行枚举（限深度 3、限时 6s、单层并行）
		if (result.Launchers.Count == 0)
		{
			ShallowDriveScan(result, seen, log);
		}
		log?.Invoke($"[扫盘] 目录扫描完成：{result.Launchers.Count} 处官方启动器");
	}

	private static string Expand(string tpl)
	{
		return tpl.Replace("{d}", "C:");
	}

	private static int CompareVersionLocal(string a, string b)
	{
		try
		{
			int[] x = Array.ConvertAll(a.Split('.'), int.Parse);
			int[] y = Array.ConvertAll(b.Split('.'), int.Parse);
			int n = Math.Max(x.Length, y.Length);
			for (int i = 0; i < n; i++)
			{
				int dx = (i < x.Length) ? x[i] : 0;
				int dy = (i < y.Length) ? y[i] : 0;
				int c = dx.CompareTo(dy);
				if (c != 0)
				{
					return c;
				}
			}
			return 0;
		}
		catch
		{
			return string.CompareOrdinal(a, b);
		}
	}

	private static IEnumerable<string> EnumerateRegistryUninstallPaths()
	{
		List<string> paths = new List<string>();
		string[] roots = new string[2]
		{
			"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
			"SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall"
		};
		Microsoft.Win32.RegistryKey[] views = new Microsoft.Win32.RegistryKey[2]
		{
			Microsoft.Win32.Registry.LocalMachine,
			Microsoft.Win32.Registry.CurrentUser
		};
		Microsoft.Win32.RegistryKey[] array = views;
		foreach (Microsoft.Win32.RegistryKey hive in array)
		{
			string[] array2 = roots;
			foreach (string sub in array2)
			{
				try
				{
					using Microsoft.Win32.RegistryKey key = hive.OpenSubKey(sub);
					if (key == null)
					{
						continue;
					}
					foreach (string name in key.GetSubKeyNames())
					{
						try
						{
							using Microsoft.Win32.RegistryKey app = key.OpenSubKey(name);
							if (app == null)
							{
								continue;
							}
							string displayName = (app.GetValue("DisplayName") as string) ?? "";
							if (displayName.IndexOf("我的世界", StringComparison.OrdinalIgnoreCase) >= 0 || displayName.IndexOf("Minecraft", StringComparison.OrdinalIgnoreCase) >= 0 || displayName.IndexOf("网易", StringComparison.OrdinalIgnoreCase) >= 0)
							{
								string loc = (app.GetValue("InstallLocation") as string) ?? (app.GetValue("InstallSource") as string) ?? "";
								if (!string.IsNullOrWhiteSpace(loc))
								{
									paths.Add(loc);
								}
							}
						}
						catch
						{
						}
					}
				}
				catch
				{
				}
			}
		}
		return paths;
	}

	private static void ShallowDriveScan(LauncherScanResult result, HashSet<string> seen, Action<string>? log)
	{
		try
		{
			DriveInfo[] drives = DriveInfo.GetDrives();
			DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6.0);
			List<Task> tasks = new List<Task>();
			DriveInfo[] array = drives;
			foreach (DriveInfo d in array)
			{
				if (!d.IsReady || d.DriveType != DriveType.Fixed)
				{
					continue;
				}
				string root = d.RootDirectory.FullName;
				tasks.Add(Task.Run(delegate
				{
					try
					{
						if (DateTime.UtcNow > deadline)
						{
							return;
						}
						foreach (string dir3 in Directory.EnumerateDirectories(root))
						{
							if (DateTime.UtcNow > deadline)
							{
								break;
							}
							try
							{
								if (File.Exists(Path.Combine(dir3, "WPFLauncher.exe")))
								{
									TryAddLauncher(result, dir3);
									continue;
								}
								foreach (string sub in Directory.EnumerateDirectories(dir3))
								{
									if (DateTime.UtcNow > deadline)
									{
										break;
									}
									try
									{
										if (File.Exists(Path.Combine(sub, "WPFLauncher.exe")))
										{
											TryAddLauncher(result, sub);
										}
										else if (File.Exists(Path.Combine(Path.Combine(sub, "MC"), "WPFLauncher.exe")))
										{
											TryAddLauncher(result, Path.Combine(sub, "MC"));
										}
									}
									catch
									{
									}
								}
							}
							catch
							{
							}
						}
					}
					catch
					{
					}
				}, CancellationToken.None));
			}
			Task.WhenAll(tasks).Wait(7000);
		}
		catch
		{
		}
	}

	private static void TryAddLauncher(LauncherScanResult result, string dir)
	{
		try
		{
			string exe = Path.Combine(dir, "WPFLauncher.exe");
			if (!File.Exists(exe))
			{
				return;
			}
			// 扫盘/注册表/路径候选多个来源会命中同一目录，按 exe 路径去重（否则 UI 会列出 3 份相同安装）
			foreach (OfficialLauncherInfo existing in result.Launchers)
			{
				if (string.Equals(existing.LauncherExe, exe, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
			}
			OfficialLauncherInfo info = new OfficialLauncherInfo
			{
				LauncherExe = exe,
				Directory = dir,
				Version = ReadLauncherVersion(dir),
				HasMclCommon = File.Exists(Path.Combine(dir, "mcl.common.dll")),
				HasAuthBridgeDll = File.Exists(Path.Combine(dir, "api-ms-win-crt-utility-l1-1-1.dll"))
			};
			foreach (RunningGameInfo runningGame in result.RunningGames)
			{
				if (runningGame.ProcessName.Contains("wpf", StringComparison.OrdinalIgnoreCase))
				{
					info.IsRunning = true;
				}
			}
			result.Launchers.Add(info);
			try
			{
				MhyunBox.Settings.AppSettings? cfg = MhyunBox.Settings.SettingsManager.Instance;
				if (cfg != null && cfg.OfficialLauncherPath != exe)
				{
					cfg.OfficialLauncherPath = exe;
					MhyunBox.Settings.SettingsManager.Save(cfg);
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

	/// <summary>
	/// 读官方启动器版本号：**WPFLauncher.exe 的文件版本优先**，versionInfo 首行仅作回退。
	///
	/// 实测（2026-10-18）：官方更新器（`Updater\MCLauncherUpdater.exe`）更新时只替换
	/// WPFLauncher.exe 等文件，**不会刷新 versionInfo**。本机 exe 已是 1.15.30.18895
	/// （10-16 更新），而 versionInfo 仍停在 10-06 的 1.15.10.27269，
	/// 若以 versionInfo 为准就会误报版本落后、并让盐↔版本配对用上错误的版本号。
	/// </summary>
	public static string ReadLauncherVersion(string dir)
	{
		try
		{
			string exe = Path.Combine(dir, "WPFLauncher.exe");
			if (File.Exists(exe))
			{
				FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(exe);
				string v = (fvi.FileVersion ?? "").Trim();
				if (Regex.IsMatch(v, "^\\d+\\.\\d+\\.\\d+\\.\\d+$"))
				{
					return v;
				}
				string pv = (fvi.ProductVersion ?? "").Trim();
				if (Regex.IsMatch(pv, "^\\d+\\.\\d+\\.\\d+\\.\\d+$"))
				{
					return pv;
				}
			}
			string vi = Path.Combine(dir, "versionInfo");
			if (File.Exists(vi))
			{
				string first = (File.ReadAllLines(vi).FirstOrDefault() ?? "").Trim();
				if (Regex.IsMatch(first, "^\\d+\\.\\d+\\.\\d+\\.\\d+$"))
				{
					return first;
				}
			}
		}
		catch
		{
		}
		return "";
	}

	// ---------- 主动触发官方更新 ----------

	/// <summary>官方自更新程序文件名（位于启动器安装目录下）。</summary>
	public const string UpdaterRelativePath = "Updater\\MCLauncherUpdater.exe";

	/// <summary>官方启动器主程序文件名。</summary>
	public const string LauncherExeName = "WPFLauncher.exe";

	/// <summary>发烧游戏平台主程序文件名。</summary>
	public const string FeverPlatformExeName = "FeverGamesLauncher.exe";

	/// <summary>发烧游戏平台常见安装目录候选（{d} 为盘符）。</summary>
	private static readonly string[] FeverDirCandidates = new string[5]
	{
		"{d}\\FeverGames", "{d}\\FeverApps\\FeverGames", "{d}\\Program Files\\FeverGames",
		"{d}\\Program Files (x86)\\FeverGames", "{d}\\Games\\FeverGames"
	};

	/// <summary>
	/// 查找发烧游戏平台主程序（FeverGamesLauncher.exe）。
	/// 来源顺序：设置记忆 → 候选目录 → 桌面/公共桌面快捷方式（解析 .lnk 指向）。
	/// 网易现在的启动顺序是：先起发烧平台 → 再起我的世界启动器，所以取盐前必须先把它拉起来。
	/// </summary>
	public static string? FindFeverPlatform()
	{
		try
		{
			string saved = MhyunBox.Settings.SettingsManager.Instance?.FeverPlatformPath ?? "";
			if (!string.IsNullOrEmpty(saved) && File.Exists(saved))
			{
				return saved;
			}
		}
		catch
		{
		}
		foreach (string d in GetFixedDrives())
		{
			foreach (string candidate in FeverDirCandidates)
			{
				string exe = Path.Combine(candidate.Replace("{d}", d), FeverPlatformExeName);
				if (File.Exists(exe))
				{
					RememberFeverPlatform(exe);
					return exe;
				}
			}
		}
		// 桌面快捷方式：用户常把平台/启动器放桌面，从 .lnk 反解真实目标
		foreach (string dir in ShortcutDirs)
		{
			if (!Directory.Exists(dir))
			{
				continue;
			}
			string[] lnks;
			try
			{
				lnks = Directory.GetFiles(dir, "*.lnk");
			}
			catch
			{
				continue;
			}
			foreach (string lnk in lnks)
			{
				string name = Path.GetFileNameWithoutExtension(lnk);
				if (!(name.Contains("发烧", StringComparison.OrdinalIgnoreCase)
					|| name.Contains("Fever", StringComparison.OrdinalIgnoreCase)
					|| name.Contains("我的世界", StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}
				string? target = ResolveShortcutTarget(lnk);
				if (string.IsNullOrEmpty(target) || !File.Exists(target))
				{
					continue;
				}
				if (Path.GetFileName(target).Equals(FeverPlatformExeName, StringComparison.OrdinalIgnoreCase))
				{
					RememberFeverPlatform(target);
					return target;
				}
				// 快捷方式若直接指向我的世界启动器，顺带记住它
				if (Path.GetFileName(target).Equals(LauncherExeName, StringComparison.OrdinalIgnoreCase))
				{
					try
					{
						AppSettings cfg = MhyunBox.Settings.SettingsManager.Instance;
						if (cfg.OfficialLauncherPath != target)
						{
							cfg.OfficialLauncherPath = target;
							MhyunBox.Settings.SettingsManager.Save(cfg);
						}
					}
					catch
					{
					}
				}
			}
		}
		return null;
	}

	private static readonly string[] ShortcutDirs = new string[2]
	{
		Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory))
	};

	private static void RememberFeverPlatform(string exe)
	{
		try
		{
			AppSettings cfg = MhyunBox.Settings.SettingsManager.Instance;
			if (cfg.FeverPlatformPath != exe)
			{
				cfg.FeverPlatformPath = exe;
				MhyunBox.Settings.SettingsManager.Save(cfg);
			}
		}
		catch
		{
		}
	}

	/// <summary>解析 .lnk 快捷方式的目标路径（经 PowerShell COM，避免引入 COM 互操作依赖）。</summary>
	public static string? ResolveShortcutTarget(string lnkPath)
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
			psi.ArgumentList.Add($"(New-Object -ComObject WScript.Shell).CreateShortcut(\"{lnkPath}\").TargetPath");
			using Process p = Process.Start(psi);
			if (p == null)
			{
				return null;
			}
			string outp = p.StandardOutput.ReadToEnd().Trim();
			p.WaitForExit(8000);
			return string.IsNullOrEmpty(outp) ? null : outp;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>发烧游戏平台是否正在运行。</summary>
	public static bool IsFeverPlatformRunning()
	{
		try
		{
			return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(FeverPlatformExeName)).Length > 0;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>拉起发烧游戏平台。</summary>
	public static (bool Ok, string Message, string? ExePath) LaunchFeverPlatform()
	{
		try
		{
			string? exe = FindFeverPlatform();
			if (string.IsNullOrEmpty(exe))
			{
				return (false, "未找到 " + FeverPlatformExeName + "（已扫候选目录与桌面快捷方式），请手动打开发烧平台", null);
			}
			ProcessStartInfo psi = new ProcessStartInfo(exe)
			{
				WorkingDirectory = Path.GetDirectoryName(exe),
				UseShellExecute = true
			};
			Process.Start(psi);
			return (true, "已拉起发烧平台 " + exe, exe);
		}
		catch (Exception ex)
		{
			return (false, "拉起发烧平台失败: " + ex.GetType().Name + ": " + ex.Message, null);
		}
	}

	private static List<string> GetFixedDrives()
	{
		List<string> list = new List<string>();
		try
		{
			foreach (DriveInfo d in DriveInfo.GetDrives())
			{
				if (d.DriveType == DriveType.Fixed && d.IsReady)
				{
					list.Add(d.Name.TrimEnd('\\'));
				}
			}
		}
		catch
		{
		}
		return list;
	}

	/// <summary>
	/// 官方 DLL 取盐的前置条件提示。
	/// 实测：直接点启动器左下角「开始游戏」会直接和服务器断开连接，拿不到控制端口；
	/// 必须走「游戏中心 → 服务器 → 跳转运行游戏」才会建立 launcherControlPort 链路。
	/// </summary>
	public const string NeedGameProcessHint =
		"未发现运行中的游戏进程，需要先让游戏真正跑起来：打开官方启动器 → 游戏中心 → 选一个服务器 → 点「跳转运行游戏」。"
		+ "注意：不要点启动器左下角的「开始游戏」，那样会直接和服务器断开连接。";

	/// <summary>拉起网易官方启动器主程序（WPFLauncher.exe）。</summary>
	/// <returns>(是否拉起, 说明, exe 路径)</returns>
	public static (bool Ok, string Message, string? ExePath) LaunchOfficialLauncher(string? launcherExe = null)
	{
		try
		{
			string exe = launcherExe ?? "";
			if (string.IsNullOrEmpty(exe))
			{
				exe = MhyunBox.Settings.SettingsManager.Instance?.OfficialLauncherPath ?? "";
			}
			if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
			{
				return (false, "未找到 " + LauncherExeName + "，请先执行一次扫描", null);
			}
			ProcessStartInfo psi = new ProcessStartInfo(exe)
			{
				WorkingDirectory = Path.GetDirectoryName(exe),
				UseShellExecute = true
			};
			Process.Start(psi);
			return (true, "已拉起官方启动器 " + exe, exe);
		}
		catch (Exception ex)
		{
			return (false, "拉起失败: " + ex.GetType().Name + ": " + ex.Message, null);
		}
	}

	/// <summary>官方启动器主程序是否正在运行。</summary>
	public static bool IsLauncherRunning()
	{
		try
		{
			Process[] all = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(LauncherExeName));
			return all.Length > 0;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// 主动触发网易官方启动器的自我更新：直接拉起 <c>&lt;安装目录&gt;\Updater\MCLauncherUpdater.exe</c>。
	/// 更新器会自己去 X19 配置层取 patchlist / gray 配置并把启动器升到最新版，
	/// 版本号变化后本程序再扫盘即可读到新版本（盐值与版本号是成对校验的，版本必须跟上）。
	/// </summary>
	/// <returns>(是否拉起成功, 说明, 更新器路径)</returns>
	public static (bool Ok, string Message, string? UpdaterPath) TriggerOfficialUpdate(string? launcherExe = null)
	{
		try
		{
			string? dir = null;
			if (!string.IsNullOrEmpty(launcherExe))
			{
				dir = Path.GetDirectoryName(launcherExe);
			}
			if (string.IsNullOrEmpty(dir))
			{
				try
				{
					dir = MhyunBox.Settings.SettingsManager.Instance?.OfficialLauncherPath;
					dir = string.IsNullOrEmpty(dir) ? null : Path.GetDirectoryName(dir);
				}
				catch
				{
					dir = null;
				}
			}
			if (string.IsNullOrEmpty(dir))
			{
				return (false, "未知官方启动器安装目录，请先执行一次扫描", null);
			}
			string updater = Path.Combine(dir!, UpdaterRelativePath);
			if (!File.Exists(updater))
			{
				return (false, "该目录没有 " + UpdaterRelativePath + "，无法主动触发更新（目录：" + dir + "）", null);
			}
			ProcessStartInfo psi = new ProcessStartInfo(updater)
			{
				WorkingDirectory = dir,
				UseShellExecute = true
			};
			Process.Start(psi);
			return (true, "已拉起官方更新器 " + updater, updater);
		}
		catch (Exception ex)
		{
			return (false, "触发更新失败: " + ex.GetType().Name + ": " + ex.Message, null);
		}
	}

	// ---------- 主动更新检查（patchlist） ----------

	private static async Task FetchPatchlistVersionAsync(LauncherScanResult result, CancellationToken ct, Action<string>? log)
	{
		try
		{
			using HttpClient http = new HttpClient
			{
				Timeout = PatchlistTimeout
			};
			http.DefaultRequestHeaders.UserAgent.ParseAdd("WPFLauncher/0.0.0.0");
			string body = await http.GetStringAsync("https://x19.update.netease.com/pl/x19_java_patchlist", ct).ConfigureAwait(false);
			string? best = null;
			foreach (Match item in Regex.Matches(body, "\"(\\d+\\.\\d+\\.\\d+\\.\\d+)\""))
			{
				string value = item.Groups[1].Value;
				if (best == null || CompareVersionLocal(value, best) > 0)
				{
					best = value;
				}
			}
			result.PatchlistVersion = best ?? "";
		}
		catch (Exception ex)
		{
			log?.Invoke("[扫盘] patchlist 获取失败: " + ex.GetType().Name + ": " + ex.Message);
		}
	}
}
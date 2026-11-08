using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Settings;

namespace MhyunBox.Services;

// 行情模组 token 自动同步：
// 元素之诗「交易行行情」mod（dev.marketprices）把同步配置放在游戏目录下的
// market-prices\sync.txt（Properties 格式，含 backend= / token= 等键）。
// 玩家在 sync.txt 里填好 token 后，本服务自动把它同步给 MarisaBot 机器人
// （写入 C:\MarisaBot\secrets\market.token），供机器人走行情站 JSON 接口。
// 找不到 token 时静默跳过，不影响任何现有流程。
public static class MarketTokenService
{
	// 机器人所在目录（secrets\market.token 的写入目标）
	private static readonly string[] BotDirs = new string[1] { @"C:\MarisaBot" };

	// 除了设置里的 PCL 路径外，额外浅扫描这些根目录下的 .minecraft
	private static readonly string[] ExtraRoots = new string[1] { @"C:\Notel_Exe" };

	private const int ScanMaxDepth = 6;

	private const int PeriodMs = 600000;

	private static Timer? _timer;

	private static string? _lastSyncedToken;

	private static bool _loggedEmptySync;

	public static void StartBackgroundSync()
	{
		if (_timer != null)
		{
			return;
		}
		_timer = new Timer(delegate
		{
			try
			{
				SyncAsync().GetAwaiter().GetResult();
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[行情token] 后台同步异常");
			}
		}, null, 8000, PeriodMs);
		LogHelper.Log("[行情token] 后台同步已启动（每 10 分钟扫描一次 market-prices\\sync.txt）");
	}

	public static async Task SyncAsync()
	{
		string token = await Task.Run(delegate
		{
			string source;
			bool syncFileExists;
			return FindToken(out source, out syncFileExists);
		}).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(token))
		{
			bool emptySyncLogged = await Task.Run(delegate
			{
				string source2;
				bool syncFileExists2;
				FindToken(out source2, out syncFileExists2);
				return syncFileExists2;
			}).ConfigureAwait(false);
			if (emptySyncLogged && !_loggedEmptySync)
			{
				_loggedEmptySync = true;
				LogHelper.Log("[行情token] 已找到 market-prices\\sync.txt，但 token 为空 —— 请把分享者提供的 token 填进 sync.txt 的 token= 行，10 分钟内会自动同步给机器人");
			}
			if (_lastSyncedToken != null)
			{
				LogHelper.Log("[行情token] 实例里已找不到有效 token（sync.txt 被清空或移除）");
				_lastSyncedToken = null;
			}
			return;
		}
		if (string.Equals(_lastSyncedToken, token, StringComparison.Ordinal))
		{
			return;
		}
		foreach (string botDir in BotDirs)
		{
			try
			{
				string secretsDir = Path.Combine(botDir, "secrets");
				Directory.CreateDirectory(secretsDir);
				string path = Path.Combine(secretsDir, "market.token");
				string old = (File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "").Trim();
				if (string.Equals(old, token, StringComparison.Ordinal))
				{
					LogHelper.Log("[行情token] token 未变化，跳过写入");
				}
				else
				{
					File.WriteAllText(path, token + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
					LogHelper.Log($"[行情token] 已写入 {path}（长度 {token.Length}）");
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[行情token] 写入失败: " + botDir);
			}
		}
		_lastSyncedToken = token;
	}

	// 返回 "token" 或空串；sourceOut 输出来源路径；syncFileExistsOut 表示找到了 sync.txt（哪怕 token 为空）
	private static string FindToken(out string source, out bool syncFileExists)
	{
		source = "";
		syncFileExists = false;
		foreach (string mcDir in CandidateMinecraftDirs())
		{
			foreach (string candidate in new string[2]
			{
				Path.Combine(mcDir, "market-prices", "sync.txt"),
				Path.Combine(mcDir, "Market-prices", "sync.txt")
			})
			{
				if (!syncFileExists && File.Exists(candidate))
				{
					syncFileExists = true;
				}
				string token = ReadTokenFromSyncFile(candidate);
				if (!string.IsNullOrWhiteSpace(token))
				{
					source = candidate;
					return token;
				}
			}
			try
			{
				string versionsDir = Path.Combine(mcDir, "versions");
				if (!Directory.Exists(versionsDir))
				{
					continue;
				}
				foreach (DirectoryInfo item in new DirectoryInfo(versionsDir).EnumerateDirectories())
				{
					string candidate2 = Path.Combine(item.FullName, "market-prices", "sync.txt");
					if (!syncFileExists && File.Exists(candidate2))
					{
						syncFileExists = true;
					}
					string token2 = ReadTokenFromSyncFile(candidate2);
					if (!string.IsNullOrWhiteSpace(token2))
					{
						source = candidate2;
						return token2;
					}
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[行情token] 枚举 versions 失败: " + mcDir);
			}
		}
		return "";
	}

	private static string ReadTokenFromSyncFile(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return "";
			}
			string[] array = File.ReadAllLines(path, Encoding.UTF8);
			foreach (string line in array)
			{
				string text = line.Trim();
				if (text.StartsWith("#") || !text.StartsWith("token=", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				return text.Substring("token=".Length).Trim();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[行情token] 读取失败: " + path);
		}
		return "";
	}

	private static IEnumerable<string> CandidateMinecraftDirs()
	{
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<string> list = new List<string>();
		Action<string?> add = delegate(string? mc)
		{
			if (!string.IsNullOrWhiteSpace(mc) && Directory.Exists(mc) && seen.Add(mc))
			{
				list.Add(mc);
			}
		};
		try
		{
			AppSettings instance = SettingsManager.Instance;
			string? pclPath = instance.PclPath;
			if (!string.IsNullOrWhiteSpace(pclPath) && File.Exists(pclPath))
			{
				add(Path.Combine(Path.GetDirectoryName(pclPath) ?? "", ".minecraft"));
			}
			string pclClientRootDir = instance.PclClientRootDir;
			if (!string.IsNullOrWhiteSpace(pclClientRootDir))
			{
				if (Path.GetFileName(pclClientRootDir.TrimEnd(Path.DirectorySeparatorChar))?.Equals(".minecraft", StringComparison.OrdinalIgnoreCase) ?? false)
				{
					add(pclClientRootDir);
				}
				else
				{
					add(Path.Combine(pclClientRootDir, ".minecraft"));
				}
			}
			string dragonCoreTargetDir = instance.DragonCoreTargetDir;
			if (!string.IsNullOrWhiteSpace(dragonCoreTargetDir))
			{
				add(AscendToMinecraft(dragonCoreTargetDir));
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[行情token] 从设置推导 .minecraft 失败");
		}
		string[] extraRoots = ExtraRoots;
		foreach (string root in extraRoots)
		{
			if (!Directory.Exists(root))
			{
				continue;
			}
			foreach (string item in FindMinecraftDirs(root, 0))
			{
				add(item);
			}
		}
		return list;
	}

	// 从任意子目录向上找最近的 .minecraft（最多 6 层）
	private static string? AscendToMinecraft(string startDir)
	{
		string? text = startDir;
		for (int i = 0; i < 6 && text != null; i++)
		{
			if ((Path.GetFileName(text)?.Equals(".minecraft", StringComparison.OrdinalIgnoreCase)) ?? false)
			{
				return text;
			}
			text = Path.GetDirectoryName(text);
		}
		return null;
	}

	private static readonly string[] PrunedNames = new string[9]
	{
		"windows", "program files", "program files (x86)", "programdata", "appdata",
		"node_modules", "$recycle.bin", "system volume information", "users"
	};

	private static IEnumerable<string> FindMinecraftDirs(string root, int depth)
	{
		if (depth > ScanMaxDepth)
		{
			yield break;
		}
		DirectoryInfo[] directories;
		try
		{
			directories = new DirectoryInfo(root).EnumerateDirectories().ToArray();
		}
		catch
		{
			yield break;
		}
		DirectoryInfo[] array = directories;
		foreach (DirectoryInfo directoryInfo in array)
		{
			string name = directoryInfo.Name;
			if (PrunedNames.Any((string p) => name.Equals(p, StringComparison.OrdinalIgnoreCase)) || name.StartsWith("$"))
			{
				continue;
			}
			if (name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(directoryInfo.FullName, "versions")))
			{
				yield return directoryInfo.FullName;
				continue;
			}
			foreach (string item in FindMinecraftDirs(directoryInfo.FullName, depth + 1))
			{
				yield return item;
			}
		}
	}
}

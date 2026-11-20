using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MhyunBox.Services;

/// <summary>
/// 一键导出问题日志：把最近 3 天的日志 + 环境信息打包成 zip 放到桌面，供反馈问题时直接发送。
/// 仅在调试模式开启时可用（按钮随调试模式启用）。
///
/// 2026-11-19 修复与增强：
/// - 导出前先 <see cref="LogHelper.FlushAndClose"/>，避免「当前正在写的日志」内容还没落盘；
/// - 读取源文件一律用 FileShare.ReadWrite|Delete 复制，规避 FileStream 占用导致的导出失败；
/// - **把布吉岛代理 payload 日志（%TEMP%\WtfNel\logs）也打进包**：注入/挂起问题的证据在那里；
/// - 环境信息里补上布吉岛代理运行态、payload 文件校验值、payload 日志尾部；
/// - 同名 zip 自动加序号，失败时删除半成品，不再留下坏包。
/// </summary>
public static class LogExporter
{
	/// <summary>payload 日志在 zip 内的目录名。</summary>
	private const string PayloadLogEntryDir = "布吉岛代理payload日志";

	public static string ExportToDesktop()
	{
		string logDir = Path.Combine(AppContext.BaseDirectory, "MhyUnBox_Logs");
		string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		if (string.IsNullOrEmpty(desktop))
		{
			desktop = AppContext.BaseDirectory;
		}
		Directory.CreateDirectory(desktop);

		string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
		string zipPath = BuildUniquePath(desktop, "MhyUnBox_日志_" + stamp);

		try
		{
			// 当前日志文件由 LogHelper 持有，先刷盘并释放，导出后再按需自动重开追加
			LogHelper.FlushAndClose();

			DateTime cutoff = DateTime.Now.AddDays(-3.0);
			using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
			{
				AddDirectory(zip, logDir, cutoff);
				AddPayloadLogs(zip, cutoff);
				AddText(zip, "环境信息.txt", BuildSummary());
			}
		}
		catch (Exception ex)
		{
			try
			{
				if (File.Exists(zipPath))
				{
					File.Delete(zipPath);
				}
			}
			catch
			{
			}
			LogHelper.LogError(ex, "[日志导出] 导出失败");
			throw;
		}

		LogHelper.Force("[日志导出] 已导出问题日志: " + zipPath);
		return zipPath;
	}

	/// <summary>把日志目录里最近 N 天的 .log/.txt/.json 收进 zip。</summary>
	private static void AddDirectory(ZipArchive zip, string dir, DateTime cutoff)
	{
		if (!Directory.Exists(dir))
		{
			return;
		}
		foreach (FileInfo fi in new DirectoryInfo(dir)
			.EnumerateFiles("*", SearchOption.AllDirectories)
			.Where(f => f.LastWriteTime >= cutoff && IsWantedExtension(f.Extension))
			.OrderBy(f => f.FullName))
		{
			// 跳过可能含会话敏感信息的缓存/动态预设文件，只带日志与排查信息
			if (fi.Name.StartsWith("server_list_cache", StringComparison.OrdinalIgnoreCase) ||
				fi.Name.StartsWith("discovered_servers", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string rel;
			try
			{
				rel = Path.GetRelativePath(dir, fi.FullName);
			}
			catch
			{
				rel = fi.Name;
			}
			AddFileLockedSafe(zip, fi.FullName, rel);
		}
	}

	/// <summary>把 payload 日志（游戏进程内原生 DLL 写的）收进 zip 的独立子目录。</summary>
	private static void AddPayloadLogs(ZipArchive zip, DateTime cutoff)
	{
		foreach (FileInfo fi in BjdProxy.BjdPayloadLog.RecentFiles(3))
		{
			if (fi.LastWriteTime < cutoff)
			{
				continue;
			}
			AddFileLockedSafe(zip, fi.FullName, PayloadLogEntryDir + "/" + fi.Name);
		}
	}

	/// <summary>
	/// 用「共享读 + 复制到 zip 条目」的方式收文件：
	/// 目标日志可能正被本程序或游戏进程以写方式打开（只允许共享读），
	/// 直接 CreateEntryFromFile 会因占用抛 IOException。
	/// </summary>
	private static void AddFileLockedSafe(ZipArchive zip, string sourcePath, string entryName)
	{
		try
		{
			using FileStream src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
			using Stream dst = entry.Open();
			src.CopyTo(dst);
		}
		catch (Exception ex)
		{
			// 单个文件失败不整体失败：写一条说明条目，便于知道缺了什么
			try
			{
				AddText(zip, entryName + ".读取失败.txt", sourcePath + Environment.NewLine + ex.GetType().Name + ": " + ex.Message + Environment.NewLine);
			}
			catch
			{
			}
		}
	}

	private static void AddText(ZipArchive zip, string entryName, string text)
	{
		ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
		using StreamWriter writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
		writer.Write(text);
	}

	private static bool IsWantedExtension(string ext)
	{
		return ext.Equals(".log", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".json", StringComparison.OrdinalIgnoreCase);
	}

	private static string BuildUniquePath(string dir, string baseName)
	{
		string path = Path.Combine(dir, baseName + ".zip");
		int suffix = 2;
		while (File.Exists(path))
		{
			path = Path.Combine(dir, baseName + "_" + suffix + ".zip");
			suffix++;
			if (suffix > 99)
			{
				path = Path.Combine(dir, baseName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".zip");
				break;
			}
		}
		return path;
	}

	private static string BuildSummary()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("程序版本: " + AppVersion.Display);
		sb.AppendLine("导出时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
		sb.AppendLine("系统: " + Environment.OSVersion.VersionString + " (" + (Environment.Is64BitProcess ? "x64" : "x86") + ")");
		sb.AppendLine("进程: pid " + Environment.ProcessId + "，程序目录 " + AppContext.BaseDirectory);
		sb.AppendLine("调试模式: " + (LogHelper.DebugEnabled ? "开启" : "关闭"));
		sb.AppendLine();

		// ---- 布吉岛专用代理（A/B 会话共享）运行态 ----
		sb.AppendLine("[布吉岛代理]");
		try
		{
			sb.AppendLine("状态: " + BjdProxy.BjdProxyService.DescribeStatus());
			sb.AppendLine("最近结果: " + (BjdProxy.BjdProxyService.LastMessage ?? ""));
			sb.AppendLine("主线程挂起: " + (BjdProxy.BjdProxyService.IsGameFrozen ? "是（画面卡住，需 B 连端口恢复）" : "否"));
			sb.AppendLine("门禁 GameId: " + BjdProxy.BjdGate.GameId + "（预设 " + BjdProxy.BjdGate.PresetName + "）");
		}
		catch (Exception ex)
		{
			sb.AppendLine("状态读取失败: " + ex.Message);
		}

		// ---- payload 文件信息 ----
		sb.AppendLine();
		sb.AppendLine("[payload]");
		try
		{
			string? payload = BjdProxy.BjdInjector.ResolvePayloadPath(out string payloadDetail);
			sb.AppendLine(payloadDetail);
			if (payload != null)
			{
				FileInfo fi = new FileInfo(payload);
				sb.AppendLine("大小: " + fi.Length + " 字节");
				sb.AppendLine("SHA256: " + Sha256Of(payload));
			}
		}
		catch (Exception ex)
		{
			sb.AppendLine("payload 解析失败: " + ex.Message);
		}

		// ---- payload 日志 ----
		sb.AppendLine();
		sb.AppendLine("[payload 日志]");
		try
		{
			string? path = BjdProxy.BjdPayloadLog.CurrentPath;
			sb.AppendLine("目录: " + BjdProxy.BjdPayloadLog.LogDir);
			sb.AppendLine("当前文件: " + (path ?? "（无）"));
			if (BjdProxy.BjdPayloadLog.TryReadTail(40, out string tail))
			{
				sb.AppendLine("---- 末尾 40 行 ----");
				sb.AppendLine(tail);
			}
		}
		catch (Exception ex)
		{
			sb.AppendLine("payload 日志读取失败: " + ex.Message);
		}

		return sb.ToString();
	}

	private static string Sha256Of(string path)
	{
		try
		{
			using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			using SHA256 sha = SHA256.Create();
			return Convert.ToHexString(sha.ComputeHash(fs));
		}
		catch (Exception ex)
		{
			return "计算失败: " + ex.Message;
		}
	}
}

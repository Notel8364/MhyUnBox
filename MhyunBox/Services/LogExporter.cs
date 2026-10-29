using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace MhyunBox.Services;

/// <summary>
/// 一键导出问题日志：把日志目录里最近 3 天的日志 + 版本信息打包成 zip 放到桌面，
/// 供用户反馈问题时直接发送。仅在调试模式开启时可用（按钮随调试模式启用）。
/// </summary>
public static class LogExporter
{
	public static string ExportToDesktop()
	{
		string logDir = System.IO.Path.Combine(AppContext.BaseDirectory, "MhyUnbox_Logs");
		string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		if (string.IsNullOrEmpty(desktop))
		{
			desktop = AppContext.BaseDirectory;
		}
		Directory.CreateDirectory(desktop);
		string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
		string zipPath = System.IO.Path.Combine(desktop, "MhyunBox_日志_" + stamp + ".zip");

		DateTime cutoff = DateTime.Now.AddDays(-3.0);
		using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
		{
			if (Directory.Exists(logDir))
			{
				foreach (FileInfo fi in new DirectoryInfo(logDir)
					.EnumerateFiles("*", SearchOption.AllDirectories)
					.Where(f => f.LastWriteTime >= cutoff && (f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)))
					.OrderBy(f => f.FullName))
				{
					// 跳过可能包含会话敏感信息的缓存类文件，只打包日志与服务器列表
					if (fi.Name.StartsWith("server_list_cache", StringComparison.OrdinalIgnoreCase) || fi.Name.StartsWith("discovered_servers", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					string rel = System.IO.Path.GetRelativePath(logDir, fi.FullName);
					zip.CreateEntryFromFile(fi.FullName, rel, CompressionLevel.Optimal);
				}
			}
			// 版本信息摘要，方便定位问题版本
			string summary = "程序版本: " + AppVersion.Display + "\n导出时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n系统: " + Environment.OSVersion.VersionString + " (" + (Environment.Is64BitProcess ? "x64" : "x86") + ")\n调试模式: 开启\n";
			ZipArchiveEntry infoEntry = zip.CreateEntry("环境信息.txt");
			using (StreamWriter w = new StreamWriter(infoEntry.Open()))
			{
				w.Write(summary);
			}
		}
		LogHelper.Force("[日志导出] 已导出问题日志: " + zipPath);
		return zipPath;
	}
}

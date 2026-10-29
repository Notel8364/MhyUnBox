using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace MhyunBox.Services;

public static class LogCleanup
{
	private static readonly Timer? _timer;

	private const int DefaultRetentionDays = 30;

	static LogCleanup()
	{
		try
		{
			_timer = new Timer(delegate
			{
				CleanOldLogs(30);
			}, null, TimeSpan.FromMinutes(5.0), TimeSpan.FromDays(1.0));
		}
		catch
		{
		}
	}

	public static void RunScheduledCleanup()
	{
		CleanOldLogs(30);
	}

	public static int CleanOldLogs(int retentionDays)
	{
		if (retentionDays < 1)
		{
			retentionDays = 1;
		}
		DateTime dateTime = DateTime.Now.AddDays(-retentionDays);
		int num = 0;
		long num2 = 0L;
		string[] array = new string[2]
		{
			Path.Combine(AppContext.BaseDirectory, "MhyUnbox_Logs"),
			ProxyCaptureService.CaptureDir
		};
		string[] array2 = array;
		foreach (string path in array2)
		{
			try
			{
				foreach (string item in Directory.EnumerateFiles(path, "*.log", SearchOption.TopDirectoryOnly))
				{
					try
					{
						FileInfo fileInfo = new FileInfo(item);
						if (fileInfo.LastWriteTime < dateTime)
						{
							long length = fileInfo.Length;
							fileInfo.Delete();
							num++;
							num2 += length;
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
		if (num > 0)
		{
			LogHelper.Log($"日志自动清理: 删除 {num} 个(超过 {retentionDays} 天), 释放 {num2 / 1024} KB");
		}
		return num;
	}

	public static int CleanNow()
	{
		int num = 0;
		long num2 = 0L;
		try
		{
			foreach (string item in Directory.EnumerateFiles(ProxyCaptureService.CaptureDir, "*.log", SearchOption.TopDirectoryOnly))
			{
				try
				{
					FileInfo fileInfo = new FileInfo(item);
					long length = fileInfo.Length;
					fileInfo.Delete();
					num++;
					num2 += length;
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		string today = DateTime.Now.ToString("yyyyMMdd");
		try
		{
			foreach (string item2 in from f in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "MhyUnbox_Logs"), "MhyUnBox-*.log", SearchOption.TopDirectoryOnly)
				where !string.Equals(Path.GetFileName(f), "MhyUnBox-" + today + ".log", StringComparison.Ordinal)
				select f)
			{
				try
				{
					FileInfo fileInfo2 = new FileInfo(item2);
					long length2 = fileInfo2.Length;
					fileInfo2.Delete();
					num++;
					num2 += length2;
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		if (num > 0)
		{
			LogHelper.Log($"日志手动清理: 删除 {num} 个, 释放 {num2 / 1024} KB");
		}
		else
		{
			LogHelper.Log("日志手动清理: 没有可清理的日志");
		}
		return num;
	}
}

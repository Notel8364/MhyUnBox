using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace MhyunBox.Services;

/// <summary>
/// 程序内存诊断：每 60 秒把进程工作集 / 私有内存 / GC 各代 / LOH / 线程句柄 /
/// 代理会话数写成一行，追加到 MhyUnBox_Logs\MemoryDiag.log（独立文件，不受调试开关门控）。
/// 目的：定位多会话时 193MB 工作集的构成——托管堆、本机（WinUI/驱动）、线程栈哪一块大。
/// </summary>
public static class MemoryDiagnosticsService
{
	private const int IntervalMs = 60_000;

	private static readonly object Lock = new object();

	private static Thread? _thread;

	public static void Start()
	{
		if (_thread != null)
		{
			return;
		}
		_thread = new Thread(Run)
		{
			IsBackground = true,
			Name = "MemoryDiag"
		};
		_thread.Start();
	}

	private static void Run()
	{
		// 程序刚启动先等一轮，避开启动瞬间的正常峰值。
		Thread.Sleep(IntervalMs);
		while (true)
		{
			try
			{
				Write(snapshot());
			}
			catch
			{
			}
			Thread.Sleep(IntervalMs);
		}
	}

	private static string snapshot()
	{
		var sb = new StringBuilder(400);
		using var p = Process.GetCurrentProcess();
		sb.Append("ws=").Append(p.WorkingSet64 / 1048576).Append("MB");
		sb.Append(" private=").Append(p.PrivateMemorySize64 / 1048576).Append("MB");
		sb.Append(" threads=").Append(p.Threads.Count);
		sb.Append(" handles=").Append(p.HandleCount);

		var gi = GC.GetGCMemoryInfo();
		sb.Append(" gcHeap=").Append(gi.HeapSizeBytes / 1048576).Append("MB");
		sb.Append(" gcFrag=").Append(gi.FragmentedBytes / 1048576).Append("MB");
		sb.Append(" committed=").Append(gi.TotalCommittedBytes / 1048576).Append("MB");
		sb.Append(" pause%=").Append(gi.PauseTimePercentage.ToString("F2"));
		// GenerationInfo 顺序：0=Gen0 1=Gen1 2=Gen2 3=LOH 4=POH
		var gens = gi.GenerationInfo;
		if (gens.Length > 0)
		{
			sb.Append(" gen=[");
			string[] names = { "g0", "g1", "g2", "loh", "poh" };
			for (int i = 0; i < gens.Length && i < names.Length; i++)
			{
				sb.Append(names[i]).Append('=').Append(gens[i].SizeAfterBytes / 1048576).Append("MB ");
			}
			sb.Append(']');
		}

		ThreadPool.GetAvailableThreads(out int worker, out int io);
		sb.Append(" pool(w/i)=").Append(worker).Append('/').Append(io);

		sb.Append(" proxies: listeners=").Append(ProxyCaptureService.ActiveBindingsSnapshot().Count);
		sb.Append(" sessions=").Append(ProxyCaptureService.SessionsSnapshot().Count);
		return sb.ToString();
	}

	private static void Write(string line)
	{
		try
		{
			lock (Lock)
			{
				string dir = Path.Combine(AppContext.BaseDirectory, "MhyUnBox_Logs");
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, "MemoryDiag.log");
				var info = new FileInfo(path);
				if (info.Exists && info.Length > 2L * 1024L * 1024L)
				{
					string old = Path.Combine(dir, "MemoryDiag.old.log");
					if (File.Exists(old))
					{
						File.Delete(old);
					}
					File.Move(path, old);
				}
				File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}", Encoding.UTF8);
			}
		}
		catch
		{
		}
	}
}

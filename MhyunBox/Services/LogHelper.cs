using System;
using System.IO;
using System.Text;

namespace MhyunBox.Services;

public static class LogHelper
{
	private static readonly object Lock = new object();

	private static StreamWriter _writer;

	private static string _writerDate = "";

	private static string LogDir => Path.Combine(AppContext.BaseDirectory, "MhyUnbox_Logs");

	private static string LogPath => Path.Combine(LogDir, $"MhyUnBox-{DateTime.Now:yyyyMMdd}.log");

	public static bool DebugEnabled { get; set; }

	public static void Debug(string message)
	{
		if (DebugEnabled)
		{
			Log(message);
		}
	}

	public static void DebugError(Exception? ex, string message)
	{
		if (DebugEnabled)
		{
			LogError(ex, message);
		}
	}

	public static void ClearLogs()
	{
		try
		{
			lock (Lock)
			{
				_writer?.Dispose();
				_writer = null;
				if (Directory.Exists(LogDir))
				{
					Directory.Delete(LogDir, recursive: true);
				}
			}
		}
		catch
		{
		}
	}

	private static void EnsureWriter()
	{
		if (_writer == null || _writerDate != DateTime.Now.ToString("yyyyMMdd"))
		{
			_writer?.Dispose();
			string today = DateTime.Now.ToString("yyyyMMdd");
			_writerDate = today;
			Directory.CreateDirectory(LogDir);
			// 追加写：不再按进程重启覆盖当天日志。
			// 原因：排查类问题（如官方 DLL 取盐握手）需要跨多次进程留存线索，
			// 一重启就被清空会导致刚抓到的现场丢失。日志体积由 LogCleanup 负责清理。
			bool append = true;
			_writer = new StreamWriter(LogPath, append, Encoding.UTF8)
			{
				AutoFlush = true
			};
		}
	}

	/// <summary>普通运行日志：受调试模式门控，关调试时不产出任何冗余日志。</summary>
	public static void Log(string message)
	{
		if (!DebugEnabled)
		{
			return;
		}
		Write(message);
	}

	private static void Write(string message)
	{
		try
		{
			lock (Lock)
			{
				EnsureWriter();
				_writer?.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
			}
		}
		catch
		{
		}
	}

	public static void FlushAndClose()
	{
		try
		{
			lock (Lock)
			{
				_writer?.Flush();
				_writer?.Dispose();
				_writer = null;
			}
		}
		catch
		{
		}
	}

	/// <summary>错误日志：同样受调试模式门控（用户要求所有恒写日志统一接入设置里的调试模式；
	/// 关调试时整个日志系统都不产出文件，需要排障时到设置里打开调试模式即可）。</summary>
	public static void LogError(Exception? ex, string message)
	{
		if (!DebugEnabled)
		{
			return;
		}
		Write($"{message} | {ex}");
	}

	/// <summary>关键事件日志：同样受调试模式门控（用户要求恒写日志全部接入调试模式，关调试时不写；错误日志 LogError 仍保留恒写）。</summary>
	public static void Force(string message)
	{
		if (!DebugEnabled)
		{
			return;
		}
		Write(message);
	}

	/// <summary>
	/// 盐值链路诊断日志：与其他日志一致，**受 DebugEnabled 门控**（设置里打开调试模式即可看到）。
	/// 记录盐值获取（Alane / 404 / 官方 DLL）的关键步骤，不做逐帧全量 dump。
	/// </summary>
	public static void Salt(string message)
	{
		if (!DebugEnabled)
		{
			return;
		}
		Write($"[salt] {message}");
	}

	/// <summary>
	/// 受调试模式门控的**非致命错误**日志。
	///
	/// 与 LogError 的区别：LogError 记录真正的故障（认证异常、中继异常等，需留痕排障），
	/// 而这一类只是**常见的网络超时/取消**——例如 uni_sauth 的
	/// TaskCanceledException，在正常网络波动下会反复出现，
	/// 归到调试模式下才输出，避免把有诊断价值的错误淹掉。
	/// </summary>
	public static void LogTransientError(Exception? ex, string message)
	{
		if (!DebugEnabled)
		{
			return;
		}
		Write($"{message} | {ex}");
	}
}

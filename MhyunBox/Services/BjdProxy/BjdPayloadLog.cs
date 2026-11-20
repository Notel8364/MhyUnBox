using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace MhyunBox.Services.BjdProxy;

/// <summary>
/// 布吉岛代理 payload（游戏进程内的原生 DLL）日志接入。
///
/// payload 自己写日志，位置由它决定：
///   - 优先环境变量 <c>WTFNEL_LOG_DIR</c>（启动器可以指定）；
///   - 否则 <c>%TEMP%\WtfNel\logs\proxy-yyyyMMdd.log</c>。
/// 这些日志里有注入/挂载/MAIN-GATE 的关键证据，但原本和本程序日志完全隔离。
/// 本类把新写入的行**镜像**进 <see cref="LogHelper"/>（前缀 <c>[布吉岛代理/payload]</c>），
/// 于是调试模式开关、日志页、一键导出都能拿到同一份证据。
///
/// 注意：payload 是别人写的原生模块，日志文件由它持有；这里一律用
/// FileShare.ReadWrite | FileShare.Delete 打开，绝不锁它。
/// </summary>
public static class BjdPayloadLog
{
	private static readonly object Sync = new object();
	private static Thread? _thread;
	private static volatile bool _running;
	private static long _offset;
	private static long _baselineOffset;
	private static string _currentPath = "";
	private static int _mirroredLines;

	// —— 逐包刷屏过滤 ——
	// payload 的 relay_handler.cpp:130 对**每一个** C2S 包都 LogTo("[C2S PASS] 类名")，
	// 游戏里每秒几十上百行，10 分钟就能把日志刷到 1MB+。这行是 payload 内部无条件输出，
	// 改不了（除非重编 payload），只能在镜像侧过滤：不转发逐包行，只定期报一句汇总。
	private static readonly string[] NoisePrefixes = new string[3] { "[C2S ", "[S2C ", "[VIEW] S2C" };
	private static long _noiseSkipped;
	private static long _noiseReported;
	private static DateTime _noiseLastReportUtc;

	/// <summary>单次 Pump 最多转发多少行，防止突发刷屏一次性打爆日志。</summary>
	private const int MaxForwardPerPump = 200;

	/// <summary>累计被过滤的逐包日志行数（诊断/导出用）。</summary>
	public static long FilteredPacketLines => Interlocked.Read(ref _noiseSkipped);

	/// <summary>payload 日志目录。</summary>
	public static string LogDir
	{
		get
		{
			try
			{
				string? env = Environment.GetEnvironmentVariable("WTFNEL_LOG_DIR");
				if (!string.IsNullOrWhiteSpace(env))
				{
					return env.Trim();
				}
			}
			catch
			{
			}
			return Path.Combine(Path.GetTempPath(), "WtfNel", "logs");
		}
	}

	/// <summary>当前最新的 payload 日志文件（没有则 null）。</summary>
	public static string? CurrentPath
	{
		get
		{
			try
			{
				if (!Directory.Exists(LogDir))
				{
					return null;
				}
				FileInfo? newest = null;
				foreach (FileInfo fi in new DirectoryInfo(LogDir).EnumerateFiles("proxy-*.log"))
				{
					if (newest == null || fi.LastWriteTimeUtc > newest.LastWriteTimeUtc)
					{
						newest = fi;
					}
				}
				return newest?.FullName;
			}
			catch
			{
				return null;
			}
		}
	}

	/// <summary>已镜像的行数（诊断用）。</summary>
	public static int MirroredLines => _mirroredLines;

	/// <summary>镜像线程是否在跑。</summary>
	public static bool IsMirroring => _running;

	/// <summary>
	/// 把「当前文件末尾」记为起点：注入前调用，之后镜像只会输出本次注入产生的新行，
	/// 不会把上一次注入的历史重新刷进日志。
	/// </summary>
	public static void MarkInjectionStart()
	{
		lock (Sync)
		{
			_currentPath = CurrentPath ?? "";
			_offset = 0;
			_baselineOffset = 0;
			if (!string.IsNullOrEmpty(_currentPath))
			{
				try
				{
					_offset = new FileInfo(_currentPath).Length;
				}
				catch
				{
					_offset = 0;
				}
			}
			// 之后等待标记时只看这个位置之后的内容，避免上几次注入的历史行误判
			_baselineOffset = _offset;
			_mirroredLines = 0;
			Interlocked.Exchange(ref _noiseSkipped, 0L);
			Interlocked.Exchange(ref _noiseReported, 0L);
			_noiseLastReportUtc = DateTime.MinValue;
		}
	}

	/// <summary>启动镜像线程（幂等）。仅在调试模式开启时真正写日志。</summary>
	public static void StartMirror()
	{
		lock (Sync)
		{
			if (_running)
			{
				return;
			}
			_running = true;
			_thread = new Thread(MirrorLoop)
			{
				IsBackground = true,
				Name = "BjdPayloadLogMirror"
			};
			_thread.Start();
		}
	}

	public static void StopMirror()
	{
		Thread? thread;
		lock (Sync)
		{
			_running = false;
			thread = _thread;
			_thread = null;
		}
		try
		{
			thread?.Join(TimeSpan.FromSeconds(2));
		}
		catch
		{
		}
	}

	/// <summary>调试模式切换时调用：开=启动镜像并抓一次尾部，关=停止。</summary>
	public static void SetEnabled(bool enabled)
	{
		if (enabled)
		{
			StartMirror();
			LogHelper.Log("[布吉岛代理/payload] 日志目录: " + LogDir + "（当前文件: " + (CurrentPath ?? "暂无") + "）");
		}
		else
		{
			StopMirror();
		}
	}

	private static void MirrorLoop()
	{
		while (_running)
		{
			try
			{
				PumpOnce();
			}
			catch
			{
				// 镜像失败不影响主流程
			}
			try
			{
				Thread.Sleep(700);
			}
			catch
			{
				return;
			}
		}
	}

	/// <summary>把当前文件自 offset 起的新内容逐行送进 LogHelper。</summary>
	private static void PumpOnce()
	{
		string? path = CurrentPath;
		if (string.IsNullOrEmpty(path))
		{
			return;
		}

		lock (Sync)
		{
			if (!string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
			{
				// 换天/换了新文件：从头读
				_currentPath = path;
				_offset = 0;
				LogHelper.Log("[布吉岛代理/payload] 跟踪新日志文件: " + path);
			}
		}

		long offset;
		string current;
		lock (Sync)
		{
			offset = _offset;
			current = _currentPath;
		}

		byte[] bytes;
		long length;
		try
		{
			using FileStream fs = new FileStream(current, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			length = fs.Length;
			if (length < offset)
			{
				offset = 0; // 文件被截断/重写
			}
			if (length == offset)
			{
				return;
			}
			fs.Seek(offset, SeekOrigin.Begin);
			int take = (int)Math.Min(256 * 1024, length - offset);
			bytes = new byte[take];
			int read = fs.Read(bytes, 0, take);
			if (read <= 0)
			{
				return;
			}
			if (read != bytes.Length)
			{
				Array.Resize(ref bytes, read);
			}
		}
		catch
		{
			return;
		}

		lock (Sync)
		{
			_offset = offset + bytes.Length;
		}

		string text = Encoding.UTF8.GetString(bytes);
		if (text.Length == 0)
		{
			return;
		}
		int forwarded = 0;
		foreach (string raw in text.Split('\n'))
		{
			string line = raw.TrimEnd('\r');
			if (line.Length == 0)
			{
				continue;
			}
			if (IsPacketNoise(line))
			{
				Interlocked.Increment(ref _noiseSkipped);
				continue;
			}
			if (forwarded >= MaxForwardPerPump)
			{
				// 超出本轮预算：丢弃但不影响 offset（已整体消费），下一轮会继续读新内容
				continue;
			}
			forwarded++;
			Interlocked.Increment(ref _mirroredLines);
			LogHelper.Log("[布吉岛代理/payload] " + line);
		}
		MaybeReportNoise();
	}

	/// <summary>payload 的逐包刷屏行（[C2S PASS/ROUTE/DROP]、[S2C ...]、[VIEW] S2C ...）。</summary>
	private static bool IsPacketNoise(string line)
	{
		foreach (string prefix in NoisePrefixes)
		{
			if (line.StartsWith(prefix, StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>过滤掉大量逐包行后，每 5 秒最多报一句汇总，避免用户以为镜像没工作。</summary>
	private static void MaybeReportNoise()
	{
		long skipped = Interlocked.Read(ref _noiseSkipped);
		if (skipped - _noiseReported < 20)
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		if ((now - _noiseLastReportUtc).TotalSeconds < 5.0)
		{
			return;
		}
		long delta = skipped - _noiseReported;
		_noiseReported = skipped;
		_noiseLastReportUtc = now;
		LogHelper.Log($"[布吉岛代理/payload] 已过滤 {delta} 行逐包日志（C2S/S2C 刷屏行；累计 {skipped} 行）。"
			+ "原始逐包日志仍在 payload 日志文件里。");
	}

	/// <summary>
	/// 等待日志里出现某个标记（注入后判定 payload 走了哪条路径）。
	/// 只看 <see cref="MarkInjectionStart"/> 记录的起点之后的内容，
	/// 避免上一次注入留下的同样标记造成误判。
	/// </summary>
	public static bool WaitForMarker(string marker, TimeSpan timeout, out string matchedLine)
	{
		matchedLine = "";
		DateTime deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			string? path = CurrentPath;
			if (!string.IsNullOrEmpty(path))
			{
				string tail = ReadFromBaseline(path);
				int idx = tail.LastIndexOf(marker, StringComparison.Ordinal);
				if (idx >= 0)
				{
					int lineStart = tail.LastIndexOf('\n', idx) + 1;
					int lineEnd = tail.IndexOf('\n', idx);
					matchedLine = (lineEnd < 0 ? tail.Substring(lineStart) : tail.Substring(lineStart, lineEnd - lineStart)).TrimEnd('\r');
					return true;
				}
			}
			Thread.Sleep(250);
		}
		return false;
	}

	/// <summary>从「本次注入起点」之后读取日志内容。</summary>
	private static string ReadFromBaseline(string path)
	{
		long baseline;
		string expected;
		lock (Sync)
		{
			baseline = _baselineOffset;
			expected = _currentPath;
		}
		if (!string.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
		{
			// 换文件了：新文件从头算
			baseline = 0;
		}
		try
		{
			using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			if (fs.Length <= baseline)
			{
				return "";
			}
			fs.Seek(baseline, SeekOrigin.Begin);
			using StreamReader reader = new StreamReader(fs, Encoding.UTF8);
			return reader.ReadToEnd();
		}
		catch
		{
			return "";
		}
	}

	/// <summary>读日志尾部（给设置页/导出用）。</summary>
	public static bool TryReadTail(int maxLines, out string text)
	{
		text = "";
		string? path = CurrentPath;
		if (string.IsNullOrEmpty(path))
		{
			return false;
		}
		string content = ReadFileShareSafe(path);
		if (content.Length == 0)
		{
			return false;
		}
		string[] lines = content.Replace("\r\n", "\n").Split('\n');
		int take = Math.Min(Math.Max(0, maxLines), lines.Length);
		text = string.Join(Environment.NewLine, lines, lines.Length - take, take);
		return true;
	}

	/// <summary>把最近 3 天的 payload 日志文件列出来（导出用）。</summary>
	public static IReadOnlyList<FileInfo> RecentFiles(int days)
	{
		List<FileInfo> list = new List<FileInfo>();
		try
		{
			if (!Directory.Exists(LogDir))
			{
				return list;
			}
			DateTime cutoff = DateTime.Now.AddDays(-Math.Abs(days));
			foreach (FileInfo fi in new DirectoryInfo(LogDir).EnumerateFiles("*.log"))
			{
				if (fi.LastWriteTime >= cutoff)
				{
					list.Add(fi);
				}
			}
		}
		catch
		{
		}
		return list;
	}

	/// <summary>
	/// 统计「本次注入起点之后」某个标记出现了几次。
	/// 用于判断 B 是否接入（`BServer: B channelActive`）、A 是否重新建连
	/// （`Native_onChannelActive fired`）等运行期事实。
	/// </summary>
	public static int CountSinceBaseline(string marker)
	{
		if (string.IsNullOrEmpty(marker))
		{
			return 0;
		}
		string? path = CurrentPath;
		if (string.IsNullOrEmpty(path))
		{
			return 0;
		}
		string text = ReadFromBaseline(path);
		if (text.Length == 0)
		{
			return 0;
		}
		int count = 0;
		int index = 0;
		while (true)
		{
			index = text.IndexOf(marker, index, StringComparison.Ordinal);
			if (index < 0)
			{
				break;
			}
			count++;
			index += marker.Length;
		}
		return count;
	}

	/// <summary>状态文本（设置页底部展示）。</summary>
	public static string DescribeStatus()
	{
		string path = CurrentPath ?? "（暂无日志文件）";
		string mirror = _running
			? ("镜像中，已转入 " + _mirroredLines + " 行，已过滤逐包行 " + FilteredPacketLines)
			: "镜像未启动";
		return "payload 日志：" + path + "；" + mirror;
	}

	private static string ReadFileShareSafe(string path)
	{
		try
		{
			using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			using StreamReader reader = new StreamReader(fs, Encoding.UTF8);
			return reader.ReadToEnd();
		}
		catch
		{
			return "";
		}
	}
}

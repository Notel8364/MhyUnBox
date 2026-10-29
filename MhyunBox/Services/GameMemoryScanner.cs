using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>一次扫描里找到的候选盐值及其上下文。</summary>
public sealed class MemoryHit
{
	/// <summary>找到的 32 位十六进制串（已统一大写）。</summary>
	public string Value = "";

	/// <summary>命中的内存地址。</summary>
	public string Address = "";

	/// <summary>编码形态：ASCII / UTF-16LE。</summary>
	public string Encoding = "";

	/// <summary>命中位置前后的一段可读文本，便于判断它到底是什么。</summary>
	public string Context = "";

	/// <summary>所属内存区域的基址（VirtualQueryEx 的 BaseAddress）。</summary>
	public string RegionBase = "";

	/// <summary>命中处相对区域基址的偏移 —— 用于判断跨进程启动是否稳定。</summary>
	public long Offset;

	/// <summary>该区域的大小。</summary>
	public long RegionSize;

	public override string ToString()
	{
		return $"{Value}  @{Address}  [{Encoding}]"
			+ (RegionBase.Length > 0 ? $"  base={RegionBase}+0x{Offset:X}" : "")
			+ $"  …{Context}…";
	}
}

/// <summary>
/// 游戏进程内存扫描器 —— 在 TCP 通道之外寻找 CrcSalt 的备选路径。
///
/// 为什么要做这个（2026-10-18 实测结论）：
/// · 控制端口能连上，但对我们**和游戏自己**的登录帧都**不回包**
///   （真实帧重放 6 条也全部无回包，见 §37.3）；
/// · 这说明盐值很可能**不是从这个 TCP 通道下发的**，而是游戏从别处拿到：
///   启动器写入的文件、JVM 参数，或者就在进程内存里。
///
/// 因此直接从 javaw.exe 的内存里搜 32 位十六进制串。已知当前有效盐值的形态，
/// 只要能命中，就证明盐值确实驻留在游戏内存中，并且能定位它的存储位置。
/// </summary>
public static class GameMemoryScanner
{
	private const uint PROCESS_QUERY_INFORMATION = 0x0400;
	private const uint PROCESS_VM_READ = 0x0010;
	private const uint PROCESS_VM_OPERATION = 0x0008;

	private const int PAGE_NOACCESS = 0x01;
	private const int MEM_COMMIT = 0x1000;

	[StructLayout(LayoutKind.Sequential)]
	private struct MEMORY_BASIC_INFORMATION
	{
		public IntPtr BaseAddress;
		public IntPtr AllocationBase;
		public int AllocationProtect;
		public IntPtr RegionSize;
		public int State;
		public int Protect;
		public int Type;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr hObject);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, int dwLength);

	/// <summary>单次读取的块大小（4 MB）。</summary>
	private const int ChunkSize = 4 * 1024 * 1024;

	/// <summary>最多扫描多少字节，避免在大内存游戏里跑太久（默认 3 GB）。</summary>
	private const long MaxScanBytes = 3L * 1024 * 1024 * 1024;

	/// <summary>32 位十六进制盐值的正则（前后不允许再接字母数字，避免从长串里截断）。</summary>
	private static readonly Regex Hex32 = new Regex("(?<![0-9A-Za-z])([0-9A-Fa-f]{32})(?![0-9A-Za-z])", RegexOptions.Compiled);

	/// <summary>
	/// 扫描指定进程的内存，找出所有形如 32 位十六进制的串。
	/// </summary>
	/// <param name="pid">目标进程 ID（javaw.exe）</param>
	/// <param name="maxBytes">最多扫描的字节数</param>
	/// <param name="ct">取消令牌</param>
	public static async Task<List<MemoryHit>> ScanForSaltAsync(int pid, long maxBytes = MaxScanBytes, CancellationToken ct = default(CancellationToken))
	{
		// 关键：整个扫描必须丢到线程池。
		// 只靠 await Task.Yield() 不够——它仍会在**原线程**继续，而调用方是 UI 线程，
		// 扫 900MB~1.8GB 会把界面卡成"未响应"（实测现象）。
		return await Task.Run(() => ScanTextCore(pid, maxBytes, ct));
	}

	/// <summary>文本形态扫描的同步主体（由 ScanForSaltAsync 在线程池里调用）。</summary>
	private static List<MemoryHit> ScanTextCore(int pid, long maxBytes, CancellationToken ct)
	{
		List<MemoryHit> hits = new List<MemoryHit>();
		IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_VM_OPERATION, false, pid);
		if (h == IntPtr.Zero)
		{
			int err = Marshal.GetLastWin32Error();
			LogHelper.Salt($"[内存] OpenProcess({pid}) 失败，Win32 错误 {err}"
				+ (err == 5 ? "（访问被拒绝 —— 需要以管理员运行本程序）" : ""));
			return hits;
		}
		try
		{
			long scanned = 0;
			IntPtr addr = IntPtr.Zero;
			byte[] buf = new byte[ChunkSize];

			while (scanned < maxBytes && !ct.IsCancellationRequested)
			{
				MEMORY_BASIC_INFORMATION mbi;
				int ret = VirtualQueryEx(h, addr, out mbi, Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION)));
				if (ret == 0)
				{
					break;
				}
				long region = mbi.RegionSize.ToInt64();
				if (region <= 0)
				{
					break;
				}
				// 只看已提交且非 NOACCESS 的区域
				bool readable = (mbi.State & MEM_COMMIT) != 0 && (mbi.Protect & PAGE_NOACCESS) == 0;
				if (readable)
				{
					long toRead = Math.Min(region, maxBytes - scanned);
					for (long off = 0; off < toRead && !ct.IsCancellationRequested; off += ChunkSize)
					{
						int want = (int)Math.Min(ChunkSize, toRead - off);
						// 必须用 64 位地址运算：64 位进程的 RegionSize 常达 TB 级，
						// 强转 int 会截断成负数/小值，导致地址倒退 → VirtualQueryEx 反复
						// 返回同一区域 → **死循环**（实测就是这个原因卡住）。
						IntPtr readAt = new IntPtr(mbi.BaseAddress.ToInt64() + off);
						if (!ReadProcessMemory(h, readAt, buf, want, out int got) || got <= 0)
						{
							break;
						}
						scanned += got;
						FindInBlock(buf, got, readAt.ToInt64(), hits);
					}
				}
				addr = new IntPtr(mbi.BaseAddress.ToInt64() + region);
			}
			LogHelper.Salt($"[内存] 扫描结束：扫描 {scanned / (1024 * 1024)} MB，命中 {hits.Count} 个候选");
		}
		catch (Exception ex)
		{
			LogHelper.Salt("[内存] 扫描异常: " + ex.GetType().Name + ": " + ex.Message);
		}
		finally
		{
			CloseHandle(h);
		}
		return hits;
	}

	/// <summary>
	/// 在一个内存块里搜索 32 位十六进制（ASCII 与 UTF-16LE 两种形态）。
	///
	/// 用**字节级扫描**而不是 `Encoding.GetString + Regex`：
	/// 4 MB 的块做两次大字符串分配加正则匹配太慢（实测跑 512 MB 会超时），
	/// 直接按字节判断快一个数量级，且几乎不产生垃圾。
	/// </summary>
	private static void FindInBlock(byte[] buf, int len, long blockAddr, List<MemoryHit> hits)
	{
		// --- ASCII：32 个连续 hex 字节 ---
		int limit = len - 32;
		for (int i = 0; i <= limit; i++)
		{
			// 先查首字节和末字节，绝大多数位置会被立刻跳过
			if (!IsHex(buf[i]) || !IsHex(buf[i + 31]))
			{
				continue;
			}
			int k = 1;
			while (k < 32 && IsHex(buf[i + k]))
			{
				k++;
			}
			if (k < 32)
			{
				continue;
			}
			// 前后不能紧邻字母数字，避免从更长的十六进制串里截出来
			if (i > 0 && IsAlnum(buf[i - 1]))
			{
				continue;
			}
			if (i + 32 < len && IsAlnum(buf[i + 32]))
			{
				continue;
			}
			AddHitBytes(hits, buf, i, 1, blockAddr + i, "ASCII");
			i += 31;
		}

		// --- UTF-16LE：hex 字节后各跟一个 0（Java String 的常见内存形态）---
		int limitU = len - 64;
		for (int i = 0; i <= limitU; i++)
		{
			if (buf[i + 1] != 0 || !IsHex(buf[i]) || buf[i + 63] != 0 || !IsHex(buf[i + 62]))
			{
				continue;
			}
			int k = 1;
			while (k < 32 && buf[i + k * 2 + 1] == 0 && IsHex(buf[i + k * 2]))
			{
				k++;
			}
			if (k < 32)
			{
				continue;
			}
			if (i >= 2 && buf[i - 1] == 0 && IsAlnum(buf[i - 2]))
			{
				continue;
			}
			if (i + 64 < len && buf[i + 65] == 0 && IsAlnum(buf[i + 64]))
			{
				continue;
			}
			AddHitBytes(hits, buf, i, 2, blockAddr + i, "UTF-16LE");
			i += 63;
		}
	}

	private static bool IsHex(byte b)
	{
		return (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'f') || (b >= (byte)'A' && b <= (byte)'F');
	}

	private static bool IsAlnum(byte b)
	{
		return (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z');
	}

	private static void AddHitBytes(List<MemoryHit> hits, byte[] buf, int start, int stride, long addr, string enc)
	{
		StringBuilder val = new StringBuilder(32);
		for (int k = 0; k < 32; k++)
		{
			val.Append((char)buf[start + k * stride]);
		}
		string v = val.ToString().ToUpperInvariant();
		// 过滤：uuid / accessToken / md5 / 路径哈希这类噪音占绝大多数，
		// 不过滤的话几千个候选根本没法逐个验证。
		if (!IsPlausibleSalt(v))
		{
			return;
		}
		// 同值同编码最多保留 3 处
		int same = 0;
		foreach (MemoryHit h in hits)
		{
			if (h.Value == v && h.Encoding == enc)
			{
				same++;
			}
		}
		if (same >= 3)
		{
			return;
		}
		// 上下文：取命中点前后若干字节里的可打印字符
		StringBuilder ctx = new StringBuilder();
		int from = Math.Max(0, start - 40);
		int to = Math.Min(buf.Length, start + 32 * stride + 40);
		for (int p = from; p < to; p++)
		{
			byte c = buf[p];
			ctx.Append((c >= 32 && c < 127) ? ((char)c) : '.');
		}
		hits.Add(new MemoryHit
		{
			Value = v,
			Address = "0x" + addr.ToString("X"),
			Encoding = enc,
			Context = ctx.ToString()
		});
	}

	// ---------- 过滤 / 黑名单 ----------

	/// <summary>扫描过程中被过滤掉的候选数（用于报告"排除了多少噪音"）。</summary>
	public static int FilteredCount;

	/// <summary>过滤原因统计。</summary>
	public static readonly Dictionary<string, int> FilterReasons = new Dictionary<string, int>();

	/// <summary>常见占位/测试数据，绝不可能是盐值。</summary>
	private static readonly string[] BuiltinBlacklist = new string[9]
	{
		"00000000000000000000000000000000",
		"FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
		"11111111111111111111111111111111",
		"0123456789ABCDEF0123456789ABCDEF",
		"ABCDEF0123456789ABCDEF0123456789",
		"A1B2C3D4E5F60718293A4B5C6D7E8F9",
		"DEADBEEFDEADBEEFDEADBEEFDEADBEEF",
		"CAFEBABECAFEBABECAFEBABECAFEBABE",
		"12345678123456781234567812345678"
	};

	/// <summary>用户自定义黑名单（来自设置，逗号/换行分隔，自动转大写）。</summary>
	public static HashSet<string> CustomBlacklist { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private static void NoteFiltered(string reason)
	{
		FilteredCount++;
		FilterReasons[reason] = FilterReasons.TryGetValue(reason, out int n) ? (n + 1) : 1;
	}

	/// <summary>
	/// 判断一个 32 位十六进制串是否**可能**是盐值。
	/// 扫出来的绝大多数是 uuid / accessToken / md5 / 路径哈希，必须过滤掉，
	/// 否则几百上千个候选根本没法逐个验证。
	/// </summary>
	public static bool IsPlausibleSalt(string hex)
	{
		if (string.IsNullOrEmpty(hex) || hex.Length != 32)
		{
			return false;
		}
		string s = hex.ToUpperInvariant();

		// 1) 内置黑名单
		foreach (string b in BuiltinBlacklist)
		{
			if (s == b)
			{
				NoteFiltered("内置黑名单");
				return false;
			}
		}
		// 2) 用户黑名单
		if (CustomBlacklist.Contains(s))
		{
			NoteFiltered("用户黑名单");
			return false;
		}
		// 3) 全同一种字符（0000… / FFFF… / AAAA…）
		bool allSame = true;
		for (int i = 1; i < 32; i++)
		{
			if (s[i] != s[0])
			{
				allSame = false;
				break;
			}
		}
		if (allSame)
		{
			NoteFiltered("全同字符");
			return false;
		}
		// 4) 以 2/4/8/16 字节为周期重复（如 1A1A1A…、1111222211112222…）
		int[] periods = new int[4] { 2, 4, 8, 16 };
		foreach (int p in periods)
		{
			bool repeat = true;
			for (int i = p; i < 32; i++)
			{
				if (s[i] != s[i % p])
				{
					repeat = false;
					break;
				}
			}
			if (repeat)
			{
				NoteFiltered("周期重复(" + p + ")");
				return false;
			}
		}
		// 5) 连续步进序列（0123456789ABCDEF… 之类）
		{
			const string order = "0123456789ABCDEF";
			int steps = 0;
			for (int i = 1; i < 32; i++)
			{
				int a = order.IndexOf(s[i - 1]);
				int b = order.IndexOf(s[i]);
				if (a >= 0 && b >= 0 && ((a + 1) % 16) == b)
				{
					steps++;
				}
			}
			if (steps >= 24)
			{
				NoteFiltered("连续步进");
				return false;
			}
		}
		// 6) 字符种类过少：像 1111222233334444… 这种"块递增"测试数据只有 8 种字符，
		//    而真盐值（32 个随机 hex）通常有 12~16 种不同字符。
		//    取 ≤8 为阈值：真盐值低于 9 种的概率极低，不会误伤。
		{
			HashSet<char> kinds = new HashSet<char>();
			for (int i = 0; i < 32; i++)
			{
				kinds.Add(s[i]);
			}
			if (kinds.Count <= 8)
			{
				NoteFiltered("字符种类过少(" + kinds.Count + ")");
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// **二进制形态搜索**：把已知盐值（32 位 hex）解码成 **16 字节原始字节**，直接在内存里搜这段字节。
	///
	/// 为什么需要它（2026-10-18 16:25 实测）：
	/// 文本形态扫描（ASCII / UTF-16LE 的 32 个 hex 字符）在两个游戏进程里共命中
	/// 4934 个候选，但**与已知有效盐值比对命中 0 处**；清单里全是 uuid / accessToken / md5
	/// 之类的哈希值，没有 CrcSalt。
	/// → 说明游戏**不是把盐值当十六进制字符串保存**，而是以 `byte[16]` 持有
	///   （Java 侧拿到后直接用于 CRC，没必要转成字符串）。
	/// 所以必须按原始字节去搜。
	/// </summary>
	/// <param name="pid">目标进程</param>
	/// <param name="hexSalts">已知盐值（32 位 hex），会解码成 16 字节后搜索</param>
	public static async Task<List<MemoryHit>> ScanForBinarySaltAsync(int pid, IEnumerable<string> hexSalts, long maxBytes = MaxScanBytes, CancellationToken ct = default(CancellationToken))
	{
		// 同 ScanForSaltAsync：必须在线程池跑，否则会卡住 UI 线程
		return await Task.Run(() => ScanBinaryCore(pid, hexSalts, maxBytes, ct));
	}

	/// <summary>二进制形态扫描的同步主体（由 ScanForBinarySaltAsync 在线程池里调用）。</summary>
	private static List<MemoryHit> ScanBinaryCore(int pid, IEnumerable<string> hexSalts, long maxBytes, CancellationToken ct)
	{
		List<MemoryHit> hits = new List<MemoryHit>();
		// 构造待搜模式（16 字节）
		List<byte[]> patterns = new List<byte[]>();
		List<string> labels = new List<string>();
		foreach (string s in hexSalts)
		{
			string t = (s ?? "").Trim();
			if (t.Length != 32)
			{
				continue;
			}
			try
			{
				patterns.Add(Convert.FromHexString(t));
				labels.Add(t.ToUpperInvariant());
			}
			catch
			{
			}
		}
		if (patterns.Count == 0)
		{
			return hits;
		}

		IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_VM_OPERATION, false, pid);
		if (h == IntPtr.Zero)
		{
			int err = Marshal.GetLastWin32Error();
			LogHelper.Salt($"[内存-二进制] OpenProcess({pid}) 失败，Win32 错误 {err}");
			return hits;
		}
		try
		{
			long scanned = 0;
			IntPtr addr = IntPtr.Zero;
			byte[] buf = new byte[ChunkSize];
			while (scanned < maxBytes && !ct.IsCancellationRequested)
			{
				MEMORY_BASIC_INFORMATION mbi;
				if (VirtualQueryEx(h, addr, out mbi, Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == 0)
				{
					break;
				}
				long region = mbi.RegionSize.ToInt64();
				if (region <= 0)
				{
					break;
				}
				if ((mbi.State & MEM_COMMIT) != 0 && (mbi.Protect & PAGE_NOACCESS) == 0)
				{
					long toRead = Math.Min(region, maxBytes - scanned);
					for (long off = 0; off < toRead && !ct.IsCancellationRequested; off += ChunkSize)
					{
						int want = (int)Math.Min(ChunkSize, toRead - off);
						IntPtr readAt = new IntPtr(mbi.BaseAddress.ToInt64() + off);
						if (!ReadProcessMemory(h, readAt, buf, want, out int got) || got <= 0)
						{
							break;
						}
						scanned += got;
						FindBinary(buf, got, readAt.ToInt64(), mbi.BaseAddress.ToInt64(), region, patterns, labels, hits);
					}
				}
				addr = new IntPtr(mbi.BaseAddress.ToInt64() + region);
			}
			LogHelper.Salt($"[内存-二进制] 扫描结束：{scanned / (1024 * 1024)} MB，模式 {patterns.Count} 个，命中 {hits.Count} 处");
		}
		catch (Exception ex)
		{
			LogHelper.Salt("[内存-二进制] 扫描异常: " + ex.GetType().Name + ": " + ex.Message);
		}
		finally
		{
			CloseHandle(h);
		}
		return hits;
	}

	/// <summary>在一个块里搜索所有 16 字节模式。</summary>
	private static void FindBinary(byte[] buf, int len, long blockAddr, long regionBase, long regionSize,
		List<byte[]> patterns, List<string> labels, List<MemoryHit> hits)
	{
		for (int p = 0; p < patterns.Count; p++)
		{
			byte[] pat = patterns[p];
			if (pat.Length == 0 || pat.Length > len)
			{
				continue;
			}
			byte first = pat[0];
			byte last = pat[pat.Length - 1];
			int n = pat.Length;
			int limit = len - n;
			for (int i = 0; i <= limit; i++)
			{
				// 先比首末字节，绝大多数位置立刻跳过
				if (buf[i] != first || buf[i + n - 1] != last)
				{
					continue;
				}
				int k = 1;
				while (k < n - 1 && buf[i + k] == pat[k])
				{
					k++;
				}
				if (k < n - 1)
				{
					continue;
				}
				StringBuilder ctx = new StringBuilder();
				int from = Math.Max(0, i - 32);
				int to = Math.Min(len, i + n + 32);
				for (int q = from; q < to; q++)
				{
					byte c = buf[q];
					ctx.Append((c >= 32 && c < 127) ? ((char)c) : '.');
				}
				long hitAddr = blockAddr + i;
				hits.Add(new MemoryHit
				{
					Value = labels[p],
					Address = "0x" + hitAddr.ToString("X"),
					Encoding = "BIN(" + n + "B)",
					Context = ctx.ToString(),
					// 记录区域基址与偏移：跨一次游戏启动后地址会变（ASLR），
					// 但**相对区域基址的偏移**有可能稳定 —— 这是判断能否定点读取的关键。
					RegionBase = "0x" + regionBase.ToString("X"),
					Offset = hitAddr - regionBase,
					RegionSize = regionSize
				});
				if (hits.Count > 200)
				{
					return;
				}
			}
		}
	}

	/// <summary>
	/// 把候选清单落盘。游戏进程退出后就再也扫不到了，必须先存下来供事后分析。
	/// </summary>
	public static string SaveHits(List<MemoryHit> hits, int port = 0)
	{
		string dir = Path.Combine(Path.GetTempPath(), "MhyunBoxCapture");
		Directory.CreateDirectory(dir);
		CleanupOldHitFiles(dir);
		string file = Path.Combine(dir, $"memsalt_{port}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
		using (StreamWriter w = new StreamWriter(file, false, Encoding.UTF8))
		{
			w.WriteLine($"# 内存扫描候选 {hits.Count} 个  pid={port}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			w.WriteLine("# 列：值\t地址\t编码\t区域基址\t偏移(hex)\t区域大小\t上下文");
			foreach (MemoryHit h in hits)
			{
				w.WriteLine($"{h.Value}\t{h.Address}\t{h.Encoding}\t{h.RegionBase}\t0x{h.Offset:X}\t{h.RegionSize}\t{h.Context}");
			}
		}
		LogHelper.Salt($"[内存] 候选清单已保存: {file}");
		return file;
	}

	/// <summary>清理 7 天前的扫描落盘文件（候选清单含内存上下文片段，不长期滞留在临时目录）。</summary>
	private static void CleanupOldHitFiles(string dir)
	{
		try
		{
			DateTime threshold = DateTime.Now.AddDays(-7.0);
			foreach (FileInfo f in new DirectoryInfo(dir).GetFiles("memsalt_*.txt"))
			{
				if (f.LastWriteTime < threshold)
				{
					f.Delete();
				}
			}
		}
		catch
		{
		}
	}

	/// <summary>
	/// 在候选里找出与已知盐值相同的项（用于验证"盐值确实驻留在游戏内存"这一判断）。
	/// </summary>
	public static List<MemoryHit> MatchKnown(List<MemoryHit> hits, IEnumerable<string> knownSalts)
	{
		List<MemoryHit> matched = new List<MemoryHit>();
		HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string s in knownSalts)
		{
			if (!string.IsNullOrWhiteSpace(s))
			{
				set.Add(s!.Trim().ToUpperInvariant());
			}
		}
		foreach (MemoryHit h in hits)
		{
			if (set.Contains(h.Value))
			{
				matched.Add(h);
			}
		}
		return matched;
	}
}

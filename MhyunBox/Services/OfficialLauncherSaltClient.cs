using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace MhyunBox.Services;

/// <summary>官方启动器 DLL 盐值索取结果。</summary>
public sealed class OfficialSaltResult
{
	public bool Ok;

	public string Message = "";

	/// <summary>索取到的 32 位十六进制盐值（控制端口链路成功时非空）。</summary>
	public string? Salt;

	/// <summary>启动器版本号（已安装 versionInfo 优先，其次 patchlist 最新）。</summary>
	public string? Version;

	public string? LauncherPath;

	/// <summary>使用的控制端口（0 = 未发现）。</summary>
	public int ControlPort;

	/// <summary>是否完成了令牌握手（即使最终没解析出盐值）。</summary>
	public bool HandshakeDone;

	/// <summary>本地算法自检是否通过（ComputeDynamicToken 往返一致性）。</summary>
	public bool AlgorithmSelfCheck;

	/// <summary>守候模式命中时的游戏进程 PID（0 = 非守候路径）。</summary>
	public int WatchHitPid;
}

/// <summary>
/// 网易官方启动器 DLL 盐值算法逆向索取客户端。
/// 逆向结论（本次会话实测验证）：
/// 1. 游戏 JNI GetToken(byte[32]) 输出：
///    token[0:16]  = 16 个 rand() 原始字节（MSVC LCG，srand 种子取 GetTickCount 类计时源）
///    token[16:32] = AES-128-ECB(key = 游戏进程 PID 小端 uint32 + 12 个零字节, plaintext = token[0:16])
/// 2. 游戏 NetworkSocket 连启动器控制端口 127.0.0.1:&lt;launcherControlPort&gt;，
///    首条明文登录消息 = smid(0, 2B 大端) + gameid(short, 2B 小端) + token(UTF-8, 2B 小端长度前缀)；
///    之后全部 ChaCha8 流式加密（收密钥=令牌原样，发密钥=两半交换）。
/// 3. 启动器回包携带 CRC 盐值（32 位大写十六进制）——本客户端复算令牌后主动索取。
/// </summary>
public static class OfficialLauncherSaltClient
{
	private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(6.0);

	/// <summary>单个「令牌 × 端口」组合的等待回包上限（多组依次尝试，避免总体卡太久）。</summary>
	// 组合数 = 令牌候选(2) × 编码(2) × 端口候选(2) = 最多 8 组。
	// 若每组都等 5s 就是 40s，守候时一轮就要 40s，会错过握手窗口，故压到 2s。
	// 启动器若真会回包，通常毫秒级就回来了。
	private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(2.0);

	/// <summary>本地复算官方 DLL 的 GetToken 动态令牌算法（32 字节）。</summary>
	/// <param name="gamePid">运行 GetToken 的游戏进程 PID（AES 密钥 = PID 小端 + 12 零字节）</param>
	/// <param name="random16">可选固定随机块（测试用；为空则真随机</param>
	public static byte[] ComputeDynamicToken(int gamePid, byte[]? random16 = null)
	{
		byte[] rand = random16 ?? RandomNumberGenerator.GetBytes(16);
		if (rand.Length != 16)
		{
			throw new ArgumentException("random block must be 16 bytes", "random16");
		}
		byte[] key = new byte[16];
		BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(0, 4), (uint)gamePid);
		// key[4..16] 保持零
		byte[] token = new byte[32];
		Array.Copy(rand, 0, token, 0, 16);
		using Aes aes = Aes.Create();
		aes.Key = key;
		aes.Mode = CipherMode.ECB;
		aes.Padding = PaddingMode.None;
		using ICryptoTransform enc = aes.CreateEncryptor();
		byte[] second = enc.TransformFinalBlock(rand, 0, 16);
		Array.Copy(second, 0, token, 16, 16);
		return token;
	}

	/// <summary>算法自检：加密半段应能用同一密钥解密回随机块。</summary>
	public static bool SelfCheck()
	{
		try
		{
			byte[] rand = new byte[16];
			for (int i = 0; i < 16; i++)
			{
				rand[i] = (byte)(i * 7 + 3);
			}
			byte[] token = ComputeDynamicToken(12345, rand);
			byte[] key = new byte[16];
			BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(0, 4), 12345u);
			using Aes aes = Aes.Create();
			aes.Key = key;
			aes.Mode = CipherMode.ECB;
			aes.Padding = PaddingMode.None;
			using ICryptoTransform dec = aes.CreateDecryptor();
			byte[] back = dec.TransformFinalBlock(token, 16, 16);
			return back.AsSpan().SequenceEqual(rand);
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// **守候抢连**（闭合官方取盐的关键）。
	///
	/// 实测（2026-10-18 14:39）发现的事实：
	/// · 控制端口每次启动**随机分配**（用户已确认），命令行里的 launcherport/ControlPort 才是指向；
	/// · 游戏**稳定运行时该端口并不 LISTEN** —— 握手只在游戏启动瞬间发生一次，
	///   之后监听关闭；等我们看到「游戏已在跑」再去连，窗口早就过了（这就是此前一直
	///   「连上不回包 / 连接被拒」的真正原因，不是令牌错、也不是协议错）。
	///
	/// 因此必须反过来做：**先守候 → 用户启动游戏 → 进程一出现就立刻抢连**。
	/// 进程刚出现时监听可能还没建好，所以发现后要在短窗口内连续重试若干次。
	/// </summary>
	/// <param name="waitSeconds">守候总时长（等用户去启动游戏）</param>
	public static async Task<OfficialSaltResult> WatchAndAcquireAsync(int waitSeconds, Action<string>? log = null, CancellationToken ct = default(CancellationToken))
	{
		OfficialSaltResult result = new OfficialSaltResult
		{
			AlgorithmSelfCheck = SelfCheck()
		};
		DateTime deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
		LogHelper.Salt($"[官方守候] 开始守候 {waitSeconds}s，等待游戏进程出现");
		log?.Invoke($"[官方守候] 已启动，最长等 {waitSeconds} 秒。现在请去启动游戏。");

		int seenPid = 0;
		while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
		{
			LauncherScanResult scan = await LauncherScanner.ScanProcessesOnlyAsync(ct).ConfigureAwait(false);
			RunningGameInfo? game = scan.FirstGameWithPort;
			if (game != null && (game!.LauncherPort > 0 || game!.ControlPort > 0))
			{
				if (game!.Pid != seenPid)
				{
					seenPid = game!.Pid;
					LogHelper.Salt($"[官方守候] 发现游戏进程 pid={game!.Pid} {game!.ProcessName} "
						+ $"端口候选=[{game!.LauncherPort},{game!.ControlPort}] gameid={game!.GameIdShort}(已解析={game!.HasGameId})");
					log?.Invoke($"[官方守候] 捕捉到游戏进程 pid={game!.Pid}，立即抢连控制端口…");
					// 立刻启动内存扫描：TCP 通道已证实拿不到盐值（真实帧重放也无回包），
					// 而游戏随时可能退出，必须在捕捉到的第一时间就把内存扫了。
					if (!MemoryScans.ContainsKey(game!.Pid))
					{
						int scanPid = game!.Pid;
						// 文本形态与二进制形态**同时**启动：
						// 游戏随时会退出，任何一种晚一步都会变成 OpenProcess 错误 87。
						MemoryScans[scanPid] = GameMemoryScanner.ScanForSaltAsync(scanPid);
						MemoryBinScans[scanPid] = GameMemoryScanner.ScanForBinarySaltAsync(scanPid, GetKnownSalts());
						LogHelper.Salt($"[官方守候] 已对 pid={scanPid} 启动内存扫描（文本 + 二进制，后台）");
					}
				}
				// 进程刚出现时监听可能尚未建好 → 在短窗口内连续重试。
				// 只试 3 轮就回到外层重新扫描：实测发现若第一个进程是无效/残留的，
				// 连续试 6 轮（每轮 8 组合 × 2s ≈ 16s）会白耗近 100 秒，
				// 期间即使新游戏进程起来了也检测不到（15:00 那次就是这样错过 pid=9664 的）。
				for (int attempt = 1; attempt <= 3; attempt++)
				{
					OfficialSaltResult r = await TryAcquireAsync(ct, log).ConfigureAwait(false);
					if (r.Salt != null)
					{
						LogHelper.Salt($"[官方守候] 第 {attempt} 次抢连成功，盐值={r.Salt} 端口={r.ControlPort}");
						r.WatchHitPid = game!.Pid;
						return r;
					}
					result = r;
					if (r.HandshakeDone)
					{
						// 已经连上并发出登录帧，只是没解析出盐值 —— 再等回包试试
						await Task.Delay(300, ct).ConfigureAwait(false);
						continue;
					}
					await Task.Delay(120, ct).ConfigureAwait(false);
				}
			}
			await Task.Delay(150, ct).ConfigureAwait(false);
		}
		result.Message = "守候结束（" + waitSeconds + "s）未取到盐值："
			+ (seenPid == 0 ? "期间没有捕捉到任何游戏进程，请确认已在守候期间启动游戏并进入服务器。"
							: "捕捉到了游戏进程但抢连未成功，端口可能在进程出现前就已完成握手。可把守候时间再缩短、或先开抓包再启动游戏。");
		LogHelper.Salt("[官方守候] " + result.Message);
		return result;
	}

	/// <summary>
	/// 守候期间启动的内存扫描任务，按 pid 归档。
	/// **每捕捉到一个新的游戏进程就启动一次**（游戏会掉线重启，
	/// 实测 16:09 那次就先后出现了 pid=16880 与 pid=6672，只扫第一个会漏掉后来那个）。
	/// 之所以要在捕捉到的那一刻立刻扫，是因为游戏随时可能退出，晚一点就没得扫了。
	/// </summary>
	private static readonly Dictionary<int, Task<List<MemoryHit>>> MemoryScans = new Dictionary<int, Task<List<MemoryHit>>>();

	/// <summary>
	/// 二进制形态扫描任务，同样按 pid 归档。
	/// **必须和文本扫描同时启动**：实测（16:46 / 16:54）两次"二进制未命中"
	/// 其实是因为它被安排在守候**结束之后**才跑，那时游戏进程早已退出
	/// （`OpenProcess 失败，Win32 错误 87`），根本没扫成。
	/// </summary>
	private static readonly Dictionary<int, Task<List<MemoryHit>>> MemoryBinScans = new Dictionary<int, Task<List<MemoryHit>>>();

	public static Dictionary<int, Task<List<MemoryHit>>> LastMemoryScans => MemoryScans;

	/// <summary>当前可用于比对的已知盐值（本机记录 + 程序在用）。</summary>
	public static List<string> GetKnownSalts()
	{
		List<string> known = new List<string>();
		try
		{
			string? lastGood = MhyunBox.Settings.SettingsManager.Instance.LastGoodCrcSalt;
			if (!string.IsNullOrWhiteSpace(lastGood))
			{
				known.Add(lastGood!.Trim().ToUpperInvariant());
			}
		}
		catch
		{
		}
		try
		{
			if (!string.IsNullOrWhiteSpace(NeteaseYggdrasil.CrcSalt))
			{
				known.Add(NeteaseYggdrasil.CrcSalt.Trim().ToUpperInvariant());
			}
		}
		catch
		{
		}
		return known;
	}

	/// <summary>
	/// **守候抢连 + 同步抓包**（二合一）。
	///
	/// 为什么必须合在一起：盲试令牌/编码/端口组合已经穷尽（两次守候共试过 4 种组合 ×
	/// 多个端口，全部「连得上、无回包」），再猜下去没有意义——**必须看到游戏自己发的那条
	/// 真实登录帧**。而握只在启动瞬间发生，所以抓包和守候必须同时开始，用户只需启动一次游戏。
	///
	/// 抓包用 port=0（全回环），结束后再按守候实际用过的端口过滤，
	/// 这样即使握手早于我们发现进程，也能在 pcap 里找到那段报文。
	/// </summary>
	public static async Task<string> WatchWithCaptureAsync(int waitSeconds, Action<string>? log = null, CancellationToken ct = default(CancellationToken))
	{
		StringBuilder sb = new StringBuilder();
		string? tshark = OfficialTrafficCapture.FindTshark();
		if (tshark == null)
		{
			return "未找到 tshark，只做守候。";
		}
		// 抓包比守候多跑 20 秒，保证守候结束时抓包仍在窗口内
		Task<CaptureResult> capTask = OfficialTrafficCapture.CaptureAsync(0, waitSeconds + 20, ct);
		log?.Invoke("[官方守候] 抓包已同步启动，现在请去启动游戏");
		sb.Append("抓包已同步启动（").Append(waitSeconds + 20).Append("s 全回环）。现在请去启动游戏。\n");

		OfficialSaltResult r = await WatchAndAcquireAsync(waitSeconds, log, ct).ConfigureAwait(false);

		CaptureResult cap;
		try
		{
			cap = await capTask.ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			cap = new CaptureResult
			{
				Message = "抓包异常: " + ex.Message
			};
		}

		sb.Append('\n');
		if (r.Salt != null)
		{
			sb.Append("✅ 守候抢连成功，盐值 = ").Append(r.Salt).Append("（端口 ").Append(r.ControlPort).Append("）\n");
		}
		else
		{
			sb.Append("❌ 抢连未成功：").Append(r.Message).Append('\n');
		}
		sb.Append("抓包：").Append(cap.Message).Append('\n');

		// ---- 内存扫描结果（TCP 通道之外的备选路径）----
		sb.Append('\n');
		if (MemoryScans.Count == 0)
		{
			sb.Append("内存扫描：未捕捉到游戏进程，未执行。\n");
		}
		// 逐个进程汇总（游戏可能重启过，会有多个 pid）
		foreach (KeyValuePair<int, Task<List<MemoryHit>>> kv in MemoryScans)
		{
		sb.Append("--- 内存扫描 pid=").Append(kv.Key).Append(" ---\n");
		Task<List<MemoryHit>>? scanTask = kv.Value;
		{
			List<MemoryHit> hits;
			try
			{
				hits = await scanTask.ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				hits = new List<MemoryHit>();
				sb.Append("内存扫描异常: ").Append(ex.Message).Append('\n');
			}
			sb.Append("内存扫描：命中 ").Append(hits.Count).Append(" 个 32 位十六进制候选。\n");
			// 与已知有效盐值比对：命中则说明盐值确实驻留在游戏内存里
			List<string> known = new List<string>();
			string? lastGood = MhyunBox.Settings.SettingsManager.Instance.LastGoodCrcSalt;
			if (!string.IsNullOrWhiteSpace(lastGood))
			{
				known.Add(lastGood!);
			}
			if (!string.IsNullOrWhiteSpace(NeteaseYggdrasil.CrcSalt))
			{
				known.Add(NeteaseYggdrasil.CrcSalt);
			}
			List<MemoryHit> matched = GameMemoryScanner.MatchKnown(hits, known);
			// 关键结论必须进日志：面板上的内容一闪而过，日志才是事后能查的
			LogHelper.Salt($"[内存] 已知盐值比对：候选 {hits.Count} 个，比对 [{string.Join(",", known)}]，命中 {matched.Count} 处");
			if (matched.Count > 0)
			{
				sb.Append("\n★★★ 在游戏内存里找到了已知有效盐值！\n");
				foreach (MemoryHit mh in matched)
				{
					sb.Append("  ").Append(mh.ToString()).Append('\n');
					LogHelper.Salt("[内存] ★★★ 命中已知有效盐值: " + mh);
				}
				sb.Append("→ 证明 CrcSalt 确实驻留在游戏进程内存中，且不通过控制端口的 TCP 通道下发。\n");
			}
			else
			{
				sb.Append("（文本形态未命中；看**二进制形态**结果）\n");
				// 注意：二进制扫描在捕捉到进程时就已启动，这里只是取结果。
				// 如果到现在才发起，进程多半已经退出（OpenProcess 错误 87）。
				List<MemoryHit> binHits;
				if (MemoryBinScans.TryGetValue(kv.Key, out Task<List<MemoryHit>>? binTask))
				{
					binHits = await binTask.ConfigureAwait(false);
				}
				else
				{
					binHits = await GameMemoryScanner.ScanForBinarySaltAsync(kv.Key, known, ct: ct).ConfigureAwait(false);
				}
				LogHelper.Salt($"[内存-二进制] pid={kv.Key} 命中 {binHits.Count} 处");
				if (binHits.Count > 0)
				{
					sb.Append("\n★★★ 二进制形态命中已知盐值！\n");
					foreach (MemoryHit bh in binHits.Take(10))
					{
						sb.Append("  ").Append(bh.ToString()).Append('\n');
						LogHelper.Salt("[内存-二进制] ★★★ " + bh);
					}
					sb.Append("→ 盐值以 byte[16] 形式驻留在游戏内存，可据此定位其来源。\n");
				}
				else
				{
					sb.Append("（二进制形态也未命中：盐值可能从未以明文驻留内存，"
						+ "或游戏拿到后立即使用/加密保存，未保留副本）\n");
				}
			}
			// 候选落盘：进程退出后就再也扫不到了，必须先存下来
			string dumpPath = "";
			try
			{
				dumpPath = GameMemoryScanner.SaveHits(hits, kv.Key);
				sb.Append("候选清单已保存到：").Append(dumpPath).Append('\n');
			}
			catch (Exception ex)
			{
				sb.Append("候选清单保存失败: ").Append(ex.Message).Append('\n');
			}
			foreach (MemoryHit h in hits.Take(12))
			{
				sb.Append("  · ").Append(h.ToString()).Append('\n');
			}
		}
		}

		// 按守候实际用过的端口过滤，找出游戏 ↔ 启动器的真实报文
		int port = r.ControlPort;
		if (port > 0 && cap.Payloads.Count > 0)
		{
			List<CapturedPayload> hits = cap.Payloads.Where((CapturedPayload p) => p.SrcPort == port || p.DstPort == port).ToList();
			sb.Append("\n--- 控制端口 ").Append(port).Append(" 上的真实报文：").Append(hits.Count).Append(" 个 ---\n");
			if (hits.Count == 0)
			{
				sb.Append("（该端口上完全没有流量 —— 说明握手发生在抓包开始之前，或根本不走 TCP）\n");
			}
			// 先从游戏发出的帧里提取真实令牌，用于报文展示时的脱敏。
			CaptureResult sub = new CaptureResult
			{
				Port = port,
				Payloads = hits,
				PcapPath = cap.PcapPath
			};
			List<string> toks = OfficialTrafficCapture.ExtractTokenCandidates(sub);
			string[] tokenPatterns = toks.Distinct().ToArray();
			foreach (CapturedPayload h in hits.Take(15))
			{
				string dir = (h.DstPort == port) ? "游戏→启动器" : "启动器→游戏";
				List<string> framePatterns = new List<string>(tokenPatterns);
				// 登录帧布局：2B 长度 + smid(2B) + gameid(2B) + 令牌。
				// 游戏→启动器的报文从偏移 6 起整体脱敏，即使令牌提取失败也不会泄露。
				if (h.DstPort == port && h.Hex.Length > 12)
				{
					// 展示只取前 120 个十六进制字符，脱敏模式也只需覆盖这一段，避免复制整包。
					framePatterns.Add(h.Hex.Substring(12, Math.Min(256, h.Hex.Length - 12)));
				}
				string shown = SecretRedaction.HexText(h.Hex, 120, framePatterns.ToArray());
				sb.Append(dir).Append(' ').Append(h.SrcPort).Append("->").Append(h.DstPort).Append(' ').Append(h.Length).Append("B: ")
					.Append(shown).Append('\n');
			}
			if (toks.Count > 0)
			{
				sb.Append("\n★★ 抓到真实令牌候选（已脱敏，仅显示指纹）：\n");
				foreach (string t in toks.Distinct())
				{
					sb.Append("  ").Append(SecretRedaction.TokenFingerprint(t)).Append('\n');
				}
			}

			// 抢连失败时，把抓到的真实帧原样重放 —— 绕开全部协议猜测
			if (r.Salt == null)
			{
				sb.Append("\n--- 真实帧重放 ---\n");
				var (okR, msgR) = await ReplayCapturedFramesAsync(port, hits, log, ct).ConfigureAwait(false);
				sb.Append(okR ? "★ " : "  ").Append(msgR).Append('\n');
				if (okR)
				{
					// 回包里若有明文盐值直接取出
					System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
						msgR, "(?<![0-9A-Za-z])([0-9A-F]{32})(?![0-9A-Za-z])");
					if (m.Success)
					{
						sb.Append("\n✅✅ 通过真实帧重放取得盐值 = ").Append(m.Groups[1].Value).Append('\n');
					}
				}
			}
		}
		else if (cap.Payloads.Count > 0)
		{
			sb.Append('\n').Append(OfficialTrafficCapture.BuildReport(cap));
		}
		return sb.ToString();
	}

	/// <summary>
	/// **真实帧重放**：把抓包抓到的、游戏自己发给控制端口的帧**原样重放**。
	///
	/// 为什么需要它（2026-10-18 15:11 抓包证实）：
	/// · 游戏确实会向 launcherport 发一条登录帧（2B 长度前缀 + smid + gameid + UTF-8 令牌）；
	/// · 其**结构**和我们构造的完全一致，唯一区别是**令牌内容**——
	///   游戏用的是 JVM 内 GetToken() 的真实输出，而我们只能用复算值 / -DToken 去猜；
	/// · 既然猜不对，最省事的办法就是**不猜**：把抓到的真实字节原样发回去。
	///
	/// 这绕开了全部协议猜测，只要启动器不是"只认第一条连接"就能拿到回包。
	/// </summary>
	public static async Task<(bool Ok, string Message)> ReplayCapturedFramesAsync(
		int port, List<CapturedPayload> frames, Action<string>? log = null, CancellationToken ct = default(CancellationToken))
	{
		if (port <= 0 || frames.Count == 0)
		{
			return (false, "没有可重放的帧");
		}
		// 只取"发往控制端口"的帧（游戏→启动器方向），按内容去重，最多试 6 条
		List<string> seen = new List<string>();
		List<byte[]> candidates = new List<byte[]>();
		foreach (CapturedPayload f in frames)
		{
			if (f.DstPort != port)
			{
				continue;
			}
			if (f.Length < 10 || f.Length > 4096)
			{
				continue;
			}
			if (seen.Contains(f.Hex))
			{
				continue;
			}
			seen.Add(f.Hex);
			try
			{
				candidates.Add(Convert.FromHexString(f.Hex));
			}
			catch
			{
			}
			if (candidates.Count >= 6)
			{
				break;
			}
		}
		if (candidates.Count == 0)
		{
			return (false, "抓包里没有发往控制端口 " + port + " 的帧");
		}
		log?.Invoke($"[官方重放] 准备重放 {candidates.Count} 条真实帧到 127.0.0.1:{port}");
		for (int i = 0; i < candidates.Count; i++)
		{
			byte[] raw = candidates[i];
			// 登录帧布局：2B 长度 + smid(2B) + gameid(2B) + 令牌，故偏移 6 之后都视为敏感载荷。
			byte[] replaySecret = raw.Length > 6 ? raw[6..] : Array.Empty<byte>();
			LogHelper.Salt($"[官方重放] 第 {i + 1}/{candidates.Count} 条，{raw.Length}B = " + SecretRedaction.Hex(raw, 64, replaySecret));
			try
			{
				using TcpClient client = new TcpClient();
				using CancellationTokenSource cts = new CancellationTokenSource(2500);
				await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
				NetworkStream stream = client.GetStream();
				await stream.WriteAsync(raw, cts.Token).ConfigureAwait(false);
				await stream.FlushAsync(cts.Token).ConfigureAwait(false);
				byte[]? reply = await TryPeekAsync(stream, 2000).ConfigureAwait(false);
				if (reply != null && reply.Length > 0)
				{
					string hex = SecretRedaction.Hex(reply, 96, replaySecret);
					LogHelper.Salt($"[官方重放] ★ 收到回包 {reply.Length}B = " + hex);
					string? salt = TryExtractPlainSalt(reply);
					if (!string.IsNullOrEmpty(salt))
					{
						LogHelper.Salt("[官方重放] ★★ 回包中直接含盐值 = " + salt);
						return (true, "重放真实帧成功，回包含明文盐值 " + salt + "（" + reply.Length + "B: " + hex + "）");
					}
					return (true, "重放真实帧收到回包 " + reply.Length + "B = " + hex + "（未直接识别为盐值，需进一步解析）");
				}
				LogHelper.Salt("[官方重放] 无回包");
			}
			catch (Exception ex)
			{
				LogHelper.Salt($"[官方重放] 第 {i + 1} 条异常：{ex.GetType().Name}: {ex.Message}");
			}
		}
		return (false, $"重放了 {candidates.Count} 条真实帧，启动器均未回应");
	}

	/// <summary>
	/// 主动索取：扫盘找官方启动器 → 找运行中游戏的控制端口 → 复算令牌握手 → 解析盐值。
	/// 任何一步失败都返回带诊断信息的结果（Salt 为 null），不抛异常。
	/// </summary>
	public static async Task<OfficialSaltResult> TryAcquireAsync(CancellationToken ct, Action<string>? log = null)
	{
		OfficialSaltResult result = new OfficialSaltResult
		{
			AlgorithmSelfCheck = SelfCheck()
		};
		try
		{
			LauncherScanResult scan = await LauncherScanner.ScanAsync(ct, log).ConfigureAwait(false);
			OfficialLauncherInfo? launcher = scan.FirstLauncher;
			result.LauncherPath = launcher?.LauncherExe;
			LogHelper.Salt($"[官方] 扫盘：安装 {scan.Launchers.Count} 处，相关进程 {scan.RunningGames.Count} 个，patchlist 最新 {scan.PatchlistVersion}");
			foreach (OfficialLauncherInfo li in scan.Launchers)
			{
				LogHelper.Salt($"[官方]   安装 {li.LauncherExe} 版本={li.Version} mcl.common={li.HasMclCommon} authdll={li.HasAuthBridgeDll} 运行中={li.IsRunning}");
			}
			foreach (RunningGameInfo gi in scan.RunningGames)
			{
				LogHelper.Salt($"[官方]   进程 pid={gi.Pid} {gi.ProcessName} 控制端口={gi.LauncherPort} gameid={gi.GameIdShort} userId={gi.UserId}");
			}
			if (!string.IsNullOrEmpty(launcher?.Version))
			{
				result.Version = launcher!.Version;
			}
			else
			{
				result.Version = scan.PatchlistVersion;
			}
			RunningGameInfo? game = scan.FirstGameWithPort;
			// 注意：gameid 的合法值就可能是 0（实测常为 [0,0]），必须用 HasGameId 判断，
			// 不能用 GameIdShort != 0 —— 否则会误报「缺少 gameid」而永远取不到盐
			if (game == null || !game.HasGameId)
			{
				result.Message = ((game == null) ? LauncherScanner.NeedGameProcessHint : "游戏进程命令行里没有 userProperties.gameid 字段，无法构造登录消息");
				if (!string.IsNullOrEmpty(result.Version))
				{
					result.Message = result.Message + "（已取得启动器版本 " + result.Version + "，可用于版本配对）";
				}
				log?.Invoke("[官方盐值] " + result.Message);
				return result;
			}
			// 端口候选：游戏 NetworkSocket 实际连的 userProperties.launcherport 优先，
			// 拿不到再退到 -DlauncherControlPort（实测两者可能不是同一个值）。
			List<int> portCandidates = new List<int>();
			if (game.LauncherPort > 0)
			{
				portCandidates.Add(game.LauncherPort);
			}
			if (game.ControlPort > 0 && !portCandidates.Contains(game.ControlPort))
			{
				portCandidates.Add(game.ControlPort);
			}
			result.ControlPort = (portCandidates.Count > 0) ? portCandidates[0] : game.LauncherPort;
			// 令牌候选：
			//   1) 我们按 §5 算法复算的 GetToken（随机块是我们自己生成的）
			//   2) 启动器经 -DToken 下发给游戏的登录态令牌——这是启动器自己发出的真值，
			//      若控制端口认它，就不必依赖我们复算的随机数
			List<byte[]> tokens = new List<byte[]> { ComputeDynamicToken(game.Pid) };
			List<string> tokenTags = new List<string> { "复算GetToken" };
			if (game.Token.Length == 64)
			{
				try
				{
					tokens.Add(Convert.FromHexString(game.Token));
					tokenTags.Add("-DToken(启动器下发)");
				}
				catch
				{
				}
			}
			LogHelper.Salt($"[官方] 选中进程 pid={game.Pid} {game.ProcessName} 端口候选=[{string.Join(",", portCandidates)}] gameid={game.GameIdShort}(已解析={game.HasGameId}) userId={game.UserId} 令牌候选=[{string.Join(",", tokenTags)}]");
			log?.Invoke($"[官方盐值] 复算令牌完成（pid={game.Pid} gameid={game.GameIdShort}），开始控制端口握手");

			// 编码候选：**先试原始 32 字节直传**（长度前缀=32，最符合"令牌就是 32 字节"的直觉），
			// 再试 ISO-8859-1→UTF-8（长度前缀 45~56，随内容浮动）。
			// 守候日志显示按 UTF-8 发时长度每次都不同且启动器从不回包，故必须两种都试。
			bool[] encodings = new bool[2] { true, false };

			for (int ti = 0; ti < tokens.Count; ti++)
			{
				byte[] token = tokens[ti];
				string tag = tokenTags[ti];
				LogHelper.Salt($"[官方] 试用令牌【{tag}】= {token.Length}B " + SecretRedaction.Hex(token, token.Length, token));

				foreach (bool raw in encodings)
				{
				byte[] login = BuildLoginMessage(game.GameIdShort, token, raw);
				string encTag = raw ? "原始32B" : "UTF-8";
				LogHelper.Salt($"[官方] 登录消息 [{encTag}] ({login.Length}B) = " + SecretRedaction.Hex(login, login.Length, token));

				foreach (int candidate in portCandidates)
				{
					using TcpClient client = new TcpClient();
					using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
					cts.CancelAfter(PerAttemptTimeout);
					try
					{
						LogHelper.Salt($"[官方] 正在连接 127.0.0.1:{candidate} …");
						await client.ConnectAsync("127.0.0.1", candidate, cts.Token).ConfigureAwait(false);
						result.ControlPort = candidate;
						result.HandshakeDone = true;
						NetworkStream stream = client.GetStream();
					LogHelper.Salt($"[官方] TCP 已连接 127.0.0.1:{candidate}");

					// **被动探测**：连上后先别急着发，等一下看服务端会不会主动推数据。
					// 此前一直是"连上就发登录帧"，如果协议其实是服务端先说话，我们就永远等不到回包。
					byte[]? pushed = await TryPeekAsync(stream, 900).ConfigureAwait(false);
					if (pushed != null && pushed.Length > 0)
					{
						LogHelper.Salt($"[官方] ★ 服务端主动推送 {pushed.Length}B（未发任何请求）= " + SecretRedaction.Hex(pushed, 80, token));
						string? salt0 = TryExtractPlainSalt(pushed);
						if (salt0 == null)
						{
							salt0 = ExtractSaltFromResponse(pushed, token);
						}
						if (!string.IsNullOrEmpty(salt0))
						{
							result.Salt = salt0;
							result.Ok = true;
							result.Message = $"控制端口索取成功（服务端主动推送，端口={candidate}）";
							LogHelper.Salt("[官方] 索取成功（被动模式），盐值 = " + salt0);
							return result;
						}
					}
					else
					{
						LogHelper.Salt("[官方] 被动探测：未收到服务端推送，改为主动发登录帧");
					}

					byte[] frame = new byte[2 + login.Length];
					frame[0] = (byte)login.Length;
					frame[1] = (byte)(login.Length >> 8);
					Array.Copy(login, 0, frame, 2, login.Length);
					LogHelper.Salt($"[官方] 发送帧 [{encTag}] ({frame.Length}B) = " + SecretRedaction.Hex(frame, frame.Length, token));
					await stream.WriteAsync(frame, cts.Token).ConfigureAwait(false);
					await stream.FlushAsync(cts.Token).ConfigureAwait(false);
					LogHelper.Salt("[官方] 登录帧已发送，等待回包");
					// 读响应帧（2B 小端长度 + 载荷），最多等 2 帧
					for (int attempt = 0; attempt < 2; attempt++)
					{
						byte[]? payload = await ReadResponseFrameAsync(stream, cts.Token).ConfigureAwait(false);
						if (payload == null || payload.Length == 0)
						{
							LogHelper.Salt($"[官方] 第 {attempt + 1} 帧：空/连接关闭（payload={(payload == null ? "null" : payload.Length + "B")}）");
							break;
						}
						LogHelper.Salt($"[官方] 收到第 {attempt + 1} 帧 {payload.Length}B，明文扫描前 64B = " + SecretRedaction.Hex(payload, 64, token));
						string? salt = TryExtractPlainSalt(payload);
						if (salt == null)
						{
							salt = ExtractSaltFromResponse(payload, token);
						}
						if (!string.IsNullOrEmpty(salt))
						{
							result.Salt = salt;
							result.Ok = true;
							result.Message = $"控制端口索取成功（令牌={tag}，编码={encTag}，端口={candidate}）";
							LogHelper.Salt("[官方] 索取成功，盐值 = " + salt);
							log?.Invoke("[官方盐值] 索取成功: " + salt);
							return result;
						}
						LogHelper.Salt($"[官方] 第 {attempt + 1} 帧未解析到盐值（已试明文 + 2 种密钥 × 3 种 nonce；若握手其实成功，怀疑 ChaCha 轮数不匹配）");
						log?.Invoke($"[官方盐值] 响应帧 {payload.Length}B 未解析到盐值（已尝试多种密钥排列）");
					}
					LogHelper.Salt($"[官方] 组合【{tag}/{encTag} @ {candidate}】未拿到盐值，换下一个");
				}
				// 注意：OperationCanceledException 必须排在 Exception 之前，否则不可达
				catch (OperationCanceledException)
				{
					LogHelper.Salt($"[官方] 组合【{tag}/{encTag} @ {candidate}】等待回包超时（{PerAttemptTimeout.TotalSeconds:0}s），换下一个");
				}
				catch (Exception ex)
				{
					LogHelper.Salt($"[官方] 组合【{tag}/{encTag} @ {candidate}】异常：{ex.GetType().Name}: {ex.Message}");
				}
				}
				}
			}
			result.Message = "已连上控制端口并发出登录帧（试过全部令牌/编码/端口组合，且先做过被动探测），但启动器始终没有回包。"
				+ "常见原因：该端口只服务游戏自己建立的会话，不响应后续连入的连接。";
			if (!string.IsNullOrEmpty(result.Version))
			{
				result.Message = result.Message + "（已取得启动器版本 " + result.Version + "）";
			}
			log?.Invoke("[官方盐值] " + result.Message);
		}
		catch (OperationCanceledException)
		{
			// 已连上并发出登录帧，但启动器在超时内没回包。
			// 常见原因：控制端口只服务游戏自己建立的会话，不响应后来连入的连接；
			// 或我们复算的令牌与启动器期望的不一致。
			result.Message = "已连上控制端口并发出登录帧，但启动器在 "
				+ HandshakeTimeout.TotalSeconds.ToString("0") + " 秒内没有回包。"
				+ "常见原因：该端口只服务游戏自己建立的会话，不响应后续连入的连接"
				+ (result.HandshakeDone ? "；也可能是令牌与启动器期望的不一致" : "");
			LogHelper.Salt("[官方] 等待回包超时（OperationCanceledException）：" + result.Message);
			log?.Invoke("[官方盐值] " + result.Message);
		}
		catch (Exception ex)
		{
			result.Message = "官方盐值索取失败: " + ex.GetType().Name + ": " + ex.Message;
			log?.Invoke("[官方盐值] " + result.Message);
		}
		return result;
	}

	/// <summary>
	/// 构造 NetworkSocket 登录消息：smid(0, 2B 大端) + gameid(short, 2B 小端) + token(2B 小端长度前缀 + 载荷)。
	/// </summary>
	/// <param name="rawBytes">
	/// true = 令牌**原始 32 字节直传**（长度前缀 = 32）；
	/// false = 按 ISO-8859-1→UTF-8 转换后传（长度前缀 = 45~56，随令牌内容浮动）。
	///
	/// 实测（2026-10-18 守候日志）发现按 UTF-8 发时长度每次都不一样，且启动器从不回包。
	/// 服务端按 latin1 解析字符串才能还原 32 字节令牌，因此两种编码都要试。
	/// </param>
	public static byte[] BuildLoginMessage(int gameIdShort, byte[] token, bool rawBytes = false)
	{
		byte[] payload = rawBytes
			? token
			: Encoding.Convert(Encoding.GetEncoding("ISO-8859-1"), Encoding.UTF8, token);
		using MemoryStream ms = new MemoryStream();
		ms.WriteByte(0);
		ms.WriteByte(0);
		ms.WriteByte((byte)(gameIdShort & 0xFF));
		ms.WriteByte((byte)((gameIdShort >> 8) & 0xFF));
		ms.WriteByte((byte)(payload.Length & 0xFF));
		ms.WriteByte((byte)((payload.Length >> 8) & 0xFF));
		ms.Write(payload, 0, payload.Length);
		return ms.ToArray();
	}

	/// <summary>
	/// 被动探测：连上后先等一小会儿，看服务端会不会主动推数据。
	/// 超时返回 null（不代表失败，只是这次没推）。
	/// </summary>
	private static async Task<byte[]?> TryPeekAsync(NetworkStream stream, int millis)
	{
		try
		{
			using CancellationTokenSource cts = new CancellationTokenSource(millis);
			byte[] buf = new byte[8192];
			// Stream.ReadAsync 在没数据时会一直等，靠 cts 超时打断
			ValueTask<int> vt = stream.ReadAsync(buf, cts.Token);
			int n = await vt.AsTask().ConfigureAwait(false);
			if (n > 0)
			{
				byte[] got = new byte[n];
				Array.Copy(buf, got, n);
				return got;
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 在载荷里直接扫 32 位十六进制盐值（明文形态）。
	/// 若服务端其实没加密、或响应里就带明文盐，这一步就能直接拿到，
	/// 不必依赖 ChaCha 密钥排列是否正确。
	/// </summary>
	private static string? TryExtractPlainSalt(byte[] data)
	{
		// ASCII 形态
		string ascii = Encoding.ASCII.GetString(data);
		System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
			ascii, "(?<![0-9A-Za-z])([0-9A-Fa-f]{32})(?![0-9A-Za-z])");
		if (m.Success)
		{
			return m.Groups[1].Value.ToUpperInvariant();
		}
		// UTF-16LE 形态（Java writeString 有时用）
		try
		{
			string u16 = Encoding.Unicode.GetString(data);
			System.Text.RegularExpressions.Match m2 = System.Text.RegularExpressions.Regex.Match(
				u16, "(?<![0-9A-Za-z])([0-9A-Fa-f]{32})(?![0-9A-Za-z])");
			if (m2.Success)
			{
				return m2.Groups[1].Value.ToUpperInvariant();
			}
		}
		catch
		{
		}
		return null;
	}

	private static async Task<byte[]?> ReadResponseFrameAsync(NetworkStream stream, CancellationToken ct)
	{
		byte[] len = new byte[2];
		int read = await ReadExactAsync(stream, len, ct).ConfigureAwait(false);
		if (read < 2)
		{
			return null;
		}
		int n = len[0] | (len[1] << 8);
		if (n <= 0 || n > 65535)
		{
			return null;
		}
		byte[] body = new byte[n];
		int got = await ReadExactAsync(stream, body, ct).ConfigureAwait(false);
		if (got < n)
		{
			return null;
		}
		return body;
	}

	private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
	{
		int total = 0;
		while (total < buffer.Length)
		{
			int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
			if (n <= 0)
			{
				break;
			}
			total += n;
		}
		return total;
	}

	/// <summary>从响应载荷提取盐值：先试明文，再按多种密钥/nonce 排列试 ChaCha8 解密。</summary>
	public static string? ExtractSaltFromResponse(byte[] payload, byte[] token)
	{
		// 1) 明文直查
		string? direct = FindSalt(payload);
		if (direct != null)
		{
			return direct;
		}
		// 2) 密钥排列：原样 / 两半交换
		byte[] swapped = new byte[32];
		Array.Copy(token, 16, swapped, 0, 16);
		Array.Copy(token, 0, swapped, 16, 16);
		byte[][] keys = new byte[2][] { token, swapped };
		// 3) nonce 候选
		List<byte[]> nonces = new List<byte[]>
		{
			new byte[12],
			new byte[8]
		};
		byte[] ietf = Encoding.ASCII.GetBytes("163 NetEase\n");
		if (ietf.Length == 12)
		{
			nonces.Add(ietf);
		}
		// 注意（逆向遗留疑点）：Java 侧是 Library.NewChaCha(8, token)（见逆向文档 §10.2），
		// 字面是 8 轮；而 BouncyCastle 的 ChaCha7539Engine 是 RFC 7539 的 ChaCha20。
		// 若握手成功但后续报文解不开，第一嫌疑就是这里 —— 届时需换成 8 轮实现
		// （BouncyCastle 未提供 ChaCha8 引擎，需自行实现后用同样的 key/nonce 排列重试）。
		foreach (byte[] key in keys)
		{
			foreach (byte[] nonce in nonces)
			{
				try
				{
					ChaCha7539Engine engine = new ChaCha7539Engine();
					engine.Init(forEncryption: false, new ParametersWithIV(new KeyParameter(key), nonce));
					byte[] plain = new byte[payload.Length];
					engine.ProcessBytes(payload, 0, payload.Length, plain, 0);
					string? salt = FindSalt(plain);
					if (salt != null)
					{
						return salt;
					}
				}
				catch
				{
				}
			}
		}
		return null;
	}

	private static string? FindSalt(byte[] data)
	{
		if (data == null || data.Length < 32)
		{
			return null;
		}
		string ascii = Encoding.GetEncoding("ISO-8859-1").GetString(data);
		// JSON 形式优先
		Match mj = Regex.Match(ascii, "\"salt\"\\s*:\\s*\"([0-9A-Fa-f]{32})\"", RegexOptions.IgnoreCase);
		if (mj.Success)
		{
			return mj.Groups[1].Value.ToUpperInvariant();
		}
		Match mc = Regex.Match(ascii, "\"crcSalt\"\\s*:\\s*\"([0-9A-Fa-f]{32})\"", RegexOptions.IgnoreCase);
		if (mc.Success)
		{
			return mc.Groups[1].Value.ToUpperInvariant();
		}
		// 裸 32 位大写十六进制（盐的典型形态）
		Match mb = Regex.Match(ascii, "(?<![0-9A-Za-z])([0-9A-F]{32})(?![0-9A-Za-z])");
		if (mb.Success)
		{
			return mb.Groups[1].Value.ToUpperInvariant();
		}
		return null;
	}
}
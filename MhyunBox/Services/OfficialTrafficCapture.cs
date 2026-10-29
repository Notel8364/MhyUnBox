using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>一次抓取里解析出的单个 TCP 载荷。</summary>
public sealed class CapturedPayload
{
	public string Hex = "";

	/// <summary>源端口（用于区分方向：游戏→启动器 还是 启动器→游戏）。</summary>
	public int SrcPort;

	public int DstPort;

	public int Length => (Hex.Length + 1) / 2;

	/// <summary>去掉 2 字节小端长度头之后的登录消息体。</summary>
	public string BodyHex
	{
		get
		{
			if (Length < 2)
			{
				return "";
			}
			return Hex.Substring(4);
		}
	}
}

/// <summary>抓取结果。</summary>
public sealed class CaptureResult
{
	public bool Ok;

	public string Message = "";

	public int Port;

	public List<CapturedPayload> Payloads = new List<CapturedPayload>();

	public string PcapPath = "";
}

/// <summary>
/// 官方（网易）控制端口流量抓取 —— **仅调试模式使用**。
///
/// 目的：拿到游戏 ↔ 启动器之间控制端口上的真实报文，用于闭合官方 DLL 取盐
/// （我们目前能连上并发出登录帧，但启动器不回包，需要看到真实交互才能对齐）。
///
/// 实现：调用本机 tshark（Wireshark 命令行）抓 **Npcap 回环适配器**
/// （`\Device\NPF_Loopback`，普通网卡抓不到 127.0.0.1 的流量），
/// 按控制端口过滤，再把 TCP 载荷导出成 hex 供分析。
/// </summary>
public static class OfficialTrafficCapture
{
	/// <summary>Npcap 的回环抓包适配器（tshark -D 里显示为 "Adapter for loopback traffic capture"）。</summary>
	public const string LoopbackInterface = "\\Device\\NPF_Loopback";

	/// <summary>tshark.exe 的候选路径（第一个存在的即为所用）。</summary>
	private static readonly string[] TsharkCandidates = new string[4]
	{
		"J:\\RE_Tools\\Wireshark\\tshark.exe",
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wireshark", "tshark.exe"),
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Wireshark", "tshark.exe"),
		"C:\\Program Files\\Wireshark\\tshark.exe"
	};

	/// <summary>定位 tshark.exe，找不到返回 null。</summary>
	public static string? FindTshark()
	{
		foreach (string c in TsharkCandidates)
		{
			try
			{
				if (!string.IsNullOrEmpty(c) && File.Exists(c))
				{
					return c;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	/// <summary>列出 tshark 可见的抓包接口（含索引与描述），失败返回空串。</summary>
	public static async Task<string> ListInterfacesAsync()
	{
		string? tshark = FindTshark();
		if (tshark == null)
		{
			return "(未找到 tshark.exe)";
		}
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo(tshark)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			psi.ArgumentList.Add("-D");
			using Process p = Process.Start(psi);
			if (p == null)
			{
				return "(启动 tshark 失败)";
			}
			string outp = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
			p.WaitForExit(8000);
			return outp.Trim();
		}
		catch (Exception ex)
		{
			return "列出接口失败: " + ex.Message;
		}
	}

	/// <summary>当前正在跑的 tshark 进程（用于「主动停止」）。</summary>
	private static Process? _currentProcess;

	/// <summary>当前抓取的取消源（主动停止时先取消等待，再杀进程）。</summary>
	private static CancellationTokenSource? _currentCts;

	/// <summary>是否正在抓取。</summary>
	public static bool IsCapturing => _currentProcess != null && !_currentProcess.HasExited;

	/// <summary>
	/// 主动停止当前抓取。tshark 是逐包写盘的，Kill 之后 pcap 仍可读
	/// （可能少最后几包，tshark 会提示 "cut short"，但已抓到的部分完好）。
	/// </summary>
	public static void StopCapture()
	{
		try
		{
			_currentCts?.Cancel();
		}
		catch
		{
		}
		try
		{
			Process? p = _currentProcess;
			if (p != null && !p.HasExited)
			{
				p.Kill(entireProcessTree: true);
				LogHelper.Salt("[抓包] 已主动停止（用户点击停止）");
			}
		}
		catch
		{
		}
	}

	/// <summary>
	/// 抓取指定控制端口上的回环流量。
	/// </summary>
	/// <param name="port">控制端口（userProperties.launcherport）；传 0 表示不限端口、抓全部回环流量</param>
	/// <param name="seconds">抓取时长（秒）</param>
	public static async Task<CaptureResult> CaptureAsync(int port, int seconds, CancellationToken ct = default(CancellationToken))
	{
		CaptureResult result = new CaptureResult
		{
			Port = port
		};
		string? tshark = FindTshark();
		if (tshark == null)
		{
			result.Ok = false;
			result.Message = "未找到 tshark.exe（已试 J:\\RE_Tools\\Wireshark 与 Program Files\\Wireshark）。"
				+ "请确认 Wireshark 已安装，或在「调试」里手动指定路径。";
			return result;
		}
		// port<=0 → 不限端口，抓全部回环流量（游戏还没起来、或想看更新阶段流量时用）
		if (port <= 0)
		{
			LogHelper.Salt("[抓包] 未指定端口，将抓取全部 127.0.0.1 回环流量");
		}
		string pcap;
		try
		{
			string dir = Path.Combine(Path.GetTempPath(), "MhyunBoxCapture");
			Directory.CreateDirectory(dir);
			pcap = Path.Combine(dir, $"official_{port}_{DateTime.Now:yyyyMMdd_HHmmss}.pcap");
		}
		catch (Exception ex)
		{
			result.Ok = false;
			result.Message = "无法创建抓包输出目录: " + ex.Message;
			return result;
		}
		result.PcapPath = pcap;
		LogHelper.Salt($"[抓包] 开始：tshark={tshark} 接口={LoopbackInterface} 端口={(port > 0 ? port.ToString() : "(全部)")} 时长={seconds}s -> {pcap}");
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo(tshark)
			{
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			psi.ArgumentList.Add("-i");
			psi.ArgumentList.Add(LoopbackInterface);
			// 坑（实测 2026-10-18）：Npcap 回环适配器上 **BPF 的 `tcp port X` 捕获过滤不生效**
			// —— 用它抓会得到 0 个包。而 `host 127.0.0.1` 是生效的（同批测试抓到 163 包）。
			// 所以这里只按 host 粗筛，端口的精确过滤留到导出阶段用显示过滤 `-Y tcp.port==X`。
			psi.ArgumentList.Add("-f");
			psi.ArgumentList.Add("host 127.0.0.1");
			psi.ArgumentList.Add("-a");
			psi.ArgumentList.Add($"duration:{seconds}");
			psi.ArgumentList.Add("-w");
			psi.ArgumentList.Add(pcap);
			Process p = Process.Start(psi);
			if (p == null)
			{
				result.Ok = false;
				result.Message = "启动 tshark 失败。";
				return result;
			}
			// 登记到静态字段，供「主动停止」按钮杀进程
			using CancellationTokenSource localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			_currentProcess = p;
			_currentCts = localCts;
			try
			{
				// tshark 启动到真正建好 pcap 有一小段延迟（实测数百毫秒）。
				// 不等就会出现「刚点停止 → 文件还没建好 → 误报没有生成文件」的竞态。
				for (int i = 0; i < 40 && !File.Exists(pcap); i++)
				{
					await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
				}
				string err = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
				try
				{
					await p.WaitForExitAsync(localCts.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					// 主动停止：不等了，直接读已落盘的部分
					LogHelper.Salt("[抓包] 收到停止信号，读取已抓取部分");
					try
					{
						p.WaitForExit(3000);
					}
					catch
					{
					}
				}
				if (!string.IsNullOrWhiteSpace(err))
				{
					LogHelper.Salt("[抓包] tshark stderr: " + err.Trim().Replace("\n", " | "));
				}
			}
			finally
			{
				_currentProcess = null;
				_currentCts = null;
				try
				{
					p.Dispose();
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			result.Ok = false;
			result.Message = "抓包失败: " + ex.GetType().Name + ": " + ex.Message
				+ "（tshark 抓包通常需要管理员权限，可尝试以管理员运行本程序）";
			return result;
		}
		if (!File.Exists(pcap))
		{
			// 把路径与目录实况写进提示，避免用户/排障时只能看到一句笼统的失败
			string detail = "";
			try
			{
				string? dir = Path.GetDirectoryName(pcap);
				if (dir != null && Directory.Exists(dir))
				{
					string[] files = Directory.GetFiles(dir, "*.pcap");
					detail = "（输出目录已有 " + files.Length + " 个 pcap）";
				}
			}
			catch
			{
			}
			result.Ok = false;
			result.Message = "抓包结束但没有生成文件（多半是权限不足或接口不可用）。\n预期路径：" + pcap + detail
				+ "\n若本机有多个 tshark/接口冲突，可先停止其他抓包进程再试。";
			LogHelper.Salt("[抓包] 未生成 pcap: " + pcap + " " + detail);
			return result;
		}
		try
		{
			result.Payloads = await ReadPayloadsAsync(tshark, pcap, port, ct).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			result.Message = "抓到了包，但读取载荷失败: " + ex.Message;
			return result;
		}
		result.Ok = true;
		result.Message = $"抓取完成：{seconds}s，端口 {(port > 0 ? port.ToString() : "全部回环")}，带载荷的报文 {result.Payloads.Count} 个。文件：{pcap}";
		LogHelper.Salt("[抓包] " + result.Message);
		return result;
	}

	/// <summary>从 pcap 里导出 TCP 载荷（hex）与端口方向。</summary>
	private static async Task<List<CapturedPayload>> ReadPayloadsAsync(string tshark, string pcap, int port, CancellationToken ct)
	{
		ProcessStartInfo psi = new ProcessStartInfo(tshark)
		{
			RedirectStandardOutput = true,
			// 必须重定向 stderr：pcap 被 Kill 截断时 tshark 会往 stderr 写
			// "cut short" 警告，不接走会把管道写满并卡住进程。
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		psi.ArgumentList.Add("-r");
		psi.ArgumentList.Add(pcap);
		// 端口在这里用**显示过滤**精确筛（捕获阶段用 port 过滤在 Npcap 回环上无效）
		// port<=0 → 不加过滤，导出全部回环报文
		if (port > 0)
		{
			psi.ArgumentList.Add("-Y");
			psi.ArgumentList.Add($"tcp.port=={port}");
		}
		psi.ArgumentList.Add("-T");
		psi.ArgumentList.Add("fields");
		psi.ArgumentList.Add("-e");
		psi.ArgumentList.Add("tcp.srcport");
		psi.ArgumentList.Add("-e");
		psi.ArgumentList.Add("tcp.dstport");
		psi.ArgumentList.Add("-e");
		psi.ArgumentList.Add("tcp.payload");
		using Process p = Process.Start(psi);
		if (p == null)
		{
			return new List<CapturedPayload>();
		}
		string outp = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
		p.WaitForExit(15000);
		List<CapturedPayload> list = new List<CapturedPayload>();
		foreach (string raw in outp.Split('\n'))
		{
			string line = raw.Trim();
			if (line.Length == 0)
			{
				continue;
			}
			string[] parts = line.Split('\t');
			if (parts.Length < 3)
			{
				continue;
			}
			string hex = parts[2].Replace(":", "").Trim();
			if (hex.Length == 0)
			{
				continue;
			}
			int.TryParse(parts[0], out int sp);
			int.TryParse(parts[1], out int dp);
			list.Add(new CapturedPayload
			{
				Hex = hex,
				SrcPort = sp,
				DstPort = dp
			});
		}
		return list;
	}

	/// <summary>
	/// 从抓取结果里找出**游戏发出的登录帧**并取出其中的 32 字节令牌候选。
	/// 登录帧结构（§10.3）：2B 小端长度 + smid(2B) + gameid(2B 小端) + len(2B 小端) + token(UTF-8)。
	/// </summary>
	public static List<string> ExtractTokenCandidates(CaptureResult result)
	{
		List<string> found = new List<string>();
		if (result?.Payloads == null)
		{
			return found;
		}
		foreach (CapturedPayload pl in result.Payloads)
		{
			// 游戏 → 启动器：目的端口 = 控制端口
			if (pl.DstPort != result!.Port)
			{
				continue;
			}
			byte[] body;
			try
			{
				body = Convert.FromHexString(pl.BodyHex);
			}
			catch
			{
				continue;
			}
			// smid(2) + gameid(2) + len(2) + token(len)
			if (body.Length < 6)
			{
				continue;
			}
			int len = body[4] | (body[5] << 8);
			if (len <= 0 || len > body.Length - 6)
			{
				continue;
			}
			byte[] token = body.Skip(6).Take(len).ToArray();
			if (token.Length >= 32)
			{
				found.Add(Convert.ToHexString(token));
			}
		}
		return found;
	}

	/// <summary>生成便于阅读的抓包报告。</summary>
	public static string BuildReport(CaptureResult result)
	{
		StringBuilder sb = new StringBuilder();
		sb.Append(result.Message).Append('\n');
		if (result.Payloads.Count == 0)
		{
			sb.Append("\n没有抓到带载荷的报文。注意：\n")
				.Append("· 必须在游戏**已经连上控制端口之后**再抓，才能看到后续交互；\n")
				.Append("· 想抓到首条登录帧，需要先开始抓包、再启动游戏并进入服务器；\n")
				.Append("· 若一直抓不到，检查是否以管理员运行（tshark 抓包通常需要提权）。");
			return sb.ToString();
		}
		List<string> tokens = ExtractTokenCandidates(result);
		if (tokens.Count > 0)
		{
			sb.Append("\n★ 从登录帧里取到的真实令牌候选（已脱敏，仅显示指纹）：\n");
			foreach (string t in tokens.Distinct())
			{
				sb.Append("  ").Append(SecretRedaction.TokenFingerprint(t)).Append('\n');
			}
		}
		else
		{
			sb.Append("\n未从报文里识别出登录帧（可能只抓到握手之后的加密流量，或方向判断需调整）。");
		}
		// 端口对统计：一眼看清这次抓到了哪些本地会话
		Dictionary<string, int> sessions = new Dictionary<string, int>();
		foreach (CapturedPayload pl in result.Payloads)
		{
			int a = pl.SrcPort;
			int b = pl.DstPort;
			string key = ((a < b) ? (a + " <-> " + b) : (b + " <-> " + a));
			sessions[key] = sessions.TryGetValue(key, out int n) ? (n + 1) : 1;
		}
		if (sessions.Count > 0)
		{
			sb.Append("\n--- 本地会话（按报文数排序）---\n");
			foreach (KeyValuePair<string, int> kv in sessions.OrderByDescending((KeyValuePair<string, int> x) => x.Value).Take(12))
			{
				sb.Append("  ").Append(kv.Key).Append("   ").Append(kv.Value).Append(" 包\n");
			}
		}
		sb.Append("\n--- 报文清单（前 20 个）---\n");
		foreach (CapturedPayload pl in result.Payloads.Take(20))
		{
			string dir;
			if (result.Port <= 0)
			{
				dir = "本地";
			}
			else
			{
				dir = ((pl.DstPort == result.Port) ? "游戏→启动器" : "启动器→游戏");
			}
			List<string> framePatterns = new List<string>(tokens);
			if ((result.Port > 0 && pl.DstPort == result.Port) || LooksLikeLoginFrame(pl))
			{
				// 登录帧布局：2B 长度 + smid(2B) + gameid(2B) + 令牌。
				// 从偏移 6 起整体脱敏，即使令牌提取失败也不会泄露。
				if (pl.Hex.Length > 12)
				{
					// 展示只取前 96 个十六进制字符，脱敏模式也只需覆盖这一段，避免复制整包。
					framePatterns.Add(pl.Hex.Substring(12, Math.Min(256, pl.Hex.Length - 12)));
				}
			}
			string shown = SecretRedaction.HexText(pl.Hex, 96, framePatterns.ToArray());
			int redactedAt = shown.IndexOf('[');
			string asciiHex = (redactedAt >= 0) ? shown.Substring(0, redactedAt) : shown;
			sb.Append($"{dir} {pl.SrcPort}->{pl.DstPort} {pl.Length}B: ").Append(shown)
				.Append(pl.Hex.Length > 96 ? "…" : "").Append('\n')
				.Append("      txt: ").Append(AsciiOf(asciiHex, 48)).Append('\n');
		}
		return sb.ToString();
	}

	/// <summary>按“2B 小端长度 + 载荷”的帧头判断是否像登录帧（长度至少 10B）。</summary>
	private static bool LooksLikeLoginFrame(CapturedPayload pl)
	{
		if (pl.Length < 10 || pl.Hex.Length < 12)
		{
			return false;
		}
		try
		{
			int declared = Convert.ToByte(pl.Hex.Substring(0, 2), 16)
				| (Convert.ToByte(pl.Hex.Substring(2, 2), 16) << 8);
			return declared == pl.Length - 2;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>把 hex 转成可打印 ASCII（不可打印字符用 '.'），便于肉眼识别明文协议。</summary>
	private static string AsciiOf(string hex, int maxChars)
	{
		StringBuilder t = new StringBuilder();
		for (int i = 0; i + 1 < hex.Length && t.Length < maxChars; i += 2)
		{
			int v = Convert.ToInt32(hex.Substring(i, 2), 16);
			t.Append((v >= 32 && v < 127) ? ((char)v) : '.');
		}
		return t.ToString();
	}
}

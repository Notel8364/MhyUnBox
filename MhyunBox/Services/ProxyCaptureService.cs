using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Settings;

namespace MhyunBox.Services;

public static class ProxyCaptureService
{
	public sealed class ProxyBinding
	{
		public int ListenPort;

		public string? LoginName;

		public string? TargetHost;

		public int TargetPort;

		public int AuthAccountIndex = -1;

		/// <summary>启动时解析到的认证账号键（Channel|Account）：账号列表增删会让索引漂移，
		/// 会话认证时用它重新对准账号，避免拿到别人的登录态。</summary>
		public string AuthAccountKey = "";

		public long GameId;

		/// <summary>
		/// 代理来源："" / "tuohe" = 脱盒原服务器代理；"nexus-bot" / "nexus-market" / "nexus-manual" = 实例多开机器人侧。
		/// 停止机器人代理时只允许停 nexus-* 的监听，绝不误停脱盒代理。
		/// </summary>
		public string Tag = "";

		/// <summary>必须使用配置的监听端口：端口被占用时直接报错，不静默改绑其他端口。</summary>
		public bool RequireExactPort;

		/// <summary>
		/// 端口被占用时自动改用其它空闲端口（实例多开的机器人/行情绑定用）。
		/// 启动后调用方会把实际端口写回配置和客户端账号，避免游戏一直连旧端口。
		/// </summary>
		public bool AllowPortFallback;
	}

	public sealed class ActiveBindingInfo
	{
		public int ListenPort;

		public string? LoginName;

		public int AuthAccountIndex = -1;

		public string? TargetHost;

		public int TargetPort;

		public string Tag = "";
	}

	/// <summary>单个本地监听端口的实时流量统计（供实例多开角色卡片展示）。</summary>
	public sealed class TrafficInfo
	{
		public int ListenPort;

		public long UpBytes;

		public long DownBytes;

		public long UpPackets;

		public long DownPackets;

		public int Sessions;

		public long ScreenPackets;

		public DateTime LastActivityUtc;

		public string LastEvent = "";

		public DateTime LastEventUtc;

		/// <summary>该端口最近捕获的聊天栏内容（旧→新，最多 12 条）。</summary>
		public string[] ChatLines = Array.Empty<string>();

		/// <summary>最近一条聊天栏内容的时刻（UTC）。卡片「已进服」只在 10 秒内有效。</summary>
		public DateTime LastChatUtc;

		/// <summary>当前连接阶段：已连接 / 选择进服 / 进入服务器 / 进入子服 / 已进入游戏 / 已断开。</summary>
		public string Status = "";

		/// <summary>该端口最近一次连接失败或被踢的原因（卡片回显用）。</summary>
		public string LastError = "";

		public DateTime LastErrorUtc;
	}

	private sealed class TrafficCounter
	{
		public long UpBytes;

		public long DownBytes;

		public long UpPackets;

		public long DownPackets;

		public int Sessions;

		public long ScreenPackets;

		public DateTime LastActivityUtc;

		public string LastEvent = "";

		public DateTime LastEventUtc;

		public string Status = "";

		public string LastError = "";

		public DateTime LastErrorUtc;

		/// <summary>聊天栏历史（线程安全，按端口），供卡片「实例视角」直接显示。</summary>
		public readonly object ChatLock = new object();

		public readonly List<string> ChatLines = new List<string>();

		/// <summary>最近一条聊天栏内容的时刻（UTC）。</summary>
		public DateTime LastChatUtc;
	}

	private static readonly ConcurrentDictionary<int, TrafficCounter> TrafficCounters = new ConcurrentDictionary<int, TrafficCounter>();

	private sealed class ListenerEntry
	{
		public TcpListener? Listener;

		public CancellationTokenSource? Cts;

		public ProxyBinding? Binding;
	}

	private sealed class ActiveSession : IDisposable
	{
		public long Id;

		public TcpClient? Client;

		public TcpClient? Upstream;

		public Stream? ClientStream;

		public Stream? UpstreamStream;

		public FileStream? LogStream;

		public string SessionLabel = "";

		public DateTime Started;

		public long UpBytes;

		public long DownBytes;

		public long BytesLogged;

		public string TargetHostUsed = "";

		public int TargetPortUsed;

		// 本次上游连接实际走的线路（直连 / 预设IP池 x.x.x.x），认证失败时用于区分代理线路问题与账号问题
		public string UpstreamRoute = "";

		public int ListenPort;

		public ServerPreset ProtectedPreset;

		public volatile bool Closed;

		public Cfb8Runner? UpCfb;

		public Cfb8Runner? DownCfb;

		public int CompressionThreshold = -1;

		public int ProtocolVersion;

		/// <summary>用于「能不能收到游戏画面」的日志去重：Join Game / 首个区块包只记一次。</summary>
		public bool JoinGameLogged;

		public bool ChunkLogged;

		public bool PlayerListLogged;

		/// <summary>
		/// 本会话收到过服务器下发的 Disconnect 包（踢人）。
		/// 之后上游 EOF 属于「服务端主动断开」，不能当成上游被打挂去热切换线路：
		/// 之前每次被踢都会切一次 IP，结果线路越切越乱（失落世界就是这样被切到
		/// 1.95.232.232 后一直重连失败）。
		/// </summary>
		public bool SawDisconnectPacket;

		public string? LoginRoleName;

		public string? ClientName;

		public byte[]? ClientUuid;

		public string? ClientUuidHex;

		public string? ServerOwnUuidHex;

		public readonly SemaphoreSlim UpstreamWriteLock = new SemaphoreSlim(1, 1);

		/// <summary>抓包日志落盘锁：上下行两个泵线程会并发写同一个 FileStream，必须串行化。</summary>
		public readonly object LogLock = new object();

		public void Dispose()
		{
			Closed = true;
			try
			{
				lock (LogLock)
				{
					LogStream?.Dispose();
				}
			}
			catch
			{
			}
			try
			{
				Client?.Dispose();
			}
			catch
			{
			}
			try
			{
				Upstream?.Dispose();
			}
			catch
			{
			}
		}
	}

	public sealed class SessionInfo
	{
		public long Id;

		public string Client = "";

		public string Target = "";

		public string LogPath = "";

		public string Status = "";

		public DateTime Started;

		public DateTime? Ended;

		public long UpBytes;

		public long DownBytes;

		public long BytesLogged;
	}

	private sealed class Cfb8Runner
	{
		private readonly ICryptoTransform _enc;

		private readonly byte[] _reg = new byte[16];

		private readonly byte[] _ek = new byte[16];

		public Cfb8Runner(byte[] key)
		{
			using Aes aes = Aes.Create();
			aes.Mode = CipherMode.ECB;
			aes.Padding = PaddingMode.None;
			aes.KeySize = 128;
			aes.Key = key;
			_enc = aes.CreateEncryptor();
			Array.Copy(key, _reg, 16);
		}

		public byte DecryptByte(byte c)
		{
			_enc.TransformBlock(_reg, 0, 16, _ek, 0);
			byte result = (byte)(c ^ _ek[0]);
			Array.Copy(_reg, 1, _reg, 0, 15);
			_reg[15] = c;
			return result;
		}

		public byte EncryptByte(byte p)
		{
			_enc.TransformBlock(_reg, 0, 16, _ek, 0);
			byte b = (byte)(p ^ _ek[0]);
			Array.Copy(_reg, 1, _reg, 0, 15);
			_reg[15] = b;
			return b;
		}

		public byte[] Decrypt(byte[] data)
		{
			byte[] array = new byte[data.Length];
			for (int i = 0; i < data.Length; i++)
			{
				array[i] = DecryptByte(data[i]);
			}
			return array;
		}

		public byte[] Encrypt(byte[] data)
		{
			byte[] array = new byte[data.Length];
			for (int i = 0; i < data.Length; i++)
			{
				array[i] = EncryptByte(data[i]);
			}
			return array;
		}
	}

	private static readonly object Sync = new object();

	private static readonly List<ListenerEntry> Listeners = new List<ListenerEntry>();

	private static readonly ConcurrentDictionary<long, ActiveSession> Active = new ConcurrentDictionary<long, ActiveSession>();

	private static readonly List<SessionInfo> History = new List<SessionInfo>();

	private static long _nextId;

	private static readonly ConcurrentDictionary<string, ProxyUpstreamHealth> UpstreamHealth = new ConcurrentDictionary<string, ProxyUpstreamHealth>(StringComparer.OrdinalIgnoreCase);

	private static CancellationTokenSource HealthMonitorCts;

	private static Task HealthMonitorTask;

	private static readonly object HealthMonitorSync = new object();

	/// <summary>最近一次客户端登录握手的真实协议版本（用于健康探测伪装，避免用固定 340 老协议号暴露代理特征触发风控）。</summary>
	private static volatile int LastClientProtocol;

	// ===== 2026-11-07 修复：恢复 v3.8.0 的代理层连接节流 =====
	// 8.0.0 曾移除这层保护，改成「认证失败冷却期仍照常直连 + 换节点」，
	// 结果是网易风控拒绝后连接数与 login-otp 一起叠加，把 IP 级标记越刷越深。
	private static readonly ConcurrentDictionary<string, DateTime> TargetAuthFailAt = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentDictionary<string, DateTime[]> TargetConnectTimes = new ConcurrentDictionary<string, DateTime[]>(StringComparer.OrdinalIgnoreCase);

	private static readonly object ConnectThrottleSync = new object();

	private const int MaxActivePerTarget = 6;

	private const int ConnectRateWindowSeconds = 8;

	private const int MaxConnectsPerWindow = 8;

	/// <summary>
	/// 代理模式分流：true=正常代理登录（对齐 v3.8.0 的单目标直连，去掉自带连接节流/风控限制）；
	/// false=防DDOS模式（对齐 v7.8.0 的节点轮换、预设 IP 池、健康探测与地址安全策略）。
	/// 默认 normal：防DDOS 的节点轮换与探测流量容易被网易判为异常登录环境。
	/// </summary>
	internal static bool IsNormalMode
	{
		get
		{
			try
			{
				return !string.Equals(SettingsManager.Instance.ProxyMode, "ddos", StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return true;
			}
		}
	}

	private static int _normalIpPoolCursor;

	private static readonly string[] CredentialKickPhrases = new string[25]
	{
		"会话已失效", "会话失效", "凭证失效", "凭证已失效", "请先登录", "请重新登录", "重新登录", "登录已失效", "登录失效", "登录过期",
		"已过期", "认证失败", "验证失败", "账号或密码", "invalid session", "session expired", "token expired", "token 过期", "not_logged_in", "not logged", "unauthorized", "authentication failed",
		"bad login", "account expired", "credentials"
	};

	private static readonly string[] RiskKickPhrases = new string[15]
	{
		"异地", "风控", "异常登录", "异常 IP", "IP 变动", "IP变动", "ip changed", "unusual login", "abnormal", "risk control",
		"new device", "device change", "安全提醒", "账号安全", "异地登录"
	};

	// 抓包每块最大转储字节数：默认全量（0 = 不截断）。
	// 通过环境变量 MHYUNBOX_CAPTURE_MAX_DUMP 控制；不设置或 <=0 时按 65536 处理，
	// 用于完整记录交易行等大数据包（此前固定 32B 导致 GUI 数据无法分析）。
	private static readonly int MaxDumpBytes = Math.Max(4096, ParseEnvInt("MHYUNBOX_CAPTURE_MAX_DUMP", 65536));

	private static int ParseEnvInt(string name, int fallback)
	{
		try
		{
			string v = Environment.GetEnvironmentVariable(name) ?? "";
			return int.TryParse(v, out int n) && n > 0 ? n : fallback;
		}
		catch
		{
			return fallback;
		}
	}

	public static bool IsRunning { get; private set; }

	public static int ListenPort { get; private set; }

	public static bool AllowLanBindings { get; set; }

	private static IPAddress ListenAddress
	{
		get
		{
			if (!AllowLanBindings)
			{
				return IPAddress.Loopback;
			}
			return IPAddress.Any;
		}
	}

	public static int[] ActivePorts { get; private set; } = Array.Empty<int>();

	public static string? TargetHost { get; private set; }

	public static int TargetPort { get; private set; }

	public static bool TargetFromHandshake { get; set; }

	public static string AutoStartPresetName { get; set; } = "";

	public static string AutoStartPorts { get; set; } = "";

	public static string CaptureDir => Path.Combine(AppContext.BaseDirectory, "ProxyCaptures");

	public static bool CaptureLogEnabled => LogHelper.DebugEnabled;

	public static event Action<SessionInfo>? SessionOpened;

	public static event Action<SessionInfo>? SessionClosed;

	public static event Action<bool>? StateChanged;

	public static string SessionLogLabel(string logPath)
	{
		if (!string.IsNullOrWhiteSpace(logPath))
		{
			return logPath;
		}
		return "未记录（调试模式已关闭）";
	}

	public static void SetCaptureLogEnabled(bool enabled)
	{
		if (enabled)
		{
			LogHelper.DebugEnabled = true;
			try
			{
				Directory.CreateDirectory(CaptureDir);
				return;
			}
			catch
			{
				return;
			}
		}
		LogHelper.DebugEnabled = false;
		lock (Sync)
		{
			foreach (KeyValuePair<long, ActiveSession> item in Active)
			{
				try
				{
					if (item.Value?.LogStream != null)
					{
						lock (item.Value.LogLock)
						{
							if (item.Value.LogStream == null)
							{
								continue;
							}
							string name = item.Value.LogStream.Name;
							item.Value.LogStream.Dispose();
							item.Value.LogStream = null;
							item.Value.BytesLogged = 0L;
							if (!string.IsNullOrWhiteSpace(name) && File.Exists(name))
							{
								File.Delete(name);
							}
						}
					}
				}
				catch
				{
				}
			}
		}
		int num = 0;
		try
		{
			if (Directory.Exists(CaptureDir))
			{
				foreach (string item2 in Directory.EnumerateFiles(CaptureDir, "*.log", SearchOption.TopDirectoryOnly))
				{
					try
					{
						File.Delete(item2);
						num++;
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
		LogHelper.Log($"[抓包日志] 调试模式已关闭，已停止产出抓包日志并清理 {num} 个历史文件");
	}

	private static void AddHistory(SessionInfo info)
	{
		lock (Sync)
		{
			History.Add(info);
			if (History.Count > 256)
			{
				History.RemoveRange(0, History.Count - 256);
			}
		}
	}

	public static bool HasEarliestRecord()
	{
		AppSettings instance = SettingsManager.Instance;
		if (instance != null && !string.IsNullOrWhiteSpace(instance.LastProxyPresetName))
		{
			return (instance.LastProxyBindings?.Count ?? 0) > 0;
		}
		return false;
	}

	public static IReadOnlyList<SessionInfo> SessionsSnapshot()
	{
		lock (Sync)
		{
			return History.ToArray();
		}
	}

	public static void StartBatch(IReadOnlyList<ProxyBinding> bindings, string targetHost, int targetPort)
	{
		if (bindings == null || bindings.Count == 0)
		{
			LogHelper.Log("[代理抓取] 启动失败：没有可用的监听绑定");
			StateChanged?.Invoke(obj: false);
			return;
		}
		TargetHost = targetHost;
		TargetPort = targetPort;
		LogHelper.Log("[代理模式] " + (IsNormalMode ? "正常代理登录（v3.8.0 单目标直连，无风控限制）" : "防DDOS（v7.8.0 节点轮换 / 预设 IP 池 / 健康探测）"));
		if (CaptureLogEnabled)
		{
			Directory.CreateDirectory(CaptureDir);
		}
		lock (Sync)
		{
			foreach (ProxyBinding b in bindings)
			{
				if (string.IsNullOrWhiteSpace(b.TargetHost))
				{
					b.TargetHost = targetHost;
				}
				if (b.TargetPort <= 0)
				{
					b.TargetPort = targetPort;
				}
				string value = b.TargetHost;
				int targetPort2 = b.TargetPort;
				if (string.IsNullOrWhiteSpace(value) || targetPort2 <= 0)
				{
					LogHelper.Log($"[代理抓取] 绑定（监听 {b.ListenPort}）无有效目标服务器，已跳过");
					continue;
				}
				ServerPreset serverPreset = ServerPresets.All.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, value, StringComparison.OrdinalIgnoreCase));
				b.GameId = ((serverPreset != null && serverPreset.GameId > 0) ? serverPreset.GameId : b.GameId);
				if (b.GameId > 0)
				{
					LogHelper.Log($"[代理抓取] 绑定（监听 {b.ListenPort}）目标 {value}:{targetPort2} 使用预设游戏 GameId={b.GameId}");
				}
				// 同一「角色 + 目标服务器」全进程只允许一条监听。
				// 快速启动的脱盒代理与「会话多开」的机器人代理是两套入口，各自算端口，
				// 历史上还漂移过，于是同一账号出现 20229+20205 这样的两条连接互相顶号。
				// 这里在绑定前先按角色+目标查一遍：已有就复用那个端口。
				ListenerEntry? sameRoleAnyPort = Listeners.FirstOrDefault((ListenerEntry x) =>
					x.Binding != null
					&& string.Equals((x.Binding.LoginName ?? "").Trim(), (b.LoginName ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
					&& string.Equals(x.Binding.TargetHost, value, StringComparison.OrdinalIgnoreCase)
					&& x.Binding.TargetPort == targetPort2);
				if (sameRoleAnyPort?.Binding != null && sameRoleAnyPort.Binding.ListenPort != b.ListenPort)
				{
					LogHelper.Log("[代理抓取] 角色 " + b.LoginName + " 已在 127.0.0.1:"
						+ sameRoleAnyPort.Binding.ListenPort + " 有代理，改用该端口（不再新开一条，避免同账号顶号）");
					b.ListenPort = sameRoleAnyPort.Binding.ListenPort;
				}
				// 同一个本地端口只允许一个监听；但必须区分代理来源：
				// 脱盒原服务器代理与机器人代理互不抢占/互不复用，避免停止机器人时误停脱盒侧。
				ListenerEntry existing = Listeners.FirstOrDefault((ListenerEntry x) =>
					x.Binding != null && x.Binding.ListenPort == b.ListenPort);
				if (existing != null)
				{
					bool sameTag = string.Equals(existing.Binding.Tag ?? "", b.Tag ?? "", StringComparison.OrdinalIgnoreCase);
					// 同一端口 + 同一目标 + 同一登录名 = 同一个角色。
					// 之前只认 Tag，脱盒代理占了端口就判定"另一类代理"，机器人侧被迫
					// 改到下一个空闲端口 → 同账号出现两条连接互相踢（日志里
					// Notel 20231+20239、Ja0keyL0ve 20236+20247 就是这么来的）。
					// 现在只要目标与登录名一致就直接复用这条监听。
					bool sameRole = string.Equals((existing.Binding.LoginName ?? "").Trim(), (b.LoginName ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
						&& string.Equals(existing.Binding.TargetHost, value, StringComparison.OrdinalIgnoreCase)
						&& existing.Binding.TargetPort == targetPort2;
					if (!sameTag)
					{
						if (sameRole)
						{
							LogHelper.Log("[代理抓取] 监听 " + b.ListenPort + " 已有同角色代理（"
								+ (string.IsNullOrWhiteSpace(existing.Binding.Tag) ? "tuohe" : existing.Binding.Tag)
								+ "），机器人侧复用该监听，不再新开第二条连接");
							continue;
						}
						if (b.AllowPortFallback)
						{
							int alt = NextFreePort(b.ListenPort + 1);
							LogHelper.Log("[代理抓取] 监听 " + b.ListenPort + " 已被另一类代理占用（"
								+ (string.IsNullOrWhiteSpace(existing.Binding.Tag) ? "tuohe" : existing.Binding.Tag)
								+ "），机器人侧自动改用空闲端口 " + alt);
							b.ListenPort = alt;
						}
						else
						{
							string msg2 = "监听 " + b.ListenPort + " 已被另一类代理占用（" + (string.IsNullOrWhiteSpace(existing.Binding.Tag) ? "tuohe" : existing.Binding.Tag)
								+ "），机器人侧绑定已跳过，不会停止现有代理";
							LogHelper.Log("[代理抓取] " + msg2);
							ReportBindingError(b.ListenPort, msg2);
							continue;
						}
					}
					else if (string.Equals(existing.Binding.TargetHost, value, StringComparison.OrdinalIgnoreCase)
						&& existing.Binding.TargetPort == targetPort2)
					{
						LogHelper.Log($"[代理抓取] 监听 {b.ListenPort} 已在运行（-> {value}:{targetPort2}），复用现有监听");
						continue;
					}
					else
					{
						LogHelper.Log($"[代理抓取] 监听 {b.ListenPort} 目标变更（{existing.Binding.TargetHost}:{existing.Binding.TargetPort} -> {value}:{targetPort2}），重启该端口");
						StopPort(b.ListenPort);
					}
				}
				int num = b.ListenPort;
				TcpListener listener = null;
				for (int num2 = 0; num2 < 100 && num >= 1 && num <= 65535; num2++)
				{
					try
					{
						TcpListener tcpListener = new TcpListener(ListenAddress, num);
						tcpListener.Start();
						listener = tcpListener;
					}
					catch (SocketException)
					{
						if (b.RequireExactPort)
						{
							// 机器人侧端口必须与客户端配置一致，不能静默改绑。
							break;
						}
						num = NextFreePort(num + 1);
						continue;
					}
					break;
				}
				if (listener == null)
				{
					string msg = "无法为端口 " + b.ListenPort + " 建立监听（端口被占用或不可用），该绑定已跳过";
					LogHelper.Log("[代理抓取] " + msg);
					ReportBindingError(b.ListenPort, msg);
					continue;
				}
				b.ListenPort = num;
				CancellationTokenSource cts = new CancellationTokenSource();
				ListenerEntry item = new ListenerEntry
				{
					Listener = listener,
					Cts = cts,
					Binding = b
				};
				Listeners.Add(item);
				SetTrafficStatus(b.ListenPort, "监听中");
				ClearTrafficError(b.ListenPort);
				string value2 = (string.IsNullOrWhiteSpace(b.LoginName) ? "无账号（不改写登录名）" : ("登录名 " + b.LoginName));
				string value3 = (AllowLanBindings ? "0.0.0.0" : "127.0.0.1");
				LogHelper.Log($"[代理抓取] 启动实例: 监听 {value3}:{num} ({value2}) -> {value}:{targetPort2} (被动中继，无主动请求)");
				Task.Run(() => AcceptLoopAsync(listener, b, cts.Token));
			}
		}
		ListenPort = ((Listeners.Count > 0) ? (Listeners[0].Binding?.ListenPort ?? 0) : 0);
		ActivePorts = Listeners.Select((ListenerEntry e) => e.Binding?.ListenPort ?? 0).ToArray();
		IsRunning = Listeners.Count > 0;
		StateChanged?.Invoke(IsRunning);
		WriteRolesFile();
		SyncServerListDat();
		StartHealthMonitor(from e in Listeners where e.Binding != null select new KeyValuePair<string, int>(e.Binding.TargetHost ?? "", e.Binding.TargetPort));
	}

	public static IReadOnlyList<ActiveBindingInfo> ActiveBindingsSnapshot()
	{
		lock (Sync)
		{
			return (from e in Listeners
				where e.Binding != null
				select new ActiveBindingInfo
				{
					ListenPort = e.Binding.ListenPort,
					LoginName = e.Binding.LoginName,
					AuthAccountIndex = e.Binding.AuthAccountIndex,
					TargetHost = e.Binding.TargetHost,
					TargetPort = e.Binding.TargetPort,
					Tag = e.Binding.Tag ?? ""
				}).ToList();
		}
	}

	/// <summary>各本地端口的实时收发流量 / 包量 / 会话数 / 最近聊天与出入事件。</summary>
	public static IReadOnlyList<TrafficInfo> TrafficSnapshot()
	{
		var list = new List<TrafficInfo>(TrafficCounters.Count);
		foreach (KeyValuePair<int, TrafficCounter> pair in TrafficCounters)
		{
			TrafficCounter c = pair.Value;
			list.Add(new TrafficInfo
			{
				ListenPort = pair.Key,
				UpBytes = Interlocked.Read(ref c.UpBytes),
				DownBytes = Interlocked.Read(ref c.DownBytes),
				UpPackets = Interlocked.Read(ref c.UpPackets),
				DownPackets = Interlocked.Read(ref c.DownPackets),
				ScreenPackets = Interlocked.Read(ref c.ScreenPackets),
				Sessions = c.Sessions,
				LastActivityUtc = c.LastActivityUtc,
				LastEvent = c.LastEvent,
				LastEventUtc = c.LastEventUtc,
				Status = c.Status,
				LastError = c.LastError,
				LastErrorUtc = c.LastErrorUtc,
				LastChatUtc = c.LastChatUtc,
				ChatLines = SnapshotChatLines(c)
			});
		}
		return list;
	}

	private static string[] SnapshotChatLines(TrafficCounter counter)
	{
		lock (counter.ChatLock)
		{
			return counter.ChatLines.ToArray();
		}
	}

	private static TrafficCounter TrafficFor(int listenPort)
	{
		return TrafficCounters.GetOrAdd(listenPort, _ => new TrafficCounter());
	}

	private static void AddTraffic(ActiveSession session, bool isUp, long bytes, long packets)
	{
		if (session == null || session.ListenPort <= 0)
		{
			return;
		}
		TrafficCounter c = TrafficFor(session.ListenPort);
		if (isUp)
		{
			Interlocked.Add(ref c.UpBytes, bytes);
			Interlocked.Add(ref c.UpPackets, packets);
		}
		else
		{
			Interlocked.Add(ref c.DownBytes, bytes);
			Interlocked.Add(ref c.DownPackets, packets);
		}
		c.LastActivityUtc = DateTime.UtcNow;
	}

	private static void AddScreenPackets(ActiveSession session, long count)
	{
		if (session != null && session.ListenPort > 0)
		{
			Interlocked.Add(ref TrafficFor(session.ListenPort).ScreenPackets, count);
		}
	}

	private static void SetTrafficEvent(ActiveSession session, string text, bool chat = false)
	{
		if (session == null || session.ListenPort <= 0 || string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		TrafficCounter c = TrafficFor(session.ListenPort);
		string value = text.Trim();
		if (value.Length > 180)
		{
			value = value.Substring(0, 180) + "…";
		}
		c.LastEvent = value;
		c.LastEventUtc = DateTime.UtcNow;
	}

	/// <summary>记录一条聊天栏内容（卡片「实例视角」按端口显示最近 12 条）。</summary>
	private static void AddTrafficChat(ActiveSession session, string text, bool joinLeave)
	{
		if (session == null || session.ListenPort <= 0 || string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		string value = CleanChatLine(text);
		if (value.Length == 0)
		{
			return;
		}
		if (value.Length > 180)
		{
			value = value.Substring(0, 180) + "…";
		}
		string line = (joinLeave ? "[出入] " : "[聊天] ") + value;
		TrafficCounter counter = TrafficFor(session.ListenPort);
		lock (counter.ChatLock)
		{
			counter.ChatLines.Add(line);
			counter.LastChatUtc = DateTime.UtcNow;
			if (counter.ChatLines.Count > MaxChatLines)
			{
				counter.ChatLines.RemoveRange(0, counter.ChatLines.Count - MaxChatLines);
			}
		}
		SetTrafficEvent(session, line);
	}

	private const int MaxChatLines = 12;

	/// <summary>去掉聊天里的 § 颜色/格式控制符，卡片里显示纯文本。</summary>
	private static string CleanChatLine(string value)
	{
		try
		{
			return Regex.Replace(value, "§.", "").Trim();
		}
		catch
		{
			return value.Trim();
		}
	}

	/// <summary>记录端口当前所处的连接阶段（卡片状态徽标用）。</summary>
	private static void SetTrafficStatus(ActiveSession session, string status)
	{
		if (session == null || session.ListenPort <= 0 || string.IsNullOrWhiteSpace(status))
		{
			return;
		}
		SetTrafficStatus(session.ListenPort, status);
	}

	private static void SetTrafficStatus(int listenPort, string status)
	{
		if (listenPort <= 0 || string.IsNullOrWhiteSpace(status))
		{
			return;
		}
		TrafficCounter c = TrafficFor(listenPort);
		c.Status = status.Trim();
	}

	/// <summary>记录端口最近一次连接失败 / 被踢原因（卡片红色回显）。</summary>
	private static void SetTrafficError(ActiveSession session, string error)
	{
		if (session == null || session.ListenPort <= 0 || string.IsNullOrWhiteSpace(error))
		{
			return;
		}
		SetTrafficError(session.ListenPort, error);
	}

	private static void SetTrafficError(int listenPort, string error)
	{
		if (listenPort <= 0 || string.IsNullOrWhiteSpace(error))
		{
			return;
		}
		TrafficCounter c = TrafficFor(listenPort);
		string value = error.Trim();
		if (value.Length > 200)
		{
			value = value.Substring(0, 200) + "…";
		}
		c.LastError = value;
		c.LastErrorUtc = DateTime.UtcNow;
	}

	/// <summary>启动失败等代理侧错误，直接写到端口状态，供实例卡片回显。</summary>
	public static void ReportBindingError(int listenPort, string message)
	{
		SetTrafficStatus(listenPort, "启动失败");
		SetTrafficError(listenPort, message);
	}

	/// <summary>端口成功建立监听后清掉上一次的错误，避免卡片一直显示旧错误。</summary>
	public static void ClearTrafficError(int listenPort)
	{
		if (listenPort <= 0)
		{
			return;
		}
		TrafficCounter c = TrafficFor(listenPort);
		c.LastError = "";
		c.LastErrorUtc = DateTime.MinValue;
	}

	public static int NextFreePort(int startPort)
	{
		lock (Sync)
		{
			HashSet<int> hashSet = new HashSet<int>(Listeners.Select((ListenerEntry e) => e.Binding?.ListenPort ?? 0));
			int num = startPort;
			for (int num2 = 0; num2 < 65536; num2++)
			{
				if (num > 65535)
				{
					num = 1;
				}
				if (num < 1 || hashSet.Contains(num))
				{
					num++;
					continue;
				}
				TcpListener tcpListener = null;
				try
				{
					tcpListener = new TcpListener(ListenAddress, num);
					tcpListener.Start();
					return num;
				}
				catch
				{
				}
				finally
				{
					try
					{
						tcpListener?.Stop();
					}
					catch
					{
					}
				}
				num++;
			}
			return 0;
		}
	}

	public static void Stop()
	{
		// 记录调用来源：代理被停会直接掐断游戏连接，留下线索便于定位「是谁停的」
		try
		{
			System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
			System.Diagnostics.StackFrame[] frames = st.GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>();
			string from = string.Join(" <- ", frames.Take(5).Select((System.Diagnostics.StackFrame f) =>
			{
				System.Reflection.MethodBase? m = f.GetMethod();
				return ((m != null && m.DeclaringType != null) ? m.DeclaringType!.Name : "?") + "." + ((m != null) ? m!.Name : "?");
			}));
			LogHelper.Log("[代理抓取] 停止代理，调用来源: " + (from.Length > 0 ? from : "(未知)"));
		}
		catch
		{
		}
		lock (Sync)
		{
			foreach (ListenerEntry listener in Listeners)
			{
				try
				{
					listener.Cts?.Cancel();
				}
				catch
				{
				}
				try
				{
					listener.Listener?.Stop();
				}
				catch
				{
				}
			}
			Listeners.Clear();
		}
		foreach (long key in Active.Keys)
		{
			if (Active.TryRemove(key, out var value))
			{
				SessionInfo sessionInfo = ToInfo(value, "已停止");
				value.Dispose();
				AddHistory(sessionInfo);
				SessionClosed?.Invoke(sessionInfo);
			}
		}
		IsRunning = false;
		ActivePorts = Array.Empty<int>();
		ProxyStarter.ClearRemembered();
		StopHealthMonitor();
		LogHelper.Log("[代理抓取] 已停止");
		StateChanged?.Invoke(obj: false);
		WriteRolesFile();
		SyncServerListDat();
	}

	public static void RestartIfRunningForPreset()
	{
		// 只在「脱盒侧服务器代理确实在运行」时才重启它：
		// 1) 账号取消登录/删除不该凭空把代理启动起来（此前只看 IsRunning，
		//    机器人监听也会让这里为真 → Stop() 全停 + 重建服务器代理，表现为莫名启动代理）；
		// 2) 机器人代理监听保持不动，只有服务器侧被重建。
		if (!HasServerProxies())
		{
			LogHelper.Log("[账号检测] 服务器代理未在运行，账号变更不启停任何代理（机器人代理不受影响）");
			return;
		}
		try
		{
			AppSettings instance = SettingsManager.Instance;
			if (instance == null)
			{
				return;
			}
			string text = (string.IsNullOrWhiteSpace(AutoStartPresetName) ? instance.LastProxyPresetName : AutoStartPresetName);
			if (string.IsNullOrWhiteSpace(text))
			{
				return;
			}
			ServerPreset serverPreset = ServerPresets.Find(text);
			if (serverPreset == null || serverPreset.IsPending)
			{
				return;
			}
			int basePort = ((instance.ProxyListenPort > 0) ? instance.ProxyListenPort : 25565);
			List<ProxyBinding> list = ProxyStarter.BuildSavedBindings(instance, serverPreset, basePort, out var usedSaved);
			if (!usedSaved || list.Count == 0)
			{
				list = ProxyStarter.BuildRoleBindings(serverPreset, basePort);
			}
			if (list.Count == 0)
			{
				LogHelper.Log("[账号检测] 重启代理：无可用角色，已停止服务器代理");
				StopServerProxies();
				return;
			}
			StopServerProxies();
			TargetFromHandshake = true;
			StartBatch(list, serverPreset.Host, serverPreset.Port);
			AutoStartPresetName = serverPreset.Name;
			AutoStartPorts = string.Join("、", list.Select((ProxyBinding b) => b.ListenPort.ToString()));
			LogHelper.Log("[账号检测] 已按预设重新启动服务器代理: " + text + " 端口: " + AutoStartPorts + "（机器人代理未受影响）");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[账号检测] 重启代理失败");
		}
	}

	// 当前存活的代理会话数（>0 表示有客户端正连着游戏），供自动登录保活判断是否在游戏中
	public static int ActiveSessionCount => Active.Count;

	/// <summary>
	/// 判断某账号当前是否有正在游戏内（已建立/正在建立网易云认证）的代理会话。
	/// 后台保活用它跳过游戏内账号：此时 login-otp 换新会话可能把游戏内旧会话顶掉，
	/// 表现为「玩着玩着网易云验证失败」。按 监听端口→绑定→账号索引（兜底 LoginName）回查，尽力而为；
	/// 账号列表发生增删导致下标漂移时以 LoginName 兜底，查不到就返回 false（保活照常执行）。
	/// </summary>
	public static bool IsAccountInGame(string channel, string account)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(account))
			{
				return false;
			}
			string trimmed = account.Trim();
			IReadOnlyList<AccountStore.LocalAccount> all = AccountStore.All;
			HashSet<int>? indexes = null;
			for (int i = 0; i < all.Count; i++)
			{
				AccountStore.LocalAccount a = all[i];
				if (string.Equals(a.Channel, channel ?? "", StringComparison.Ordinal) && string.Equals(a.Account, trimmed, StringComparison.OrdinalIgnoreCase))
				{
					(indexes ??= new HashSet<int>()).Add(i);
				}
			}
			if (indexes == null)
			{
				return false;
			}
			List<ListenerEntry> listeners;
			lock (Sync)
			{
				listeners = Listeners.ToList();
			}
			Dictionary<int, ProxyBinding?> byPort = new Dictionary<int, ProxyBinding?>();
			foreach (ListenerEntry entry in listeners)
			{
				if (entry?.Binding != null)
				{
					byPort[entry.Binding.ListenPort] = entry.Binding;
				}
			}
			foreach (ActiveSession session in Active.Values)
			{
				if (session.Closed || !byPort.TryGetValue(session.ListenPort, out ProxyBinding? binding) || binding == null)
				{
					continue;
				}
				if ((binding.AuthAccountIndex >= 0 && indexes.Contains(binding.AuthAccountIndex))
					|| (!string.IsNullOrWhiteSpace(binding.LoginName) && string.Equals(binding.LoginName.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
				{
					return true;
				}
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	public static void StopTarget(string targetHost)
	{
		lock (Sync)
		{
			foreach (ListenerEntry item in Listeners.ToList())
			{
				if (item.Binding != null && string.Equals(item.Binding.TargetHost, targetHost, StringComparison.OrdinalIgnoreCase))
				{
					try
					{
						item.Cts?.Cancel();
					}
					catch
					{
					}
					try
					{
						item.Listener?.Stop();
					}
					catch
					{
					}
					Listeners.Remove(item);
				}
			}
		}
		foreach (long item2 in Active.Keys.ToList())
		{
			if (Active.TryGetValue(item2, out var value) && string.Equals(value.TargetHostUsed, targetHost, StringComparison.OrdinalIgnoreCase) && Active.TryRemove(item2, out var value2))
			{
				SessionInfo sessionInfo = ToInfo(value2, "已停止");
				value2.Dispose();
				AddHistory(sessionInfo);
				SessionClosed?.Invoke(sessionInfo);
			}
		}
		lock (Sync)
		{
			IsRunning = Listeners.Count > 0;
			ActivePorts = Listeners.Select((ListenerEntry e) => e.Binding?.ListenPort ?? 0).ToArray();
		}
		LogHelper.Log($"[代理抓取] 已停止目标 {targetHost} 的代理监听（剩余 {Listeners.Count} 个）");
		StateChanged?.Invoke(IsRunning);
		WriteRolesFile();
		SyncServerListDat();
	}

	public static bool StopPort(int listenPort)
	{
		return StopPort(listenPort, null);
	}

	/// <summary>
	/// 只停止机器人侧（Tag 以 nexus- 开头）的监听；脱盒原服务器代理绝不误停。
	/// 返回是否真的停止了监听。
	/// </summary>
	public static bool StopNexusBinding(int listenPort)
	{
		return StopPort(listenPort, "nexus-");
	}

	/// <summary>是否存在脱盒侧（非 nexus- 标记）的服务器代理监听。</summary>
	public static bool HasServerProxies()
	{
		lock (Sync)
		{
			return Listeners.Any(item => item.Binding != null
				&& !(item.Binding.Tag ?? "").StartsWith("nexus-", StringComparison.OrdinalIgnoreCase));
		}
	}

	/// <summary>
	/// 只停止脱盒侧（非 nexus- 标记）监听：机器人代理监听不受影响。
	/// 端口归机器人代理所有时拒绝并记录；端口无监听时记日志返回 false。返回是否真的停止。
	/// </summary>
	public static bool StopServerBinding(int listenPort)
	{
		bool botOwned;
		bool hasAny;
		lock (Sync)
		{
			hasAny = Listeners.Any(item => item.Binding != null && item.Binding.ListenPort == listenPort);
			botOwned = Listeners.Any(item => item.Binding != null && item.Binding.ListenPort == listenPort
				&& (item.Binding.Tag ?? "").StartsWith("nexus-", StringComparison.OrdinalIgnoreCase));
		}
		if (botOwned)
		{
			LogHelper.Log($"[代理抓取] 端口 {listenPort} 归机器人代理所有，已忽略服务器侧停止请求（请到「会话多开」停止机器人）");
			return false;
		}
		if (!hasAny)
		{
			LogHelper.Log($"[代理抓取] 端口 {listenPort} 当前没有代理监听，无需停止");
			return false;
		}
		return StopPort(listenPort);
	}

	/// <summary>
	/// 按「具体角色」停止监听：先按服务器代理停；若该端口被机器人代理（nexus-）占用、
	/// 但绑定的登录名正是这个角色，就连机器人代理一起停。
	///
	/// 背景：快速启动的某一组角色与「会话多开」撞了同一个端口时，端口会被 nexus-
	/// 抢先占用。旧实现让 StopServerBinding 一律拒绝，按钮看起来「点了没反应」。
	/// 用户点的就是这一行角色的「停止」，所以按角色名核对后再放行是安全的。
	/// </summary>
	public static bool StopBindingForRole(int listenPort, string? roleName)
	{
		if (listenPort <= 0)
		{
			return false;
		}
		bool sameRoleBot;
		lock (Sync)
		{
			sameRoleBot = !string.IsNullOrWhiteSpace(roleName)
				&& Listeners.Any(item => item.Binding != null
					&& item.Binding.ListenPort == listenPort
					&& (item.Binding.Tag ?? "").StartsWith("nexus-", StringComparison.OrdinalIgnoreCase)
					&& string.Equals((item.Binding.LoginName ?? "").Trim(), roleName!.Trim(),
						StringComparison.OrdinalIgnoreCase));
		}
		if (sameRoleBot)
		{
			LogHelper.Log($"[代理抓取] 停止角色「{roleName}」：端口 {listenPort} 由机器人代理占用，按角色精确匹配后一并停止");
			return StopPort(listenPort);
		}
		return StopServerBinding(listenPort);
	}

	/// <summary>停止所有脱盒侧（非 nexus- 标记）监听；机器人代理监听保持不动。返回停止的端口数。</summary>
	public static int StopServerProxies()
	{
		List<int> ports;
		lock (Sync)
		{
			ports = Listeners
				.Where(item => item.Binding != null && !(item.Binding.Tag ?? "").StartsWith("nexus-", StringComparison.OrdinalIgnoreCase))
				.Select(item => item.Binding!.ListenPort)
				.Distinct()
				.ToList();
		}
		int stopped = 0;
		foreach (int port in ports)
		{
			if (StopPort(port))
			{
				stopped++;
			}
		}
		return stopped;
	}

	/// <summary>
	/// 端口归脱盒代理所有时，只挂断该端口上指定机器人账号的会话，监听与其它玩家连接保持不动。
	/// 返回挂断的会话数。
	/// </summary>
	public static int DropNexusSessions(int listenPort, string? loginName)
	{
		int dropped = 0;
		foreach (long id in Active.Keys.ToList())
		{
			if (!Active.TryGetValue(id, out var session) || session.ListenPort != listenPort)
			{
				continue;
			}
			bool matches = string.IsNullOrWhiteSpace(loginName)
				|| string.Equals((session.LoginRoleName ?? "").Trim(), loginName.Trim(), StringComparison.OrdinalIgnoreCase)
				|| string.Equals((session.ClientName ?? "").Trim(), loginName.Trim(), StringComparison.OrdinalIgnoreCase);
			if (!matches || !Active.TryRemove(id, out var removed))
			{
				continue;
			}
			LogHelper.Log("[代理抓取] 端口 " + listenPort + " 上机器人会话 " + (loginName ?? "(全部)") + " 已挂断（监听仍保留）");
			removed.Dispose();
			dropped++;
		}
		return dropped;
	}

	/// <summary>
	/// 会话认证前重新校验账号索引：账号列表增删会让监听启动时记录的索引漂移，
	/// 按启动时固化的 Channel|Account 键重新定位；对不上且找不到时回退原索引并记日志。
	/// </summary>
	private static int ResolveAuthIndexForSession(ProxyBinding binding, string sessionLabel)
	{
		try
		{
			System.Collections.Generic.IReadOnlyList<AccountStore.LocalAccount> all = AccountStore.All;
			int idx = binding.AuthAccountIndex;
			if (idx >= 0 && idx < all.Count)
			{
				string key = (all[idx].Channel ?? "") + "|" + (all[idx].Account ?? "");
				if (string.Equals(key, binding.AuthAccountKey ?? "", StringComparison.OrdinalIgnoreCase))
				{
					return idx;
				}
			}
			if (!string.IsNullOrWhiteSpace(binding.AuthAccountKey))
			{
				string[] parts = binding.AuthAccountKey.Split('|', 2);
				if (parts.Length == 2)
				{
					for (int i = 0; i < all.Count; i++)
					{
						if (string.Equals(all[i].Channel ?? "", parts[0], StringComparison.Ordinal)
							&& string.Equals(all[i].Account ?? "", parts[1], StringComparison.OrdinalIgnoreCase))
						{
							LogHelper.Log("[认证] " + sessionLabel + " 账号索引漂移 " + binding.AuthAccountIndex + " → " + i
								+ "（" + parts[1] + "），按账号键重新对准");
							return i;
						}
					}
					LogHelper.Log("[认证] " + sessionLabel + " 账号键 " + binding.AuthAccountKey + " 在账号列表中已不存在，保持原索引 " + idx);
				}
			}
			return binding.AuthAccountIndex;
		}
		catch
		{
			return binding.AuthAccountIndex;
		}
	}

	private static bool StopPort(int listenPort, string? requiredTagPrefix)
	{
		bool ownsTarget = false;
		bool hasAnyListener = false;
		lock (Sync)
		{
			hasAnyListener = Listeners.Any(item => item.Binding != null && item.Binding.ListenPort == listenPort);
			ownsTarget = Listeners.Any(item => item.Binding != null && item.Binding.ListenPort == listenPort
				&& (requiredTagPrefix == null || (item.Binding.Tag ?? "").StartsWith(requiredTagPrefix, StringComparison.OrdinalIgnoreCase)));
			if (requiredTagPrefix != null && !ownsTarget)
			{
				// 区分「根本没有监听」与「监听归另一类代理所有」，避免误导排障。
				if (!hasAnyListener)
				{
					LogHelper.Log($"[代理抓取] 端口 {listenPort} 当前没有代理监听，无需停止");
				}
				else
				{
					LogHelper.Log($"[代理抓取] 端口 {listenPort} 不归机器人代理所有，已忽略停止请求（保护脱盒代理）");
				}
				return false;
			}
		}
		SetTrafficStatus(listenPort, "已停止代理");
		lock (Sync)
		{
			foreach (ListenerEntry item in Listeners.ToList())
			{
				if (item.Binding != null && item.Binding.ListenPort == listenPort)
				{
					try
					{
						item.Cts?.Cancel();
					}
					catch
					{
					}
					try
					{
						item.Listener?.Stop();
					}
					catch
					{
					}
					Listeners.Remove(item);
				}
			}
		}
		// 只停监听不够：该端口上已经建立的会话也要主动断开，否则 StopPort 后
		// 玩家仍会通过旧会话继续转发（会话只在任一侧 EOF 时才消失）。
		foreach (long item2 in Active.Keys.ToList())
		{
			if (Active.TryGetValue(item2, out var value) && value.ListenPort == listenPort && Active.TryRemove(item2, out var removed))
			{
				SessionInfo sessionInfo = ToInfo(removed, "已停止");
				removed.Dispose();
				AddHistory(sessionInfo);
				SessionClosed?.Invoke(sessionInfo);
			}
		}
		lock (Sync)
		{
			IsRunning = Listeners.Count > 0;
			ActivePorts = Listeners.Select((ListenerEntry e) => e.Binding?.ListenPort ?? 0).ToArray();
		}
		LogHelper.Log($"[代理抓取] 已停止端口 {listenPort} 的代理监听（剩余 {Listeners.Count} 个）");
		StateChanged?.Invoke(IsRunning);
		WriteRolesFile();
		SyncServerListDat();
		return true;
	}

	private static void WriteRolesFile()
	{
		try
		{
			List<ListenerEntry> list;
			string targetHost;
			lock (Sync)
			{
				list = Listeners.ToList();
				targetHost = TargetHost ?? "";
			}
			string game = ServerPresets.All.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, targetHost, StringComparison.OrdinalIgnoreCase))?.Name ?? targetHost;
			List<AccountStore.LocalAccount> list2 = AccountStore.All.ToList();
			List<object> list3 = new List<object>();
			foreach (ListenerEntry item in list)
			{
				ProxyBinding binding = item.Binding;
				if (binding != null && binding.ListenPort > 0)
				{
					string bindingHost = binding.TargetHost ?? targetHost;
					string serverName = ServerPresets.All.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, bindingHost, StringComparison.OrdinalIgnoreCase))?.Name ?? bindingHost;
					string auth = "";
					if (binding.AuthAccountIndex >= 0 && binding.AuthAccountIndex < list2.Count)
					{
						auth = (list2[binding.AuthAccountIndex].Channel ?? "").Trim();
					}
					list3.Add(new
					{
						name = (binding.LoginName ?? ""),
						port = binding.ListenPort,
						host = "127.0.0.1",
						online = true,
						serverName = serverName,
						auth = auth,
						tag = ""
					});
				}
			}
			var value = new
			{
				version = 1,
				game = game,
				host = "127.0.0.1",
				roles = list3
			};
			string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MhyCPTD");
			Directory.CreateDirectory(text);
			string contents = JsonSerializer.Serialize((object)value, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			string text2 = Path.Combine(text, "roles.json");
			string text3 = text2 + ".tmp";
			// 内容没变就不写，避免 CPTD 反复重读时抓到半截状态。
			try
			{
				if (File.Exists(text2) && string.Equals(File.ReadAllText(text2), contents, StringComparison.Ordinal))
				{
					return;
				}
			}
			catch
			{
			}
			File.WriteAllText(text3, contents);
			// 覆盖式移动是原子替换：不能先 Delete 再 Move，否则 CPTD 会有一瞬间读不到
			// roles.json，表现为「角色端口列表在 0 个和 N 个之间快速横跳」。
			File.Move(text3, text2, true);
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[MhyCPTD] roles.json 写入失败");
		}
	}

	private static void SyncServerListDat()
	{
		try
		{
			List<MinecraftServerListService.Row> rows = new List<MinecraftServerListService.Row>();
			List<ListenerEntry> list;
			string text;
			lock (Sync)
			{
				list = Listeners.ToList();
				text = TargetHost ?? "";
			}
			foreach (ListenerEntry item2 in list)
			{
				ProxyBinding binding = item2.Binding;
				if (binding != null && binding.ListenPort > 0)
				{
					string bindingHost = binding.TargetHost ?? text;
					string text2 = ServerPresets.All.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, bindingHost, StringComparison.OrdinalIgnoreCase))?.Name ?? bindingHost;
					string text3 = (binding.LoginName ?? "").Trim();
					MinecraftServerListService.Row item = new MinecraftServerListService.Row
					{
						Port = binding.ListenPort,
						Name = ((text3.Length > 0) ? (text2 + " - " + text3) : text2)
					};
					rows.Add(item);
				}
			}
			Task.Run(() =>
			{
				try
				{
					var (num, list2) = MinecraftServerListService.Sync(rows);
					if (num > 0)
					{
						LogHelper.Log($"[服务器列表] 已同步 {rows.Count} 个角色端口到 {num} 个 servers.dat");
					}
					else
					{
						LogHelper.Debug("[服务器列表] 无需更新（" + rows.Count + " 个角色端口）");
					}
					foreach (string item3 in list2)
					{
						LogHelper.Debug("[服务器列表] " + item3);
					}
				}
				catch (Exception ex2)
				{
					LogHelper.DebugError(ex2, "[服务器列表] 同步失败");
				}
			});
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[服务器列表] 准备同步失败");
		}
	}

	// ---------- 对齐 404 的防炸机制（§3.4 / §3.5 / §4）----------

	/// <summary>全局并发连接上限。404 用 SemaphoreSlim(256,256) + WaitAsync(TimeSpan.Zero) 零等待，超限直接拒。</summary>
	private const int GlobalConnectionLimit = 256;

	private static readonly SemaphoreSlim GlobalConnections = new SemaphoreSlim(GlobalConnectionLimit, GlobalConnectionLimit);

	/// <summary>单 IP 并发上限：防止局域网内单台设备（或本机恶意进程）占满全局连接池。</summary>
	private const int PerIpConnectionLimit = 32;

	private static readonly ConcurrentDictionary<string, int> PerIpConnections = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	/// <summary>单个 Minecraft 包解压后的安全上限；1MB 压缩包最多允许膨胀到 4MB，防 zlib 炸弹。</summary>
	private const int MaxDecompressedPacketBytes = 4 * 1024 * 1024;

	/// <summary>当前并发会话数（诊断用）。</summary>
	public static int CurrentConnectionCount => GlobalConnectionLimit - GlobalConnections.CurrentCount;

	/// <summary>
	/// **上游热切换**（对齐 404 §3.4 的 `UpstreamRoute` + `Volatile.Write` 原子替换）。
	///
	/// 关键点：**只影响之后新建的连接，不动正在转发中的会话**。
	/// 这是「换上游 / 换线路时玩家不掉线」的根本做法 ——
	/// 此前我们是 Stop() 再 Start()，必然掐断所有会话，这也正是用户反馈的
	/// 「代理一重启游戏就断」的原因。
	///
	/// 流程（对齐 404 §3.5）：先做 3 秒 TCP 连通性探测，通过才切换；失败则保持原上游。
	/// </summary>
	public static async Task<(bool Ok, string Message)> SwitchUpstreamAsync(string host, int port, string? serverName = null)
	{
		if (string.IsNullOrWhiteSpace(host) || port <= 0)
		{
			return (false, "上游地址无效");
		}
		// 1) 先探测，别把玩家切到一个连不上的上游
		Exception? probeError = null;
		using (System.Net.Sockets.TcpClient probe = new System.Net.Sockets.TcpClient())
		{
			try
			{
				using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.0));
				await probe.ConnectAsync(host!, port, cts.Token).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				probeError = ex;
			}
		}
		if (probeError != null)
		{
			LogHelper.Log($"[代理抓取] 上游 TCP 探测未通过，保持原上游：{host}:{port} {probeError.Message}");
			return (false, "上游探测未通过，继续使用原上游：" + probeError.Message);
		}

		// 2) 原子替换目标：只改全局默认与监听绑定的目标，
		//    已经在转发中的会话（Active）沿用其建立时拿到的 upstream，不受影响。
		TargetHost = host;
		TargetPort = port;
		int changed = 0;
		lock (Sync)
		{
			foreach (ListenerEntry le in Listeners)
			{
				if (le.Binding != null)
				{
					le.Binding.TargetHost = host;
					le.Binding.TargetPort = port;
					changed++;
				}
			}
		}
		LogHelper.Log($"[代理抓取] 上游热切换完成：{host}:{port}"
			+ (string.IsNullOrEmpty(serverName) ? "" : ("（" + serverName + "）"))
			+ $"，影响 {changed} 个监听；已有会话保持不动（共 {Active.Count} 条）");
		return (true, $"已切换上游为 {host}:{port}，已有 {Active.Count} 条会话不受影响");
	}

	private static async Task AcceptLoopAsync(TcpListener listener, ProxyBinding binding, CancellationToken ct)
	{
		try
		{
			while (!ct.IsCancellationRequested)
			{
				TcpClient client;
				try
				{
					client = await listener.AcceptTcpClientAsync();
				}
				catch (Exception)
				{
					break;
				}
				// 全局并发上限（对齐 404 §3.4）：零等待拿不到额度就说明到顶了，
				// 直接关掉这条连接，避免被大量并发连接拖垮。
				if (!GlobalConnections.Wait(TimeSpan.Zero))
				{
					try
					{
						string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
						LogHelper.Log($"[代理抓取] 拒绝连接：达到全局连接上限 {GlobalConnectionLimit}，远端={remote}");
					}
					catch
					{
					}
					client.Dispose();
					continue;
				}
				// 单 IP 配额：在全局配额之后检查，避免单个远端把 256 个名额全部占满。
				string? remoteIp = GetRemoteIp(client);
				if (!TryAcquirePerIp(remoteIp))
				{
					try
					{
						string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
						LogHelper.Log($"[代理抓取] 拒绝连接：单 IP 并发超过 {PerIpConnectionLimit}，远端={remote}");
					}
					catch
					{
					}
					client.Dispose();
					GlobalConnections.Release();
					continue;
				}
				Task.Run(async () =>
				{
					try
					{
						await HandleClientAsync(client, binding, ct);
					}
					finally
					{
						GlobalConnections.Release();
						ReleasePerIp(remoteIp);
					}
				});
			}
		}
		catch (Exception ex2)
		{
			LogHelper.LogError(ex2, "[代理抓取] 接受循环退出");
		}
	}

	private static string? GetRemoteIp(TcpClient client)
	{
		try
		{
			IPAddress? address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
			if (address == null)
			{
				return null;
			}
			if (address.IsIPv4MappedToIPv6)
			{
				address = address.MapToIPv4();
			}
			return address.ToString();
		}
		catch
		{
			return null;
		}
	}

	private static bool TryAcquirePerIp(string? remoteIp)
	{
		if (string.IsNullOrWhiteSpace(remoteIp))
		{
			return true;
		}
		string key = remoteIp!;
		int count = PerIpConnections.AddOrUpdate(key, 1, (string _, int current) => current + 1);
		if (count <= PerIpConnectionLimit)
		{
			return true;
		}
		ReleasePerIp(key);
		return false;
	}

	private static void ReleasePerIp(string? remoteIp)
	{
		if (string.IsNullOrWhiteSpace(remoteIp))
		{
			return;
		}
		string key = remoteIp!;
		while (true)
		{
			if (!PerIpConnections.TryGetValue(key, out int current))
			{
				return;
			}
			if (current <= 1)
			{
				if (PerIpConnections.TryRemove(key, out _))
				{
					return;
				}
			}
			else if (PerIpConnections.TryUpdate(key, current - 1, current))
			{
				return;
			}
		}
	}

	/// <summary>开启 TCP keepalive，及时回收对端异常掉线后残留的半开连接（不做应用层空闲超时，避免误踢挂机玩家）。</summary>
	private static void ConfigureKeepAlive(TcpClient client)
	{
		try
		{
			Socket socket = client.Client;
			socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
			socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
			socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
			socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
		}
		catch
		{
		}
	}

	private static async Task HandleClientAsync(TcpClient client, ProxyBinding binding, CancellationToken acceptCt)
	{
		long id = Interlocked.Increment(ref _nextId);
		string sessionLabel = $"S{id:000}";
		string clientEp = client.Client.RemoteEndPoint?.ToString() ?? "?";
		client.NoDelay = true;
		ConfigureKeepAlive(client);
		ActiveSession session = null;
		ActiveSession value2;
		try
		{
			string targetHost = binding?.TargetHost ?? TargetHost ?? "";
			int targetPort = (((binding?.TargetPort ?? 0) > 0) ? binding.TargetPort : TargetPort);
			byte[] handshake = null;
			using (CancellationTokenSource peekCts = CancellationTokenSource.CreateLinkedTokenSource(acceptCt))
			{
				peekCts.CancelAfter(TimeSpan.FromSeconds(10.0));
				try
				{
					handshake = await ReadPacketAsync(client.GetStream(), peekCts.Token);
					if (TargetFromHandshake && handshake != null && TryParseHandshake(handshake, out var host, out var port) && !string.IsNullOrWhiteSpace(host) && port > 0)
					{
						if (IsLocalHost(host))
						{
							LogHelper.Log($"[代理抓取] {sessionLabel} 握手地址是回环地址 {host}:{port}，改用预设目标 {targetHost}:{targetPort}（避免连回自己）");
						}
						else
						{
							// 局域网监听时，远端客户端可自选握手目标。若允许任意目标，本机就会变成
							// 一个对外开放的 TCP 中继（可被用来探测内网/绕过网络边界）。这里只放行
							// 已配置的绑定目标或服务器预设，其余一律拒绝。
							if (AllowLanBindings && !IsConfiguredTarget(host, port, binding))
							{
								LogHelper.Log($"[代理抓取] {sessionLabel} 局域网模式下拒绝未配置的握手目标 {host}:{port}（防止开放中继）");
								client.Dispose();
								return;
							}
							targetHost = host;
							targetPort = port;
							LogHelper.Log($"[代理抓取] {sessionLabel} 握手包目标: {host}:{port}");
						}
					}
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "[代理抓取] " + sessionLabel + " 读取首包失败，使用预设目标");
				}
			}
			if (string.IsNullOrWhiteSpace(targetHost) || targetPort <= 0)
			{
				LogHelper.Log($"[代理抓取] {sessionLabel} 目标无效（{targetHost}:{targetPort}），关闭连接");
				client.Dispose();
				return;
			}
			if (IsLoopbackHost(targetHost) && Array.IndexOf(ActivePorts, targetPort) >= 0)
			{
				LogHelper.Log($"[代理抓取] {sessionLabel} 目标 {targetHost}:{targetPort} 是本代理自身的监听端口，拒绝自连环，关闭连接");
				client.Dispose();
				return;
			}
			// 目标若已被配置（绑定/全局/预设），允许它位于内网；否则（尤其局域网监听时）
			// 禁止回环、链路本地、云元数据等保留地址，防止 SSRF。
			bool allowPrivateTarget = !AllowLanBindings || IsConfiguredTarget(targetHost, targetPort, binding);
			if (IPAddress.TryParse(targetHost, out IPAddress? literalTarget) && IsBlockedTargetAddress(literalTarget, allowPrivateTarget))
			{
				LogHelper.Log($"[代理抓取] {sessionLabel} 目标 {targetHost}:{targetPort} 属于回环/内网/保留地址，已按安全策略拒绝");
				client.Dispose();
				return;
			}
			bool statusPing = false;
			int clientProtocol = 0;
			if (handshake != null && TryParseHandshakeEx(handshake, out _, out _, out int handshakeProtocol, out int handshakeNextState))
			{
				if (handshakeProtocol > 0)
				{
					LastClientProtocol = handshakeProtocol;
					clientProtocol = handshakeProtocol;
				}
				if (handshakeNextState == 2)
				{
					EnsureHealthMonitorStarted();
				}
				statusPing = handshakeNextState == 1;
			}
			if (!IsNormalMode && statusPing && IsProtectedTarget(targetHost, targetPort))
			{
				await AnswerStatusLocallyAsync(client, handshake, targetHost, targetPort, sessionLabel, acceptCt);
				return;
			}
			// 防DDOS模式保留连接节流；正常代理登录模式（v3.8.0 行为）去掉自带风控限制。
			string targetKey = targetHost + ":" + targetPort;
			if (!IsNormalMode)
			{
			// ===== 代理层连接节流（对齐 v3.8.0：认证失败冷却 8s / 8s 内 ≥8 次连接延迟 2.5s / 同目标并发 ≤6）=====
			DateTime nowUtc = DateTime.UtcNow;
			if (TargetAuthFailAt.TryGetValue(targetKey, out DateTime lastFailAt) && nowUtc - lastFailAt < TimeSpan.FromSeconds((double)ConnectRateWindowSeconds))
			{
				LogHelper.Log($"[代理抓取] {sessionLabel} 目标 {targetKey} 认证刚失败（{(int)(nowUtc - lastFailAt).TotalSeconds} 秒前），冷却 8 秒规避风控踢包");
				try
				{
					await Task.Delay(8000, acceptCt).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (OperationCanceledException)
				{
					client.Dispose();
					return;
				}
			}
			for (int w = 0; w < 25; w++)
			{
				int activeForTarget = Active.Values.Count((ActiveSession s) => !s.Closed
					&& string.Equals(s.TargetHostUsed, targetHost, StringComparison.OrdinalIgnoreCase)
					&& s.TargetPortUsed == targetPort);
				if (activeForTarget < MaxActivePerTarget)
				{
					break;
				}
				try
				{
					await Task.Delay(200, acceptCt).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (OperationCanceledException)
				{
					client.Dispose();
					return;
				}
			}
			bool needSlowDown = false;
			lock (ConnectThrottleSync)
			{
				DateTime[] recent = TargetConnectTimes.GetOrAdd(targetKey, (string _) => Array.Empty<DateTime>())
					.Where((DateTime t) => DateTime.UtcNow - t < TimeSpan.FromSeconds((double)ConnectRateWindowSeconds)).ToArray();
				needSlowDown = recent.Length >= MaxConnectsPerWindow;
				TargetConnectTimes[targetKey] = recent.Append(DateTime.UtcNow).ToArray();
			}
			if (needSlowDown)
			{
				LogHelper.Log($"[代理抓取] {sessionLabel} 目标 {targetKey} 8 秒内连接过于频繁，延迟 2.5 秒规避风控踢包");
				try
				{
					await Task.Delay(2500, acceptCt).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (OperationCanceledException)
				{
					client.Dispose();
					return;
				}
			}
			}
			TcpClient upstream;
			using (CancellationTokenSource peekCts = CancellationTokenSource.CreateLinkedTokenSource(acceptCt))
			{
				peekCts.CancelAfter(TimeSpan.FromSeconds(20.0));
				upstream = await ConnectUpstreamAsync(targetHost, targetPort, sessionLabel, allowPrivateTarget, peekCts.Token, session);
			}
			try
			{
				string value = (upstream.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString();
				if (!string.IsNullOrWhiteSpace(value))
				{
					LogHelper.Log($"[代理抓取] {sessionLabel} 目标 {targetHost}:{targetPort} 本次解析为 {value}");
				}
			}
			catch
			{
			}
			string logPath = Path.Combine(path2: sessionLabel + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log", path1: CaptureDir);
			FileStream logStream = null;
			if (CaptureLogEnabled)
			{
				try
				{
					logStream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
				}
				catch
				{
					logStream = null;
				}
			}
			session = new ActiveSession
			{
				Id = id,
				Client = client,
				Upstream = upstream,
				ClientStream = client.GetStream(),
				UpstreamStream = upstream.GetStream(),
				LogStream = logStream,
				SessionLabel = sessionLabel,
				Started = DateTime.Now,
				TargetHostUsed = targetHost,
				TargetPortUsed = targetPort,
				LoginRoleName = binding?.LoginName,
				ListenPort = (binding?.ListenPort ?? 0),
				ProtectedPreset = FindProtectedPreset(targetHost, targetPort)
			};
			session.ProtocolVersion = clientProtocol;
			Interlocked.Increment(ref TrafficFor(session.ListenPort).Sessions);
			SetTrafficStatus(session, "已连接，等待登录");
			if (session.ListenPort > 0)
			{
				LogHelper.Log("[代理画面] " + sessionLabel + " 本地通道 127.0.0.1:" + session.ListenPort + " 已建立（协议=" + clientProtocol + "），开始监听游戏画面 / 聊天 / 玩家出入数据");
			}
			Active[id] = session;
			if (handshake != null)
			{
				byte[] data = BuildForwardHandshake(handshake, targetHost, targetPort, sessionLabel);
				await WriteToUpstreamAsync(session, data);
			}
			if (!string.IsNullOrWhiteSpace(binding?.LoginName))
			{
				try
				{
					using CancellationTokenSource peekCts = CancellationTokenSource.CreateLinkedTokenSource(acceptCt);
					peekCts.CancelAfter(TimeSpan.FromSeconds(10.0));
					byte[] array = await ReadPacketAsync(session.ClientStream, peekCts.Token);
					if (array != null)
					{
						if (TryParseLoginStartIdentity(array, out var name, out var uuid))
						{
							session.ClientName = name;
							if (uuid.Length == 16)
							{
								session.ClientUuid = uuid;
								session.ClientUuidHex = Convert.ToHexString(uuid).ToLowerInvariant();
								LogHelper.Log($"[代理抓取] {sessionLabel} 客户端离线身份: name={name} uuid={session.ClientUuidHex}");
							}
							else
							{
								string text = (SettingsManager.Instance.ClientUuidOverride ?? "").Trim().Replace("-", "").ToLowerInvariant();
								if (string.IsNullOrEmpty(text))
								{
									text = "0000000000003004998f501a96cc968b";
								}
								if (text.Length == 32 && TryParseHexBytes(text, out var bytes))
								{
									session.ClientUuid = bytes;
									session.ClientUuidHex = text;
									LogHelper.Log($"[代理抓取] {sessionLabel} 客户端离线身份: name={name} uuid={session.ClientUuidHex} (LoginStart 无 uuid，使用内置回退)");
								}
								else
								{
									LogHelper.Log($"[代理抓取] {sessionLabel} 客户端离线身份: name={name} uuid=无 (LoginStart 无 uuid，且未配置 ClientUuidOverride)");
								}
							}
						}
						byte[] rewritten = RewriteLoginStart(array, binding.LoginName);
						byte[] data2 = BuildPacket(rewritten ?? array);
						await WriteToUpstreamAsync(session, data2);
						if (rewritten != null)
						{
							LogHelper.Log("[代理抓取] " + sessionLabel + " LoginStart 用户名改写为 " + binding.LoginName);
						}
					}
				}
				catch (Exception ex2)
				{
					LogHelper.LogError(ex2, "[代理抓取] " + sessionLabel + " LoginStart 改写失败，按原样转发");
				}
			}
			SessionInfo sessionInfo = ToInfo(session, "已连接");
			AddHistory(sessionInfo);
			SessionOpened?.Invoke(sessionInfo);
			LogHelper.Log($"[代理抓取] {sessionLabel} 新连接 {clientEp} -> {targetHost}:{targetPort} 日志={logPath}");
			if (binding != null && binding.AuthAccountIndex >= 0)
			{
				try
				{
					int sessionAuthIndex = ResolveAuthIndexForSession(binding, sessionLabel);
					LogHelper.Log($"[代理抓取] {sessionLabel} 进入网易云认证 MITM: 账号索引={sessionAuthIndex} GameId={binding.GameId} 登录名改写={(string.IsNullOrWhiteSpace(binding.LoginName) ? "(不改名)" : binding.LoginName)} 目标={binding.TargetHost ?? targetHost}:{targetPort}");
					NeteaseAuthBridge bridge = new NeteaseAuthBridge
					{
						AccountIndex = sessionAuthIndex,
						GameId = binding.GameId,
						ServerName = ServerPresets.All.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, binding.TargetHost ?? targetHost, StringComparison.OrdinalIgnoreCase))?.Name
					};
					using CancellationTokenSource peekCts = CancellationTokenSource.CreateLinkedTokenSource(acceptCt);
					peekCts.CancelAfter(TimeSpan.FromSeconds(45.0));
					// 认证单独给 150 秒预算：Auth fail / token expired 时会做原地自动重登（账密登录 + GameStart + join 重试），
					// 可能耗时 20-40 秒，不能挤占握手 peek 的 45 秒预算（否则重登到一半就被取消踢下线）
					using CancellationTokenSource authCts = CancellationTokenSource.CreateLinkedTokenSource(acceptCt);
					authCts.CancelAfter(TimeSpan.FromSeconds(150.0));
					var (flag, text2) = await UpgradeEncryptionAuthAsync(session, bridge, binding, peekCts.Token, authCts.Token);
				if (!flag)
				{
					// 防DDOS模式：认证失败后对该目标冷却 8 秒；正常代理登录模式不记录（去掉自带风控限制）。
					if (!IsNormalMode)
					{
						TargetAuthFailAt[targetKey] = DateTime.UtcNow;
					}
					string text3 = (string.IsNullOrWhiteSpace(text2) ? "网易云认证失败" : text2);
						SetTrafficError(session, "认证未完成: " + text3);
						LogHelper.Log("[代理抓取] " + sessionLabel + " 网易云认证未完成（" + text3 + "）");
						session.Dispose();
						if (Active.TryRemove(id, out value2))
						{
							SessionInfo sessionInfo2 = ToInfo(session, "认证失败");
							AddHistory(sessionInfo2);
							SessionClosed?.Invoke(sessionInfo2);
						}
						return;
					}
				}
				catch (Exception ex3)
				{
					LogHelper.LogError(ex3, "[代理抓取] " + sessionLabel + " 网易云认证升级异常，关闭会话");
					session.Dispose();
					if (Active.TryRemove(id, out value2))
					{
						SessionInfo sessionInfo3 = ToInfo(session, "认证异常");
						AddHistory(sessionInfo3);
						SessionClosed?.Invoke(sessionInfo3);
					}
					return;
				}
			}
			WatchdogAsync(session, acceptCt);
			Task<string> pumpUp = PumpAsync(session, isUp: true);
			Task<string> pumpDown = PumpAsync(session, isUp: false);
			Task<string> firstTask = await Task.WhenAny(pumpUp, pumpDown);
			string value3 = await firstTask;
			string value4 = ((firstTask == pumpUp) ? "客户端→服务器（客户端侧先断）" : "服务器→客户端（服务器侧先断）");
			LogHelper.Log($"[代理抓取] {sessionLabel} 中继结束: 先结束={value4} 原因=\"{value3}\" 存活={DateTime.Now - session.Started:hh\\:mm\\:ss} 上行={session.UpBytes}B 下行={session.DownBytes}B");
			SetTrafficStatus(session, "已断开（" + value3 + "）");
			if (value3 != null && (value3.Contains("服务器流 EOF") || value3.Contains("服务器先断开")))
			{
				SetTrafficError(session, "服务器断开: " + value3);
			}
			await Task.WhenAll<string>(pumpUp, pumpDown);
		}
		catch (Exception ex4)
		{
			LogHelper.LogError(ex4, "[代理抓取] " + sessionLabel + " 中继异常");
		}
		finally
		{
			if (session != null)
			{
				Interlocked.Decrement(ref TrafficFor(session.ListenPort).Sessions);
				if (Active.TryRemove(id, out value2))
				{
					SessionInfo sessionInfo4 = ToInfo(session, "已关闭");
					session.Dispose();
					AddHistory(sessionInfo4);
					SessionClosed?.Invoke(sessionInfo4);
				}
			}
			else
			{
				try
				{
					client.Dispose();
				}
				catch
				{
				}
			}
		}
	}

	/// <summary>
	/// 正常代理登录模式（对齐 v3.8.0）：单目标直连，不做节点轮换 / IP 池健康排序 /
	/// 地址安全拦截 / 健康探测 / 本地状态应答，也不带任何连接节流（自带风控限制已去掉）。
	/// 仅当目标被 hosts 劫持解析为回环时，才按预设 IP 池直连真实服务器 —— 这是 3.8.0 原有的
	/// hosts 兜底，不是防DDOS 模式的节点轮换。
	/// </summary>
	private static async Task<TcpClient> ConnectUpstreamNormalAsync(string targetHost, int targetPort, string label, CancellationToken ct, ActiveSession? session)
	{
		bool loopResolved = false;
		try
		{
			IPAddress[] addresses = await Dns.GetHostAddressesAsync(targetHost).ConfigureAwait(continueOnCapturedContext: false);
			loopResolved = addresses.Length != 0 && addresses.All((IPAddress a) => IPAddress.IsLoopback(a));
		}
		catch
		{
		}
		TcpClient upstream = new TcpClient
		{
			NoDelay = true
		};
		try
		{
			if (loopResolved)
			{
				string[] ipPool = ServerPresets.All.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, targetHost, StringComparison.OrdinalIgnoreCase))?.IpPool ?? Array.Empty<string>();
				if (ipPool.Length == 0)
				{
					throw new InvalidOperationException("目标 " + targetHost + " 解析到回环地址且无预设 IP 池，无法直连真实服务器");
				}
				Exception? last = null;
				int start = Interlocked.Increment(ref _normalIpPoolCursor) % ipPool.Length;
				for (int i = 0; i < ipPool.Length; i++)
				{
					string ip = ipPool[(start + i) % ipPool.Length];
					try
					{
						await upstream.ConnectAsync(IPAddress.Parse(ip), targetPort, ct).ConfigureAwait(continueOnCapturedContext: false);
						if (session != null)
						{
							session.UpstreamRoute = "预设IP池 " + ip + ":" + targetPort;
						}
						LogHelper.Log($"[代理抓取] {label} 目标 {targetHost} 被劫持解析为回环，改用预设 IP 池直连 {ip}:{targetPort}（正常代理登录模式）");
						return upstream;
					}
					catch (Exception ex)
					{
						last = ex;
					}
				}
				throw last ?? new InvalidOperationException("预设 IP 池全部连接失败");
			}
			await upstream.ConnectAsync(targetHost, targetPort, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (session != null)
			{
				session.UpstreamRoute = "直连 " + targetHost + ":" + targetPort;
			}
			LogHelper.Log($"[代理抓取] {label} 已连接上游 {targetHost}:{targetPort}（正常代理登录模式）");
			return upstream;
		}
		catch
		{
			upstream.Dispose();
			throw;
		}
	}

	private static async Task<TcpClient> ConnectUpstreamAsync(string targetHost, int targetPort, string label, bool allowPrivateTarget, CancellationToken ct, ActiveSession? session = null)
	{
		if (IsNormalMode)
		{
			return await ConnectUpstreamNormalAsync(targetHost, targetPort, label, ct, session).ConfigureAwait(continueOnCapturedContext: false);
		}
		IPAddress[] resolved;
		try
		{
			resolved = await ResolveHostAsync(targetHost, ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			resolved = Array.Empty<IPAddress>();
			LogHelper.Log($"[防DDoS] {label} 目标 {targetHost} 解析失败: {ex.Message}");
		}
		// 只连接通过安全策略的地址；连接时直接用 IP，避免校验后 DNS 再次解析造成 rebinding。
		List<IPAddress> allowed = new List<IPAddress>();
		foreach (IPAddress address in resolved)
		{
			if (IsBlockedTargetAddress(address, allowPrivateTarget))
			{
				LogHelper.Log($"[防DDoS] {label} 目标 {targetHost} 解析到受限地址 {address}，已跳过");
				continue;
			}
			allowed.Add(address);
		}
		Exception ex2 = null;
		if (allowed.Count > 0)
		{
			// 既定原则：程序不替线路做主。健康检查与节点轮换仅记录日志、供 IP 池排序，
			// 不再强制跳过直连——直连失败自然回落 IP 池（下方 catch），避免本地误判把用户全赶到备用线路。
			if (UpstreamHealth.TryGetValue(targetHost + ":" + targetPort, out ProxyUpstreamHealth health) && !health.Reachable)
			{
				LogHelper.Log($"[防DDoS] {label} 健康表暂标记 {targetHost}:{targetPort} 不可用，仍先照常直连（失败自动回落 IP 池）");
			}
			if (IsLinePenalized(targetHost, targetPort, null))
			{
				LogHelper.Log($"[节点轮换] {label} 直连节点 {targetHost}:{targetPort} 处于认证失败冷却期（连接入口已按 8 秒冷却等待），IP 池节点排序中已降权");
			}
			try
			{
				TcpClient tcpClient = await TryConnectAnyAsync(allowed, targetPort, 8000, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (session != null)
				{
					session.UpstreamRoute = "直连 " + targetHost + ":" + targetPort;
				}
				LogHelper.Log($"[代理抓取] {label} 已连接上游 {targetHost}:{targetPort}");
				return tcpClient;
			}
			catch (Exception ex3)
			{
				ex2 = ex3;
				LogHelper.Log($"[防DDoS] {label} 主目标 {targetHost}:{targetPort} 连接失败: {ex3.Message}");
			}
		}
		else
		{
			LogHelper.Log($"[防DDoS] {label} 目标 {targetHost} 无可用公网解析地址，跳过直连，改用预设 IP 池");
		}
		ServerPreset serverPreset = FindProtectedPreset(targetHost, targetPort);
		if (serverPreset == null || serverPreset.IpPool == null || serverPreset.IpPool.Length == 0)
		{
			if (resolved.Length > 0 && allowed.Count == 0)
			{
				string blockedKind = (allowPrivateTarget ? "回环/链路本地/保留地址" : "回环/内网/保留地址");
				throw new InvalidOperationException("目标 " + targetHost + " 指向" + blockedKind + "，已按安全策略拒绝连接");
			}
			throw ex2 ?? new InvalidOperationException("无法连接上游 " + targetHost + ":" + targetPort);
		}
		foreach (string ip in OrderIpPoolByHealth(targetHost, targetPort, serverPreset.IpPool))
		{
			try
			{
				if (!IPAddress.TryParse(ip, out IPAddress? poolAddress))
				{
					LogHelper.Log($"[防DDoS] {label} IP 池条目不是合法 IP，已跳过: {ip}");
					continue;
				}
				// 预设 IP 池属于本地可信配置，允许内网地址（例如自建服务器），但仍拒绝回环/链路本地。
				if (IsBlockedTargetAddress(poolAddress, allowPrivate: true))
				{
					LogHelper.Log($"[防DDoS] {label} IP 池条目属于回环/保留地址，已跳过: {ip}");
					continue;
				}
				TcpClient tcpClient2 = await TryConnectAsync(poolAddress, targetPort, 4000, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (session != null)
				{
					session.UpstreamRoute = "预设IP池 " + ip + ":" + targetPort;
				}
				LogHelper.Log($"[防DDoS] {label} 改用预设 IP 池直连 {ip}:{targetPort}（主目标不可用）{(IsLinePenalized(targetHost, targetPort, ip) ? "（该节点处于认证失败冷却期，仅作兜底）" : "")}");
				return tcpClient2;
			}
			catch (Exception ex4)
			{
				ex2 = ex4;
				LogHelper.Log($"[防DDoS] {label} IP 池 {ip}:{targetPort} 连接失败: {ex4.Message}");
			}
		}
		throw ex2 ?? new InvalidOperationException("预设 IP 池全部连接失败");
	}

	/// <summary>
	/// 判断地址是否应被代理拒绝。回环、链路本地（含云元数据 169.254.169.254）、组播、保留段始终拒绝；
	/// RFC1918/ULA/CGNAT 仅在 allowPrivate=false（局域网开放且目标未配置）时拒绝。
	/// </summary>
	private static bool IsBlockedTargetAddress(IPAddress address, bool allowPrivate)
	{
		if (address == null)
		{
			return true;
		}
		if (address.IsIPv4MappedToIPv6)
		{
			address = address.MapToIPv4();
		}
		if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
		{
			return true;
		}
		if (address.AddressFamily == AddressFamily.InterNetwork)
		{
			byte[] b = address.GetAddressBytes();
			if (b[0] == 0 || b[0] == 127 || b[0] >= 224)
			{
				return true;
			}
			if (b[0] == 169 && b[1] == 254)
			{
				return true;
			}
			if (b[0] == 198 && (b[1] == 18 || b[1] == 19))
			{
				return true;
			}
			if (!allowPrivate)
			{
				if (b[0] == 10)
				{
					return true;
				}
				if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
				{
					return true;
				}
				if (b[0] == 192 && b[1] == 168)
				{
					return true;
				}
				if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
				{
					return true;
				}
			}
			return false;
		}
		if (address.AddressFamily == AddressFamily.InterNetworkV6)
		{
			if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
			{
				return true;
			}
			byte[] b = address.GetAddressBytes();
			if ((b[0] & 0xFE) == 0xFC)
			{
				return !allowPrivate;
			}
			return false;
		}
		return true;
	}

	/// <summary>在给定的已校验地址集合中依次尝试连接，总预算 timeoutMs，避免逐个地址叠加超时。</summary>
	private static async Task<TcpClient> TryConnectAnyAsync(IReadOnlyList<IPAddress> addresses, int port, int timeoutMs, CancellationToken ct)
	{
		Exception last = null;
		DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
		foreach (IPAddress address in addresses)
		{
			int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
			if (remaining <= 0)
			{
				break;
			}
			try
			{
				return await TryConnectAsync(address, port, remaining, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex)
			{
				last = ex;
			}
		}
		throw last ?? new SocketException((int)SocketError.TimedOut);
	}

	private static async Task<TcpClient> TryConnectAsync(IPAddress address, int port, int timeoutMs, CancellationToken ct)
	{
		TcpClient tcpClient = new TcpClient
		{
			NoDelay = true
		};
		try
		{
			using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
			{
				cts.CancelAfter(timeoutMs);
				await tcpClient.ConnectAsync(address, port, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			ConfigureKeepAlive(tcpClient);
			return tcpClient;
		}
		catch
		{
			try
			{
				tcpClient.Dispose();
			}
			catch
			{
			}
			throw;
		}
	}

	// ===== 线路自愈（自动更换节点）=====
	// 认证阶段反复失败的节点（服务器未响应/网易风控）临时降权，新会话自动轮换到下一可用节点。
	// 只影响节点选择顺序，不彻底禁用：池内全部被罚时仍可作最后手段使用。
	private sealed record LineFailureState(int Count, DateTime WindowStartUtc, DateTime PenaltyUntilUtc);

	private static readonly ConcurrentDictionary<string, LineFailureState> LineFailures = new ConcurrentDictionary<string, LineFailureState>(StringComparer.Ordinal);

	/// <summary>触发停用的失败次数阈值（时间窗口内）。</summary>
	private const int LineFailureThreshold = 2;

	/// <summary>失败计数时间窗口：超过后计数重新开始。</summary>
	private static readonly TimeSpan LineFailureWindow = TimeSpan.FromMinutes(10.0);

	/// <summary>节点停用（降权）时长：到期后自动恢复候选资格。</summary>
	private static readonly TimeSpan LinePenalty = TimeSpan.FromMinutes(10.0);

	private static string LineKey(string host, int port, string? node)
	{
		return host + ":" + port + "|" + (string.IsNullOrWhiteSpace(node) ? "直连" : node.Trim());
	}

	private static bool IsLinePenalized(string host, int port, string? node)
	{
		if (LineFailures.TryGetValue(LineKey(host, port, node), out LineFailureState? state))
		{
			return state.PenaltyUntilUtc > DateTime.UtcNow;
		}
		return false;
	}

	/// <summary>
	/// 上报一次某节点的认证阶段失败。同一节点在窗口内累计达到阈值后临时降权，
	/// 之后的连接自动优先其它节点（自动更换节点）；到期自动恢复。
	/// </summary>
	public static void ReportLineFailure(string host, int port, string? node, string reason)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(host) || port <= 0)
			{
				return;
			}
			string key = LineKey(host, port, node);
			DateTime now = DateTime.UtcNow;
			LineFailures.AddOrUpdate(key,
				_ => new LineFailureState(1, now, DateTime.MinValue),
				(_, old) => (old.WindowStartUtc + LineFailureWindow < now)
					? new LineFailureState(1, now, DateTime.MinValue)
					: new LineFailureState(old.Count + 1, old.WindowStartUtc, old.PenaltyUntilUtc));
			LineFailureState cur = LineFailures[key];
			if (cur.Count >= LineFailureThreshold && cur.PenaltyUntilUtc <= now)
			{
				DateTime until = now + LinePenalty;
				LineFailures[key] = new LineFailureState(cur.Count, cur.WindowStartUtc, until);
				string nodeText = string.IsNullOrWhiteSpace(node) ? ("直连 " + host + ":" + port) : (node.Trim() + ":" + port);
				LogHelper.Force($"[节点轮换] 节点 {nodeText} 在 {(int)LineFailureWindow.TotalMinutes} 分钟内累计 {cur.Count} 次认证失败（{reason}），已降权 {(int)LinePenalty.TotalMinutes} 分钟（IP 池排序后置，直连不再强制跳过，照常请求）");
			}
		}
		catch
		{
		}
	}

	/// <summary>按当前会话的实际线路（直连/预设IP池节点）上报认证阶段失败，供自动更换节点。</summary>
	private static void ReportAuthLineFailureFromSession(ActiveSession session, string reason)
	{
		// 正常代理登录模式不做节点轮换（单目标固定，换节点是防DDOS 模式特性）。
		if (IsNormalMode)
		{
			return;
		}
		try
		{
			string route = session.UpstreamRoute ?? "";
			if (route.StartsWith("预设IP池 ", StringComparison.Ordinal))
			{
				string node = route.Substring("预设IP池 ".Length);
				int colon = node.IndexOf(':');
				if (colon > 0)
				{
					node = node.Substring(0, colon);
				}
				ReportLineFailure(session.TargetHostUsed, session.TargetPortUsed, node, reason);
			}
			else
			{
				ReportLineFailure(session.TargetHostUsed, session.TargetPortUsed, null, reason);
			}
		}
		catch
		{
		}
	}

	/// <summary>认证通过后清除所用节点的失败计数：节点恢复正常即立即洗白，不留旧账。</summary>
	private static void ReportAuthLineSuccessFromSession(ActiveSession session)
	{
		if (IsNormalMode)
		{
			return;
		}
		try
		{
			string route = session.UpstreamRoute ?? "";
			string? node = null;
			if (route.StartsWith("预设IP池 ", StringComparison.Ordinal))
			{
				node = route.Substring("预设IP池 ".Length);
				int colon = node.IndexOf(':');
				node = (colon > 0) ? node.Substring(0, colon) : node;
			}
			LineFailures.TryRemove(LineKey(session.TargetHostUsed, session.TargetPortUsed, node), out _);
		}
		catch
		{
		}
	}

	private static IEnumerable<string> OrderIpPoolByHealth(string targetHost, int targetPort, string[] pool)
	{
		ProxyUpstreamHealth health;
		UpstreamHealth.TryGetValue(targetHost + ":" + targetPort, out health);
		// 健康 + 未被罚的节点排最前；健康但被罚的靠后（认证反复失败，自动换节点）；
		// 全都不健康时至少按相对优先级兜底，保证始终有返回值。
		return pool.OrderByDescending((string ip) =>
		{
			int score = 0;
			if (health != null && health.IpReachable.TryGetValue(ip, out var reachable) && reachable)
			{
				score += 2;
			}
			if (!IsLinePenalized(targetHost, targetPort, ip))
			{
				score += 1;
			}
			return score;
		});
	}

	/// <summary>
	/// 认证失败原因后的线路自检提示：让玩家/开发者一眼区分「代理线路问题」和「账号问题」。
	/// 走预设 IP 池的会话若反复认证失败，大概率是线路/服务器侧问题而非账号会话失效。
	/// </summary>
	private static string RouteHint(ActiveSession session)
	{
		string route = session?.UpstreamRoute ?? "";
		if (string.IsNullOrWhiteSpace(route))
		{
			return "";
		}
		if (route.StartsWith("预设IP池", StringComparison.Ordinal))
		{
			return "（自检: 本次线路=" + route + "，非主目标直连；若该提示反复出现，多为线路/服务器侧问题，可先测速或在服务器列表检查该服务器）";
		}
		return "（自检: 本次线路=" + route + "）";
	}

	private static async Task<(bool Success, string Reason)> UpgradeEncryptionAuthAsync(ActiveSession session, NeteaseAuthBridge bridge, ProxyBinding binding, CancellationToken ct, CancellationToken authCt)
	{
		bool gotRequest = false;
		while (!gotRequest)
		{
			byte[] body = await ReadPacketAsync(session.UpstreamStream, ct);
			if (body == null)
			{
				string reason1 = "服务器未响应，连接在认证前被关闭（若反复出现，该账号的网易会话可能已失效，请重新登录绑定）" + RouteHint(session);
				// 典型线路问题：同一节点 10 分钟内反复出现将自动更换节点
				ReportAuthLineFailureFromSession(session, "服务器在认证前关闭连接");
				await SendLoginDisconnectAsync(session, reason1, ct);
				return (Success: false, Reason: reason1);
			}
			int num = PeekPacketId(body);
			switch (num)
			{
			case 0:
			{
				byte[] plain = BuildPacket(body);
				await session.ClientStream.WriteAsync(plain, 0, plain.Length, ct);
				Interlocked.Add(ref session.DownBytes, plain.Length);
				WriteChunk(session, plain, plain.Length, isUp: false);
				LogHelper.Log($"[代理抓取] {session.SessionLabel} 服务器在登录期踢出客户端，原样转发（{body.Length}B: {HexShort(body)}）");
				SetTrafficError(session, "登录被踢: " + ExtractKickText(body));
				ReportKickCredentialFailure(binding, ExtractKickText(body));
				return (Success: false, Reason: "被服务器在登录期踢出");
			}
			case 3:
			{
				int pos = 0;
				NetShared.ReadVarInt(body, ref pos);
				int th = (session.CompressionThreshold = NetShared.ReadVarInt(body, ref pos));
				byte[] plain = BuildPacket(body);
				await session.ClientStream.WriteAsync(plain, 0, plain.Length, ct);
				Interlocked.Add(ref session.DownBytes, plain.Length);
				WriteChunk(session, plain, plain.Length, isUp: false);
				LogHelper.Log($"[代理抓取] {session.SessionLabel} 上游设置压缩阈值 {th}（实际已转发给客户端，压缩直通模式）");
				break;
			}
			default:
				if (bridge.ParseEncryptionRequest(body))
				{
					gotRequest = true;
					LogHelper.Log("[代理抓取] " + session.SessionLabel + " 已拦截上游 EncryptionRequest（服务器公钥不转发给客户端，进入全 MITM）");
					break;
				}
				LogHelper.Log($"[代理抓取] {session.SessionLabel} 登录期上游包暂缓转发 {body.Length}B (pid={num})");
				break;
			}
		}
		bridge.GenerateOwnSecret();
		if (!(await bridge.JoinServerAsync(authCt)))
		{
			string failReason = (string.IsNullOrWhiteSpace(bridge.LastFailReason) ? "网易云认证未通过（joinServer 失败）" : bridge.LastFailReason);
			// 风控类失败（login abnormally）：当前接入节点可能已被网易侧标记，反复出现时自动更换节点；
			// 注意会话类失效（auth fail/token expired）是账号会话问题，与节点无关，不参与节点轮换。
			if (NeteaseAuthBridge.IsRiskControlMessage(failReason))
			{
				ReportAuthLineFailureFromSession(session, "网易风控拦截（login abnormally）");
			}
			string reason1 = failReason + RouteHint(session);
			LogHelper.Log("[代理抓取] " + session.SessionLabel + " 网易云认证未通过: " + reason1);
			await SendLoginDisconnectAsync(session, reason1, ct);
			return (Success: false, Reason: reason1);
		}
		byte[] data = bridge.BuildOwnEncryptionResponse();
		await WriteToUpstreamAsync(session, data);
		LogHelper.Log("[代理抓取] " + session.SessionLabel + " 网易云认证通过并提交代理侧 EncryptionResponse，建立双侧密钥");
		// 本次线路认证通过：清除该节点的失败计数（自动洗白）
		ReportAuthLineSuccessFromSession(session);
		byte[] ownSecret = bridge.OwnSecret;
		session.UpCfb = new Cfb8Runner(ownSecret);
		session.DownCfb = new Cfb8Runner(ownSecret);
		using CancellationTokenSource respCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		respCts.CancelAfter(TimeSpan.FromSeconds(30.0));
		while (true)
		{
			byte[] body = await ReadPacketCipheredAsync(session.UpstreamStream, session.UpCfb, respCts.Token);
			if (body == null)
			{
				break;
			}
			byte[] plain = DecodeUpstream(session, body);
			int num2 = PeekPacketId(plain);
			switch (num2)
			{
			case 0:
			{
				byte[] plain3 = BuildPacket(body);
				await session.ClientStream.WriteAsync(plain3, 0, plain3.Length, respCts.Token);
				Interlocked.Add(ref session.DownBytes, plain3.Length);
				WriteChunk(session, plain3, plain3.Length, isUp: false);
				LogHelper.Log($"[代理抓取] {session.SessionLabel} 服务器加密后在登录期踢出客户端，原样转发原因后结束（{body.Length}B: {HexShort(body)}）");
				SetTrafficError(session, "登录被踢: " + ExtractKickText(plain));
				ReportKickCredentialFailure(binding, ExtractKickText(plain));
				return (Success: false, Reason: "被服务器在登录期踢出（加密后）");
			}
			case 3:
			{
				int pos2 = 0;
				NetShared.ReadVarInt(plain, ref pos2);
				int th = (session.CompressionThreshold = NetShared.ReadVarInt(plain, ref pos2));
				byte[] plain3 = BuildPacket(body);
				await session.ClientStream.WriteAsync(plain3, 0, plain3.Length, respCts.Token);
				Interlocked.Add(ref session.DownBytes, plain3.Length);
				WriteChunk(session, plain3, plain3.Length, isUp: false);
				LogHelper.Log($"[代理抓取] {session.SessionLabel} 加密后上游设置压缩阈值 {th}（实际已转发给客户端，压缩直通模式）");
				break;
			}
			case 2:
			{
				byte[] plain3 = BuildPacket(body);
				await session.ClientStream.WriteAsync(plain3, 0, plain3.Length, respCts.Token);
				Interlocked.Add(ref session.DownBytes, plain3.Length);
				WriteChunk(session, plain3, plain3.Length, isUp: false);
				LogHelper.Log("[代理抓取] " + session.SessionLabel + " LoginSuccess 已解密原样转发给客户端（实验:压缩直通模式）身份=" + (session.ClientName ?? "?") + " uuid=" + (session.ClientUuidHex ?? "无"));
				SetTrafficStatus(session, "登录成功，选择进服");
				MarkEnterSuccess(binding);
				return (Success: true, Reason: "");
			}
			default:
				LogHelper.Log($"[代理抓取] {session.SessionLabel} 加密后登录期上游包暂不转发 {body.Length}B (pid={num2})");
				break;
			}
		}
		LogHelper.Log("[代理抓取] " + session.SessionLabel + " 未收到上游 LoginSuccess（加密流读取失败）");
		string reason3 = "未收到上游 LoginSuccess";
		await SendLoginDisconnectAsync(session, reason3, ct);
		return (Success: false, Reason: reason3);
	}

	private static async Task WriteToUpstreamAsync(ActiveSession session, byte[] data)
	{
		await session.UpstreamStream.WriteAsync(data, 0, data.Length);
		Interlocked.Add(ref session.UpBytes, data.Length);
		WriteChunk(session, data, data.Length, isUp: true);
	}

	private static void MarkEnterSuccess(ProxyBinding binding)
	{
		try
		{
			if (binding != null && binding.AuthAccountIndex >= 0)
			{
				IReadOnlyList<AccountStore.LocalAccount> all = AccountStore.All;
				if (binding.AuthAccountIndex < all.Count)
				{
					AccountStore.LocalAccount localAccount = all[binding.AuthAccountIndex];
					AccountAutoLoginService.MarkEnterSuccess(localAccount.Channel, localAccount.Account);
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[账号检测] 进服成功标记失败");
		}
	}

	private static byte[] BuildForwardHandshake(byte[] handshake, string targetHost, int targetPort, string label)
	{
		if (TryParseHandshake(handshake, out var host, out var port) && (port != targetPort || !string.Equals(host, targetHost, StringComparison.OrdinalIgnoreCase)))
		{
			byte[] payload = RewriteHandshakeHost(handshake, targetHost, targetPort);
			LogHelper.Log($"[代理抓取] {label} 握手地址改写 {host}:{port} -> {targetHost}:{targetPort}（保留 FML 标识）");
			return BuildPacket(payload);
		}
		return BuildPacket(handshake);
	}

	private static byte[] RewriteHandshakeHost(byte[] packet, string newHost, int newPort)
	{
		int pos = 0;
		int value = NetShared.ReadVarInt(packet, ref pos);
		int value2 = NetShared.ReadVarInt(packet, ref pos);
		string text = NetShared.ReadString(packet, ref pos);
		string text2 = "";
		int num = text.IndexOf('\0');
		if (num >= 0)
		{
			text2 = text.Substring(num);
		}
		byte[] array;
		if (pos + 2 > packet.Length)
		{
			array = Array.Empty<byte>();
		}
		else
		{
			array = new byte[packet.Length - pos - 2];
			Array.Copy(packet, pos + 2, array, 0, array.Length);
		}
		byte[] array2 = NetShared.WriteVarIntBytes(value);
		byte[] array3 = NetShared.WriteVarIntBytes(value2);
		byte[] bytes = Encoding.UTF8.GetBytes(newHost + text2);
		byte[] array4 = new byte[2]
		{
			(byte)((newPort >> 8) & 0xFF),
			(byte)(newPort & 0xFF)
		};
		byte[] array5 = new byte[array2.Length + array3.Length + NetShared.WriteVarIntBytes(bytes.Length).Length + bytes.Length + array4.Length + array.Length];
		int num2 = 0;
		Array.Copy(array2, 0, array5, num2, array2.Length);
		num2 += array2.Length;
		Array.Copy(array3, 0, array5, num2, array3.Length);
		num2 += array3.Length;
		byte[] array6 = NetShared.WriteVarIntBytes(bytes.Length);
		Array.Copy(array6, 0, array5, num2, array6.Length);
		num2 += array6.Length;
		Array.Copy(bytes, 0, array5, num2, bytes.Length);
		num2 += bytes.Length;
		Array.Copy(array4, 0, array5, num2, array4.Length);
		num2 += array4.Length;
		Array.Copy(array, 0, array5, num2, array.Length);
		return array5;
	}

	private static byte[]? RewriteLoginStart(byte[] packet, string loginName)
	{
		try
		{
			int pos = 0;
			int num = NetShared.ReadVarInt(packet, ref pos);
			if (num != 0)
			{
				return null;
			}
			NetShared.ReadString(packet, ref pos);
			byte[] array = ((pos < packet.Length) ? packet[pos..] : Array.Empty<byte>());
			byte[] array2 = NetShared.WriteVarIntBytes(num);
			byte[] bytes = Encoding.UTF8.GetBytes(loginName);
			byte[] array3 = NetShared.WriteVarIntBytes(bytes.Length);
			byte[] array4 = new byte[array2.Length + array3.Length + bytes.Length + array.Length];
			Array.Copy(array2, 0, array4, 0, array2.Length);
			Array.Copy(array3, 0, array4, array2.Length, array3.Length);
			Array.Copy(bytes, 0, array4, array2.Length + array3.Length, bytes.Length);
			Array.Copy(array, 0, array4, array2.Length + array3.Length + bytes.Length, array.Length);
			return array4;
		}
		catch
		{
			return null;
		}
	}

	private static bool TryParseLoginStartIdentity(byte[] packet, out string name, out byte[] uuid)
	{
		name = "";
		uuid = Array.Empty<byte>();
		try
		{
			int pos = 0;
			if (NetShared.ReadVarInt(packet, ref pos) != 0)
			{
				return false;
			}
			name = NetShared.ReadString(packet, ref pos);
			if (pos + 16 <= packet.Length)
			{
				uuid = packet[pos..(pos + 16)];
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryParseHexBytes(string hex, out byte[] bytes)
	{
		bytes = Array.Empty<byte>();
		if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
		{
			return false;
		}
		try
		{
			bytes = Convert.FromHexString(hex);
			return bytes.Length != 0;
		}
		catch
		{
			return false;
		}
	}

	private static byte[] BuildPacket(byte[] payload)
	{
		byte[] array = NetShared.WriteVarIntBytes(payload.Length);
		byte[] array2 = new byte[array.Length + payload.Length];
		Array.Copy(array, 0, array2, 0, array.Length);
		Array.Copy(payload, 0, array2, array.Length, payload.Length);
		return array2;
	}

	private static bool IsLoopbackHost(string host)
	{
		if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (host.StartsWith("[") && host.EndsWith("]"))
		{
			host = host.Trim('[', ']');
		}
		if (string.Equals(host, "::1", StringComparison.Ordinal))
		{
			return true;
		}
		if (IPAddress.TryParse(host, out IPAddress address))
		{
			return IPAddress.IsLoopback(address);
		}
		return false;
	}

	private static bool IsLocalHost(string host)
	{
		if (IsLoopbackHost(host))
		{
			return true;
		}
		if (!IPAddress.TryParse(host, out IPAddress address) || address.AddressFamily != AddressFamily.InterNetwork)
		{
			return false;
		}
		try
		{
			IPAddress[] hostAddresses = Dns.GetHostAddresses(Dns.GetHostName());
			foreach (IPAddress iPAddress in hostAddresses)
			{
				if (iPAddress.AddressFamily == AddressFamily.InterNetwork && iPAddress.Equals(address))
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	/// <summary>一次性调用（登录嗅探 / 状态查询）用：不在高频循环里，临时分配头部缓冲即可。</summary>
	private static Task<byte[]?> ReadPacketAsync(Stream stream, CancellationToken ct)
	{
		return ReadPacketAsync(stream, new byte[8], ct);
	}

	private static async Task<byte[]?> ReadPacketAsync(Stream stream, byte[] header, CancellationToken ct)
	{
		int length = 0;
		bool complete = false;
		for (int i = 0; i < 3; i++)
		{
			if (await stream.ReadAsync(header, 0, 1, ct) != 1)
			{
				return null;
			}
			int num = header[0];
			length |= (num & 0x7F) << 7 * i;
			if ((num & 0x80) == 0)
			{
				complete = true;
				break;
			}
		}
		// 3 字节 VarInt 已能覆盖 1MB 上限；若第 3 字节仍带续位，说明长度前缀异常，直接断开。
		if (!complete || length <= 0 || length > 1048576)
		{
			return null;
		}
		byte[] buffer = new byte[length];
		int num2;
		for (int i = 0; i < length; i += num2)
		{
			num2 = await stream.ReadAsync(buffer, i, length - i, ct);
			if (num2 <= 0)
			{
				return null;
			}
		}
		return buffer;
	}

	private static bool TryParseHandshake(byte[] packet, out string host, out int port)
	{
		int protocolVersion;
		int nextState;
		return TryParseHandshakeEx(packet, out host, out port, out protocolVersion, out nextState);
	}

	private static bool TryParseHandshakeEx(byte[] packet, out string host, out int port, out int protocolVersion, out int nextState)
	{
		host = "";
		port = 0;
		protocolVersion = 0;
		nextState = 0;
		try
		{
			int pos = 0;
			if (NetShared.ReadVarInt(packet, ref pos) != 0)
			{
				return false;
			}
			protocolVersion = NetShared.ReadVarInt(packet, ref pos);
			nextState = NetShared.ReadVarInt(packet, ref pos);
			host = NetShared.ReadString(packet, ref pos);
			int num = host.IndexOf('\0');
			if (num >= 0)
			{
				host = host.Substring(0, num);
			}
			if (pos + 2 > packet.Length)
			{
				return false;
			}
			port = (packet[pos] << 8) | packet[pos + 1];
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static async Task SendLoginDisconnectAsync(ActiveSession session, string reason, CancellationToken ct)
	{
		try
		{
			if (session == null || session.ClientStream == null || session.DownCfb != null)
			{
				return;
			}
			using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(TimeSpan.FromSeconds(5.0));
			string s = "{\"text\":\"" + NetShared.EscapeJson(reason) + "\"}";
			byte[] bytes = Encoding.UTF8.GetBytes(s);
			using (MemoryStream ms = new MemoryStream())
			{
				byte[] array = NetShared.WriteVarIntBytes(0);
				ms.Write(array, 0, array.Length);
				byte[] array2 = NetShared.WriteVarIntBytes(bytes.Length);
				ms.Write(array2, 0, array2.Length);
				ms.Write(bytes, 0, bytes.Length);
				byte[] array3 = ms.ToArray();
				byte[] array4 = NetShared.WriteVarIntBytes(array3.Length);
				using MemoryStream outMs = new MemoryStream();
				outMs.Write(array4, 0, array4.Length);
				outMs.Write(array3, 0, array3.Length);
				byte[] packet = outMs.ToArray();
				await session.ClientStream.WriteAsync(packet, 0, packet.Length, cts.Token);
				Interlocked.Add(ref session.DownBytes, packet.Length);
				WriteChunk(session, packet, packet.Length, isUp: false);
			}
			LogHelper.Log("[代理抓取] " + session.SessionLabel + " 已向客户端发送登录断开原因: " + reason);
		}
		catch
		{
		}
	}

	private static void ReportKickCredentialFailure(ProxyBinding binding, string kickText)
	{
		try
		{
			if (binding == null || binding.AuthAccountIndex < 0 || string.IsNullOrWhiteSpace(kickText))
			{
				return;
			}
			string lowered = kickText.ToLowerInvariant();
			bool flag = false;
			string[] credentialKickPhrases = CredentialKickPhrases;
			foreach (string value in credentialKickPhrases)
			{
				if (lowered.Contains(value))
				{
					flag = true;
					break;
				}
			}
			if (!flag)
			{
				return;
			}
			string[] array = RiskKickPhrases.Where((string p) => lowered.Contains(p)).ToArray();
			if (array.Length != 0)
			{
				LogHelper.Log("[账号检测] 登录期被踢，原因含风控特征（疑似异地/IP 变动，仅提示不删账号）[" + string.Join("/", array) + "]: " + kickText);
				IReadOnlyList<AccountStore.LocalAccount> all2 = AccountStore.All;
				if (binding.AuthAccountIndex < all2.Count)
				{
					AccountStore.LocalAccount localAccount2 = all2[binding.AuthAccountIndex];
					ElsewhereLoginNotice.Mark(localAccount2.Channel, localAccount2.Account, "游戏内被踢，原因含异地/风控特征：" + kickText);
				}
				return;
			}
			IReadOnlyList<AccountStore.LocalAccount> all = AccountStore.All;
			if (binding.AuthAccountIndex < all.Count)
			{
				AccountStore.LocalAccount localAccount = all[binding.AuthAccountIndex];
				LogHelper.Log("[账号检测] 登录期被踢，原因含凭证失效特征（" + localAccount.Nickname + "）: " + kickText);
				AccountInvalidationDetector.ReportInGameSessionFailure(localAccount.Channel, localAccount.Account, localAccount.Nickname, "游戏内登录被踢（凭证失效特征）");
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[账号检测] 踢出原因分析失败");
		}
	}

	private static string ExtractKickText(byte[] body)
	{
		try
		{
			int pos = 0;
			if (NetShared.ReadVarInt(body, ref pos) != 0)
			{
				return "";
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(NetShared.ReadString(body, ref pos));
			StringBuilder stringBuilder = new StringBuilder();
			CollectKickText(jsonDocument.RootElement, stringBuilder);
			return stringBuilder.ToString();
		}
		catch
		{
			return "";
		}
	}

	private static void CollectKickText(JsonElement element, StringBuilder sb)
	{
		if (element.ValueKind == JsonValueKind.String)
		{
			sb.Append(element.GetString());
		}
		else
		{
			if (element.ValueKind == JsonValueKind.Object)
			{
				if (element.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
				{
					sb.Append(value.GetString());
				}
				if (element.TryGetProperty("translate", out var value2) && value2.ValueKind == JsonValueKind.String)
				{
					sb.Append(" ").Append(value2.GetString());
				}
				if (!element.TryGetProperty("extra", out var value3) || value3.ValueKind != JsonValueKind.Array)
				{
					return;
				}
				{
					foreach (JsonElement item in value3.EnumerateArray())
					{
						CollectKickText(item, sb);
					}
					return;
				}
			}
			if (element.ValueKind != JsonValueKind.Array)
			{
				return;
			}
			foreach (JsonElement item2 in element.EnumerateArray())
			{
				CollectKickText(item2, sb);
			}
		}
	}

	private static async Task WatchdogAsync(ActiveSession session, CancellationToken acceptCt)
	{
		try
		{
			long t0Up = session.UpBytes;
			using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(acceptCt);
			cts.CancelAfter(TimeSpan.FromSeconds(10.0));
			for (int i = 0; i < 120; i++)
			{
				if (session.Closed)
				{
					break;
				}
				if (cts.IsCancellationRequested)
				{
					break;
				}
				await Task.Delay(100, cts.Token);
				if (session.UpBytes > t0Up)
				{
					return;
				}
			}
			LogHelper.Log("[代理抓取] " + session.SessionLabel + " 加密握手后 10 秒内未见上游数据流动，会话无进展（网易客户端不会自行回复 263B，加密流程由代理侧完成；此处可能为客户端主动放弃或对端异常关闭）");
		}
		catch
		{
		}
	}

	private static async Task<string> PumpAsync(ActiveSession session, bool isUp)
	{
		string sideName = (isUp ? "客户端→服务器" : "服务器→客户端");
		// 读包头缓冲在泵线程内复用：每包不再 new byte[1] / new byte[3]，多会话时显著减少小对象分配。
		byte[] packetHeader = new byte[8];
		try
		{
			if (isUp && session.DownCfb != null)
			{
				while (!session.Closed)
				{
					byte[] body = await ReadPacketAsync(session.ClientStream, packetHeader, CancellationToken.None);
					if (body == null)
					{
						break;
					}
					byte[] data = BuildPacket(body);
					byte[] enc = session.DownCfb.Encrypt(data);
					await session.UpstreamWriteLock.WaitAsync();
					try
					{
						await session.UpstreamStream.WriteAsync(enc, 0, enc.Length);
					}
					finally
					{
						session.UpstreamWriteLock.Release();
					}
					Interlocked.Add(ref session.UpBytes, enc.Length);
					AddTraffic(session, true, enc.Length, 1);
					SniffServerbound(session, body);
					WriteChunk(session, body, body.Length, isUp: true);
				}
				if (!session.Closed)
				{
					session.Dispose();
					return "客户端流 EOF（客户端先关闭）";
				}
				return "会话已关闭（对侧先结束）";
			}
			if (!isUp && session.UpCfb != null)
			{
				while (!session.Closed)
				{
					byte[] array = await ReadPacketCipheredAsync(session.UpstreamStream, session.UpCfb, packetHeader, CancellationToken.None);
					if (array == null)
					{
						break;
					}
					byte[] enc = BuildPacket(array);
					await session.ClientStream.WriteAsync(enc, 0, enc.Length);
					Interlocked.Add(ref session.DownBytes, enc.Length);
					AddTraffic(session, false, enc.Length, 1);
					SniffClientbound(session, array);
					WriteChunk(session, enc, enc.Length, isUp: false);
				}
				if (!session.Closed)
				{
					MaybeSignalUpstreamSwitch(session, "upstream-eof");
					session.Dispose();
					return "服务器流 EOF（服务器先断开）";
				}
				return "会话已关闭（对侧先结束）";
			}
			Stream from = (isUp ? session.ClientStream : session.UpstreamStream);
			Stream to = (isUp ? session.UpstreamStream : session.ClientStream);
			if (from == null || to == null)
			{
				return "流为空（未初始化）";
			}
			// 纯转发模式的 64KB 缓冲走池：多角色多会话时不再每条线路常驻两块 64KB 私有数组。
			byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
			try
			{
				while (!session.Closed)
				{
					int read = await from.ReadAsync(buffer, 0, 65536);
					if (read <= 0)
					{
						break;
					}
					await to.WriteAsync(buffer, 0, read);
					if (isUp)
					{
						Interlocked.Add(ref session.UpBytes, read);
					}
					else
					{
						Interlocked.Add(ref session.DownBytes, read);
					}
					// 纯转发模式拿不到包边界，这里按读取块粗略计包，B 值仍准确
					AddTraffic(session, isUp, read, 1);
					WriteChunk(session, buffer, read, isUp);
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
			if (!session.Closed)
			{
				if (!isUp)
				{
					MaybeSignalUpstreamSwitch(session, "upstream-eof");
				}
				session.Dispose();
				return sideName + " EOF（纯转发模式对端先关闭）";
			}
			return "会话已关闭（对侧先结束）";
		}
		catch (Exception ex)
		{
			if (session.Closed)
			{
				return "会话已关闭（对侧先结束）";
			}
			LogHelper.LogError(ex, $"[代理抓取] {session.SessionLabel} 泵异常（{sideName}）");
			if (!isUp)
			{
				MaybeSignalUpstreamSwitch(session, "upstream-error");
			}
			session.Dispose();
			return "异常 [" + ex.GetType().Name + "]: " + ex.Message;
		}
	}

	// ===== 机器人代理：游戏内聊天 / 玩家出入 / 画面包嗅探（尽力而为，仅解析 1.12.2 协议） =====
	private const int Protocol1122 = 340;

	private static bool IsSniffableProtocol(ActiveSession session)
	{
		// 网易客户端的握手协议号解析出来是 0，但正文仍然是 1.12.2（340）协议，
		// 不能因为协议号不是 340 就跳过聊天 / 画面嗅探。
		return session.ProtocolVersion == Protocol1122 || session.ProtocolVersion == 0;
	}

	private static void SniffClientbound(ActiveSession session, byte[] body)
	{
		if (session == null || !IsSniffableProtocol(session) || body == null || body.Length < 2)
		{
			return;
		}
		byte[] packet;
		try
		{
			packet = DecodeUpstream(session, body);
		}
		catch
		{
			return;
		}
		if (packet.Length < 1)
		{
			return;
		}
		try
		{
			int pos = 0;
			int pid = NetShared.ReadVarInt(packet, ref pos);
			if (IsVisualPacket(pid))
			{
				AddScreenPackets(session, 1);
			}
			switch (pid)
			{
			case 15:
			{
				string json = NetShared.ReadString(packet, ref pos);
				string text = ExtractChatText(json);
				if (string.IsNullOrWhiteSpace(text))
				{
					return;
				}
				bool joinLeave = LooksPlayerJoinLeave(text);
				LogHelper.Log("[代理聊天] " + session.SessionLabel + " 端口 " + session.ListenPort + (joinLeave ? " 玩家出入: " : " 聊天: ") + Clip(text, 200));
				AddTrafficChat(session, text, joinLeave);
				// 只有明确读到「传送至」+「幸存者」才认为进了子服，其它聊天不再改状态。
				if (text.Contains("传送至", StringComparison.Ordinal)
					&& text.Contains("幸存者", StringComparison.Ordinal))
				{
					SetTrafficStatus(session, "进入子服");
				}
				break;
			}
			case 45:
				if (!session.PlayerListLogged)
				{
					session.PlayerListLogged = true;
					int action = NetShared.ReadVarInt(packet, ref pos);
					int count = NetShared.ReadVarInt(packet, ref pos);
					LogHelper.Log("[代理画面] " + session.SessionLabel + " 端口 " + session.ListenPort + " 已收到玩家列表包（action=" + action + "，人数=" + count + "），画面数据通道正常");
					SetTrafficEvent(session, "玩家列表已同步");
				}
				break;
			case 32:
				if (!session.ChunkLogged)
				{
					session.ChunkLogged = true;
					LogHelper.Log("[代理画面] " + session.SessionLabel + " 端口 " + session.ListenPort + " 已收到首个区块数据包（" + packet.Length + "B），游戏画面开始加载");
					SetTrafficEvent(session, "区块数据到达，画面加载中");
				}
				break;
			case 35:
				if (!session.JoinGameLogged)
				{
					session.JoinGameLogged = true;
					LogHelper.Log("[代理画面] " + session.SessionLabel + " 端口 " + session.ListenPort + " 已收到 Join Game，服务器已下发进入游戏画面");
					SetTrafficEvent(session, "已进入游戏画面");
				}
				SetTrafficStatus(session, "进入服务器");
				break;
			case 26:
			{
				// 1.12.2 游戏内 Disconnect 包：服务器踢人时把原因回显到卡片。
				string json = NetShared.ReadString(packet, ref pos);
				string text = ExtractChatText(json);
				session.SawDisconnectPacket = true;
				SetTrafficStatus(session, "已被服务器踢出");
				SetTrafficError(session, "被服务器踢出: " + (string.IsNullOrWhiteSpace(text) ? "(无文本原因)" : text));
				LogHelper.Log("[代理抓取] " + session.SessionLabel + " 端口 " + session.ListenPort + " 收到游戏内断开包: " + Clip(text, 200));
				break;
			}
			}
		}
		catch
		{
		}
	}

	private static void SniffServerbound(ActiveSession session, byte[] body)
	{
		if (session == null || !IsSniffableProtocol(session) || body == null || body.Length < 3)
		{
			return;
		}
		byte[] packet;
		try
		{
			packet = DecodeUpstream(session, body);
		}
		catch
		{
			return;
		}
		try
		{
			int pos = 0;
			if (NetShared.ReadVarInt(packet, ref pos) != 1)
			{
				return;
			}
			string text = NetShared.ReadString(packet, ref pos);
			if (string.IsNullOrWhiteSpace(text))
			{
				return;
			}
			LogHelper.Log("[代理聊天] " + session.SessionLabel + " 端口 " + session.ListenPort + " 发送: " + Clip(text, 200));
			AddTrafficChat(session, text, false);
		}
		catch
		{
		}
	}

	private static bool IsVisualPacket(int pid)
	{
		switch (pid)
		{
		case 3:
		case 4:
		case 5:
		case 11:
		case 32:
		case 33:
		case 34:
		case 35:
		case 37:
		case 38:
		case 39:
		case 40:
		case 46:
		case 75:
		case 76:
			return true;
		default:
			return false;
		}
	}

	private static string ExtractChatText(string json)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			StringBuilder sb = new StringBuilder();
			CollectKickText(document.RootElement, sb);
			return sb.ToString().Trim();
		}
		catch
		{
			return "";
		}
	}

	private static bool LooksPlayerJoinLeave(string text)
	{
		return text.Contains("加入了游戏", StringComparison.Ordinal)
			|| text.Contains("离开了游戏", StringComparison.Ordinal)
			|| text.Contains("加入游戏", StringComparison.Ordinal)
			|| text.Contains("离开游戏", StringComparison.Ordinal)
			|| text.Contains("上线了", StringComparison.Ordinal)
			|| text.Contains("下线了", StringComparison.Ordinal)
			|| text.Contains("joined the game", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("left the game", StringComparison.OrdinalIgnoreCase);
	}

	private static string Clip(string text, int max)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}
		return (text.Length <= max) ? text : (text.Substring(0, max) + "…");
	}

	private const string SwitchSignalDirName = "MhyCPTD";

	private static readonly object SwitchSignalFileLock = new object();

	private static void MaybeSignalUpstreamSwitch(ActiveSession session, string reason)
	{
		if (session == null || session.Closed)
		{
			return;
		}
		if (session.ListenPort <= 0)
		{
			return;
		}
		ServerPreset preset = session.ProtectedPreset;
		if (preset == null || preset.IpPool == null || preset.IpPool.Length == 0)
		{
			return;
		}
		// 服务端踢人后断开：不是上游故障，绝不能热切换线路。
		if (session.SawDisconnectPacket)
		{
			LogHelper.Log($"[防DDoS] {session.SessionLabel} 本次是服务器踢人后断开（{(DateTime.Now - session.Started).TotalSeconds:F0}s），判定为账号/客户端原因，不写切换信号");
			return;
		}
		// 正常下线判定：上游 EOF 且会话已存活超过 120s → 视为玩家正常退出，不写切换信号（异常路径 upstream-error 不受此限制）
		if (reason == "upstream-eof" && (DateTime.Now - session.Started).TotalSeconds >= 120.0)
		{
			LogHelper.Log($"[防DDoS] {session.SessionLabel} 会话已存活 {(DateTime.Now - session.Started).TotalSeconds:F0}s 后上游正常关闭，判定为正常下线，不写切换信号");
			return;
		}
		string address = "127.0.0.1:" + session.ListenPort;
		string upstream = session.TargetHostUsed + ":" + session.TargetPortUsed;
		SignalUpstreamSwitch(session.ListenPort, address, upstream, reason);
		// 除了给外部工具（MhyCPTD）写信号，程序内部也要立刻做热切换：
		// 否则玩家点「重连」时仍然会去撞刚刚被打挂的那个上游，等于白等一次超时。
		string failHost = session.TargetHostUsed;
		int failPort = session.TargetPortUsed;
		ServerPreset failPreset = session.ProtectedPreset;
		_ = Task.Run(() => FailoverToHealthyUpstreamAsync(failHost, failPort, failPreset));
	}

	/// <summary>记录主目标连续探测成功的轮数，用于「恢复后切回」时抑制抖动。</summary>
	private static readonly ConcurrentDictionary<string, int> UpstreamRecoverStreak = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// 异步版热切换：上游刚被打挂的瞬间，后台健康表可能仍标记为「可达」（探测周期最长 60s），
	/// 此时直接看表会误判为「主域名还活着」而放弃切换。这里先对主域名做一次
	/// 立即探测（复用真实客户端协议的 status ping，非可疑特征），确认真的挂了再切 IP 池。
	/// </summary>
	private static async Task FailoverToHealthyUpstreamAsync(string targetHost, int targetPort, ServerPreset? preset)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(targetHost) || targetPort <= 0)
			{
				return;
			}
			ServerPreset p = preset ?? FindProtectedPreset(targetHost, targetPort);
			if (p == null || p.IpPool == null || p.IpPool.Length == 0)
			{
				return;
			}
			string key = targetHost + ":" + targetPort;
			if (!UpstreamHealth.TryGetValue(key, out ProxyUpstreamHealth health))
			{
				return;
			}
			if (health.Reachable)
			{
				// 表上还是「可达」，但玩家会话刚刚在上游挂掉——先立即复测一次再下结论。
				if (LastClientProtocol > 0)
				{
					await ProbeUpstreamAsync(health, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
				}
				if (health.Reachable)
				{
					LogHelper.Log($"[防DDoS] {key} 会话异常断开但复测仍可达，暂不切换（可能只是单连接抖动）");
					return;
				}
				LogHelper.Force($"[防DDoS] {key} 复测确认不可达（健康表此前滞后），立即执行热切换");
			}
			string pick = "";
			foreach (string ip in OrderIpPoolByHealth(targetHost, targetPort, p.IpPool))
			{
				if (!string.IsNullOrWhiteSpace(ip) && health.IpReachable.TryGetValue(ip, out bool ok) && ok)
				{
					pick = ip;
					break;
				}
			}
			if (string.IsNullOrWhiteSpace(pick))
			{
				// IP 池健康缓存全空/全挂：现测一遍池内 IP，拿一个真可达的
				foreach (string ip in OrderIpPoolByHealth(targetHost, targetPort, p.IpPool))
				{
					if (string.IsNullOrWhiteSpace(ip))
					{
						continue;
					}
					bool ok = await ProbeHostReachableAsync(ip, targetPort).ConfigureAwait(continueOnCapturedContext: false);
					health.IpReachable[ip] = ok;
					if (ok)
					{
						pick = ip;
						break;
					}
				}
				if (string.IsNullOrWhiteSpace(pick))
				{
					LogHelper.Force($"[防DDoS] {key} 主上游不可达且 IP 池内无可用地址，保持原线路等待恢复");
					return;
				}
			}
			RedirectBindingsToUpstream(targetHost, targetPort, pick, p.Name);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[防DDoS] 上游热切换失败");
		}
	}

	/// <summary>对单个候选 IP 做一次快速可达性探测（TCP 3s 超时；已有客户端协议时用真实 status ping）。</summary>
	private static async Task<bool> ProbeHostReachableAsync(string ip, int port)
	{
		try
		{
			if (LastClientProtocol <= 0)
			{
				// 尚无客户端协议信息：退化为纯 TCP 连通性测试，不做 status ping（避免暴露代理特征）
				using System.Net.Sockets.TcpClient tcp = new System.Net.Sockets.TcpClient();
				Task connect = tcp.ConnectAsync(ip, port);
				Task done = await Task.WhenAny(connect, Task.Delay(3000)).ConfigureAwait(continueOnCapturedContext: false);
				return done == connect && tcp.Connected;
			}
			// 已知客户端协议：走统一的 status-ping 探测路径（与正常客户端特征一致）
			ProxyUpstreamHealth temp = new ProxyUpstreamHealth { Host = ip, Port = port };
			await ProbeUpstreamAsync(temp, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
			return temp.Reachable;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>把指向 fromHost:fromPort 的绑定（以及全局默认目标）改写到 toHost，并清理恢复计数。</summary>
	private static void RedirectBindingsToUpstream(string fromHost, int fromPort, string toHost, string serverName)
	{
		if (string.Equals(fromHost, toHost, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		int changed = 0;
		lock (Sync)
		{
			foreach (ListenerEntry le in Listeners)
			{
				ProxyBinding b = le.Binding;
				if (b == null)
				{
					continue;
				}
				if (string.Equals(b.TargetHost ?? "", fromHost, StringComparison.OrdinalIgnoreCase) && b.TargetPort == fromPort)
				{
					b.TargetHost = toHost;
					changed++;
				}
			}
		}
		if (string.Equals(TargetHost ?? "", fromHost, StringComparison.OrdinalIgnoreCase) && TargetPort == fromPort)
		{
			TargetHost = toHost;
		}
		UpstreamRecoverStreak[fromHost + ":" + fromPort] = 0;
		LogHelper.Force($"[防DDoS] 上游 {fromHost} 当前不可用，已热切换 {serverName} 至 {toHost}:{fromPort}，影响 {changed} 个监听；正在游戏内的会话保持不动，玩家重连即走新线路");
	}

	internal static void SignalUpstreamSwitch(int listenPort, string address, string upstream, string reason)
	{
		try
		{
			if (listenPort <= 0)
			{
				return;
			}
			string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), SwitchSignalDirName);
			Directory.CreateDirectory(dir);
			string file = Path.Combine(dir, "switch.json");
			lock (SwitchSignalFileLock)
			{
				List<(int Port, string Address, string Upstream, string Reason, long Time)> entries = new List<(int, string, string, string, long)>();
				if (File.Exists(file))
				{
					try
					{
						using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
						if (doc.RootElement.TryGetProperty("switches", out JsonElement switches) && switches.ValueKind == JsonValueKind.Array)
						{
							foreach (JsonElement item in switches.EnumerateArray())
							{
								int existPort = ((item.TryGetProperty("port", out JsonElement pe) && pe.ValueKind == JsonValueKind.Number) ? pe.GetInt32() : 0);
								if (existPort <= 0 || existPort == listenPort)
								{
									continue;
								}
								string existAddress = (item.TryGetProperty("address", out JsonElement ae) ? (ae.GetString() ?? "") : "");
								string existUpstream = (item.TryGetProperty("upstream", out JsonElement ue) ? (ue.GetString() ?? "") : "");
								string existReason = (item.TryGetProperty("reason", out JsonElement re) ? (re.GetString() ?? "") : "");
								long existTime = ((item.TryGetProperty("time", out JsonElement te) && te.ValueKind == JsonValueKind.Number) ? te.GetInt64() : 0L);
								// 淘汰超过 24h 的旧条目，防止 switch.json 无限增长
								if (existTime > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - existTime > 86400000L)
								{
									continue;
								}
								entries.Add((existPort, existAddress, existUpstream, existReason, existTime));
							}
						}
					}
					catch
					{
					}
				}
				entries.Add((listenPort, address ?? "", upstream ?? "", reason ?? "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
				StringBuilder sb = new StringBuilder();
				sb.Append("{\n");
				sb.Append("  \"version\": 1,\n");
				sb.Append("  \"switches\": [\n");
				for (int i = 0; i < entries.Count; i++)
				{
					var entry = entries[i];
					sb.Append("    {\"port\": ").Append(entry.Port)
						.Append(", \"address\": \"").Append(NetShared.EscapeJson(entry.Address))
						.Append("\", \"upstream\": \"").Append(NetShared.EscapeJson(entry.Upstream))
						.Append("\", \"reason\": \"").Append(NetShared.EscapeJson(entry.Reason))
						.Append("\", \"time\": ").Append(entry.Time)
						.Append('}');
					if (i + 1 < entries.Count)
					{
						sb.Append(',');
					}
					sb.Append('\n');
				}
				sb.Append("  ]\n");
				sb.Append("}\n");
				string tmp = file + ".tmp";
				File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
				File.Move(tmp, file, overwrite: true);
				LogHelper.Force($"[防DDoS] 写入热切换信号: port={listenPort} upstream={upstream} reason={reason}");
			}
			LogHelper.Log($"[防DDoS] 已写出上游会话切换信号: {address} (upstream={upstream}, reason={reason})");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[防DDoS] 写上游会话切换信号失败");
		}
	}

	/// <summary>一次性调用用：临时分配头部缓冲。</summary>
	private static Task<byte[]?> ReadPacketCipheredAsync(Stream stream, Cfb8Runner cfb, CancellationToken ct)
	{
		return ReadPacketCipheredAsync(stream, cfb, new byte[8], ct);
	}

	private static async Task<byte[]?> ReadPacketCipheredAsync(Stream stream, Cfb8Runner cfb, byte[] header, CancellationToken ct)
	{
		int rawCount = 0;
		int length = 0;
		bool complete = false;
		for (int i = 0; i < 3; i++)
		{
			// header[0..2] 存加密原始字节（仅错误日志用），header[3] 作为单字节读缓冲复用。
			if (await stream.ReadAsync(header, 3, 1, ct) != 1)
			{
				LogHelper.Log($"[加密流] 长度前缀读取中断（第 {i} 字节即流结束，已读原始 {HexShort(header.AsSpan(0, rawCount).ToArray())}）");
				return null;
			}
			header[rawCount++] = header[3];
			int num = cfb.DecryptByte(header[3]);
			length |= (num & 0x7F) << 7 * i;
			if ((num & 0x80) == 0)
			{
				complete = true;
				break;
			}
		}
		if (!complete)
		{
			LogHelper.Log($"[加密流] 长度前缀超过 3 字节（原始 {HexShort(header.AsSpan(0, rawCount).ToArray())}）");
			return null;
		}
		if (length <= 0 || length > 1048576)
		{
			LogHelper.Log($"[加密流] 非法长度 {length}（原始前 {rawCount}B: {HexShort(header.AsSpan(0, rawCount).ToArray())}）");
			return null;
		}
		byte[] ciphertext = new byte[length];
		int num2;
		for (int i = 0; i < length; i += num2)
		{
			num2 = await stream.ReadAsync(ciphertext, i, length - i, ct);
			if (num2 <= 0)
			{
				LogHelper.Log($"[加密流] 包体读取中断（长度={length} 已读={i}B，原始前 {HexShort(header.AsSpan(0, rawCount).ToArray())}）");
				return null;
			}
		}
		return cfb.Decrypt(ciphertext);
	}

	private static string HexShort(byte[] data, int max = 48)
	{
		int num = Math.Min(max, data.Length);
		StringBuilder stringBuilder = new StringBuilder(num * 3);
		for (int i = 0; i < num; i++)
		{
			stringBuilder.Append(data[i].ToString("X2")).Append(' ');
		}
		return stringBuilder.ToString().TrimEnd();
	}

	private static byte[] DecodeUpstream(ActiveSession session, byte[] payload)
	{
		if (session.CompressionThreshold < 0)
		{
			return payload;
		}
		int pos = 0;
		if (NetShared.ReadVarInt(payload, ref pos) == 0)
		{
			if (payload.Length <= pos)
			{
				return Array.Empty<byte>();
			}
			return payload[pos..];
		}
		return ZlibDecompress(payload.AsSpan(pos).ToArray());
	}

	private static byte[] ZlibDecompress(byte[] data)
	{
		using MemoryStream stream = new MemoryStream(data);
		using ZLibStream zLibStream = new ZLibStream(stream, CompressionMode.Decompress);
		using MemoryStream memoryStream = new MemoryStream();
		// 80KB 走池：开压缩后每个下行包都会解压，多会话时这里曾是最大的 LOH 分配源。
		byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
		try
		{
			int total = 0;
			int read;
			while ((read = zLibStream.Read(buffer, 0, 81920)) > 0)
			{
				total += read;
				if (total > MaxDecompressedPacketBytes)
				{
					throw new InvalidDataException("压缩包解压后超过安全上限 " + MaxDecompressedPacketBytes + "B（疑似 zlib 炸弹）");
				}
				memoryStream.Write(buffer, 0, read);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
		return memoryStream.ToArray();
	}



	private static int PeekPacketId(byte[] body)
	{
		return ReadBodyFirstVarInt(body);
	}

	private static int ReadBodyFirstVarInt(byte[] body)
	{
		try
		{
			int pos = 0;
			return NetShared.ReadVarInt(body, ref pos);
		}
		catch
		{
			return -1;
		}
	}

	private static void WriteChunk(ActiveSession session, byte[] data, int count, bool isUp)
	{
		try
		{
			if (!CaptureLogEnabled || session.LogStream == null)
			{
				return;
			}
			lock (session.LogLock)
			{
				if (!CaptureLogEnabled || session.LogStream == null)
				{
					return;
				}
				FileStream logStream = session.LogStream;
				string value = (isUp ? "C-->S" : "S-->C");
				long value2 = (isUp ? session.UpBytes : session.DownBytes);
				string text = $"[{DateTime.Now:HH:mm:ss.fff}] {value} {count}B (累计 {value2}B)";
				text = ((count <= MaxDumpBytes) ? (text + Environment.NewLine + HexDump(data, count)) : (text + Environment.NewLine + HexDump(data, MaxDumpBytes) + $"  ... (共 {count}B，前 {MaxDumpBytes}B)"));
				text += Environment.NewLine;
				logStream.Write(Encoding.UTF8.GetBytes(text));
				Interlocked.Add(ref session.BytesLogged, count);
			}
		}
		catch
		{
		}
	}

	private static SessionInfo ToInfo(ActiveSession session, string status)
	{
		string client = "?";
		try
		{
			client = session.Client?.Client.RemoteEndPoint?.ToString() ?? "?";
		}
		catch
		{
		}
		return new SessionInfo
		{
			Id = session.Id,
			Client = client,
			Target = $"{session.TargetHostUsed}:{session.TargetPortUsed}",
			LogPath = (session.LogStream?.Name ?? ""),
			Status = status,
			Started = session.Started,
			Ended = ((status == "已连接") ? ((DateTime?)null) : new DateTime?(DateTime.Now)),
			UpBytes = session.UpBytes,
			DownBytes = session.DownBytes,
			BytesLogged = session.BytesLogged
		};
	}

	private static string HexDump(byte[] data, int count)
	{
		StringBuilder stringBuilder = new StringBuilder();
		int num = Math.Min(count, data.Length);
		for (int i = 0; i < num; i += 16)
		{
			stringBuilder.Append("  ");
			for (int j = 0; j < 16; j++)
			{
				if (i + j < num)
				{
					stringBuilder.Append(data[i + j].ToString("X2")).Append(' ');
				}
				else
				{
					stringBuilder.Append("   ");
				}
			}
			stringBuilder.Append("  |");
			for (int k = 0; k < 16 && i + k < num; k++)
			{
				byte b = data[i + k];
				stringBuilder.Append((char)((b >= 32 && b < 127) ? b : 46));
			}
			stringBuilder.Append('|').AppendLine();
		}
		return stringBuilder.ToString();
	}

	private sealed class ProxyUpstreamHealth
	{
		public string Host = "";

		public int Port;

		public string ServerName = "";

		public volatile bool Reachable;

		public string StatusJson = "";

		public string LastError = "";

		public DateTime LastOkUtc;

		public int LatencyMs;

		public readonly ConcurrentDictionary<string, bool> IpReachable = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
	}

	/// <summary>握手目标是否已由本地配置（绑定/全局/服务器预设）明确指定；用于局域网模式的目标白名单。</summary>
	private static bool IsConfiguredTarget(string host, int port, ProxyBinding? binding)
	{
		if (string.IsNullOrWhiteSpace(host) || port <= 0)
		{
			return false;
		}
		if (binding != null && !string.IsNullOrWhiteSpace(binding.TargetHost) && string.Equals(binding.TargetHost, host, StringComparison.OrdinalIgnoreCase) && binding.TargetPort == port)
		{
			return true;
		}
		if (!string.IsNullOrWhiteSpace(TargetHost) && string.Equals(TargetHost, host, StringComparison.OrdinalIgnoreCase) && TargetPort == port)
		{
			return true;
		}
		return FindProtectedPreset(host, port) != null;
	}

	private static ServerPreset FindProtectedPreset(string host, int port)
	{
		if (string.IsNullOrWhiteSpace(host))
		{
			return null;
		}
		foreach (ServerPreset preset in ServerPresets.All)
		{
			if (string.Equals(preset.Host, host, StringComparison.OrdinalIgnoreCase))
			{
				return preset;
			}
			if (preset.IpPool != null)
			{
				foreach (string ip in preset.IpPool)
				{
					if (string.Equals(ip, host, StringComparison.Ordinal))
					{
						return preset;
					}
				}
			}
		}
		return null;
	}

	private static bool IsProtectedTarget(string host, int port)
	{
		return FindProtectedPreset(host, port) != null;
	}

	private static void StartHealthMonitor(IEnumerable<KeyValuePair<string, int>> targets)
	{
		lock (HealthMonitorSync)
		{
			List<KeyValuePair<string, int>> list = targets.Distinct().Where((KeyValuePair<string, int> t) => IsProtectedTarget(t.Key, t.Value)).ToList();
			if (list.Count == 0)
			{
				return;
			}
			foreach (KeyValuePair<string, int> item in list)
			{
				ServerPreset preset = FindProtectedPreset(item.Key, item.Value);
				string key = item.Key + ":" + item.Value;
				UpstreamHealth.GetOrAdd(key, (string _) => new ProxyUpstreamHealth
				{
					Host = item.Key,
					Port = item.Value,
					ServerName = ((preset != null) ? preset.Name : item.Key)
				});
			}
			// 注意：此处仅注册监控目标，不启动探测循环。
			// 探测循环延迟到首个登录会话（EnsureHealthMonitorStarted）才启动，
			// 避免程序一开就对网易服务器持续发 status ping 暴露代理特征触发风控。
		}
	}

	private static void EnsureHealthMonitorStarted()
	{
		// 正常代理登录模式不启动健康探测：周期性 status ping 是网易侧易识别的代理特征。
		if (IsNormalMode)
		{
			return;
		}
		lock (HealthMonitorSync)
		{
			if (UpstreamHealth.Count == 0 || (HealthMonitorTask != null && !HealthMonitorTask.IsCompleted))
			{
				return;
			}
			HealthMonitorCts?.Dispose();
			HealthMonitorCts = new CancellationTokenSource();
			HealthMonitorTask = Task.Run(() => HealthMonitorLoopAsync(HealthMonitorCts.Token));
			LogHelper.Log("[防DDoS] 健康检查已启动（首个登录会话触发，探测协议=真实客户端协议）");
		}
	}

	private static void StopHealthMonitor()
	{
		lock (HealthMonitorSync)
		{
			StopHealthMonitorLocked();
		}
	}

	private static void StopHealthMonitorLocked()
	{
		try
		{
			HealthMonitorCts?.Cancel();
		}
		catch
		{
		}
		HealthMonitorCts = null;
		HealthMonitorTask = null;
	}

	private static async Task HealthMonitorLoopAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			foreach (ProxyUpstreamHealth st in UpstreamHealth.Values)
			{
				if (ct.IsCancellationRequested)
				{
					return;
				}
				try
				{
					await ProbeUpstreamAsync(st, ct).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch
				{
				}
			}
			// 探测频率按活动会话动态化：有玩家在玩 60s 一轮（保持热切换灵敏度），
			// 无会话 300s 一轮（低频保活，减少服务器侧异常 ping 特征）。
			bool hasActive;
			lock (Sync)
			{
				hasActive = Active.Count > 0;
			}
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(hasActive ? 60.0 : 300.0), ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	private static async Task ProbeUpstreamAsync(ProxyUpstreamHealth st, CancellationToken ct)
	{
		// 尚未见过任何真实客户端会话时跳过探测：
		// 固定 340（1.12.2 老协议）的 status ping 是服务器侧极易识别的代理特征（疑似风控诱因之一），
		// 首个登录会话握手的真实协议版本会写入 LastClientProtocol，之后探测与正常客户端特征一致。
		int probeProtocol = LastClientProtocol;
		if (probeProtocol <= 0)
		{
			return;
		}
		ServerPreset preset = FindProtectedPreset(st.Host, st.Port);
		List<string> list = new List<string> { st.Host };
		if (preset != null && preset.IpPool != null)
		{
			foreach (string ip in preset.IpPool)
			{
				if (!string.IsNullOrWhiteSpace(ip))
				{
					list.Add(ip);
				}
			}
		}
		string statusJson = null;
		string error = null;
		int latency = 0;
		foreach (string candidate in list)
		{
			if (ct.IsCancellationRequested)
			{
				return;
			}
			using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(TimeSpan.FromSeconds(5.0));
			DateTime startUtc = DateTime.UtcNow;
			try
			{
				statusJson = await QueryStatusJsonAsync(candidate, st.Port, probeProtocol, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
				latency = (int)(DateTime.UtcNow - startUtc).TotalMilliseconds;
				st.IpReachable[candidate] = true;
				break;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				st.IpReachable[candidate] = false;
			}
		}
		if (!string.IsNullOrEmpty(statusJson))
		{
			bool wasDown = !st.Reachable;
			st.StatusJson = statusJson;
			st.Reachable = true;
			st.LastOkUtc = DateTime.UtcNow;
			st.LatencyMs = latency;
			st.LastError = "";
			LogHelper.Log($"[防DDoS] 上游 {st.Host}:{st.Port} 在线（{latency}ms）{(wasDown ? "（已恢复）" : "")}");
		}
		else
		{
			bool wasUp = st.Reachable;
			st.Reachable = false;
			st.LastError = error ?? "无响应";
			LogHelper.Log($"[防DDoS] 上游 {st.Host}:{st.Port} 探测失败: {st.LastError}{(wasUp ? "（刚掉线）" : "")}");
		}
	}

	private static async Task<string> QueryStatusJsonAsync(string host, int port, int protocolVersion, CancellationToken ct)
	{
		using TcpClient tcpClient = new TcpClient
		{
			NoDelay = true
		};
		await tcpClient.ConnectAsync(host, port, ct).ConfigureAwait(continueOnCapturedContext: false);
		Stream stream = tcpClient.GetStream();
		using (MemoryStream memoryStream = new MemoryStream())
		{
			NetShared.WriteVarInt(memoryStream, 0);
			NetShared.WriteVarInt(memoryStream, protocolVersion);
			NetShared.WriteString(memoryStream, host);
			memoryStream.WriteByte((byte)((port >> 8) & 0xFF));
			memoryStream.WriteByte((byte)(port & 0xFF));
			NetShared.WriteVarInt(memoryStream, 1);
			byte[] array = BuildPacket(memoryStream.ToArray());
			await stream.WriteAsync(array, 0, array.Length, ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		byte[] array2 = BuildPacket(new byte[1]);
		await stream.WriteAsync(array2, 0, array2.Length, ct).ConfigureAwait(continueOnCapturedContext: false);
		byte[] array3 = await ReadPacketAsync(stream, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (array3 == null)
		{
			throw new IOException("上游未返回状态响应");
		}
		int pos = 0;
		NetShared.ReadVarInt(array3, ref pos);
		return NetShared.ReadString(array3, ref pos);
	}

	private static async Task AnswerStatusLocallyAsync(TcpClient client, byte[] handshake, string targetHost, int targetPort, string label, CancellationToken ct)
	{
		ServerPreset preset = FindProtectedPreset(targetHost, targetPort);
		string serverName = ((preset != null && !string.IsNullOrWhiteSpace(preset.Name)) ? preset.Name : targetHost);
		TryParseHandshakeEx(handshake, out _, out _, out int protocolVersion, out _);
		ProxyUpstreamHealth health;
		UpstreamHealth.TryGetValue(targetHost + ":" + targetPort, out health);
		string json = BuildLocalStatusJson(serverName, protocolVersion, health);
		try
		{
			Stream stream = client.GetStream();
			using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(TimeSpan.FromSeconds(15.0));
			await ReadPacketAsync(stream, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
			using (MemoryStream memoryStream = new MemoryStream())
			{
				NetShared.WriteVarInt(memoryStream, 0);
				NetShared.WriteString(memoryStream, json);
				byte[] array = BuildPacket(memoryStream.ToArray());
				await stream.WriteAsync(array, 0, array.Length, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			byte[] array2 = await ReadPacketAsync(stream, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
			if (array2 != null && array2.Length >= 9)
			{
				int pos = 0;
				if (NetShared.ReadVarInt(array2, ref pos) == 1 && pos + 8 <= array2.Length)
				{
					using MemoryStream memoryStream2 = new MemoryStream();
					NetShared.WriteVarInt(memoryStream2, 1);
					memoryStream2.Write(array2, pos, 8);
					byte[] array3 = BuildPacket(memoryStream2.ToArray());
					await stream.WriteAsync(array3, 0, array3.Length, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			string state = ((health == null) ? "在线（未探测）" : (health.Reachable ? "在线" : ("离线，已用缓存状态保持在线: " + health.LastError)));
			LogHelper.Log($"[防DDoS] {label} 拦截服务器列表请求，本地回应 {serverName}（上游 {targetHost}:{targetPort} {state}）");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[防DDoS] " + label + " 本地状态回应失败");
		}
	}

	private static string BuildLocalStatusJson(string serverName, int clientProtocol, ProxyUpstreamHealth health)
	{
		int protocolVersion = ((clientProtocol > 0) ? clientProtocol : 340);
		string versionName = "1.12.2";
		int online = 1;
		int max = 100;
		if (health != null && !string.IsNullOrEmpty(health.StatusJson))
		{
			try
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(health.StatusJson);
				JsonElement rootElement = jsonDocument.RootElement;
				if (rootElement.TryGetProperty("players", out var value))
				{
					if (value.TryGetProperty("online", out var value2) && value2.TryGetInt32(out var value3))
					{
						online = value3;
					}
					if (value.TryGetProperty("max", out var value4) && value4.TryGetInt32(out var value5))
					{
						max = value5;
					}
				}
				if (rootElement.TryGetProperty("version", out var value6) && value6.TryGetProperty("name", out var value7) && value7.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value7.GetString()))
				{
					versionName = value7.GetString();
				}
			}
			catch
			{
			}
		}
		return "{\"version\":{\"name\":\"" + NetShared.EscapeJson(versionName) + "\",\"protocol\":" + protocolVersion + "},\"players\":{\"max\":" + max + ",\"online\":" + online + "},\"description\":{\"text\":\"" + NetShared.EscapeJson(serverName + "（防护在线）") + "\"}}";
	}

	private static readonly ConcurrentDictionary<string, (IPAddress[] Addresses, DateTime Expires)> DnsCache = new ConcurrentDictionary<string, (IPAddress[], DateTime)>(StringComparer.OrdinalIgnoreCase);

	private static async Task<IPAddress[]> ResolveHostAsync(string host, CancellationToken ct)
	{
		if (DnsCache.TryGetValue(host, out var entry) && entry.Expires > DateTime.UtcNow)
		{
			return entry.Addresses;
		}
		IPAddress[] addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(continueOnCapturedContext: false);
		DnsCache[host] = (addresses, DateTime.UtcNow.AddMinutes(5.0));
		return addresses;
	}
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Services.BjdProxy.Native;

namespace MhyunBox.Services.BjdProxy;

/// <summary>
/// 布吉岛专用 A/B 会话共享代理的服务层。
///
/// 能力说明（与通用代理的区别，务必分清）：
/// - 通用代理 <see cref="ProxyCaptureService"/>：每个客户端各自建一条到 127.0.0.1 的隧道，
///   由本程序拦 EncryptionRequest、用账号会话重新走网易云认证。**需要给每个客户端准备账号**。
/// - 本服务：把现成 payload 反射注入 A 客户端 JVM，payload 挂住 A 已登录的远端连接、
///   把世界快照推给 B，B 只要连本机端口就能共用 A 的身份。**不需要第二个账号**。
///
/// 生命周期：payload 常驻 A 进程，**无法远程卸载**。
///
/// ⚠ 最重要的约束（2026-11-19 实测确认）：
/// payload 有两条路径，取决于「注入时 A 是否已经进入服务器世界」：
///   - A 已入服 → mid-session 捕获 → **不阻塞**，正常共享；
///   - A 未入服 → 把 A 的 Render 线程挂进 <c>BServer_WaitForBConnected(-1)</c>，
///     **无限期阻塞**（画面卡死），且该等待没有超时、也没有 stop 唤醒，
///     连按 DEL 都解不开 —— 只能让 B 连上代理端口，或结束游戏进程。
/// 因此 <see cref="StartAsync"/> 在注入前用 <see cref="BjdConnectionProbe"/> 硬性拦截；
/// 注入后还会读 payload 日志识别是否已经挂起，并把警告回传给 UI。
/// </summary>
public static class BjdProxyService
{
	private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

	private static volatile bool _active;
	private static volatile int _port;
	private static volatile int _pid;
	private static volatile string _lastMessage = "";
	private static volatile bool _gameFrozen;
	/// <summary>本进程内已注入过的目标 pid（同一文档的 payload 不支持重复注入）。</summary>
	private static volatile int _injectedPid;
	/// <summary>本次注入是否走了 mid-session 捕获（A 注入时已在游戏内）。</summary>
	private static volatile bool _capturedLive;

	/// <summary>状态变化（注入成功 / 端口消失 / 挂起状态变化）。</summary>
	public static event Action? StateChanged;

	/// <summary>代理是否已在运行（以端口实测为准，不以注入返回值为准）。</summary>
	public static bool IsActive => _active;

	/// <summary>当前代理端口（B 客户端连这个）。</summary>
	public static int ActivePort => _port;

	/// <summary>已注入的 A 客户端 pid。</summary>
	public static int ActivePid => _pid;

	/// <summary>最近一次结果说明（UI 状态行用）。</summary>
	public static string LastMessage => _lastMessage;

	/// <summary>
	/// 最近一次注入是否把 A 的 Render 线程挂起了（payload 走了预连接等待）。
	/// true 表示游戏画面是卡住的，只能靠 B 连上代理端口来恢复。
	/// </summary>
	public static bool IsGameFrozen => _gameFrozen;

	/// <summary>B 是否已经连上 13337（payload 日志出现 B 侧 channelActive / B reached PLAY）。</summary>
	public static bool BConnected
	{
		get
		{
			if (!_active)
			{
				return false;
			}
			return BjdPayloadLog.CountSinceBaseline("login: B in PLAY") > 0
				|| BjdPayloadLog.CountSinceBaseline("[MAIN-GATE] B reached PLAY") > 0;
		}
	}

	/// <summary>本次注入后 A 重新建连的次数（>0 表示 A 重连过，payload 会在新连接上重挂 Relay）。</summary>
	public static int AConnectionCount => _active ? BjdPayloadLog.CountSinceBaseline("Native_onChannelActive fired") : 0;

	/// <summary>本次注入是否走了 mid-session 捕获。</summary>
	public static bool CapturedLiveSession => _capturedLive;

	/// <summary>停用提示（payload 不能远程卸载；且挂起后 DEL 也无效）。</summary>
	public const string StopHint =
		"payload 常驻在 A 客户端进程内，程序无法远程卸载。"
		+ "正常共享（mid-session 捕获）时，把 A 客户端窗口切到前台、松开再按一次 DEL 可停用；"
		+ "但若 payload 已把 Render 线程挂起（画面卡死），按 DEL 不会解除，只能让 B 连上代理端口或结束游戏进程。";

	/// <summary>
	/// 门禁：只有选中布吉岛预设（GameId 4661334467366178884）时才允许使用。
	/// UI 可见性、按钮点击、注入编排三处都调这里，禁止绕开。
	/// </summary>
	public static bool CanUse(ServerPreset? preset, out string reason)
	{
		if (!BjdGate.IsBjd(preset))
		{
			reason = "「布吉岛 A/B 会话共享代理」只对布吉岛服务器开放（GameId "
				+ BjdGate.GameId + "），当前选择的是「" + (preset?.Name ?? "未选择服务器") + "」。";
			return false;
		}
		reason = "";
		return true;
	}

	/// <summary>
	/// 注入并等待代理端口就绪。
	/// <paramref name="forceInject"/> = true 时跳过「A 是否已入服」的检查（用户显式勾选确认后）。
	/// 返回 (Ok, Message)；失败时 Message 已包含可读原因，可直接展示给用户。
	/// </summary>
	public static async Task<(bool Ok, string Message)> StartAsync(
		ServerPreset? preset, bool forceInject = false, bool reInject = false, Action<string>? log = null, CancellationToken ct = default(CancellationToken))
	{
		await _gate.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			// 第 1 道门禁（调用方可能从任意入口进来）
			if (!CanUse(preset, out string gateReason))
			{
				LogHelper.Log("[布吉岛代理] 拒绝启动：" + gateReason);
				return (false, gateReason);
			}

			// 已在运行：默认直接回报；reInject=true 时才真的再注入一次（payload 用命名事件
			// 识别重复加载并请求"恢复"，适合 DEL 停用后唤醒，或想重新挂 Hook 时用）。
			int existing = ProbeExistingPort();
			if (existing > 0 && !reInject)
			{
				MarkActive(_pid, existing, _gameFrozen);
				string already = "布吉岛代理已在运行：127.0.0.1:" + existing
					+ (_gameFrozen ? "（⚠ 游戏主线程处于挂起等待，B 连上后自动恢复）" : "（直接让第二个客户端连这个地址即可）")
					+ "。若要重新挂载，请点「重新注入（唤醒/恢复）」。";
				LogHelper.Force("[布吉岛代理] " + already);
				return (true, already);
			}
			if (existing > 0)
			{
				LogHelper.Force("[布吉岛代理] 代理已在运行（端口 " + existing + "），按用户要求重新注入以唤醒/恢复");
				log?.Invoke("已在运行，重新注入以唤醒/恢复…");
			}

			string? payloadPath = BjdInjector.ResolvePayloadPath(out string payloadDetail);
			if (payloadPath == null)
			{
				LogHelper.Log("[布吉岛代理] " + payloadDetail);
				return (false, payloadDetail);
			}
			LogHelper.Log("[布吉岛代理] " + payloadDetail);
			log?.Invoke(payloadDetail);

			// 定位 A 客户端（第二道门禁：窗口标题 + java 进程）
			(ProcessCandidate? target, string locateDetail) = await BjdProcessLocator
				.WaitAsync(TimeSpan.FromSeconds(180), log, ct)
				.ConfigureAwait(continueOnCapturedContext: false);
			if (target == null)
			{
				LogHelper.Log("[布吉岛代理] " + locateDetail + "；当前候选：" + BjdProcessLocator.Describe());
				return (false, locateDetail);
			}
			if (!BjdProcessLocator.MatchesBjdWindow(target))
			{
				string mismatch = "目标进程复核未通过（不是布吉岛 java 客户端），已取消注入：" + locateDetail;
				LogHelper.Log("[布吉岛代理] " + mismatch);
				return (false, mismatch);
			}
			LogHelper.Log("[布吉岛代理] 目标客户端：" + locateDetail);
			log?.Invoke("目标客户端：" + locateDetail);

			int pid = target.ProcessId;

			// 第 3 道门禁（**实测新增**）：A 必须已经进入服务器，否则 payload 会挂起 Render 线程。
			bool inGame = BjdConnectionProbe.IsLikelyInGame(pid, out string inGameDetail);
			LogHelper.Log("[布吉岛代理] 入服检查：" + (inGame ? "通过 — " : "未通过 — ") + inGameDetail);
			if (!inGame && !forceInject)
			{
				string blocked = "已拒绝注入：没有检测到 A 客户端已进入服务器。"
					+ inGameDetail
					+ " 此时注入会让 payload 在游戏 Render 线程上无限期等待 B（画面卡死），"
					+ "而且该等待没有超时，按 DEL 也解不开，只能结束游戏进程。"
					+ "请先用 A 进入布吉岛服务器、看到游戏世界后再点注入；"
					+ "若你确认已在游戏世界内（例如检测不到代理链路），请勾选「强制注入」再试。";
				LogHelper.Force("[布吉岛代理] " + blocked);
				return (false, blocked);
			}
			if (!inGame)
			{
				LogHelper.Force("[布吉岛代理] 用户选择强制注入（入服检查未通过）：" + inGameDetail);
				log?.Invoke("⚠ 入服检查未通过，按用户要求强制注入 —— 若画面卡住，让 B 连代理端口即可恢复。");
			}

			// 同进程重复注入防护（实测依据见 README 第十节）：
			// 再次 DllMain 会重新 InstallHookBridge / InstallRelayHandler / InstallConnectionHook，
			// 在 Forge 47.4.29 上会导致 NetworkFilters 重复注册 forge:vanilla_filter
			// （java.lang.IllegalArgumentException: Duplicate handler name），
			// 进而 sendMCRegistryPackets 中断、进服失败、服务端"反作弊验证超时"。
			if (reInject || _injectedPid == pid)
			{
				string refuse = "已阻止重复注入：该版本 payload 不能对同一进程二次注入 —— "
					+ "实测二次 DllMain 会重复安装 Hook / Relay，触发 Forge 网络过滤器重复注册"
					+ "（Duplicate handler name: forge:vanilla_filter），导致进服失败、服务端反作弊验证超时。"
					+ "正确做法：重启客户端 → 先进入布吉岛服务器 → 再点「注入布吉岛代理」。";
				LogHelper.Force("[布吉岛代理] " + refuse);
				return (false, refuse);
			}

			// 注入前快照监听表，注入后按增量认端口（payload 不写交接文件）
			HashSet<int> before = ProxyPortProbe.Snapshot();

			// 记录 payload 日志起点并开始镜像，便于判定走了哪条路径
			BjdPayloadLog.MarkInjectionStart();
			BjdPayloadLog.StartMirror();

			Action<string> injectLog = m => log?.Invoke(m);
			InjectionOptions options = new InjectionOptions
			{
				Strategy = InjectionStrategy.Auto,
				FallbackToLoadLibrary = true,
				WaitMs = 20000,
				Log = injectLog,
			};

			InjectionResult result = await Task
				.Run(() => BjdInjector.Inject(pid, payloadPath, options), ct)
				.ConfigureAwait(continueOnCapturedContext: false);

			if (!result.Success)
			{
				string injectFail = "注入失败：" + result.Detail;
				LogHelper.Log("[布吉岛代理] " + injectFail);
				return (false, injectFail);
			}
			LogHelper.Log("[布吉岛代理] " + result.ToString());
			log?.Invoke(result.ToString());

			// 注入返回成功 != payload 真的跑起来了：反射加载在带 .tls 的 mingw 产物上会静默失败
			// （远程线程退出码 0x80000003，DllMain 根本没执行）。payload 的 "[raw] DllMain entry"
			// 是 RawWriteFile 直写、不经过缓冲，用它做一次落地校验；没写出来就改用 LoadLibrary 重试。
			if (!BjdPayloadLog.WaitForMarker("DllMain entry", TimeSpan.FromSeconds(3), out string dllMainLine))
			{
				LogHelper.Force("[布吉岛代理] 注入返回成功，但 payload 未写出 DllMain 日志（反射/映射可能静默失败），改用 LoadLibrary 重试…");
				log?.Invoke("注入未生效（无 DllMain 日志），改用 LoadLibrary 重试…");
				InjectionOptions fallbackOptions = new InjectionOptions
				{
					Strategy = InjectionStrategy.LoadLibrary,
					FallbackToLoadLibrary = false,
					WaitMs = 20000,
					Log = injectLog,
				};
				InjectionResult fallbackResult = await Task
					.Run(() => BjdInjector.Inject(pid, payloadPath, fallbackOptions), ct)
					.ConfigureAwait(continueOnCapturedContext: false);
				if (!fallbackResult.Success)
				{
					return (false, "注入未生效（ManualMap/Reflective 与 LoadLibrary 均未启动 payload）：" + fallbackResult.Detail);
				}
				result = fallbackResult;
				LogHelper.Log("[布吉岛代理] " + result.ToString());
				log?.Invoke(result.ToString());
			}
			else
			{
				LogHelper.Log("[布吉岛代理] payload 已启动：" + dllMainLine);
			}

			// payload 的常驻控制器要先等 jvm.dll / JVMTI 附加，再装 Hook、起 B 监听，给足预算
			int port;
			if (existing > 0)
			{
				// 重新注入：监听本来就还在，等它重新挂载稳定即可，不能去等"新增监听"
				await Task.Delay(1500, ct).ConfigureAwait(continueOnCapturedContext: false);
				port = ProbeExistingPort();
			}
			else
			{
				port = await WaitForNewPortAsync(before, TimeSpan.FromSeconds(120), log, ct)
					.ConfigureAwait(continueOnCapturedContext: false);
			}
			if (port <= 0)
			{
				string timeout = "注入已返回成功，但 " + ProxyPortProbe.BasePort + " 起 "
					+ ProxyPortProbe.Span + " 个端口内没有出现新监听。"
					+ "常见原因：客户端还在加载（可稍后重试本按钮确认）、payload 版本与客户端不匹配、或被安全软件拦截。";
				LogHelper.Log("[布吉岛代理] " + timeout);
				return (false, timeout);
			}

			// 判定 payload 走的是 mid-session（安全）还是 MAIN-GATE（挂起）
			bool frozen = DetectRenderGate(out string gateEvidence, out bool capturedLive);
			_injectedPid = pid;
			_capturedLive = capturedLive;
			MarkActive(pid, port, frozen);

			if (frozen)
			{
				string warn = "⚠ 已注入，但 payload 判定 A 尚未进入服务器，游戏 Render 线程已被挂起（画面卡住）。"
					+ "这不是崩溃，也不是注入失败。恢复方式：立刻用第二个客户端连 127.0.0.1:" + port
					+ "（到达 PLAY 后 A 会自动恢复）。注意：在游戏里按 DEL 无法解除该等待。"
					+ "若无法恢复，只能结束游戏进程。证据：" + gateEvidence;
				LogHelper.Force("[布吉岛代理] " + warn);
				return (true, warn);
			}

			string ok = "布吉岛 A/B 会话共享代理已就绪（mid-session 捕获成功，未阻塞游戏）：127.0.0.1:" + port
				+ "。第二个客户端在游戏内添加服务器填 127.0.0.1，端口 " + port + "。";
			LogHelper.Force("[布吉岛代理] " + ok);
			return (true, ok);
		}
		catch (OperationCanceledException)
		{
			LogHelper.Log("[布吉岛代理] 启动已取消");
			return (false, "启动已取消");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[布吉岛代理] 启动异常");
			return (false, "启动异常：" + ex.Message);
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// 从 payload 日志判定是否进入了「挂起 Render 线程」的预连接等待。
	/// 先等挂起标记（正常路径下这个标记很快出现）；没等到再看 mid-session 捕获标记。
	/// </summary>
	private static bool DetectRenderGate(out string evidence, out bool capturedLive)
	{
		evidence = "";
		capturedLive = false;
		if (BjdPayloadLog.WaitForMarker("MAIN-GATE] blocking A Render thread", TimeSpan.FromSeconds(3), out string gateLine))
		{
			evidence = gateLine;
			return true;
		}
		if (BjdPayloadLog.WaitForMarker("mid-session: captured A's live connection", TimeSpan.FromMilliseconds(800), out string capturedLine))
		{
			evidence = capturedLine;
			capturedLive = true;
			return false;
		}
		// 两个标记都没等到：通常意味着 A 未入服但 gate 还没执行（极少见），按未挂起处理，
		// 后续由 Refresh/日志镜像继续观察；不误报卡死。
		evidence = "未在 payload 日志中找到 MAIN-GATE / mid-session 标记";
		return false;
	}

	/// <summary>
	/// 程序重启后接管「上次注入仍存活」的会话：payload 常驻游戏进程，13337 可能仍在监听，
	/// 但本进程状态是空的。发现端口就标记为运行中，避免界面误报「未运行」。
	/// 主线程是否挂起无法从端口判断，故保守标记为未挂起，并在提示里说明。
	/// </summary>
	public static void AdoptExistingSession()
	{
		if (_active)
		{
			return;
		}
		int port = ProbeExistingPort();
		if (port <= 0)
		{
			return;
		}
		MarkActive(0, port, false,
			"检测到已有代理端口 127.0.0.1:" + port + "（可能是上次注入的 payload 仍存活）。"
			+ "若状态不符（例如游戏画面卡住），请点「重新注入（唤醒 / 恢复）」或查看 payload 日志。");
		LogHelper.Force("[布吉岛代理] " + _lastMessage);
	}

	/// <summary>探测已就绪的端口：优先记住的端口，其次扫 payload 端口段。</summary>
	public static int ProbeExistingPort()
	{
		int remembered = _port;
		if (remembered > 0 && ProxyPortProbe.IsListening(remembered))
		{
			return remembered;
		}
		foreach (int candidate in ProxyPortProbe.Snapshot())
		{
			if (candidate >= ProxyPortProbe.BasePort && candidate < ProxyPortProbe.BasePort + ProxyPortProbe.Span)
			{
				return candidate;
			}
		}
		return 0;
	}

	/// <summary>刷新运行态：端口消失（客户端被关 / DEL 停用）就清空。</summary>
	public static void Refresh()
	{
		if (!_active)
		{
			return;
		}
		if (ProbeExistingPort() > 0)
		{
			return;
		}
		Clear("布吉岛代理端口已关闭（客户端可能已退出，或在游戏内按 DEL 停用了代理）");
	}

	/// <summary>供 UI 展示的一行状态（含 B 是否接入、A 是否重连过）。</summary>
	public static string DescribeStatus()
	{
		if (!_active)
		{
			return "未运行";
		}
		string mode = _gameFrozen ? "⚠ 游戏主线程挂起等待 B" : "运行中（未阻塞游戏）";
		string bState = BConnected
			? "B 已接入"
			: "B 未接入 —— 在 B 连上 127.0.0.1:" + _port + " 之前不会有任何共享效果";
		string aState = AConnectionCount > 0
			? ("A 已重新建连 " + AConnectionCount + " 次（payload 会在新连接上重挂 Relay，可能触发 Forge 重复注册）")
			: "A 未重连";
		return mode + " · " + bState + " · " + aState
			+ " · 127.0.0.1:" + _port + (_pid > 0 ? "（A pid " + _pid + "）" : "");
	}

	private static async Task<int> WaitForNewPortAsync(
		HashSet<int> before, TimeSpan timeout, Action<string>? log, CancellationToken ct)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		bool announced = false;
		while (DateTime.UtcNow < deadline)
		{
			ct.ThrowIfCancellationRequested();
			foreach (int candidate in ProxyPortProbe.Snapshot())
			{
				if (candidate < ProxyPortProbe.BasePort || candidate >= ProxyPortProbe.BasePort + ProxyPortProbe.Span)
				{
					continue;
				}
				if (before.Contains(candidate))
				{
					continue;
				}
				return candidate;
			}
			if (!announced)
			{
				announced = true;
				log?.Invoke("已注入，等待代理端口（" + ProxyPortProbe.BasePort + " 起）就绪…");
			}
			try
			{
				await Task.Delay(500, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
		}
		return 0;
	}

	private static void MarkActive(int pid, int port, bool frozen, string? message = null)
	{
		_pid = pid;
		_port = port;
		_active = true;
		_gameFrozen = frozen;
		_lastMessage = message ?? ((frozen ? "⚠ 运行中但游戏被挂起：" : "运行中：") + "127.0.0.1:" + port
			+ (pid > 0 ? "（A 客户端 pid " + pid + "）" : ""));
		StateChanged?.Invoke();
	}

	private static void Clear(string message)
	{
		_pid = 0;
		_port = 0;
		_active = false;
		_gameFrozen = false;
		_lastMessage = message;
		StateChanged?.Invoke();
	}
}

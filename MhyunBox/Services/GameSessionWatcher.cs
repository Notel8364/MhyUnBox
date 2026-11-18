using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>
/// 游戏实例监听：让脱盒知道「游戏起来了 / 已经对上了服务器」。
///
/// 判定方式不依赖进程命令行（PCL 用 jre-legacy 的 javaw.exe 启动，命令行要 WMI 才读得到，
/// 既慢又容易被杀软拦）。改成读客户端目录里的 logs/latest.log：
///  - 文件还在持续写入  → 游戏实例在运行；
///  - 新写入的内容里出现进服标记（joined server / vanilla connection established 等）→ 已对上服务器；
///  - latest.log 被截断（游戏重启会重建该文件）→ 视为新的一局，状态清零重新判断。
///
/// 一局游戏「启动」时补写机器人代理卡片（账号写回 mythosnexus.json），
/// 「对上服务器」时把勾了自启的机器人代理重启一遍 —— 模组每 3 秒热加载配置，
/// 会自己按新账号重建机器人会话。
/// </summary>
public static class GameSessionWatcher
{
	/// <summary>单个客户端的监听状态。</summary>
	private sealed class ClientState
	{
		public string Dir = "";
		public long Offset;
		public bool Running;
		public bool Connected;
		public long StartedMs;
		public long ConnectedMs;
	}

	/// <summary>轮询间隔：3 秒足够快，读日志是小文件增量，不占 CPU。</summary>
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

	/// <summary>latest.log 多久没写入就认为游戏已退出。</summary>
	private const int IdleToStopMs = 20_000;

	/// <summary>进服标记（都是 ASCII，不需要关心日志编码）。</summary>
	private static readonly string[] ConnectMarkers =
	{
		"joined server:",
		"Client side vanilla connection established",
		"Joined the game",
		"[CHAT]"
	};

	/// <summary>离服标记。</summary>
	private static readonly string[] DisconnectMarkers =
	{
		"Disconnected from server",
		"Stopping!"
	};

	private static readonly object Sync = new object();
	private static readonly Dictionary<string, ClientState> States = new(StringComparer.OrdinalIgnoreCase);
	private static int _started;

	/// <summary>启动监听（重复调用只生效一次）。</summary>
	public static void Start()
	{
		if (Interlocked.Exchange(ref _started, 1) == 1)
		{
			return;
		}
		_ = Task.Run(LoopAsync);
		LogHelper.Log("[游戏监听] 已启动：按客户端 logs/latest.log 判断游戏是否运行 / 是否已进服");
	}

	/// <summary>当前是否有游戏实例在运行（任一客户端）。</summary>
	public static bool AnyGameRunning()
	{
		lock (Sync)
		{
			foreach (ClientState s in States.Values)
			{
				if (s.Running)
				{
					return true;
				}
			}
		}
		return false;
	}

	private static async Task LoopAsync()
	{
		// 启动后先等一会儿，避免和主窗口初始化抢 IO
		await Task.Delay(4000).ConfigureAwait(false);
		while (true)
		{
			try
			{
				Poll();
			}
			catch (Exception ex)
			{
				LogHelper.Log("[游戏监听] 轮询失败：" + ex.Message);
			}
			try
			{
				await Task.Delay(PollInterval).ConfigureAwait(false);
			}
			catch
			{
				return;
			}
		}
	}

	private static void Poll()
	{
		List<string> dirs = NexusEndpointService.EnumerateClientDirs();
		if (dirs.Count == 0)
		{
			return;
		}
		var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string dir in dirs)
		{
			alive.Add(dir);
			try
			{
				PollClient(dir);
			}
			catch (Exception ex)
			{
				LogHelper.Log("[游戏监听] 读取 " + Path.GetFileName(dir) + " 失败：" + ex.Message);
			}
		}
		lock (Sync)
		{
			List<string> stale = new List<string>();
			foreach (string key in States.Keys)
			{
				if (!alive.Contains(key))
				{
					stale.Add(key);
				}
			}
			foreach (string key in stale)
			{
				States.Remove(key);
			}
		}
	}

	private static void PollClient(string dir)
	{
		// 只盯「挂号版」客户端：合并版 / 备份副本也有 logs/latest.log，
		// 但它们既不是机器人账号的去处，也会把历史进服记录当成新一局。
		string name = Path.GetFileName(dir) ?? "";
		if (name.IndexOf("挂号版", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return;
		}
		string logPath = Path.Combine(dir, "logs", "latest.log");
		if (!File.Exists(logPath))
		{
			return;
		}
		ClientState state;
		bool firstPoll = false;
		lock (Sync)
		{
			if (!States.TryGetValue(dir, out ClientState? found))
			{
				found = new ClientState { Dir = dir };
				States[dir] = found;
				firstPoll = true;
			}
			state = found;
		}
		long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
		DateTime writeTime;
		long length;
		try
		{
			var info = new FileInfo(logPath);
			writeTime = info.LastWriteTime;
			length = info.Length;
		}
		catch
		{
			return;
		}
		bool writing = (DateTime.Now - writeTime).TotalMilliseconds < IdleToStopMs;

		if (firstPoll)
		{
			if (writing)
			{
				// 程序比游戏晚启动：从日志尾部 64KB 起读，判断这一局是不是已经在服里了。
				state.Offset = Math.Max(0, length - 64 * 1024);
			}
			else
			{
				// 没在运行：历史内容一律跳过，避免把上一次的「joined server」当成新进服。
				state.Offset = length;
				return;
			}
		}
		// 游戏重启会重建 latest.log：长度回退 → 当成新的一局
		if (length < state.Offset)
		{
			state.Offset = 0;
			state.Connected = false;
			state.ConnectedMs = 0;
			state.Running = false;
			LogHelper.Log("[游戏监听] " + name + " 的 latest.log 已重建（游戏重启）");
		}

		bool justConnected = false;
		bool justDisconnected = false;
		if (writing)
		{
			// 只有游戏确实在写日志时才解析标记：闲置目录里的历史内容不能算「进服」。
			string? chunk = ReadNew(logPath, state);
			if (chunk != null && chunk.Length > 0)
			{
				foreach (string marker in ConnectMarkers)
				{
					if (chunk.IndexOf(marker, StringComparison.Ordinal) >= 0)
					{
						justConnected = true;
						break;
					}
				}
				foreach (string marker in DisconnectMarkers)
				{
					if (chunk.IndexOf(marker, StringComparison.Ordinal) >= 0)
					{
						justDisconnected = true;
						break;
					}
				}
			}
		}

		if (writing && !state.Running)
		{
			state.Running = true;
			state.StartedMs = now;
			LogHelper.Log("[游戏监听] 检测到游戏实例启动：" + name);
			OnGameStarted(dir);
		}
		else if (!writing && state.Running)
		{
			state.Running = false;
			state.Connected = false;
			state.ConnectedMs = 0;
			LogHelper.Log("[游戏监听] 游戏实例已退出：" + name);
		}

		if (justDisconnected && state.Connected)
		{
			state.Connected = false;
			state.ConnectedMs = 0;
			LogHelper.Log("[游戏监听] " + name + " 已离开服务器");
		}
		if (justConnected && !state.Connected && state.Running)
		{
			state.Connected = true;
			state.ConnectedMs = now;
			LogHelper.Log("[游戏监听] " + name + " 已对上服务器");
			OnGameConnected(dir);
		}
		// 游戏在跑但一条进服标记都没有（日志被截断/模组没打日志）：
		// 启动 25 秒后兜底补写一次卡片，但**不**重启代理 —— 没读到进服标记时不该惊动机器人。
		if (state.Running && !state.Connected && state.StartedMs > 0 && now - state.StartedMs > 25_000)
		{
			state.Connected = true;
			state.ConnectedMs = now;
			LogHelper.Log("[游戏监听] " + name + " 未读到进服标记，按超时兜底补写机器人代理卡片");
			PostToUi(() =>
			{
				try
				{
					NexusEndpointService.RecreateBotCardsForClient(dir);
				}
				catch (Exception ex)
				{
					LogHelper.Log("[游戏监听] 超时兜底补写卡片失败：" + ex.Message);
				}
			});
		}
	}

	/// <summary>读取 latest.log 自上次位置以来的新增内容（一次最多 256KB，避免卡死）。</summary>
	private static string? ReadNew(string path, ClientState state)
	{
		try
		{
			using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			long length = fs.Length;
			if (length <= state.Offset)
			{
				return null;
			}
			long start = state.Offset;
			const int maxChunk = 256 * 1024;
			if (length - start > maxChunk)
			{
				start = length - maxChunk;
			}
			fs.Seek(start, SeekOrigin.Begin);
			var buffer = new byte[length - start];
			int read = fs.Read(buffer, 0, buffer.Length);
			state.Offset = start + read;
			// 标记全是 ASCII：按 Latin1 解，中文乱码不影响匹配
			return System.Text.Encoding.Latin1.GetString(buffer, 0, read);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 游戏实例刚启动：把这台客户端上的机器人代理卡片重新写一遍（账号 + 绑定）。
	/// 必须回到 UI 线程执行：实例多开页每 2 秒也会 Load 一次配置，两边并发读写设置会互相踩。
	/// </summary>
	private static void OnGameStarted(string dir)
	{
		PostToUi(() =>
		{
			try
			{
				int written = NexusEndpointService.RecreateBotCardsForClient(dir);
				LogHelper.Log("[游戏监听] 已按游戏实例补写机器人代理卡片 " + written + " 张 → " + Path.GetFileName(dir));
			}
			catch (Exception ex)
			{
				LogHelper.Log("[游戏监听] 补写机器人代理卡片失败：" + ex.Message);
			}
		});
	}

	/// <summary>游戏已对上服务器：重启这台客户端上勾了「自启动」的机器人代理会话。</summary>
	private static void OnGameConnected(string dir)
	{
		PostToUi(() =>
		{
			try
			{
				// 再补一次账号：游戏启动到进服之间客户端可能又保存过配置。
				NexusEndpointService.RecreateBotCardsForClient(dir);
				int restarted = NexusEndpointService.RestartAutoStartBotsForClient(dir);
				if (restarted > 0)
				{
					LogHelper.Log("[游戏监听] 游戏已进服，补起缺少监听的自启动机器人代理 " + restarted + " 个 → " + Path.GetFileName(dir));
				}
				else
				{
					LogHelper.Log("[游戏监听] 游戏已进服，" + Path.GetFileName(dir) + " 的自启动机器人代理都已在监听");
				}
			}
			catch (Exception ex)
			{
				LogHelper.Log("[游戏监听] 自动重启机器人代理失败：" + ex.Message);
			}
		});
	}

	/// <summary>把动作丢回主线程执行；主窗口还没建好时直接在当前线程跑。</summary>
	private static void PostToUi(Action action)
	{
		try
		{
			var window = MainWindow.Instance;
			if (window?.DispatcherQueue != null && window.DispatcherQueue.TryEnqueue(() => action()))
			{
				return;
			}
		}
		catch
		{
		}
		try
		{
			action();
		}
		catch (Exception ex)
		{
			LogHelper.Log("[游戏监听] 执行失败：" + ex.Message);
		}
	}
}

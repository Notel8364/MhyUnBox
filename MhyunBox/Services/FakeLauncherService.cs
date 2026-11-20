using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>
/// 假「网易发烧启动器」IPC 服务端（脱盒用）。
///
/// 协议来源：2026-11-19 对官方启动器 127.0.0.1:&lt;launcherport&gt; 的双向抓包逆向（已完整解出）。
/// 帧格式：[u16 LE 长度][u16 LE SMID][字段…]；字段小端；字符串 = [u16 LE 长度][UTF-8]。
///
/// 已观察消息：
///   0    C→L 登录：short gameid + 空字符串
///   530  C→L 心跳
///   261  C→L 阶段握手 short=1
///   1799 L→C ReplyJoinGame：string host + short port + short 0 + string room
///   1799 C→L 应答
///   2064 C→L "loadworld"
///   520  C→L/C→L 皮肤请求/回复（玩家名 + uuid / 玩家名 + 本地 png 路径 + 6 字节标记）
///   18/1298 C→L 低频杂项
///   1    L→C ReplyCloseMinecraft（会让客户端关游戏）
///
/// 服务端职责：接受客户端连接、回应登录（延迟后下发房间信息）、静默心跳、记录请求；
/// 对无法解析的连接（例如 native 认证的私有协议）转入原始字节记录模式，便于继续逆向。
/// </summary>
public sealed class FakeLauncherService
{
	private static readonly object Gate = new object();
	// 允许同时存在多个假启动器（每个多开的客户端一个），不再强行只保留最后一个。
	private static readonly List<FakeLauncherService> _instances = new List<FakeLauncherService>();

	private TcpListener? _listener;
	private CancellationTokenSource? _cts;
	private readonly List<string> _log = new List<string>();
	private readonly object _logLock = new object();

	/// <summary>监听端口（127.0.0.1）。</summary>
	public int Port { get; private set; }

	/// <summary>下发给客户端的房间地址（一般指向本地认证代理）。</summary>
	public string RoomHost { get; private set; } = "127.0.0.1";

	public int RoomPort { get; private set; }

	public string RoomName { get; private set; } = "MhyunBox";

	public bool IsRunning
	{
		get
		{
			TcpListener? l = _listener;
			return l != null && _cts != null && !_cts.IsCancellationRequested;
		}
	}

	public static bool IsAnyRunning
	{
		get
		{
			lock (Gate)
			{
				return _instances.Any((FakeLauncherService i) => i.IsRunning);
			}
		}
	}

	/// <summary>当前在跑（或多开中）的假启动器数量。</summary>
	public static int RunningCount
	{
		get
		{
			lock (Gate)
			{
				return _instances.Count((FakeLauncherService i) => i.IsRunning);
			}
		}
	}

	public static FakeLauncherService? Current
	{
		get
		{
			lock (Gate)
			{
				return _instances.LastOrDefault((FakeLauncherService i) => i.IsRunning);
			}
		}
	}

	private void AddLog(string s)
	{
		string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + s;
		lock (_logLock)
		{
			_log.Add(line);
			if (_log.Count > 4000)
			{
				_log.RemoveRange(0, 1000);
			}
		}
		LogHelper.Log("[假启动器] " + s);
	}

	/// <summary>启动假启动器。端口传 0 时自动挑选空闲端口。允许多开（不复用/不停掉已有实例）。</summary>
	public static FakeLauncherService Start(int port, string roomHost, int roomPort, string roomName)
	{
		FakeLauncherService svc = new FakeLauncherService
		{
			RoomHost = string.IsNullOrWhiteSpace(roomHost) ? "127.0.0.1" : roomHost,
			RoomPort = roomPort,
			RoomName = string.IsNullOrWhiteSpace(roomName) ? "MhyunBox" : roomName
		};
		svc.Bind(port);
		lock (Gate)
		{
			_instances.Add(svc);
		}
		return svc;
	}

	/// <summary>关闭所有假启动器实例（旧接口语义）。</summary>
	public static void StopCurrent()
	{
		List<FakeLauncherService> all;
		lock (Gate)
		{
			all = _instances.ToList();
			_instances.Clear();
		}
		foreach (FakeLauncherService svc in all)
		{
			svc.Stop();
		}
	}

	/// <summary>只关闭指定的假启动器实例（启动失败回滚用）。</summary>
	public static void StopInstance(FakeLauncherService? svc)
	{
		if (svc == null)
		{
			return;
		}
		lock (Gate)
		{
			_instances.Remove(svc);
		}
		svc.Stop();
	}

	private void Bind(int port)
	{
		TcpListener listener = new TcpListener(IPAddress.Loopback, port);
		listener.Start();
		Port = ((IPEndPoint)listener.LocalEndpoint).Port;
		_listener = listener;
		_cts = new CancellationTokenSource();
		AddLog("监听 127.0.0.1:" + Port + "，房间下发 " + RoomHost + ":" + RoomPort + " / " + RoomName);
		CancellationToken token = _cts.Token;
		_ = Task.Run(() => AcceptLoopAsync(listener, token), token);
	}

	private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
	{
		int connId = 0;
		while (!token.IsCancellationRequested)
		{
			TcpClient client;
			try
			{
				client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				AddLog("accept 失败：" + ex.Message);
				break;
			}
			connId++;
			_ = Task.Run(() => HandleClientAsync(client, connId, token), token);
		}
	}

	private async Task HandleClientAsync(TcpClient client, int connId, CancellationToken token)
	{
		AddLog("连接 #" + connId + " 来自 " + client.Client.RemoteEndPoint);
		using (client)
		{
			try
			{
				using NetworkStream stream = client.GetStream();
				client.NoDelay = true;
				byte[] readBuf = new byte[65536];
				List<byte> pending = new List<byte>(8192);
				bool rawMode = false;
				bool[] roomSentBox = new bool[1];
				DateTime lastHeartbeat = DateTime.UtcNow;
				while (!token.IsCancellationRequested)
				{
					int n;
					try
					{
						n = await stream.ReadAsync(readBuf.AsMemory(0, readBuf.Length), token).ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (OperationCanceledException)
					{
						break;
					}
					catch (IOException)
					{
						break;
					}
					if (n <= 0)
					{
						break;
					}
					if (rawMode)
					{
						AddLog("连接 #" + connId + " [raw] " + n + "B " + Hex(readBuf, n));
						continue;
					}
					for (int i = 0; i < n; i++)
					{
						pending.Add(readBuf[i]);
					}
					// 尝试解析帧
					while (pending.Count >= 2)
					{
						int len = pending[0] | (pending[1] << 8);
						if (len < 2 || len > 0x8000)
						{
							// 不是帧协议：可能是 native 认证私有协议，转原始记录
							rawMode = true;
							AddLog("连接 #" + connId + " 非帧数据（len=" + len + "），转原始记录：" + Hex(pending.ToArray(), pending.Count));
							pending.Clear();
							break;
						}
						if (pending.Count < 2 + len)
						{
							break;
						}
						byte[] payload = new byte[len];
						pending.CopyTo(2, payload, 0, len);
						pending.RemoveRange(0, 2 + len);
						int smid = payload[0] | (payload[1] << 8);
						OnFrame(connId, stream, smid, payload, roomSentBox, ref lastHeartbeat, token);
					}
				}
			}
			catch (Exception ex)
			{
				AddLog("连接 #" + connId + " 异常：" + ex.Message);
			}
			finally
			{
				AddLog("连接 #" + connId + " 关闭");
			}
		}
	}

	private void OnFrame(int connId, NetworkStream stream, int smid, byte[] payload, bool[] roomSentBox, ref DateTime lastHeartbeat, CancellationToken token)
	{
		switch (smid)
		{
		case 0:
		{
			AddLog("连接 #" + connId + " 登录帧 " + Hex(payload, payload.Length));
			// 官方启动器是在客户端发 261 阶段握手时才下发房间；这里额外做一个 25s 兜底，
			// 防止某些版本不经过 261（保持只发一次）。
			Task.Run(async delegate
			{
				await Task.Delay(25000).ConfigureAwait(continueOnCapturedContext: false);
				if (!roomSentBox[0])
				{
					roomSentBox[0] = true;
					SendJoinGame(connId, stream);
				}
			});
			break;
		}
		case 530:
			lastHeartbeat = DateTime.UtcNow;
			break;
		case 261:
		{
			AddLog("连接 #" + connId + " 阶段握手 261 " + Hex(payload, payload.Length));
			if (!roomSentBox[0])
			{
				roomSentBox[0] = true;
				SendJoinGame(connId, stream);
			}
			break;
		}
		case 1799:
			AddLog("连接 #" + connId + " 客户端 1799 应答 " + Hex(payload, payload.Length));
			break;
		case 2064:
			AddLog("连接 #" + connId + " loadworld " + Hex(payload, payload.Length));
			break;
		case 520:
		{
			// 皮肤请求：smid + short + 玩家名 + uuid → 回 smid + 玩家名 + 路径 + 6 字节标记
			string name = ReadString(payload, 4, out int next);
			string uuid = ReadString(payload, next, out _);
			AddLog("连接 #" + connId + " 皮肤请求 name=" + name + " uuid=" + uuid);
			byte[] resp = Frame(520, Concat(EncString(name), EncString("C:\\MCLDownload\\cache\\skin\\skin_10000.png"), new byte[6]));
			try
			{
				stream.Write(resp, 0, resp.Length);
			}
			catch (Exception ex)
			{
				AddLog("连接 #" + connId + " 回皮肤失败：" + ex.Message);
			}
			break;
		}
		case 18:
		case 1298:
			AddLog("连接 #" + connId + " 杂项 smid=" + smid + " " + Hex(payload, payload.Length));
			break;
		default:
			AddLog("连接 #" + connId + " 未知 smid=" + smid + " " + Hex(payload, payload.Length));
			break;
		}
	}

	private void SendJoinGame(int connId, NetworkStream stream)
	{
		try
		{
			byte[] room = Frame(1799, Concat(EncString(RoomHost), U16Le((ushort)RoomPort), U16Le(0), EncString(RoomName)));
			stream.Write(room, 0, room.Length);
			AddLog("连接 #" + connId + " → ReplyJoinGame " + RoomHost + ":" + RoomPort + " / " + RoomName);
		}
		catch (Exception ex)
		{
			AddLog("连接 #" + connId + " 发房间失败：" + ex.Message);
		}
	}
	private void Stop()
	{
		try
		{
			_cts?.Cancel();
		}
		catch
		{
		}
		try
		{
			_listener?.Stop();
		}
		catch
		{
		}
		_listener = null;
		AddLog("已停止");
	}

	private static byte[] U16Le(int v)
	{
		return new byte[2]
		{
			(byte)(v & 0xFF),
			(byte)((v >> 8) & 0xFF)
		};
	}

	private static byte[] EncString(string s)
	{
		byte[] b = Encoding.UTF8.GetBytes(s ?? "");
		return Concat(U16Le(b.Length), b);
	}

	private static byte[] Frame(int smid, byte[] body)
	{
		int len = 2 + body.Length;
		byte[] framed = Concat(U16Le(len), U16Le(smid), body);
		return framed;
	}

	private static byte[] Concat(params byte[][] parts)
	{
		int total = 0;
		foreach (byte[] p in parts)
		{
			total += p.Length;
		}
		byte[] result = new byte[total];
		int offset = 0;
		foreach (byte[] p in parts)
		{
			Buffer.BlockCopy(p, 0, result, offset, p.Length);
			offset += p.Length;
		}
		return result;
	}

	private static string ReadString(byte[] data, int offset, out int next)
	{
		next = offset;
		if (offset + 2 > data.Length)
		{
			return "";
		}
		int len = data[offset] | (data[offset + 1] << 8);
		next = offset + 2;
		if (len <= 0 || next + len > data.Length)
		{
			return "";
		}
		string s = Encoding.UTF8.GetString(data, next, len);
		next += len;
		return s;
	}

	private static string Hex(byte[] data, int count)
	{
		StringBuilder sb = new StringBuilder(count * 3);
		for (int i = 0; i < count && i < 256; i++)
		{
			sb.Append(data[i].ToString("X2")).Append(' ');
		}
		return sb.ToString().Trim();
	}
}

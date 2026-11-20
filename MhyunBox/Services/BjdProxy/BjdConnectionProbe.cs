using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MhyunBox.Services.BjdProxy.Native;

namespace MhyunBox.Services.BjdProxy;

/// <summary>
/// 注入前的「A 是否已进入服务器」探测（按 PID 读内核 TCP 连接表）。
///
/// 为什么必须要这道检查：
/// payload 有两条登录路径（SakuraTools/WtfNel 的 loader.cpp）：
///   - A 已入服 → <c>BServer_TryCaptureLiveConnection</c> 走 mid-session 捕获，**不阻塞主线程**；
///   - A 未入服 → <c>BServer_BlockAMainThreadUntilBConnected</c> 在 A 的 Render 线程上执行
///     <c>BServer_WaitForBConnected(-1)</c>，即**无限期阻塞**，直到 B 连上 13337 并到达 PLAY。
/// 该等待没有超时、也没有 stop 唤醒（g_bConnCv 只认 g_bConnected），所以一旦进入，
/// 连按 DEL 都解不开 —— 只能让 B 连上，或结束游戏进程。
///
/// 因此注入前必须先确认 A 已经连着服务器；否则宁可拒绝注入。
/// 判据：目标游戏进程存在一条 ESTABLISHED 连接，其远端端口是
/// 布吉岛预设端口 25565 / 本程序代理监听端口 / payload 端口 13337。
/// （客户端直连服务器时远端端口是 25565；走本程序通用代理时远端就是 127.0.0.1:&lt;监听端口&gt;。）
/// </summary>
public static class BjdConnectionProbe
{
	/// <summary>一条 ESTABLISHED 连接（端口已从网络字节序还原）。</summary>
	public readonly struct GameLink
	{
		public int LocalPort { get; init; }
		public int RemotePort { get; init; }
		public string RemoteAddress { get; init; }
	}

	/// <summary>枚举指定进程的全部 ESTABLISHED TCP 连接。</summary>
	public static IReadOnlyList<GameLink> Established(int pid)
	{
		List<GameLink> links = new List<GameLink>();
		if (pid <= 0)
		{
			return links;
		}

		int size = 0;
		uint rc = NativeApi.GetExtendedTcpTable(IntPtr.Zero, ref size, false, NativeApi.AfInet, NativeApi.TcpTableOwnerPidAll, 0u);
		if (rc != NativeApi.ErrorInsufficientBuffer || size <= 0)
		{
			return links;
		}

		IntPtr buffer = Marshal.AllocHGlobal(size);
		try
		{
			rc = NativeApi.GetExtendedTcpTable(buffer, ref size, false, NativeApi.AfInet, NativeApi.TcpTableOwnerPidAll, 0u);
			if (rc != 0u)
			{
				return links;
			}
			int count = Marshal.ReadInt32(buffer);
			for (int i = 0; i < count; i++)
			{
				IntPtr rowPtr = buffer + 4 + i * NativeApi.MibTcpRowOwnerPidSize;
				NativeApi.MibTcpRowOwnerPid row = Marshal.PtrToStructure<NativeApi.MibTcpRowOwnerPid>(rowPtr);
				if ((int)row.dwOwningPid != pid)
				{
					continue;
				}
				if (row.dwState != NativeApi.TcpStateEstablished)
				{
					continue;
				}
				links.Add(new GameLink
				{
					LocalPort = HostPort(row.dwLocalPort),
					RemotePort = HostPort(row.dwRemotePort),
					RemoteAddress = new System.Net.IPAddress(row.dwRemoteAddr).ToString(),
				});
			}
		}
		catch
		{
			// 读连接表失败不致命：调用方按「未检测到」处理并要求用户确认
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
		return links;
	}

	/// <summary>
	/// A 是否**看起来**已经进入服务器。
	/// true = 已连着服务器，可安全注入（payload 走 mid-session 捕获，不阻塞主线程）；
	/// false = 未检测到，注入有极大概率把游戏卡死。
	/// </summary>
	public static bool IsLikelyInGame(int pid, out string detail)
	{
		IReadOnlyList<GameLink> links = Established(pid);
		if (links.Count == 0)
		{
			detail = $"游戏进程 pid {pid} 当前没有任何 ESTABLISHED 连接 —— 说明客户端还没进服务器（大厅/主菜单）。";
			return false;
		}

		int[] proxyPorts = ProxyCaptureService.ActivePorts ?? Array.Empty<int>();
		List<string> matched = new List<string>();
		List<string> others = new List<string>();
		foreach (GameLink link in links)
		{
			bool isGame = link.RemotePort == BjdGate.Port
				|| link.RemotePort == 13337
				|| Array.IndexOf(proxyPorts, link.RemotePort) >= 0;
			string text = link.RemoteAddress + ":" + link.RemotePort + " (本地 " + link.LocalPort + ")";
			if (isGame)
			{
				matched.Add(text);
			}
			else
			{
				others.Add(text);
			}
		}

		if (matched.Count > 0)
		{
			detail = "已检测到到服务器的连接：" + string.Join("；", matched);
			return true;
		}

		detail = $"游戏进程 pid {pid} 有 {links.Count} 条 ESTABLISHED 连接，但没有一条的远端端口等于 "
			+ BjdGate.Port + " / 13337 / 本程序代理端口（可能是启动器/登录通道）：" + string.Join("；", others)
			+ "。若你确信已在游戏世界内，请勾选「强制注入」。";
		return false;
	}

	/// <summary>诊断用文本：列出该进程所有 ESTABLISHED 连接。</summary>
	public static string Describe(int pid)
	{
		IReadOnlyList<GameLink> links = Established(pid);
		if (links.Count == 0)
		{
			return $"pid {pid}：无 ESTABLISHED 连接";
		}
		List<string> parts = new List<string>();
		foreach (GameLink link in links)
		{
			parts.Add(link.RemoteAddress + ":" + link.RemotePort + " (本地 " + link.LocalPort + ")");
		}
		return $"pid {pid}：{string.Join("；", parts)}";
	}

	/// <summary>内核表里的端口是网络字节序，低 16 位按字节交换。</summary>
	private static int HostPort(uint value)
	{
		uint port = value & 0xFFFFu;
		return (int)(((port & 0xFFu) << 8) | ((port >> 8) & 0xFFu));
	}
}

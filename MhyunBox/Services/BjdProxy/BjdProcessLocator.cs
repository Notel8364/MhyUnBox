using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Services.BjdProxy.Native;

namespace MhyunBox.Services.BjdProxy;

/// <summary>
/// 布吉岛客户端进程定位。
///
/// 判定顺序（两道，缺一不可）：
/// 1. 调用方已通过 <see cref="BjdGate"/> 确认当前选中的是布吉岛预设（GameId 级门禁）；
/// 2. 这里再按「可见窗口标题含「布吉岛」+ 进程是 java/javaw」定位目标，
///    与 SakuraTools 的 injector/cli.c、WtfNel 的 InjectionProfile.Minecraft 一致。
///
/// 之所以不做命令行 `-DlauncherGameId=` 复核：那需要 WMI / 读取目标进程 PEB，
/// 而本工程没有 System.Management 引用，PEB 读取又要额外权限且易碎。
/// 窗口标题 + java 进程已经是两个上游工具的生产验证做法，配合第 1 道 GameId 门禁足够。
/// </summary>
public static class BjdProcessLocator
{
	/// <summary>立即查找一次，取创建时间最新的那个（多开时优先新实例）。</summary>
	public static ProcessCandidate? Find()
	{
		IReadOnlyList<ProcessCandidate> found = ProcessTarget.FindByWindow(
			new string[1] { BjdGate.WindowKeyword }, requireJava: true);
		return ProcessTarget.Newest(found);
	}

	/// <summary>
	/// 轮询等待布吉岛客户端出现。
	/// 返回 (target, detail)：target 为 null 时 detail 说明原因。
	/// </summary>
	public static async Task<(ProcessCandidate? Target, string Detail)> WaitAsync(
		TimeSpan timeout, Action<string>? log = null, CancellationToken ct = default(CancellationToken))
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		bool announced = false;
		while (DateTime.UtcNow < deadline)
		{
			ct.ThrowIfCancellationRequested();
			ProcessCandidate? candidate = Find();
			if (candidate != null)
			{
				return (candidate, $"pid {candidate.ProcessId}「{candidate.Title}」{candidate.Executable}");
			}
			if (!announced)
			{
				announced = true;
				log?.Invoke($"等待布吉岛客户端：需要可见窗口标题含「{BjdGate.WindowKeyword}」的 java/javaw 进程");
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
		return (null, $"等了 {timeout.TotalSeconds:F0} 秒仍没找到布吉岛客户端窗口（标题需含「{BjdGate.WindowKeyword}」）");
	}

	/// <summary>注入前的二次复核：目标必须仍是 java 进程且窗口标题仍含布吉岛关键词。</summary>
	public static bool MatchesBjdWindow(ProcessCandidate candidate)
	{
		if (candidate == null || candidate.ProcessId <= 0)
		{
			return false;
		}
		if (!ProcessTarget.IsJava(candidate.Executable))
		{
			return false;
		}
		return !string.IsNullOrEmpty(candidate.Title)
			&& candidate.Title.IndexOf(BjdGate.WindowKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
	}

	/// <summary>诊断用：列出当前所有候选（不注入时也能说明「为什么没找到」）。</summary>
	public static string Describe()
	{
		IReadOnlyList<ProcessCandidate> found = ProcessTarget.FindByWindow(
			new string[1] { BjdGate.WindowKeyword }, requireJava: true);
		if (found.Count == 0)
		{
			return "当前没有标题含「" + BjdGate.WindowKeyword + "」的 java 窗口";
		}
		List<string> parts = new List<string>();
		foreach (ProcessCandidate candidate in found)
		{
			parts.Add($"pid {candidate.ProcessId}「{candidate.Title}」{candidate.Executable}");
		}
		return string.Join("；", parts);
	}
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using MhyunBox.Services.BjdProxy.Native;

namespace MhyunBox.Services.BjdProxy;

/// <summary>
/// 布吉岛 A/B 会话共享代理的注入编排。
///
/// 移植自 WtfNel.Core.Injection.InjectionService（其 payload 又源自 SakuraTools），
/// 保留其策略选择与兜底顺序：
///   Auto → payload 有 ReflectiveLoader 导出就走反射注入，否则退回 LoadLibrary；
///   注入失败且 FallbackToLoadLibrary → 再走一次 LoadLibrary（含临时副本兜底）。
///
/// 与上游的差异只有三处（都是为了在 MhyunBox 里可诊断、可约束）：
/// 1. 日志走 LogHelper，前缀 [布吉岛代理]；
/// 2. payload 路径由 <see cref="ResolvePayloadPath"/> 多级解析，支持随包分发与源码目录直跑；
/// 3. 注入前统一做一次位宽（x64）检查，避免把 64 位 payload 打进 32 位 JVM。
/// </summary>
internal static class BjdInjector
{
	/// <summary>WtfNel 移植版代理 payload 的文件名（反射注入、监听 13337）。</summary>
	internal const string PayloadFileName = "WtfNel.Payload.Proxy.dll";

	/// <summary>允许用环境变量指向自定义 payload（便于换版本/排障）。</summary>
	private const string PayloadEnvVar = "MHYUNBOX_BJD_PAYLOAD";

	/// <summary>
	/// 按优先级解析 payload 路径：
	/// 环境变量 → 输出目录 payloads\ → 输出目录同级 → 从输出目录向上回溯源码仓库里的 WtfNel\native\prebuilt\。
	/// </summary>
	internal static string? ResolvePayloadPath(out string detail)
	{
		List<string> candidates = new List<string>();
		try
		{
			string? env = Environment.GetEnvironmentVariable(PayloadEnvVar);
			if (!string.IsNullOrWhiteSpace(env))
			{
				candidates.Add(env.Trim());
			}
		}
		catch
		{
		}

		string baseDir = AppContext.BaseDirectory;
		candidates.Add(Path.Combine(baseDir, "payloads", PayloadFileName));
		candidates.Add(Path.Combine(baseDir, PayloadFileName));

		// 免安装 / 直接跑 bin 目录时，回溯找源码仓库的 prebuilt
		try
		{
			DirectoryInfo? dir = new DirectoryInfo(baseDir);
			for (int depth = 0; depth < 6 && dir != null; depth++, dir = dir.Parent)
			{
				candidates.Add(Path.Combine(dir.FullName, "WtfNel", "native", "prebuilt", PayloadFileName));
				candidates.Add(Path.Combine(dir.FullName, "MhyunBox-v7.5.0_FULLVER_F", "WtfNel", "native", "prebuilt", PayloadFileName));
			}
		}
		catch
		{
		}

		foreach (string candidate in candidates)
		{
			try
			{
				if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
				{
					detail = "payload: " + candidate;
					return candidate;
				}
			}
			catch
			{
			}
		}

		detail = "未找到 " + PayloadFileName + "。已查找：" + string.Join(" | ", candidates)
			+ "。可把它放到程序目录的 payloads\\ 下，或用环境变量 " + PayloadEnvVar + " 指定完整路径。";
		return null;
	}

	/// <summary>执行注入。调用方负责门禁与目标定位。</summary>
	internal static InjectionResult Inject(int processId, string payloadPath, InjectionOptions? options)
	{
		options ??= new InjectionOptions();
		Stopwatch clock = Stopwatch.StartNew();
		Action<string> log = options.Log ?? (m => LogHelper.Log("[布吉岛代理] " + m));

		if (!File.Exists(payloadPath))
		{
			return InjectionResult.Fail(options.Strategy, "payload 不存在: " + payloadPath, clock.Elapsed);
		}

		byte[] bytes;
		try
		{
			bytes = File.ReadAllBytes(payloadPath);
		}
		catch (Exception ex)
		{
			return InjectionResult.Fail(options.Strategy, "读取 payload 失败: " + ex.Message, clock.Elapsed, 0UL, ex);
		}

		if (!PeImage.TryParse(bytes, out PeImage pe, out string parseError))
		{
			return InjectionResult.Fail(options.Strategy, "payload 不是有效 PE: " + parseError, clock.Elapsed);
		}

		InjectionStrategy strategy = options.Strategy;
		if (strategy == InjectionStrategy.Auto)
		{
			// 优先 ManualMap，而不是反射注入。原因（2026-11-19 实测）：
			// llvm-mingw 编出来的 payload 带 .tls 段，而自带 ReflectiveLoader.c 只处理
			// 导出/导入/基址重定位，**不初始化 TLS**；在 -O2 构建上会静默失败
			// （远程线程退出码 0x80000003，DllMain 根本没执行、payload 日志无输出）。
			// ManualMapper 自己做重定位/导入解析/TLS 回调/异常表注册，实测可正常启动。
			// 非 x64 payload 仍退回反射注入（ManualMap 只实现了 x64 路径）。
			strategy = pe.Is64Bit ? InjectionStrategy.ManualMap : InjectionStrategy.Reflective;
		}
		log($"注入：pid {processId}，payload {Path.GetFileName(payloadPath)}（{pe.ArchName}，{pe.SizeOfImage / 1024} KB），策略 {strategy}");

		using RemoteProcess? process = RemoteProcess.Open(processId, out string openError);
		if (process == null)
		{
			return InjectionResult.Fail(strategy, openError, clock.Elapsed);
		}

		int targetArch = process.TargetArch();
		int payloadArch = pe.Is64Bit ? 2 : 1;
		if (targetArch != 0 && targetArch != payloadArch)
		{
			return InjectionResult.Fail(strategy,
				$"位宽不匹配：目标 pid {processId} 是 {(targetArch == 2 ? "x64" : "x86")}，payload 是 {(payloadArch == 2 ? "x64" : "x86")}（请使用对应位宽的 payload）",
				clock.Elapsed);
		}

		InjectionResult result = strategy switch
		{
			InjectionStrategy.Reflective => ReflectivePeLoader.Inject(process, pe, options, clock),
			InjectionStrategy.ManualMap => ManualMapper.Inject(process, pe, options, clock),
			_ => LoadFromDisk(process, payloadPath, options, clock),
		};

		if (!result.Success && options.FallbackToLoadLibrary && strategy != InjectionStrategy.LoadLibrary)
		{
			log($"策略 {strategy} 失败（{result.Detail}），改用 LoadLibrary");
			InjectionResult fallback = LoadFromDisk(process, payloadPath, options, clock);
			if (fallback.Success)
			{
				return fallback;
			}
			return InjectionResult.Fail(fallback.Strategy,
				result.Detail + "；兜底 LoadLibrary 也失败：" + fallback.Detail, clock.Elapsed);
		}

		return result;
	}

	private static InjectionResult LoadFromDisk(RemoteProcess process, string payloadPath, InjectionOptions options, Stopwatch clock)
	{
		if (TryRemoteLoadLibrary(process, payloadPath, options, out ulong module, out string error))
		{
			return FinishLoad(process, payloadPath, module, options, clock);
		}

		try
		{
			string tempDir = Path.Combine(Path.GetTempPath(), "MhyunBox", "bjd-payloads");
			Directory.CreateDirectory(tempDir);
			string copy = Path.Combine(tempDir, Path.GetFileName(payloadPath));
			File.Copy(payloadPath, copy, overwrite: true);

			if (TryRemoteLoadLibrary(process, copy, options, out module, out string copyError))
			{
				return FinishLoad(process, copy, module, options, clock);
			}

			return InjectionResult.Fail(InjectionStrategy.LoadLibrary,
				$"远程 LoadLibraryW 失败：{error}；临时副本也失败：{copyError}", clock.Elapsed);
		}
		catch (Exception ex)
		{
			return InjectionResult.Fail(InjectionStrategy.LoadLibrary,
				"LoadLibrary 异常：" + ex.Message + "；原路径失败原因：" + error, clock.Elapsed, 0UL, ex);
		}
	}

	private static bool TryRemoteLoadLibrary(RemoteProcess process, string path, InjectionOptions options, out ulong module, out string error)
	{
		module = 0UL;
		error = "";

		byte[] pathBytes = Encoding.Unicode.GetBytes(path + "\0");
		IntPtr buffer = process.Allocate((uint)pathBytes.Length, NativeApi.PageReadWrite);
		if (buffer == IntPtr.Zero)
		{
			error = "远程分配路径缓冲失败";
			return false;
		}

		try
		{
			if (!process.Write(buffer, pathBytes, pathBytes.Length))
			{
				error = "写路径缓冲失败";
				return false;
			}

			IntPtr remoteKernel32 = process.GetModuleBase("kernel32.dll");
			IntPtr localKernel32 = NativeApi.GetModuleHandleW("kernel32.dll");
			if (remoteKernel32 == IntPtr.Zero || localKernel32 == IntPtr.Zero)
			{
				error = "定位 kernel32 失败";
				return false;
			}

			IntPtr localLoadLibrary = NativeApi.GetProcAddress(localKernel32, "LoadLibraryW");
			if (localLoadLibrary == IntPtr.Zero)
			{
				error = "找不到 LoadLibraryW";
				return false;
			}

			IntPtr remoteLoadLibrary = remoteKernel32 + (int)((ulong)localLoadLibrary - (ulong)localKernel32);
			ulong[] args = new ulong[1] { (ulong)buffer };
			if (!process.TryInvoke(remoteLoadLibrary, args, options.WaitMs, out module, out error))
			{
				return false;
			}

			if (module == 0UL)
			{
				error = "LoadLibraryW 返回 0（模块被拒或依赖缺失）";
				return false;
			}

			return true;
		}
		finally
		{
			process.Free(buffer);
		}
	}

	private static InjectionResult FinishLoad(RemoteProcess process, string path, ulong module, InjectionOptions options, Stopwatch clock)
	{
		if (!string.IsNullOrWhiteSpace(options.InitExport))
		{
			if (!PeImage.TryParse(File.ReadAllBytes(path), out PeImage pe, out _)
				|| !pe.TryGetExport(options.InitExport!, out uint initRva, out _))
			{
				return InjectionResult.Fail(InjectionStrategy.LoadLibrary,
					$"模块里没有导出 {options.InitExport}", clock.Elapsed, module);
			}

			IntPtr init = (IntPtr)(module + initRva);
			ulong[] noArgs = Array.Empty<ulong>();
			if (!process.TryInvoke(init, noArgs, options.WaitMs, out ulong initResult, out string initError))
			{
				return InjectionResult.Fail(InjectionStrategy.LoadLibrary,
					$"调用 {options.InitExport} 失败：{initError}", clock.Elapsed, module);
			}
			(options.Log ?? (m => LogHelper.Log("[布吉岛代理] " + m)))($"{options.InitExport} 返回 0x{initResult:x}");
			if (initResult == 0UL)
			{
				return InjectionResult.Fail(InjectionStrategy.LoadLibrary,
					$"{options.InitExport} 返回 0（payload 内部初始化失败）", clock.Elapsed, module);
			}
		}

		return InjectionResult.Ok(InjectionStrategy.LoadLibrary,
			$"已加载：{path}（模块 0x{module:x}）", clock.Elapsed, module);
	}
}

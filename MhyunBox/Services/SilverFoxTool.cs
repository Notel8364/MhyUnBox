using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

public static class SilverFoxTool
{
	private const string ScriptFileName = "威胁清理.ps1";

	public static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "KillTools");

	public static string ScriptPath => Path.Combine(ToolsDir, "威胁清理.ps1");

	public static string NetGuardScriptPath => Path.Combine(ToolsDir, "断网防护.ps1");

	public static string HuorongKillerPath => Path.Combine(ToolsDir, "火绒恶性木马专杀工具.exe");

	public static string KmsActivatorPath => Path.Combine(ToolsDir, "HEU_KMS_Activator_v42.3.0.exe");

	public static string AntivirusScanScriptPath => Path.Combine(ToolsDir, "杀毒扫描.ps1");

	public static string RescueKitScriptPath => Path.Combine(ToolsDir, "系统急救箱.ps1");

	public static string EvoScanScriptPath => Path.Combine(ToolsDir, "威胁进化扫描.ps1");

	public static string CommandGuardScriptPath => Path.Combine(ToolsDir, "命令守卫.ps1");

	public static string EmergencyScriptPath => Path.Combine(ToolsDir, "应急响应.ps1");

	public static bool IsAdministrator()
	{
		using WindowsIdentity ntIdentity = WindowsIdentity.GetCurrent();
		return new WindowsPrincipal(ntIdentity).IsInRole(WindowsBuiltInRole.Administrator);
	}

	public static async Task RunScanAsync(Action<string> onLine, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!File.Exists(ScriptPath))
		{
			throw new FileNotFoundException("未找到内置威胁清理脚本: " + ScriptPath);
		}
		await RunDirectAsync(BuildPowerShell("scan", null), onLine, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task<bool> RunKillAsync(Action<string> onLine, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!File.Exists(ScriptPath))
		{
			throw new FileNotFoundException("未找到内置威胁清理脚本: " + ScriptPath);
		}
		if (IsAdministrator())
		{
			await RunDirectAsync(BuildPowerShell("kill", null), onLine, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return true;
		}
		string text = Path.Combine(Path.GetTempPath(), "MhyunBox_威胁清理_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
		ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = true,
			Verb = "runas",
			WindowStyle = ProcessWindowStyle.Hidden,
			Arguments = BuildArguments("kill", text)
		};
		try
		{
			Process.Start(startInfo);
		}
		catch (Win32Exception)
		{
			onLine("【已取消】清理操作需要管理员权限（UAC），未获得授权，已终止。");
			return false;
		}
		catch (Exception ex2)
		{
			onLine("【错误】提权启动失败: " + ex2.Message);
			return false;
		}
		return await TailLogFileAsync(text, onLine, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task RunNetGuardAsync(string mode, Action<string> onLine, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!File.Exists(NetGuardScriptPath))
		{
			throw new FileNotFoundException("未找到内置断网防护脚本: " + NetGuardScriptPath);
		}
		if (mode == "status" || IsAdministrator())
		{
			await RunDirectAsync(BuildNetGuardPowerShell(mode, null), onLine, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		string text = Path.Combine(Path.GetTempPath(), "MhyunBox_断网_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
		ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = true,
			Verb = "runas",
			WindowStyle = ProcessWindowStyle.Hidden,
			Arguments = BuildNetGuardArguments(mode, text)
		};
		try
		{
			Process.Start(startInfo);
		}
		catch (Win32Exception)
		{
			onLine("【已取消】开启/关闭挖矿断网防护需要管理员权限（UAC），未获得授权，已终止。");
			return;
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			onLine("【错误】提权启动失败: " + ex3.Message);
			return;
		}
		await TailLogFileAsync(text, onLine, cancellationToken, "== 挖矿断网防护完成 ==").ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task<bool> RunAntivirusScanAsync(Action<string> onLine, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!File.Exists(AntivirusScanScriptPath))
		{
			throw new FileNotFoundException("未找到内置杀毒扫描脚本: " + AntivirusScanScriptPath);
		}
		if (IsAdministrator())
		{
			await RunDirectAsync(BuildAntivirusPowerShell(autoRun: true, null), onLine, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return true;
		}
		string text = Path.Combine(Path.GetTempPath(), "MhyunBox_杀毒扫描_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
		ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = true,
			Verb = "runas",
			WindowStyle = ProcessWindowStyle.Hidden,
			Arguments = BuildAntivirusArguments(autoRun: true, text)
		};
		try
		{
			Process.Start(startInfo);
		}
		catch (Win32Exception)
		{
			onLine("【已取消】杀毒扫描需要管理员权限（UAC），未获得授权，已终止。");
			return false;
		}
		catch (Exception ex2)
		{
			onLine("【错误】提权启动失败: " + ex2.Message);
			return false;
		}
		return await TailLogFileAsync(text, onLine, cancellationToken, "== 已调用威胁清理 ==", "== 扫描完成：未发现威胁", TimeSpan.FromMinutes(30.0)).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task RunRescueKitAsync(Action<string> onLine, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!File.Exists(RescueKitScriptPath))
		{
			throw new FileNotFoundException("未找到系统急救箱脚本: " + RescueKitScriptPath);
		}
		await RunDirectAsync(BuildRescuePowerShell(), onLine, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static ProcessStartInfo BuildAntivirusPowerShell(bool autoRun, string? logFile)
	{
		return new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			Arguments = BuildAntivirusArguments(autoRun, logFile)
		};
	}

	private static string BuildAntivirusArguments(bool autoRun, string? logFile)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append("-NoProfile -ExecutionPolicy Bypass -File \"");
		stringBuilder.Append(AntivirusScanScriptPath);
		stringBuilder.Append('"');
		if (autoRun)
		{
			stringBuilder.Append(" -AutoRun");
		}
		if (!string.IsNullOrEmpty(logFile))
		{
			stringBuilder.Append(" -LogFile \"");
			stringBuilder.Append(logFile);
			stringBuilder.Append('"');
		}
		return stringBuilder.ToString();
	}

	private static ProcessStartInfo BuildRescuePowerShell()
	{
		return new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + RescueKitScriptPath + "\""
		};
	}

	// 下一代威胁扫描（LOLBins/进程伪装/WMI 订阅/BYOVD 驱动，仅检测不清理；默认静默：隐藏窗口、只回传关键行）
	public static async Task<bool> RunEvoScanAsync(Action<string> onLine, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!File.Exists(EvoScanScriptPath))
		{
			throw new FileNotFoundException("未找到下一代威胁扫描脚本: " + EvoScanScriptPath);
		}
		if (IsAdministrator())
		{
			await RunDirectAsync(BuildModePowerShell(EvoScanScriptPath, null, silent: true), onLine, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return true;
		}
		string text = Path.Combine(Path.GetTempPath(), "MhyunBox_进化扫描_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
		ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = true,
			Verb = "runas",
			WindowStyle = ProcessWindowStyle.Hidden,
			Arguments = BuildModeArguments(EvoScanScriptPath, "scan", text, silent: true)
		};
		try
		{
			Process.Start(startInfo);
		}
		catch (Win32Exception)
		{
			onLine("【已取消】下一代威胁扫描需要管理员权限（UAC），未获得授权，已终止。");
			return false;
		}
		catch (Exception ex2)
		{
			onLine("【错误】提权启动失败: " + ex2.Message);
			return false;
		}
		return await TailLogFileAsync(text, onLine, cancellationToken, "== 下一代威胁扫描完成 ==", altMarker: "== 下一代威胁扫描失败 ==", TimeSpan.FromMinutes(15.0)).ConfigureAwait(continueOnCapturedContext: false);
	}

	// 应急响应（病毒泛滥/被入侵时一键止血）：stop=断网+强杀+冻结自启动 standard=加全项扫描 deep=加深度与取证 restore=恢复
	public static async Task RunEmergencyAsync(string mode, Action<string> onLine, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!File.Exists(EmergencyScriptPath))
		{
			throw new FileNotFoundException("未找到应急响应脚本: " + EmergencyScriptPath);
		}
		string tag = mode switch
		{
			"stop" => "应急止血",
			"standard" => "应急响应-标准",
			"deep" => "应急响应-深度",
			"restore" => "应急恢复",
			_ => "应急响应"
		};
		string text = Path.Combine(Path.GetTempPath(), "MhyunBox_" + tag + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
		if (IsAdministrator())
		{
			await RunDirectAsync(BuildEmergencyPowerShell(mode, null), onLine, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = true,
			Verb = "runas",
			WindowStyle = ProcessWindowStyle.Hidden,
			Arguments = BuildEmergencyArguments(mode, text)
		};
		try
		{
			Process.Start(startInfo);
		}
		catch (Win32Exception)
		{
			onLine("【已取消】" + tag + "需要管理员权限（UAC），未获得授权，已终止。");
			return;
		}
		catch (Exception ex2)
		{
			onLine("【错误】提权启动失败: " + ex2.Message);
			return;
		}
		await TailLogFileAsync(text, onLine, cancellationToken, "== 应急响应结束", altMarker: "== 应急响应失败", TimeSpan.FromMinutes(20.0)).ConfigureAwait(continueOnCapturedContext: false);
	}

	// 异常命令监控（命令守卫）：安装/卸载计划任务，或查看状态
	public static async Task RunCommandGuardAsync(string mode, Action<string> onLine, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!File.Exists(CommandGuardScriptPath))
		{
			throw new FileNotFoundException("未找到命令守卫脚本: " + CommandGuardScriptPath);
		}
		if (mode == "status" || IsAdministrator())
		{
			await RunDirectAsync(BuildModePowerShell(CommandGuardScriptPath, "guard-" + mode), onLine, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		string text = Path.Combine(Path.GetTempPath(), "MhyunBox_命令守卫_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
		ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = true,
			Verb = "runas",
			WindowStyle = ProcessWindowStyle.Hidden,
			Arguments = BuildModeArguments(CommandGuardScriptPath, "guard-" + mode, text)
		};
		try
		{
			Process.Start(startInfo);
		}
		catch (Win32Exception)
		{
			onLine("【已取消】命令守卫" + ((mode == "install") ? "开启" : "关闭") + "需要管理员权限（UAC），未获得授权，已终止。");
			return;
		}
		catch (Exception ex2)
		{
			onLine("【错误】提权启动失败: " + ex2.Message);
			return;
		}
		await TailLogFileAsync(text, onLine, cancellationToken, "== 命令守卫操作完成 ==", altMarker: "== 命令守卫操作失败 ==", TimeSpan.FromMinutes(3.0)).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static ProcessStartInfo BuildModePowerShell(string scriptPath, string? mode, bool silent = false)
	{
		return new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			Arguments = BuildModeArguments(scriptPath, mode, null, silent)
		};
	}

	private static string BuildModeArguments(string scriptPath, string? mode, string? logFile, bool silent = false)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append("-NoProfile -ExecutionPolicy Bypass -File \"");
		stringBuilder.Append(scriptPath);
		stringBuilder.Append('"');
		if (!string.IsNullOrEmpty(mode))
		{
			stringBuilder.Append(" -Mode ");
			stringBuilder.Append(mode);
		}
		if (silent)
		{
			stringBuilder.Append(" -Silent");
		}
		if (!string.IsNullOrEmpty(logFile))
		{
			stringBuilder.Append(" -LogFile \"");
			stringBuilder.Append(logFile);
			stringBuilder.Append('"');
		}
		return stringBuilder.ToString();
	}

	private static ProcessStartInfo BuildEmergencyPowerShell(string mode, string? logFile)
	{
		return new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			Arguments = BuildEmergencyArguments(mode, logFile)
		};
	}

	private static string BuildEmergencyArguments(string mode, string? logFile)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append("-NoProfile -ExecutionPolicy Bypass -File \"");
		stringBuilder.Append(EmergencyScriptPath);
		stringBuilder.Append("\" -Mode ");
		stringBuilder.Append(mode);
		// 应急响应默认静默+跳过二次确认（程序内已确认过），全程隐藏窗口
		stringBuilder.Append(" -Silent -Force");
		if (!string.IsNullOrEmpty(logFile))
		{
			stringBuilder.Append(" -LogFile \"");
			stringBuilder.Append(logFile);
			stringBuilder.Append('"');
		}
		return stringBuilder.ToString();
	}

	private static ProcessStartInfo BuildNetGuardPowerShell(string mode, string? logFile)
	{
		return new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			Arguments = BuildNetGuardArguments(mode, logFile)
		};
	}

	private static string BuildNetGuardArguments(string mode, string? logFile)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append("-NoProfile -ExecutionPolicy Bypass -File \"");
		stringBuilder.Append(NetGuardScriptPath);
		stringBuilder.Append("\" -Mode ");
		stringBuilder.Append(mode);
		if (!string.IsNullOrEmpty(logFile))
		{
			stringBuilder.Append(" -LogFile \"");
			stringBuilder.Append(logFile);
			stringBuilder.Append('"');
		}
		return stringBuilder.ToString();
	}

	private static ProcessStartInfo BuildPowerShell(string mode, string? logFile)
	{
		return new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			Arguments = BuildArguments(mode, logFile)
		};
	}

	private static string BuildArguments(string mode, string? logFile)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append("-NoProfile -ExecutionPolicy Bypass -File \"");
		stringBuilder.Append(ScriptPath);
		stringBuilder.Append("\" -Mode ");
		stringBuilder.Append(mode);
		if (!string.IsNullOrEmpty(logFile))
		{
			stringBuilder.Append(" -LogFile \"");
			stringBuilder.Append(logFile);
			stringBuilder.Append('"');
		}
		return stringBuilder.ToString();
	}

	private static async Task RunDirectAsync(ProcessStartInfo psi, Action<string> onLine, CancellationToken cancellationToken)
	{
		using Process proc = new Process
		{
			StartInfo = psi,
			EnableRaisingEvents = true
		};
		proc.OutputDataReceived += delegate(object _, DataReceivedEventArgs e)
		{
			if (e.Data != null)
			{
				onLine(e.Data);
			}
		};
		proc.ErrorDataReceived += delegate(object _, DataReceivedEventArgs e)
		{
			if (!string.IsNullOrWhiteSpace(e.Data))
			{
				onLine(e.Data);
			}
		};
		proc.Start();
		proc.BeginOutputReadLine();
		proc.BeginErrorReadLine();
		await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static async Task<bool> TailLogFileAsync(string logPath, Action<string> onLine, CancellationToken cancellationToken, string completionMarker = "== 威胁清理完成 ==", string altMarker = "", TimeSpan? maxDuration = null, TimeSpan? idleTimeout = null)
	{
		TimeSpan timeSpan = maxDuration ?? TimeSpan.FromMinutes(15.0);
		TimeSpan timeSpan2 = idleTimeout ?? TimeSpan.FromSeconds(60.0);
		int lastCount = 0;
		bool markerSeen = false;
		bool hasData = false;
		DateTime lastWrite = DateTime.MinValue;
		DateTime startTime = DateTime.Now;
		while (!cancellationToken.IsCancellationRequested)
		{
			if (DateTime.Now - startTime > timeSpan)
			{
				onLine("【超时】执行超过 " + (int)timeSpan.TotalMinutes + " 分钟仍未结束，请重试。");
				return false;
			}
			if (File.Exists(logPath))
			{
				try
				{
					string[] array = File.ReadAllLines(logPath, Encoding.UTF8);
					for (int i = lastCount; i < array.Length; i++)
					{
						if (!string.IsNullOrWhiteSpace(array[i]))
						{
							onLine(array[i]);
						}
					}
					if (array.Length > lastCount)
					{
						hasData = true;
					}
					lastCount = array.Length;
					if (array.Length != 0)
					{
						lastWrite = File.GetLastWriteTime(logPath);
						for (int num = array.Length - 1; num >= Math.Max(0, array.Length - 3); num--)
						{
							if (array[num].Contains(completionMarker) || array[num].Contains("== 威胁清理失败 ==") || (altMarker.Length > 0 && array[num].Contains(altMarker)))
							{
								markerSeen = true;
								break;
							}
						}
						if (markerSeen)
						{
							break;
						}
						goto IL_0220;
					}
					goto IL_0220;
				}
				catch (IOException)
				{
					goto IL_0220;
				}
				catch (UnauthorizedAccessException)
				{
					goto IL_0220;
				}
			}
			goto IL_0220;
			IL_0220:
			if (!File.Exists(logPath) && DateTime.Now - startTime > TimeSpan.FromSeconds(15.0))
			{
				onLine("【异常】清理进程未能产生日志，可能未获得管理员权限或脚本被拦截。");
				return false;
			}
			if (hasData && lastWrite != DateTime.MinValue && DateTime.Now - lastWrite > timeSpan2)
			{
				onLine("【异常】清理进程长时间无输出且未收到完成标记，可能被系统拦截，请重试。");
				return false;
			}
			try
			{
				await Task.Delay(250, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
		return true;
	}
}

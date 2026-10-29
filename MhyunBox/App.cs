#define DEBUG
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MhyunBox.Services;
using MhyunBox.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.Win32;
using WinRT;

namespace MhyunBox;

public partial class App : Application
{
	public const string AppUserModelId = "MhyunBox.MhyunBox";

	private const uint SemFailCriticalErrors = 1u;

	private const uint SemNoGpFaultErrorBox = 2u;

	private const uint SemNoOpenFileErrorBox = 32768u;

	
	
	public static Window? MainWindow { get; private set; }

	
	public App()
	{
		SetErrorMode(32771u);
		try
		{
			LogHelper.DebugEnabled = SettingsManager.Instance.DebugMode;
		}
		catch
		{
		}
		if (!SingleInstance.TryClaim())
		{
			Environment.Exit(0);
			return;
		}
		int value = SetCurrentProcessExplicitAppUserModelID("MhyunBox.MhyunBox");
		LogHelper.Log($"[SMTC诊断] SetCurrentProcessExplicitAppUserModelID({"MhyunBox.MhyunBox"}) => {value}");
		RegisterAppUserModelIdIcon();
		InitializeComponent();
		base.UnhandledException += App_UnhandledException;
		AppDomain.CurrentDomain.UnhandledException += delegate(object sender, System.UnhandledExceptionEventArgs args)
		{
			try
			{
				LogHelper.LogError((args.ExceptionObject as Exception) ?? new Exception("非托管异常"), "未处理异常(进程级)");
			}
			catch
			{
			}
		};
		TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs args)
		{
			args.SetObserved();
			try
			{
				LogHelper.LogError(args.Exception, "未处理异常(后台任务)");
			}
			catch
			{
			}
		};
		WarmFileCache();
	}

	[DllImport("kernel32.dll")]
	private static extern uint SetErrorMode(uint uMode);

	private static void WarmFileCache()
	{
		try
		{
			Task.Run(delegate
			{
				try
				{
					string baseDirectory = AppContext.BaseDirectory;
					List<string> list = new List<string>();
					string[] array = new string[4] { "*.dll", "*.exe", "*.xbf", "*.pri" };
					string[] array2 = array;
					foreach (string searchPattern in array2)
					{
						try
						{
							list.AddRange(Directory.GetFiles(baseDirectory, searchPattern, SearchOption.TopDirectoryOnly));
						}
						catch
						{
						}
					}
					byte[] array3 = new byte[65536];
					foreach (string item in list)
					{
						try
						{
							using FileStream fileStream = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
							while (fileStream.Read(array3, 0, array3.Length) > 0)
							{
							}
						}
						catch
						{
						}
					}
					LogHelper.Log("[自愈] 文件缓存预热完成: " + list.Count + " 个文件");
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "[自愈] 文件缓存预热失败");
				}
			});
		}
		catch
		{
		}
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

	internal static void RegisterAppUserModelIdIcon()
	{
		try
		{
			string text = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
			if (!File.Exists(text))
			{
				return;
			}
			using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Classes\\AppUserModelId\\MhyunBox.MhyunBox");
			registryKey.SetValue("DisplayName", "MhyUnbox", RegistryValueKind.ExpandString);
			registryKey.SetValue("DisplayIcon", text, RegistryValueKind.ExpandString);
			registryKey.SetValue("IconUri", text);
			string processPath = Environment.ProcessPath;
			if (!string.IsNullOrWhiteSpace(processPath))
			{
				registryKey.SetValue("AppPath", processPath, RegistryValueKind.ExpandString);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "注册 AppUserModelId 图标失败");
		}
	}

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		// 全局修复 ComboBox 下拉背景穿透（Acrylic 在部分环境下全透明，选项看不清）
		ThemeHelper.ApplySolidComboPopup();
		if (!CliArgs.Process(Environment.GetCommandLineArgs()))
		{
			MainWindow = new MainWindow();
			MainWindow.Activate();
			ApplyDebugModeOnStartup();
			HandleBootState();
			TryAutoStartCapture();
			TryAutoStartProxy();
			TryAutoStartTools();
			MhyunBox.MainWindow.Instance?.ScheduleGameProcessDetection();
			if (Environment.GetCommandLineArgs().Any((string a) => string.Equals(a, "--hidden", StringComparison.OrdinalIgnoreCase)))
			{
				MhyunBox.MainWindow.Instance?.HideToTrayOnStartup();
			}
		}
	}

	private static void ApplyDebugModeOnStartup()
	{
		try
		{
			ProxyCaptureService.SetCaptureLogEnabled(SettingsManager.Instance.DebugMode);
			LogHelper.Log("[调试模式] 启动时状态=" + (SettingsManager.Instance.DebugMode ? "开（产出程序日志与抓包日志）" : "关（不产出冗余日志，仅保留错误日志）"));
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[调试模式] 启动初始化失败");
		}
	}

	private static void HandleBootState()
	{
		try
		{
			AppSettings instance = SettingsManager.Instance;
			long tickCount = Environment.TickCount64;
			if (instance.AutoStartBootTick64 == 0L || instance.AutoStartBootTick64 != tickCount)
			{
				instance.AutoStartBootTick64 = tickCount;
				instance.AutoStartFired = false;
				instance.GameDetectedThisBoot = false;
				SettingsManager.Save(instance);
				LogHelper.Log("[开机状态] 检测到新的开机会话：自启动工具与游戏进程检测已重置（每次开机仅触发一次）");
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[开机状态] 处理失败");
		}
	}

	private static void TryAutoStartTools()
	{
		AppSettings settings = SettingsManager.Instance;
		if (settings.AutoStartFired)
		{
			LogHelper.Log("[自启] 自启动工具已触发过，本次跳过");
			return;
		}
		settings.AutoStartFired = true;
		SettingsManager.Save(settings);
		Task.Run(async delegate
		{
			try
			{
				await Task.Delay(800);
				List<string> list = new List<string>();
				string text = settings.AutoStartFolder?.Trim() ?? "";
				if (!string.IsNullOrWhiteSpace(text) && Directory.Exists(text))
				{
					list.AddRange(Directory.GetFiles(text, "*.bat"));
					list.AddRange(Directory.GetFiles(text, "*.exe"));
				}
				if (settings.AutoStartNapCatFiles != null)
				{
					foreach (string autoStartNapCatFile in settings.AutoStartNapCatFiles)
					{
						if (!string.IsNullOrWhiteSpace(autoStartNapCatFile) && File.Exists(autoStartNapCatFile))
						{
							list.Add(autoStartNapCatFile);
						}
					}
				}
				int num = 0;
				bool autoStartHidden = settings.AutoStartHidden;
				foreach (string item in list.Distinct())
				{
					try
					{
						string fileName = item;
						string arguments = "";
						bool flag = item.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || item.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
						if (autoStartHidden & flag)
						{
							fileName = "cmd.exe";
							arguments = "/c \"" + item + "\"";
						}
						ProcessStartInfo processStartInfo = new ProcessStartInfo(fileName, arguments)
						{
							WorkingDirectory = Path.GetDirectoryName(item),
							UseShellExecute = true
						};
						if (autoStartHidden)
						{
							processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
							processStartInfo.CreateNoWindow = true;
						}
						Process.Start(processStartInfo);
						num++;
						LogHelper.Log("[自启] 自启动工具已启动: " + item + (autoStartHidden ? "（隐藏窗口）" : ""));
					}
					catch (Exception ex)
					{
						LogHelper.LogError(ex, "[自启] 自启动工具启动失败: " + item);
					}
				}
				LogHelper.Log($"[自启] 自启动工具执行完成，本次启动 {num} 个（后续启动不再触发）");
			}
			catch (Exception ex2)
			{
				Exception ex3 = ex2;
				LogHelper.LogError(ex3, "[自启] 自启动工具执行失败");
			}
		});
	}

	private static void TryAutoStartCapture()
	{
		string capturePresetName = CliArgs.CapturePresetName;
		if (string.IsNullOrWhiteSpace(capturePresetName))
		{
			return;
		}
		if (!ActivationService.IsActivated)
		{
			LogHelper.Log("[CLI] --capture " + capturePresetName + " 未生效：未激活");
			return;
		}
		ServerPreset serverPreset = ServerPresets.Find(capturePresetName);
		if (serverPreset == null || serverPreset.IsPending)
		{
			LogHelper.Log("[CLI] --capture " + capturePresetName + " 未生效：预设不存在");
			return;
		}
		int proxyListenPort = SettingsManager.Instance.ProxyListenPort;
		ProxyCaptureService.TargetFromHandshake = true;
		ProxyCaptureService.StartBatch(new List<ProxyCaptureService.ProxyBinding>
		{
			new ProxyCaptureService.ProxyBinding
			{
				ListenPort = proxyListenPort,
				LoginName = CliArgs.LoginNameOverride,
				AuthAccountIndex = CliArgs.AuthAccountIndex
			}
		}, serverPreset.Host, serverPreset.Port);
		string value = ((CliArgs.AuthAccountIndex >= 0) ? $" (账号 {CliArgs.AuthAccountIndex} 认证@{CliArgs.LoginNameOverride ?? "不变名"})" : " (无认证)");
		LogHelper.Log($"[CLI] 代理已自动启动: 127.0.0.1:{proxyListenPort} -> {serverPreset.Host}:{serverPreset.Port} (预设 {serverPreset.Name}){value}");
	}

	private static void TryAutoStartProxy()
	{
		AppSettings instance = SettingsManager.Instance;
		if (!instance.AutoStartProxyOnStartup || ProxyCaptureService.IsRunning)
		{
			return;
		}
		if (!ActivationService.IsActivated)
		{
			LogHelper.Log("[自启] 程序自启代理未生效：未激活");
			return;
		}
		ProxyCaptureService.TargetFromHandshake = true;
		List<ProxyCaptureService.ProxyBinding> list = ProxyStarter.BuildLastLaunchBindings(out var source, out var summary, out var skippedReasons);
		if (list.Count > 0)
		{
			ServerPreset serverPreset = ServerPresets.Find(list[0].TargetHost);
			ProxyCaptureService.StartBatch(list, list[0].TargetHost ?? "", list[0].TargetPort);
			string text = string.Join("、", list.Select((ProxyCaptureService.ProxyBinding b) => b.ListenPort.ToString()));
			ProxyCaptureService.AutoStartPresetName = serverPreset?.Name ?? "";
			ProxyCaptureService.AutoStartPorts = text;
			LogHelper.Log($"[自启] 按上次启动方式恢复（{source} {summary}）：共 {list.Count} 个角色端口，端口: {text}");
			foreach (string item in skippedReasons)
			{
				LogHelper.Log("[自启] 跳过：" + item);
			}
			if (instance.AutoStartPcl)
			{
				TryAutoLaunchPcl(instance);
			}
			return;
		}
		string text2 = instance.ProxyPresetName?.Trim() ?? "";
		ServerPreset serverPreset2 = (string.IsNullOrWhiteSpace(text2) ? null : ServerPresets.Find(text2));
		if (serverPreset2 == null || serverPreset2.IsPending)
		{
			LogHelper.Log("[自启] 程序自启代理未生效：无上次启动记录，且预设不存在 (" + text2 + ")");
			MhyunBox.MainWindow.Instance?.ShowToast("[自启] 没有上次启动记录可恢复，请先在快速启动或代理启动页手动启动一次代理。");
			return;
		}
		int basePort = ((instance.ProxyListenPort > 0) ? instance.ProxyListenPort : 25565);
		List<ProxyCaptureService.ProxyBinding> list2 = ProxyStarter.BuildSavedBindings(instance, serverPreset2, basePort, out var usedSaved);
		string value;
		if (usedSaved)
		{
			value = "按上次记录启动：共 " + list2.Count + " 个账号/角色入口";
		}
		else
		{
			list2 = ProxyStarter.BuildRoleBindings(serverPreset2, basePort);
			value = "角色流：共 " + list2.Count + " 个账号/角色入口";
		}
		if (list2.Count == 0)
		{
			LogHelper.Log("[自启] 没有可用角色，代理未启动（不回退直连）");
			MhyunBox.MainWindow.Instance?.ShowToast("[自启] 没有可用角色，代理未启动（不回退直连）。请先在「账号绑定」页刷新角色。");
			return;
		}
		ProxyCaptureService.StartBatch(list2, serverPreset2.Host, serverPreset2.Port);
		string text3 = string.Join("、", list2.Select((ProxyCaptureService.ProxyBinding b) => b.ListenPort.ToString()));
		ProxyCaptureService.AutoStartPresetName = serverPreset2.Name;
		ProxyCaptureService.AutoStartPorts = text3;
		if (!ProxyCaptureService.HasEarliestRecord())
		{
			ProxyStarter.RememberStarted(serverPreset2, list2);
		}
		else
		{
			LogHelper.Log("[自启] 已有最早自启记录（" + instance.LastProxyPresetName + "），本次不覆盖其配置与端口");
		}
		LogHelper.Log($"[自启] 代理已自动启动: 预设 {serverPreset2.Name} ({serverPreset2.Host}:{serverPreset2.Port}) {value}，端口: {text3}");
		if (instance.AutoStartPcl)
		{
			TryAutoLaunchPcl(instance);
		}
	}

	private static void TryAutoLaunchPcl(AppSettings settings)
	{
		Task.Run(async delegate
		{
			try
			{
				await Task.Delay(1500);
				string text = (settings.PclPath ?? "").Trim();
				if (string.IsNullOrWhiteSpace(text) || !File.Exists(text))
				{
					LogHelper.Log("[PCL] 自启PCL未生效：未找到启动器 " + text);
				}
				else
				{
					int num = settings.PclJoinPort;
					if (num < 1 || num > 65535)
					{
						int[] activePorts = ProxyCaptureService.ActivePorts;
						num = ((activePorts.Length != 0) ? activePorts[0] : ((settings.ProxyListenPort > 0) ? settings.ProxyListenPort : 25565));
					}
					string text2 = "127.0.0.1:" + num;
					string text3 = PclLauncherService.WriteServerAddress(text, text2, out var written);
					LogHelper.Log("[PCL] 自启直连地址写入: " + text3);
					string item = ModInjectService.Inject(text).Message;
					LogHelper.Log("[模组注入] " + item);
					if (written && PclLauncherService.Launch(text))
					{
						LogHelper.Log("[PCL] 自启PCL已启动: " + text + " 直连 " + text2);
					}
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				LogHelper.LogError(ex2, "[PCL] 自启PCL失败");
			}
		});
	}

	private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
	{
		LogHelper.LogError(e.Exception, "未处理异常(UI线程)");
		e.Handled = true;
		try
		{
			MhyunBox.MainWindow.Instance?.ShowToast("程序发生异常，即将退出，请查看日志");
		}
		catch
		{
		}
		Environment.Exit(1);
	}

	
	
	
	}

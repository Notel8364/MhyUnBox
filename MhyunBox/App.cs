#define DEBUG
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
	// 媒体卡按 AUMID 反查开始菜单快捷方式/注册表；带点的长 AUMID 会被系统误判成包身份
	//（PackageFamilyName 形态）而反查失败，媒体卡左上角就回落成「未知应用」。
	// 用短名（与开始菜单 lnk 文件名一致）最稳。
	public const string AppUserModelId = "MhyUnBox";

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
		// 进程 AUMID 已在 ModuleInitializer 里最早设定（该函数一个进程只能成功一次）。
		// 旧代码在这里又调了一次反编译遗留的 SetCurrentProcessExplicitAppUserModelID，
		// 既用了未初始化的默认值，也必然二次调用失败。这里只做后续登记与回读校验。
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

	internal static void RegisterAppUserModelIdIcon()
	{
		AppIdentity.Apply();
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
			MarketTokenService.StartBackgroundSync();
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

	/// <summary>
	/// SMTC 媒体卡左上角「应用名 + 图标」的身份链（依据 SMTC 媒体卡精修文档）。
	/// 非打包 Win32 程序默认没有显式 AUMID，媒体卡会显示「未知应用」。
	/// 现在走：基础开始菜单 .lnk（AppID 与短名对齐）+ 进程 AUMID 固定用短名 MhyUnBox，
	/// 注册表 HKCU\Software\Classes\AppUserModelId\MhyUnBox 配套登记 DisplayName/IconUri。
	/// 不再给 .lnk 写 AppUserModel.ID 属性（本机写了反而让 AppsFolder 剔除条目）。
	/// AUMID 必须在 ModuleInitializer 里、任何 COM 初始化之前设置，且整个进程只设一次。
	/// </summary>
	internal static class AppIdentity
	{
		public const string Aumid = "MhyUnBox";

		public const string DisplayName = "MhyUnBox";

	/// <summary>
	/// 进程实际使用的 AUMID：固定短名 MhyUnBox，与开始菜单条目 AppID、注册表键三者一致。
	/// 由 ModuleInitializer → SetAumidEarly() 在进程最早期设定，之后只读；
	/// 不要用 exe 全路径当 AUMID，本机开始菜单条目并不以路径作为 AppID。
	/// </summary>
	public static string ResolvedAumid { get; private set; } = Aumid;

	/// <summary>System.AppUserModel.* 属性键的 FMTID（propkey.h: PKEY_AppUserModel_ID）。
	/// 旧值 9F4C2855-9F98-11D0-A54F-0014A34736F9 是错的，写进 .lnk 的是一条系统不认的属性，
	/// 读回「成功」但开始菜单条目 AppID 仍是 exe 路径，媒体卡因此显示「未知应用」。</summary>
	private static readonly Guid AppUserModelFmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

	private static readonly object Sync = new object();

	private static bool _applied;

	private static bool _aumidSet;

	private static int _aumidHr = int.MinValue;

	private static string? _aumidEarlyError;

	/// <summary>快捷方式身份版本：5 = 只建基础 .lnk（不写 AppUserModel.ID 属性）。
	/// 进程 AUMID 固定用短名 MhyUnBox，与开始菜单 AppID、注册表键三者对齐。</summary>
	private const int IdentityRev = 5;

		/// <summary>版本号存在程序自己的键里，不往 shell 的 AppUserModelId 命名空间塞私有值。</summary>
		private const string IdentityStoreKey = "Software\\MhyUnBox";

		/// <summary>本次启动时注册表里记下的上次完成版本，用来决定要不要补写快捷方式属性。</summary>
		private static int _identityRevAtStartup;

		// shell32 导出的这两个函数没有 A/W 后缀，必须精确绑定；
		// 否则 CharSet.Unicode 会先找 ...W 变体，找不到就抛 EntryPointNotFoundException。
		[DllImport("shell32.dll", EntryPoint = "SetCurrentProcessExplicitAppUserModelID", CharSet = CharSet.Unicode, ExactSpelling = true)]
		private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

		[DllImport("shell32.dll", EntryPoint = "GetCurrentProcessExplicitAppUserModelID", CharSet = CharSet.Unicode, ExactSpelling = true)]
		private static extern int GetCurrentProcessExplicitAppUserModelID(out nint appId);

		[DllImport("ole32.dll")]
		private static extern void CoTaskMemFree(nint ptr);

		/// <summary>
		/// 整个进程**只做这一件事**：设 AUMID。必须在 ModuleInitializer 里、任何 COM / 窗口 /
		/// 注册表操作之前完成。SetCurrentProcessExplicitAppUserModelID 一个进程只能成功一次，
		/// 且 COM 一旦初始化就会失败；旧版 Apply() 先跑 WScript.Shell 建 .lnk 再设置，
		/// 结果设置失败、进程回落到默认 AUMID「MhyunBox.exe」，媒体卡显示「未知应用」。
		/// </summary>
		internal static void SetAumidEarly()
		{
			lock (Sync)
			{
				if (_aumidSet)
				{
					return;
				}
				_aumidSet = true;
				ResolvedAumid = Aumid;
				try
				{
					_aumidHr = SetCurrentProcessExplicitAppUserModelID(ResolvedAumid);
				}
				catch (Exception ex)
				{
					_aumidHr = int.MinValue;
					_aumidEarlyError = ex.GetType().Name + ": " + ex.Message;
				}
			}
		}

		internal static void Apply()
		{
			lock (Sync)
			{
				if (_applied)
				{
					return;
				}
				_applied = true;
				// 宿主 guard 没放行时（例如被以别的宿主名加载），这里兜底再设一次。
				SetAumidEarly();
				// App 构造阶段 DebugEnabled 已就绪，回读结果能落盘，排障不再靠猜。
				try
				{
					string? actual = TryReadCurrentAumid();
					LogHelper.Log($"[AUMID] 目标=[{ResolvedAumid}] hr=0x{_aumidHr:X8} 回读=[{actual ?? "读取失败"}] earlyError=[{_aumidEarlyError ?? ""}]");
					if (!string.Equals(actual, ResolvedAumid, StringComparison.Ordinal))
					{
						int retry = SetCurrentProcessExplicitAppUserModelID(ResolvedAumid);
						string? actual2 = TryReadCurrentAumid();
						LogHelper.Log($"[AUMID] 补救设置 hr=0x{retry:X8} 回读=[{actual2 ?? "读取失败"}]");
					}
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "[AUMID] 回读校验失败");
				}
				// 后续登记彼此隔离：.lnk 的 COM、注册表、shell 通知任何一步抛异常，
				// 都绝不能影响已经设好的进程 AUMID。
				try
				{
					LoadIdentityRev();
					EnsureStartMenuShortcut();
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "[AUMID] 开始菜单登记失败");
				}
				try
				{
					RegisterRegistryIdentity(Aumid);
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "[AUMID] 注册表登记失败");
				}
			}
		}

		private static string? TryReadCurrentAumid()
		{
			nint ptr = IntPtr.Zero;
			try
			{
				int hr = GetCurrentProcessExplicitAppUserModelID(out ptr);
				return (hr == 0 && ptr != IntPtr.Zero) ? Marshal.PtrToStringUni(ptr) : null;
			}
			catch
			{
				return null;
			}
			finally
			{
				if (ptr != IntPtr.Zero)
				{
					CoTaskMemFree(ptr);
				}
			}
		}

		private static void LoadIdentityRev()
		{
			try
			{
				using RegistryKey state = Registry.CurrentUser.CreateSubKey(IdentityStoreKey);
				_identityRevAtStartup = state.GetValue("IdentityRev") is int i ? i : 0;
			}
			catch
			{
				_identityRevAtStartup = 0;
			}
		}

		private static void RegisterRegistryIdentity(string aumid)
		{
			try
			{
				string exe = Environment.ProcessPath ?? "";
				string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
				// 清掉上一版带点的长 AUMID（MhyunBox.MhyunBox）：媒体卡会把它当包身份反查失败，
				// 旧键留着只会让 shell 继续命中「未知应用」缓存。
				try
				{
					Registry.CurrentUser.DeleteSubKeyTree("Software\\Classes\\AppUserModelId\\MhyunBox.MhyunBox", throwOnMissingSubKey: false);
				}
				catch
				{
				}
				using RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\Classes\\AppUserModelId\\" + aumid);
				// DisplayName 必须是 REG_SZ（纯字符串）：REG_EXPAND_SZ 在部分系统上会被
				// SHLoadIndirectString 判为无效，媒体卡同样回落「未知应用」。
				key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
				// 关键：IconUri 必须指向 apphost exe（图标资源编译进 exe）；
				// 指向 Assets\app.ico 或托管 DLL 时系统抽不到图标，媒体卡左上角图标会挂空。
				if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
				{
					key.SetValue("IconUri", exe, RegistryValueKind.String);
					key.SetValue("AppPath", exe, RegistryValueKind.String);
				}
				if (File.Exists(icon))
				{
					key.SetValue("DisplayIcon", icon, RegistryValueKind.String);
				}
				LogHelper.Log("[AUMID] 注册表身份已登记: " + aumid + " / " + DisplayName + " / IconUri=" + exe);
				// 通知 shell 立即刷新应用身份缓存，避免媒体卡继续显示「未知应用」。
				try
				{
					SHChangeNotify(0x08000000u, 0u, IntPtr.Zero, IntPtr.Zero);
				}
				catch
				{
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[AUMID] 注册身份失败");
			}
		}

		[DllImport("shell32.dll")]
		private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

		/// <summary>建/维护开始菜单的基础 .lnk（目标指向当前 exe，图标取 exe 内嵌图标）。
		/// 不写任何 AppUserModel.* 属性：本机写入后 AppsFolder 会剔除该条目。</summary>
		private static void EnsureStartMenuShortcut()
		{
			try
			{
				string exe = Environment.ProcessPath ?? "";
				if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
				{
					return;
				}
				string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs");
				string lnk = Path.Combine(programs, DisplayName + ".lnk");
				// 老版本往 .lnk 里塞过错误 FMTID 的属性、以及要求间接字符串的 Relaunch* 值，
				// 属性库里清不干净：升级时直接重建一次 .lnk，得到干净的属性包。
				if (_identityRevAtStartup < IdentityRev && File.Exists(lnk))
				{
					try
					{
						File.Delete(lnk);
						LogHelper.Log("[AUMID] 身份属性升级，重建开始菜单快捷方式");
					}
					catch
					{
					}
				}
				// 幂等：文件存在且目标指向当前 exe 就不重建，避免反复刷开始菜单索引。
				bool exists = File.Exists(lnk);
				string? target = exists ? ReadShortcutTarget(lnk) : null;
				bool rebuilt = false;
				if (!exists || !string.Equals(target, exe, StringComparison.OrdinalIgnoreCase))
				{
					Directory.CreateDirectory(programs);
					Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
					object? shell = (shellType == null) ? null : Activator.CreateInstance(shellType);
					if (shellType == null || shell == null)
					{
						return;
					}
					object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
					Type t = sc.GetType();
					t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
					t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(exe) ?? "" });
					t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exe + ",0" });
					t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { DisplayName });
					t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
					rebuilt = true;
					LogHelper.Log("[AUMID] 开始菜单快捷方式已创建: " + lnk);
				}
				// 新建/升级后通知 shell 刷新开始菜单索引，并记下版本，避免每次启动重写。
				if (rebuilt || _identityRevAtStartup < IdentityRev)
				{
					NotifyShellShortcutUpdated(lnk);
					MarkIdentityRev();
				}
			}
			catch (Exception ex)
			{
				LogHelper.Log("[AUMID] 开始菜单快捷方式降级: " + ex.Message);
			}
		}

		/// <summary>标记「快捷方式身份属性已写完」，避免每次启动都重写注册表与刷新 shell。</summary>
		private static void MarkIdentityRev()
		{
			try
			{
				using RegistryKey key = Registry.CurrentUser.CreateSubKey(IdentityStoreKey);
				key.SetValue("IdentityRev", IdentityRev, RegistryValueKind.DWord);
			}
			catch
			{
			}
		}

		private const uint SHCNE_CREATE = 0x00000002u;

		private const uint SHCNE_UPDATEDIR = 0x00001000u;

		private const uint SHCNE_UPDATEITEM = 0x00002000u;

		private const uint SHCNF_PATHW = 0x0005u;

		[DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHChangeNotify")]
		private static extern void SHChangeNotifyPath(uint wEventId, uint uFlags, string dwItem1, string? dwItem2);

		/// <summary>
		/// 按路径通知 shell：新建/更新 .lnk 后必须发这几条，开始菜单应用列表（AppsFolder）才会收录。
		/// 之前只发了 SHCNE_ASSOCCHANGED（文件关联变了），它不触发应用列表刷新，
		/// 于是媒体卡的非当前会话卡片查不到应用项 → 显示「未知应用」。
		/// </summary>
		internal static void NotifyShellShortcutUpdated(string lnk)
		{
			try
			{
				SHChangeNotifyPath(SHCNE_CREATE, SHCNF_PATHW, lnk, null);
				SHChangeNotifyPath(SHCNE_UPDATEITEM, SHCNF_PATHW, lnk, null);
				string? dir = Path.GetDirectoryName(lnk);
				if (!string.IsNullOrWhiteSpace(dir))
				{
					SHChangeNotifyPath(SHCNE_UPDATEDIR, SHCNF_PATHW, dir!, null);
				}
				LogHelper.Log("[AUMID] 已通知 shell 刷新开始菜单应用项: " + Path.GetFileName(lnk));
			}
			catch (Exception ex)
			{
				LogHelper.DebugError(ex, "[AUMID] 通知 shell 刷新失败");
			}
		}

		private static string? ReadShortcutTarget(string lnk)
		{
			try
			{
				Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
				object? shell = (shellType == null) ? null : Activator.CreateInstance(shellType);
				if (shellType == null || shell == null)
				{
					return null;
				}
				object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
				return sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
			}
			catch
			{
				return null;
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct PROPERTYKEY
		{
			public Guid fmtid;

			public int pid;
		}

		// 原生 PROPVARIANT 在 x64 上是 24 字节（2 字节 vt + 6 字节保留 + 16 字节联合体）。
		// 显式声明 Size 必须给足：按字段算只有 16 字节，GetValue 输出时原生会写 24 字节，
		// 直接踩坏栈 —— 表现为启动即 AccessViolationException。
		[StructLayout(LayoutKind.Explicit, Size = 24)]
		private struct PROPVARIANT
		{
			[FieldOffset(0)]
			public ushort vt;

			[FieldOffset(8)]
			public IntPtr pwszVal;
		}

		[ComImport]
		[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IPropertyStore
		{
			void GetCount(out uint count);

			void GetAt(uint index, out PROPERTYKEY key);

			void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

			void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

			void Commit();
		}

		[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
		private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr pbc, uint flags, ref Guid riid, out IPropertyStore store);

		/// <summary>
		/// 【保留但不调用】写 System.AppUserModel.ID 的实验路径。
		/// 本机实测：写了之后开始菜单 AppsFolder 反而查不到 MhyUnBox 条目，
		/// 媒体卡还是「未知应用」；基础 .lnk + exe 路径 AUMID 才是这台机器上
		/// 稳定生效的链。留着这段代码方便以后在属性系统正常的机器上做对照验证。
		/// </summary>
		private static bool WriteShortcutAumid(string lnk)
		{
			try
			{
				Guid iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
				int hr = SHGetPropertyStoreFromParsingName(lnk, IntPtr.Zero, 2u, ref iid, out IPropertyStore store);
				if (hr != 0 || store == null)
				{
					LogHelper.Log("[AUMID] 属性系统不可用，媒体卡应用名走注册表链");
					return false;
				}
				Guid fmtid = AppUserModelFmtid;
				// 只写 System.AppUserModel.ID 这一条：媒体卡按「开始菜单里 ID 与进程
				// AUMID 匹配的 .lnk」解析应用名/图标，快捷方式本身的名字与图标就够了。
				// 不写 Relaunch* 三件套——它们要求 @"dll,-res" 形态的间接字符串，
				// 塞普通文本会让 shell 解析失败、开始菜单条目被剔除，反而更糟。
				SetShortcutString(store, fmtid, 5, Aumid);
				store.Commit();
				// 回读一遍再记日志：属性系统对这些键偶尔会「接受写入但丢弃」（权限/规范化），
				// 只有回读到值才算真的生效，否则下次启动还要再补。
				string id = ReadShortcutString(store, fmtid, 5);
				LogHelper.Log($"[AUMID] 快捷方式属性回读: System.AppUserModel.ID=[{id}]");
				return string.Equals(id, Aumid, StringComparison.Ordinal);
			}
			catch (Exception ex)
			{
				LogHelper.Log("[AUMID] 快捷方式属性写入降级（进程改用 exe 路径 AUMID）: " + ex.Message);
				return false;
			}
		}

		/// <summary>只读打开 .lnk 的属性库，读回 System.AppUserModel.ID。
		/// 读不到就说明属性系统不可用 / 属性没生效，调用方据此降级。</summary>
		private static string? ReadShortcutAumid(string lnk)
		{
			try
			{
				Guid iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
				int hr = SHGetPropertyStoreFromParsingName(lnk, IntPtr.Zero, 0u, ref iid, out IPropertyStore store);
				if (hr != 0 || store == null)
				{
					return null;
				}
				string? id = ReadShortcutString(store, AppUserModelFmtid, 5);
				return string.IsNullOrWhiteSpace(id) ? null : id;
			}
			catch (Exception ex)
			{
				LogHelper.Log("[AUMID] 快捷方式属性只读回退: " + ex.Message);
				return null;
			}
		}

		/// <summary>写一条 VT_LPWSTR 属性到快捷方式属性库（值由系统复制，写完即可释放）。</summary>
		private static void SetShortcutString(IPropertyStore store, Guid fmtid, int pid, string value)
		{
			IntPtr mem = Marshal.StringToHGlobalUni(value ?? "");
			try
			{
				PROPERTYKEY key = new PROPERTYKEY
				{
					fmtid = fmtid,
					pid = pid
				};
				PROPVARIANT pv = new PROPVARIANT
				{
					vt = 31,
					pwszVal = mem
				};
				store.SetValue(ref key, ref pv);
			}
			finally
			{
				Marshal.FreeHGlobal(mem);
			}
		}

		[DllImport("ole32.dll")]
		private static extern int PropVariantClear(ref PROPVARIANT pv);

		/// <summary>回读一条字符串属性（失败/为空返回空串），用于确认写入是否真的生效。</summary>
		private static string ReadShortcutString(IPropertyStore store, Guid fmtid, int pid)
		{
			PROPVARIANT pv = default(PROPVARIANT);
			try
			{
				PROPERTYKEY key = new PROPERTYKEY
				{
					fmtid = fmtid,
					pid = pid
				};
				store.GetValue(ref key, out pv);
				if (pv.vt != 31 || pv.pwszVal == IntPtr.Zero)
				{
					return "";
				}
				return Marshal.PtrToStringUni(pv.pwszVal) ?? "";
			}
			catch
			{
				return "";
			}
			finally
			{
				try
				{
					PropVariantClear(ref pv);
				}
				catch
				{
				}
			}
		}
	}

	/// <summary>
	/// 时序命门：SMTC 会话建立时拍下的是「当时」的进程 AUMID。
	/// 用 ModuleInitializer 在 Main / App 构造之前登记，避免窗口或托盘抢跑。
	/// </summary>
	internal static class AppIdentityBootstrap
	{
		[ModuleInitializer]
		internal static void Initialize()
		{
			try
			{
				// 只认主程序宿主：诊断工具引用 MhyunBox.dll 时若也跑这段，
				// 会把 .lnk 和注册表身份改成工具自己的 exe，媒体卡立刻错乱。
				string host = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
				if (string.Equals(host, "MhyunBox", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(host, "MhyUnBox", StringComparison.OrdinalIgnoreCase))
				{
					// 这里**只允许**调用 SetAumidEarly：它只做一次
					// SetCurrentProcessExplicitAppUserModelID，绝不碰 COM / 注册表 / 文件。
					// 一旦先初始化了 WScript.Shell 等 COM 组件，AUMID 设置就会失败，
					// 进程回落到默认身份「MhyunBox.exe」，媒体卡显示「未知应用」。
					// 后续 .lnk / 注册表登记由 App 构造函数里的 AppIdentity.Apply() 完成。
					AppIdentity.SetAumidEarly();
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[AUMID] 模块初始化身份登记失败");
			}
		}
	}

	
	
	
	}

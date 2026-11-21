using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MhyunBox.Services;
using MhyunBox.Services.BjdProxy;
using MhyunBox.Settings;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Win32;
using WinRT;
using WinRT.Interop;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace MhyunBox.Pages;

public sealed partial class SettingsPage : Page
{
	private static readonly string[] BackgroundExtensions = new string[10] { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".mp4", ".mkv", ".mov", ".wmv", ".webm" };

	private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	private const string RunValueName = "MhyunBox";

	private readonly List<string> _backgroundPaths = new List<string>();

	private bool _loading;

	private Storyboard? _listSizeSb;

	private string? _listSignature;

	private bool _reordering;

	private DispatcherQueueTimer? _fontRestartTimer;

	private bool _fontRestartPromptShown;

	private int _reorderIndex = -1;

	private bool _reorderPointerDown;

	private Point _reorderStartPos;

	private ScrollViewer? _listScrollViewer;


	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel SettingsRoot;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Grid SettingsGrid;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel ThemeGroup;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel BackgroundGroup;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel AppearanceGroup;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel WindowGroup;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel StartupGroup;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel SkinGroup;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel MusicGroup;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel Mp4Group;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Grid MediaPanel;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ListView MusicListBox;


	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock Mp4FolderHint;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBox Mp4FolderBox;



	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock MusicFolderHint;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBox MusicFolderBox;



	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ComboBox PlaybackModeCombo;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ToggleSwitch AutoPlaySwitch;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ToggleSwitch AutoStartSwitch;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ToggleSwitch AutoStartProxySwitch;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ToggleSwitch DetectGameSwitch;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ToggleSwitch ShowTrayIconSwitch;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ToggleSwitch MinimizeToTraySwitch;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ToggleSwitch CloseKeepsRunningSwitch;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ToggleSwitch AutoStartHiddenWindowSwitch;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ToggleSwitch FontBoldSwitch;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Slider FontSizeSlider;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock FontSizeValueText;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Slider OpacitySlider;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock OpacityValueText;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ComboBox BackgroundCombo;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock BackgroundPathHint;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBox CustomPathBox;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button BrowseButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ComboBox ThemeCombo;


	private TextBlock? _versionFooterText;

	private ToggleSwitch? _settingsDebugSwitch;

	private ToggleSwitch? _simpleModeSwitch;

	/// <summary>简易模式收缩栏（开关 + 导航项勾选列表）。</summary>
	private Expander? _simpleModeExpander;

	/// <summary>简易模式导航项勾选框（Tag = 页面 key）。</summary>
	private readonly List<CheckBox> _simplePageChecks = new List<CheckBox>();

	/// <summary>程序化刷新勾选框时的防递归保护。</summary>
	private bool _simplePageChecksLoading;

	/// <summary>简易模式可勾选隐藏的导航项（key = MainWindow 页面 Tag；SettingsPage 为特殊项，5 秒后自动复原）。</summary>
	private static readonly (string Key, string Label)[] SimpleModeCheckablePages = new (string, string)[9]
	{
		("HomePage", "首页"),
		("ServerListPage", "服务器列表"),
		("InstancesPage", "会话多开"),
		("ClientLaunchPage", "启动客户端"),
		("SpeedTestPage", "网速测试"),
		("ShopPage", "商城"),
		("ClientUpdatePage", "更新客户端"),
		("SessionLogPage", "日志"),
		("SettingsPage", "设置")
	};

	private Button? _exportLogButton;

	private NumberBox? _settingsBackupDaysBox;

	/// <summary>最近已知的播放模式，用于跨入口同步播放模式下拉框。</summary>
	private string _lastKnownPlaybackMode = "";

	private static string BackgroundsFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds");

	public SettingsPage()
	{
		_loading = true;
		InitializeComponent();
		// XAML 定义的下拉也强制不透明弹层（主题模式/背景/播放模式）
		ThemeHelper.SolidComboPopup(ThemeCombo);
		ThemeHelper.SolidComboPopup(BackgroundCombo);
		ThemeHelper.SolidComboPopup(PlaybackModeCombo);
		MusicService.InvalidateScanCache();
		SettingsManager.Saved += OnSettingsSaved;
		base.Unloaded += delegate
		{
			MusicService.StateChanged -= OnMusicStateChanged;
			SettingsManager.Saved -= OnSettingsSaved;
			MainWindow.SimpleModeHiddenChanged -= OnSimpleModeHiddenChanged;
		};
		base.Loaded += delegate
		{
			// 页面是缓存复用的，Unloaded 会退订；重新进入时先退订再订阅，避免重复订阅或漏订阅。
			SettingsManager.Saved -= OnSettingsSaved;
			SettingsManager.Saved += OnSettingsSaved;
			MainWindow.SimpleModeHiddenChanged -= OnSimpleModeHiddenChanged;
			MainWindow.SimpleModeHiddenChanged += OnSimpleModeHiddenChanged;
			SyncSimpleModeCheckboxes();
			OnLoaded();
			RelayoutSettings(base.ActualWidth);
			EnsureVersionFooter();
		};
	}

	private void EnsureVersionFooter()
	{
		try
		{
			if (!(_versionFooterText != null))
			{
				// —— 简易模式：收缩栏（开关 + 导航项勾选列表），挂在「界面」分区「字体加粗」下方 ——
				_simpleModeSwitch = new ToggleSwitch
				{
					Header = "开启简易模式",
					OnContent = "开",
					OffContent = "关",
					IsOn = SettingsManager.Instance.SimpleMode,
					Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
				};
				ToolTipService.SetToolTip(_simpleModeSwitch, "开启后按下方勾选隐藏左侧导航项，并自动关闭调试模式。");
				_simpleModeSwitch.Toggled += SimpleModeSwitch_Toggled;

				// 2026-11-20 收缩版二改：开关在最左，勾选项在其右侧两行四列往右排
				Grid checksGrid = new Grid
				{
					ColumnSpacing = 4.0,
					RowSpacing = 0.0,
					VerticalAlignment = VerticalAlignment.Center
				};
				for (int c = 0; c < 4; c++)
				{
					checksGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				}
				for (int r = 0; r < (SimpleModeCheckablePages.Length + 3) / 4; r++)
				{
					checksGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				}
				for (int i = 0; i < SimpleModeCheckablePages.Length; i++)
				{
					(string Key, string Label) item = SimpleModeCheckablePages[i];
					CheckBox check = new CheckBox
					{
						Content = item.Label,
						Tag = item.Key,
						IsChecked = SettingsManager.Instance.SimpleHiddenPages.Contains(item.Key),
						MinWidth = 0.0,
						Padding = new Thickness(4.0, 0.0, 4.0, 0.0)
					};
					ToolTipService.SetToolTip(check, item.Key == "SettingsPage" ? "勾选后简易模式隐藏设置入口，5 秒后自动复原。" : "简易模式下隐藏该导航页。");
					check.Checked += SimplePageCheck_Changed;
					check.Unchecked += SimplePageCheck_Changed;
					_simplePageChecks.Add(check);
					Grid.SetRow(check, i / 4);
					Grid.SetColumn(check, i % 4);
					checksGrid.Children.Add(check);
				}

				// 横排容器：左 = 开关，右 = 两行四列勾选
				Grid simpleRow = new Grid { ColumnSpacing = 16.0, Margin = new Thickness(4.0, 2.0, 0.0, 6.0) };
				simpleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				simpleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				_simpleModeSwitch.Margin = new Thickness(0.0, 0.0, 0.0, 0.0);
				_simpleModeSwitch.VerticalAlignment = VerticalAlignment.Center;
				Grid.SetColumn(_simpleModeSwitch, 0);
				Grid.SetColumn(checksGrid, 1);
				simpleRow.Children.Add(_simpleModeSwitch);
				simpleRow.Children.Add(checksGrid);
				_simpleModeExpander = new Expander
				{
					Header = "简易模式",
					Content = simpleRow,
					IsExpanded = false,
					HorizontalAlignment = HorizontalAlignment.Stretch,
					Margin = new Thickness(0.0, 10.0, 0.0, 0.0)
				};
				Panel? boldHost = FontBoldSwitch?.Parent as Panel;
				if (boldHost != null && FontBoldSwitch != null)
				{
					int boldIndex = boldHost.Children.IndexOf(FontBoldSwitch);
					boldHost.Children.Insert(boldIndex + 1, _simpleModeExpander);
				}
				else
				{
					SettingsRoot.Children.Add(_simpleModeExpander);
				}
				// 2026-11-20 底部排版定版（自上而下）：
				// 冗余备份保留天数 → 调试模式 → 一键导出问题日志 → 当前版本（最底部）
				// （代理流模式已移回账号绑定页；布吉岛 payload 日志区块已移除；「更新后自动清理」改为常开，不再提供开关）
				// 「更新后自动清理」已改为常开（设置里强制 true），只保留保留天数输入框
				_settingsBackupDaysBox = new NumberBox
				{
					Header = "冗余备份保留天数（超期移入回收站，0 = 永不清理）",
					Value = SettingsManager.Instance.RedundantBackupKeepDays,
					Minimum = 0.0,
					Maximum = 365.0,
					SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
					Margin = new Thickness(0.0, 10.0, 0.0, 0.0)
				};
				_settingsBackupDaysBox.ValueChanged += SettingsBackupDays_ValueChanged;
				SettingsRoot.Children.Add(_settingsBackupDaysBox);
				_settingsDebugSwitch = new ToggleSwitch
				{
					Header = "调试模式",
					OnContent = "开",
					OffContent = "关",
					IsOn = SettingsManager.Instance.DebugMode,
					Margin = new Thickness(0.0, 10.0, 0.0, 0.0)
				};
				_settingsDebugSwitch.Toggled += SettingsDebugSwitch_Toggled;
				SettingsRoot.Children.Add(_settingsDebugSwitch);
				// 一键导出问题日志：随调试模式启用（调试模式关 = 按钮禁用淡色）
				_exportLogButton = new Button
				{
					Content = "一键导出问题日志（到桌面）",
					Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
					IsEnabled = SettingsManager.Instance.DebugMode,
					Opacity = (SettingsManager.Instance.DebugMode ? 1.0 : 0.4)
				};
				ToolTipService.SetToolTip(_exportLogButton, "把最近 3 天日志（含布吉岛代理 payload 日志）打包成 zip 保存到桌面，反馈问题时直接发送。需先开启调试模式。");
				_exportLogButton.Click += ExportLog_Click;
				SettingsRoot.Children.Add(_exportLogButton);
				// 当前版本：最底部
				_versionFooterText = new TextBlock
				{
					Text = "当前版本 " + AppVersion.Display,
					FontSize = 12.0,
					Opacity = 0.75,
					Margin = new Thickness(0.0, 18.0, 0.0, 0.0)
				};
				SettingsRoot.Children.Add(_versionFooterText);
				// 用户定版（2026-11-01）：设置页底部「本机锁（程序账号）」区块移除，
				// 本机锁机制（程序账号/解锁弹窗/验证通道）已整体移除，启动不再要求解锁
				AppVersion.ApplyTo(SettingsRoot);
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[设置页] 添加版本信息/调试开关失败");
		}
	}

	/// <summary>统一的代理模式写入口（设置页 / 首页 / 代理启动 / 快速启动 / 账号绑定共用）。</summary>
	internal static void ApplyProxyMode(string mode)
	{
		try
		{
			AppSettings instance = SettingsManager.Instance;
			if (string.Equals(instance.ProxyMode, mode, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			instance.ProxyMode = mode;
			SettingsManager.Save(instance);
			LogHelper.Log("[代理模式] 切换为 " + mode + "（对新连接立即生效）");
			MainWindow.Instance?.ShowToast(mode == "ddos"
				? "已切换到防DDOS模式（节点轮换 / 预设 IP 池 / 健康探测）"
				: "已切换到正常代理登录模式（单目标直连，无风控限制）");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[代理模式] 切换失败");
		}
	}

	/// <summary>
	/// 构建可复用的「代理流模式」开关（设置页 / 首页 / 代理启动 / 快速启动 / 账号绑定共用）。
	/// 选项格式与「调试模式」一致：一个 ToggleSwitch，关 = 正常代理登录，开 = 防DDOS模式。
	/// withHeader=false 时不带「代理流模式」标题（账号绑定页样式，状态文字直接显示在开关右侧）。
	/// </summary>
	public static ToggleSwitch CreateProxyModeSelector(bool withHeader = true)
	{
		ToggleSwitch toggle = new ToggleSwitch
		{
			Header = withHeader ? "代理流模式" : null,
			OffContent = "正常代理登录",
			OnContent = "防DDOS模式",
			IsOn = string.Equals(SettingsManager.Instance.ProxyMode, "ddos", StringComparison.OrdinalIgnoreCase),
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		// 先设状态再订阅，避免初始化时误触发一次写入
		toggle.Toggled += delegate
		{
			ApplyProxyMode(toggle.IsOn ? "ddos" : "normal");
		};
		// 页面可能常驻内存：每次显示时按当前设置回填，避免在别的页面切换后这里显示旧状态。
		// 程序化改 IsOn 也会触发 Toggled，但 ApplyProxyMode 对同值直接返回，不会重复写盘。
		toggle.Loaded += delegate
		{
			bool on = string.Equals(SettingsManager.Instance.ProxyMode, "ddos", StringComparison.OrdinalIgnoreCase);
			if (toggle.IsOn != on)
			{
				toggle.IsOn = on;
			}
		};
		return toggle;
	}

	/// <summary>简易模式：写回设置并即时生效。开启时强制关闭调试模式（并同步调试开关 UI 与日志清理逻辑）。</summary>
	private void SimpleModeSwitch_Toggled(object sender, RoutedEventArgs e)
	{
		if (_loading || _simpleModeSwitch == null)
		{
			return;
		}
		try
		{
			AppSettings instance = SettingsManager.Instance;
			instance.SimpleMode = _simpleModeSwitch.IsOn;
			if (instance.SimpleMode && instance.DebugMode)
			{
				// 简易模式同时关掉调试模式
				instance.DebugMode = false;
				ProxyCaptureService.SetCaptureLogEnabled(false);
				LogHelper.ClearLogs();
				SessionLogStore.Clear();
				SessionLogPage.SyncDebugMode();
				try
				{
					BjdPayloadLog.SetEnabled(false);
				}
				catch
				{
				}
			}
			SettingsManager.Save(instance);
			if (_settingsDebugSwitch != null && _settingsDebugSwitch.IsOn != instance.DebugMode)
			{
				_settingsDebugSwitch.IsOn = instance.DebugMode;
			}
			if (_exportLogButton != null)
			{
				_exportLogButton.IsEnabled = instance.DebugMode;
				_exportLogButton.Opacity = (instance.DebugMode ? 1.0 : 0.4);
			}
			MainWindow.Instance?.ApplySimpleModeToNav();
			MainWindow.Instance?.ShowToast(instance.SimpleMode ? "已开启简易模式：进阶导航页已隐藏，调试模式已关闭" : "已关闭简易模式：完整导航已恢复");
		}
		catch (Exception exSimple)
		{
			LogHelper.LogError(exSimple, "[设置页] 切换简易模式失败");
		}
	}

	/// <summary>简易模式导航项勾选变化：写回 SimpleHiddenPages 并即时应用到导航。</summary>
	private void SimplePageCheck_Changed(object sender, RoutedEventArgs e)
	{
		if (_loading || _simplePageChecksLoading)
		{
			return;
		}
		try
		{
			if (sender is not CheckBox check || check.Tag is not string key)
			{
				return;
			}
			AppSettings instance = SettingsManager.Instance;
			instance.SimpleHiddenPages ??= new List<string>();
			if (check.IsChecked == true)
			{
				if (!instance.SimpleHiddenPages.Contains(key))
				{
					instance.SimpleHiddenPages.Add(key);
				}
			}
			else
			{
				instance.SimpleHiddenPages.Remove(key);
			}
			SettingsManager.Save(instance);
			MainWindow.Instance?.ApplySimpleModeToNav();
			LogHelper.Log("[简易模式] 隐藏导航项「" + key + "」：" + (check.IsChecked == true ? "勾选（简易模式隐藏）" : "取消勾选"));
		}
		catch (Exception exCheck)
		{
			LogHelper.LogError(exCheck, "[设置页] 更新简易模式隐藏导航项失败");
		}
	}

	/// <summary>按当前设置刷新勾选框（页面 Loaded / 设置入口 5 秒自动复原后调用）。</summary>
	private void SyncSimpleModeCheckboxes()
	{
		if (_simplePageChecks.Count == 0)
		{
			return;
		}
		_simplePageChecksLoading = true;
		try
		{
			List<string> hiddenPages = SettingsManager.Instance.SimpleHiddenPages ?? new List<string>();
			foreach (CheckBox check in _simplePageChecks)
			{
				bool target = check.Tag is string key && hiddenPages.Contains(key);
				if (check.IsChecked != target)
				{
					check.IsChecked = target;
				}
			}
		}
		finally
		{
			_simplePageChecksLoading = false;
		}
	}

	/// <summary>MainWindow 简易模式隐藏配置变化（如设置入口 5 秒自动复原）后刷新勾选框。</summary>
	private void OnSimpleModeHiddenChanged()
	{
		try
		{
			SyncSimpleModeCheckboxes();
			if (_simpleModeSwitch != null)
			{
				bool on = SettingsManager.Instance.SimpleMode;
				if (_simpleModeSwitch.IsOn != on)
				{
					_simpleModeSwitch.IsOn = on;
				}
			}
		}
		catch (Exception exSync)
		{
			LogHelper.LogError(exSync, "[设置页] 同步简易模式勾选状态失败");
		}
	}

	private void SettingsDebugSwitch_Toggled(object sender, RoutedEventArgs e)
	{		if (_exportLogButton != null)
		{
			_exportLogButton.IsEnabled = _settingsDebugSwitch.IsOn;
			_exportLogButton.Opacity = (_settingsDebugSwitch.IsOn ? 1.0 : 0.4);
		}
		if (_loading || _settingsDebugSwitch == null)
		{
			return;
		}
		try
		{
			AppSettings instance = SettingsManager.Instance;
			instance.DebugMode = _settingsDebugSwitch.IsOn;
			SettingsManager.Save(instance);
			ProxyCaptureService.SetCaptureLogEnabled(instance.DebugMode);
			if (!instance.DebugMode)
			{
				LogHelper.ClearLogs();
				SessionLogStore.Clear();
			}
			MainWindow.Instance?.ShowToast(instance.DebugMode ? "已开启调试模式（详细日志 + 抓包日志 + 布吉岛代理 payload 日志）" : "已关闭调试模式，冗余日志已停止产出并清空（仅保留错误日志）");
			SessionLogPage.SyncDebugMode();
			// 布吉岛代理 payload（游戏进程内原生 DLL）日志接入：开=开始镜像到本程序日志，关=停止
			try
			{
				BjdPayloadLog.SetEnabled(instance.DebugMode);
			}
			catch (Exception exBjd)
			{
				LogHelper.LogError(exBjd, "[设置页] 切换 payload 日志镜像失败");
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[设置页] 切换调试模式失败");
		}
	}

	private async void ExportLog_Click(object sender, RoutedEventArgs e)
	{
		Button? button = sender as Button;
		if (button != null)
		{
			button.IsEnabled = false;
		}
		try
		{
			string zipPath = await Task.Run(() => LogExporter.ExportToDesktop());
			MainWindow.Instance?.ShowToast("日志已导出到桌面：" + System.IO.Path.GetFileName(zipPath), ok: true);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[设置页] 导出问题日志失败");
			MainWindow.Instance?.ShowToast("导出失败：" + ex.Message, false);
		}
		finally
		{
			if (button != null)
			{
				button.IsEnabled = true;
			}
		}
	}

	private void SettingsBackupDays_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
	{
		if (_loading || _settingsBackupDaysBox == null)
		{
			return;
		}
		try
		{
			AppSettings instance = SettingsManager.Instance;
			double num2 = e.NewValue;
			if (double.IsNaN(num2))
			{
				num2 = 3.0;
			}
			int num = (int)Math.Round(num2);
			if (num < 0)
			{
				num = 0;
			}
			instance.RedundantBackupKeepDays = num;
			SettingsManager.Save(instance);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[设置页] 修改冗余备份保留天数失败");
		}
	}

	private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		RelayoutSettings(e.NewSize.Width);
	}

	private void RelayoutSettings(double width)
	{
		bool flag = width >= 1200.0;
		SettingsRoot.HorizontalAlignment = (flag ? HorizontalAlignment.Center : HorizontalAlignment.Left);
		SettingsRoot.Padding = (flag ? new Thickness(40.0, 48.0, 40.0, 28.0) : new Thickness(40.0, 28.0, 40.0, 28.0));
		SettingsGrid.MaxWidth = (flag ? 1250 : 900);
		SettingsGrid.RowDefinitions.Clear();
		SettingsGrid.ColumnDefinitions.Clear();
		if (flag)
		{
			SettingsGrid.ColumnDefinitions.Add(new ColumnDefinition());
			SettingsGrid.ColumnDefinitions.Add(new ColumnDefinition());
			for (int i = 0; i < 5; i++)
			{
				SettingsGrid.RowDefinitions.Add(new RowDefinition());
			}
			SetCell(ThemeGroup, 0, 0, 1);
			SetCell(BackgroundGroup, 1, 0, 1);
			SetCell(AppearanceGroup, 2, 0, 1);
			SetCell(WindowGroup, 3, 0, 1);
			SetCell(SkinGroup, 0, 1, 1);
			SetCell(MusicGroup, 1, 1, 1);
			SetCell(Mp4Group, 2, 1, 1);
			SetCell(StartupGroup, 3, 1, 1);
			SetCell(MediaPanel, 4, 0, 2);
		}
		else
		{
			SettingsGrid.ColumnDefinitions.Add(new ColumnDefinition());
			for (int j = 0; j < 9; j++)
			{
				SettingsGrid.RowDefinitions.Add(new RowDefinition());
			}
			SetCell(ThemeGroup, 0, 0, 1);
			SetCell(BackgroundGroup, 1, 0, 1);
			SetCell(AppearanceGroup, 2, 0, 1);
			SetCell(WindowGroup, 3, 0, 1);
			SetCell(StartupGroup, 4, 0, 1);
			SetCell(SkinGroup, 5, 0, 1);
			SetCell(MusicGroup, 6, 0, 1);
			SetCell(Mp4Group, 7, 0, 1);
			SetCell(MediaPanel, 8, 0, 1);
		}
	}

	private static void SetCell(FrameworkElement element, int row, int column, int span)
	{
		Grid.SetRow(element, row);
		Grid.SetColumn(element, column);
		Grid.SetColumnSpan(element, span);
	}

	private void OnLoaded()
	{
		_loading = true;
		try
		{
			MusicService.StateChanged += OnMusicStateChanged;
			AppSettings instance = SettingsManager.Instance;
			ComboBox themeCombo = ThemeCombo;
			EnsureThemeComboItems();
			string themeMode = instance.ThemeMode;
			int selectedIndex = ((themeMode == "dark") ? 1 : ((themeMode == "light") ? 2 : ((themeMode == "black") ? 3 : 0)));
			themeCombo.SelectedIndex = selectedIndex;
			OpacitySlider.Value = Math.Clamp(instance.BackgroundOpacity, 0.0, 1.0) * 100.0;
			double value = Math.Clamp(instance.FontSize, 12.0, 22.0);
			FontSizeSlider.Value = value;
			FontSizeSlider.Minimum = 12.0;
			FontSizeSlider.Maximum = 22.0;
			FontSizeSlider.StepFrequency = 1.0;
			FontBoldSwitch.IsOn = instance.FontBold;
			MinimizeToTraySwitch.IsOn = instance.MinimizeToTray;
			ShowTrayIconSwitch.IsOn = instance.ShowTrayIcon;
			CloseKeepsRunningSwitch.IsOn = instance.CloseKeepsRunning;
			AutoStartHiddenWindowSwitch.IsOn = instance.AutoStartHiddenWindow;
			AutoPlaySwitch.IsOn = instance.AutoPlayOnStartup;
			AutoStartSwitch.IsOn = instance.AutoStartWithSystem;
			AutoStartProxySwitch.IsOn = instance.AutoStartProxyOnStartup;
			DetectGameSwitch.IsOn = instance.DetectGameProcesses;
			SkinGroup.Visibility = Visibility.Collapsed;
			SyncAutoStartRegistry();
			PopulateBackgroundItems(instance.BackgroundPath);
			RefreshMusicSection();
		}
		finally
		{
			_loading = false;
		}
	}

	private void OnMusicStateChanged()
	{
		if (!_loading)
		{
			base.DispatcherQueue.TryEnqueue(delegate
			{
				RefreshMusicSection();
			});
		}
	}

	/// <summary>
	/// 主题下拉选项：XAML 里原本只有 跟随系统/深色/浅色 三项。
	/// 这里把「深色」改名「透明深色」（半透明卡片浮在壁纸上），并追加第 4 项「黑底深色」
	/// （ThemeMode=black，内容块与选项实黑底、文字清晰）。
	/// </summary>
	private void EnsureThemeComboItems()
	{
		try
		{
			if (ThemeCombo.Items.Count > 1 && ThemeCombo.Items[1] is ComboBoxItem darkItem)
			{
				darkItem.Content = "透明深色（视频背景推荐）";
			}
			bool hasBlack = false;
			foreach (object item in ThemeCombo.Items)
			{
				if (item is ComboBoxItem comboItem && string.Equals(comboItem.Tag as string, "black", StringComparison.Ordinal))
				{
					hasBlack = true;
					break;
				}
			}
			if (!hasBlack)
			{
				ThemeCombo.Items.Add(new ComboBoxItem
				{
					Content = "黑底深色",
					Tag = "black"
				});
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[设置] 主题下拉选项初始化失败");
		}
	}

	private async void Mp4BrowseButton_Click(object sender, RoutedEventArgs e)
	{
		FolderPicker folderPicker = new FolderPicker
		{
			FileTypeFilter = { "*" }
		};
		InitializeWithWindow.Initialize(folderPicker, WindowNative.GetWindowHandle(MainWindow.Instance));
		StorageFolder storageFolder = await folderPicker.PickSingleFolderAsync();
		if (!(storageFolder == null))
		{
			AppSettings instance = SettingsManager.Instance;
			instance.VideoFolder = storageFolder.Path;
			SettingsManager.Save(instance);
			bool flag = MusicService.CanSwitchToSource("video");
			if ((instance.MusicSource != "video") & flag)
			{
				instance.MusicSource = "video";
				SettingsManager.Save(instance);
			}
			MusicService.RefreshKnownMusic();
			RefreshMusicSection();
			LogHelper.Log("选择 MP4 文件夹: " + storageFolder.Path);
			MainWindow.Instance?.ApplySettings();
			if (!flag)
			{
				MainWindow.Instance?.ShowToast("未选择 MP4 背景视频，未切换到 MP4 音乐入口");
				return;
			}
			if (MusicService.KnownMusic.Count == 0)
			{
				MainWindow.Instance?.ShowToast("该文件夹下未检测到 mp4/mkv/mov 等视频文件");
				return;
			}
			MainWindow.Instance?.ShowToast($"播放背景MP4的音乐入口: {MusicService.KnownMusic.Count} 个音乐/视频");
		}
	}

	private void PopulateBackgroundItems(string savedPath)
	{
		_backgroundPaths.Clear();
		BackgroundCombo.Items.Clear();
		BackgroundCombo.Items.Add(new ComboBoxItem
		{
			Content = "无背景",
			Tag = "none"
		});
		try
		{
			Directory.CreateDirectory(BackgroundsFolder);
			foreach (string item2 in Directory.EnumerateFiles(BackgroundsFolder, "*", SearchOption.AllDirectories))
			{
				if (BackgroundExtensions.Contains(Path.GetExtension(item2).ToLowerInvariant()))
				{
					_backgroundPaths.Add(item2);
				}
			}
		}
		catch
		{
		}
		_backgroundPaths.Sort(StringComparer.OrdinalIgnoreCase);
		foreach (string backgroundPath in _backgroundPaths)
		{
			BackgroundCombo.Items.Add(new ComboBoxItem
			{
				Content = Path.GetFileNameWithoutExtension(backgroundPath),
				Tag = "preset:" + Path.GetFileName(backgroundPath)
			});
		}
		ComboBoxItem item = new ComboBoxItem
		{
			Content = "自定义…",
			Tag = "custom"
		};
		BackgroundCombo.Items.Add(item);
		int num = 0;
		if (!string.IsNullOrWhiteSpace(savedPath))
		{
			string fileName = Path.GetFileName(savedPath);
			for (int i = 0; i < _backgroundPaths.Count; i++)
			{
				if (string.Equals(Path.GetFileName(_backgroundPaths[i]), fileName, StringComparison.OrdinalIgnoreCase) || string.Equals(_backgroundPaths[i], savedPath, StringComparison.OrdinalIgnoreCase))
				{
					num = i + 1;
					break;
				}
			}
			if (num == 0)
			{
				num = _backgroundPaths.Count + 1;
				CustomPathBox.Text = savedPath;
			}
		}
		BackgroundCombo.SelectedIndex = num;
		UpdateCustomItemLabel();
		UpdateCustomBoxState();
		UpdateBackgroundPathHint();
	}

	private void RefreshMusicSection()
	{
		_loading = true;
		try
		{
			MusicService.RefreshKnownMusic();
			AppSettings instance = SettingsManager.Instance;
			MusicFolderBox.Text = MusicService.MusicFolder;
			string value = (MusicService.IsVideoSource ? "播放背景MP4的音乐" : "音乐文件夹");
			string value2 = (MusicService.IsVideoSource ? (MusicService.ResolveBackgroundVideoPath() ?? MusicService.MusicFolder) : MusicService.MusicFolder);
			MusicFolderHint.Text = $"{value}: {value2}   检测到 {MusicService.KnownMusic.Count} 首音乐/视频";
			string text = (MusicListBox.SelectedItem as ListViewItem)?.Tag as string;
			string currentPath = MusicService.CurrentPath;
			string text2 = BuildListSignature();
			bool flag = !string.Equals(_listSignature, text2, StringComparison.Ordinal);
			int selectedIndex = MusicListBox.SelectedIndex;
			double num = 0.0;
			if (flag)
			{
				_listSignature = text2;
				num = (_listScrollViewer ?? (_listScrollViewer = FindDescendant<ScrollViewer>(MusicListBox)))?.VerticalOffset ?? 0.0;
				MusicListBox.Items.Clear();
				MusicListBox.Items.Add(BuildNoMusicItem());
				foreach (string item in MusicService.KnownMusic)
				{
					bool flag2 = MusicService.IsSkipped(item);
					TextBlock textBlock = new TextBlock
					{
						Text = (flag2 ? (Path.GetFileName(item) + "   (" + FormatSize(SafeFileSize(item)) + ")  (已跳过)") : (Path.GetFileName(item) + "   (" + FormatSize(SafeFileSize(item)) + ")")),
						TextTrimming = TextTrimming.CharacterEllipsis,
						FontSize = 13.0
					};
					if (flag2)
					{
						textBlock.Foreground = new SolidColorBrush(Colors.Red);
					}
					MusicListBox.Items.Add(new ListViewItem
					{
						Content = textBlock,
						Tag = item
					});
				}
				AnimateListSize(ComputeListHeight());
			}
			int num2 = (MusicService.NoMusicMode ? (-1) : ResolveSelectedIndex(currentPath, text));
			int num3 = ((!MusicService.NoMusicMode) ? ((num2 >= 0) ? (num2 + 1) : (-1)) : 0);
			bool flag3 = num3 != selectedIndex;
			if (num3 >= 0 && num3 < MusicListBox.Items.Count)
			{
				MusicListBox.SelectedIndex = num3;
			}
			else if (MusicListBox.SelectedIndex >= 0)
			{
				MusicListBox.SelectedIndex = -1;
			}
			if (flag3)
			{
				ScrollSelectedIntoView();
			}
			else if (flag && num > 0.0)
			{
				_listScrollViewer?.ChangeView(null, num, null, disableAnimation: true);
			}
			LogHelper.Log($"音乐列表刷新: rebuilt={flag}, noMusic={MusicService.NoMusicMode}, current={currentPath}, userSelect={text}, knownIdx={num2}, selectedIndex={MusicListBox.SelectedIndex}");
			ComboBox playbackModeCombo = PlaybackModeCombo;
			string playbackMode = instance.PlaybackMode ?? "random";
			_lastKnownPlaybackMode = playbackMode;
			int selectedIndex2 = ((playbackMode == "loop") ? 1 : ((playbackMode == "sequential") ? 2 : 0));
			playbackModeCombo.SelectedIndex = selectedIndex2;
			RefreshMp4Hint();
		}
		finally
		{
			_loading = false;
		}
	}

	private static int ResolveSelectedIndex(string? current, string? userSelection)
	{
		if (!string.IsNullOrEmpty(current))
		{
			return MusicService.KnownMusic.FindIndex((string f) => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
		}
		if (!string.IsNullOrEmpty(userSelection))
		{
			return MusicService.KnownMusic.FindIndex((string f) => string.Equals(f, userSelection, StringComparison.OrdinalIgnoreCase));
		}
		return -1;
	}

	private void ScrollSelectedIntoView()
	{
		try
		{
			if (MusicListBox.SelectedIndex >= 0 && MusicListBox.SelectedIndex < MusicListBox.Items.Count)
			{
				MusicListBox.ScrollIntoView(MusicListBox.Items[MusicListBox.SelectedIndex]);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "音乐列表滚动到选中项失败");
		}
	}

	private static string BuildListSignature()
	{
		StringBuilder stringBuilder = new StringBuilder();
		foreach (string item in MusicService.KnownMusic)
		{
			stringBuilder.Append(item).Append('\n');
		}
		foreach (string item2 in SettingsManager.Instance.SkippedMusic ?? new List<string>())
		{
			stringBuilder.Append("SKIP:").Append(item2).Append('\n');
		}
		return stringBuilder.ToString();
	}

	private void RefreshMp4Hint()
	{
		AppSettings instance = SettingsManager.Instance;
		Mp4FolderBox.Text = instance.VideoFolder ?? "";
		List<string> mp4Library = MusicService.GetMp4Library();
		Mp4FolderHint.Text = ((mp4Library.Count > 0) ? $"检测到 {mp4Library.Count} 个视频（已并入上方音乐列表）" : "未检测到视频，请先选择 MP4 文件夹");
	}

	private void MusicListBox_ItemClick(object sender, ItemClickEventArgs e)
	{
		if (Environment.TickCount64 - MusicService.LastFlyoutClickTicks < 600)
		{
			return;
		}
		string text = ResolveItemTag(e.ClickedItem);
		if (text != null)
		{
			if (text == "<no-music>")
			{
				MusicService.EnterNoMusicMode();
			}
			else
			{
				MusicService.Play(text);
			}
		}
	}

	private void MusicListBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		if (Environment.TickCount64 - MusicService.LastFlyoutClickTicks < 600 || !(e.OriginalSource is DependencyObject start))
		{
			return;
		}
		ListViewItem listViewItem = FindItemContainer(start);
		if ((object)listViewItem != null && listViewItem.Tag is string text)
		{
			if (text == "<no-music>")
			{
				MusicService.EnterNoMusicMode();
			}
			else
			{
				MusicService.Play(text);
			}
			e.Handled = true;
		}
	}

	private void MusicListBox_RightTapped(object sender, RightTappedRoutedEventArgs e)
	{
		if (e.OriginalSource is DependencyObject start)
		{
			ListViewItem listViewItem = FindItemContainer(start);
			if ((object)listViewItem != null && listViewItem.Tag is string text && text != "<no-music>")
			{
				MusicService.ToggleSkip(text);
				e.Handled = true;
			}
		}
	}

	private static ListViewItem BuildNoMusicItem()
	{
		return new ListViewItem
		{
			Content = new TextBlock
			{
				Text = "不播放任何音乐（背景视频照常）",
				FontSize = 13.0,
				FontWeight = FontWeights.SemiBold
			},
			Tag = "<no-music>"
		};
	}

	private void AnimateListSize(double newHeight)
	{
		double num = ((MusicListBox.Height > 0.0) ? MusicListBox.Height : 132.0);
		if (!(Math.Abs(num - newHeight) < 0.5))
		{
			_listSizeSb?.Stop();
			_listSizeSb = null;
			DoubleAnimation doubleAnimation = new DoubleAnimation
			{
				From = num,
				To = newHeight,
				Duration = TimeSpan.FromMilliseconds(240.0),
				EnableDependentAnimation = true,
				EasingFunction = new CubicEase
				{
					EasingMode = EasingMode.EaseOut
				}
			};
			Storyboard.SetTarget(doubleAnimation, MusicListBox);
			Storyboard.SetTargetProperty(doubleAnimation, "Height");
			Storyboard storyboard = new Storyboard();
			storyboard.Children.Add(doubleAnimation);
			storyboard.Completed += delegate
			{
				MusicListBox.Height = newHeight;
				_listSizeSb = null;
			};
			_listSizeSb = storyboard;
			storyboard.Begin();
		}
	}

	private static double ComputeListHeight()
	{
		int num = MusicService.KnownMusic.Count + 1;
		return (num > 5) ? Math.Min(360, 132 + (num - 5) * 24) : 132;
	}

	private void SaveAndApply()
	{
		if (_loading)
		{
			return;
		}
		AppSettings instance = SettingsManager.Instance;
		if (BackgroundCombo.SelectedItem is ComboBoxItem { Tag: string tag })
		{
			string text = tag;
			if (!(text == "custom"))
			{
				if (text == "none")
				{
					instance.BackgroundPath = "";
				}
				else
				{
					instance.BackgroundPath = (tag.StartsWith("preset:") ? tag.Substring("preset:".Length) : "");
				}
			}
			else
			{
				instance.BackgroundPath = CustomPathBox.Text.Trim();
			}
		}
		AppSettings appSettings = instance;
		AppSettings appSettings2 = appSettings;
		int selectedIndex = ThemeCombo.SelectedIndex;
		if (1 == 0)
		{
		}
		string themeMode = selectedIndex switch
		{
			1 => "dark", 
			2 => "light", 
			3 => "black", 
			_ => "system", 
		};
		if (1 == 0)
		{
		}
		appSettings2.ThemeMode = themeMode;
		instance.BackgroundOpacity = OpacitySlider.Value / 100.0;
		instance.FontSize = FontSizeSlider.Value;
		instance.FontBold = FontBoldSwitch.IsOn;
		instance.MinimizeToTray = MinimizeToTraySwitch.IsOn;
		instance.ShowTrayIcon = ShowTrayIconSwitch.IsOn;
		instance.CloseKeepsRunning = CloseKeepsRunningSwitch.IsOn;
		instance.AutoStartHiddenWindow = AutoStartHiddenWindowSwitch.IsOn;
		instance.AutoPlayOnStartup = AutoPlaySwitch.IsOn;
		instance.AutoStartWithSystem = AutoStartSwitch.IsOn;
		instance.AutoStartProxyOnStartup = AutoStartProxySwitch.IsOn;
		instance.DetectGameProcesses = DetectGameSwitch.IsOn;
		SettingsManager.Save(instance);
		UpdateCustomItemLabel();
		UpdateCustomBoxState();
		UpdateBackgroundPathHint();
		MainWindow.Instance?.ApplySettings();
	}

	private void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
	{
		if (!_loading)
		{
			AppSettings instance = SettingsManager.Instance;
			instance.AutoStartWithSystem = AutoStartSwitch.IsOn;
			SettingsManager.Save(instance);
			SyncAutoStartRegistry();
			MainWindow.Instance?.ShowToast(instance.AutoStartWithSystem ? "已开启开机自启" : "已关闭开机自启");
		}
	}

	private void AutoStartProxySwitch_Toggled(object sender, RoutedEventArgs e)
	{
		if (!_loading)
		{
			AppSettings instance = SettingsManager.Instance;
			instance.AutoStartProxyOnStartup = AutoStartProxySwitch.IsOn;
			SettingsManager.Save(instance);
			MainWindow.Instance?.ShowToast(instance.AutoStartProxyOnStartup ? "已开启：启动程序后按上次启动方式自动启动服务器代理（机器人代理在会话多开页单独设置）" : "已关闭：启动程序后不自动启动服务器代理");
		}
	}

	private void DetectGameSwitch_Toggled(object sender, RoutedEventArgs e)
	{
		if (!_loading)
		{
			AppSettings instance = SettingsManager.Instance;
			instance.DetectGameProcesses = DetectGameSwitch.IsOn;
			SettingsManager.Save(instance);
			MainWindow.Instance?.ShowToast(instance.DetectGameProcesses ? "已开启：开机检测游戏进程（发烧游戏平台/网易我的世界）" : "已关闭：开机检测游戏进程");
		}
	}

	// 皮肤服务已移除：设置页皮肤分组在加载时隐藏，相关处理方法一并删除

	private static void SyncAutoStartRegistry()
	{
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
			if (registryKey == null)
			{
				return;
			}
			if (SettingsManager.Instance.AutoStartWithSystem)
			{
				string text = Process.GetCurrentProcess().MainModule?.FileName;
				if (!string.IsNullOrWhiteSpace(text))
				{
					string text2 = "\"" + text + "\"";
					if (SettingsManager.Instance.AutoStartHiddenWindow)
					{
						text2 += " --hidden";
					}
					registryKey.SetValue("MhyunBox", text2);
				}
			}
			else if (registryKey.GetValue("MhyunBox") != null)
			{
				registryKey.DeleteValue("MhyunBox", throwOnMissingValue: false);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "同步开机自启注册表失败");
		}
	}

	private static int CustomIndex(int imageCount)
	{
		return imageCount + 1;
	}

	private void UpdateCustomItemLabel()
	{
		int num = CustomIndex(_backgroundPaths.Count);
		if (BackgroundCombo.Items.Count > num && BackgroundCombo.Items[num] is ComboBoxItem comboBoxItem)
		{
			string text = CustomPathBox.Text.Trim();
			comboBoxItem.Content = (string.IsNullOrEmpty(text) ? "自定义…" : ("自定义: " + Path.GetFileName(text)));
		}
	}

	private void UpdateCustomBoxState()
	{
		bool isEnabled = BackgroundCombo.SelectedItem is ComboBoxItem { Tag: string tag } && tag == "custom";
		CustomPathBox.IsEnabled = isEnabled;
		BrowseButton.IsEnabled = isEnabled;
	}

	private void UpdateBackgroundPathHint()
	{
		try
		{
			string text = null;
			if (BackgroundCombo.SelectedItem is ComboBoxItem { Tag: string tag })
			{
				if (tag.StartsWith("preset:"))
				{
					string name = tag.Substring("preset:".Length);
					text = _backgroundPaths.FirstOrDefault((string p) => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
				}
				else if (tag == "custom")
				{
					text = CustomPathBox.Text.Trim();
				}
			}
			BackgroundPathHint.Text = (string.IsNullOrWhiteSpace(text) ? "" : (Path.GetFileName(text) + "\n" + text));
		}
		catch
		{
		}
	}

	private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		SaveAndApply();
	}

	private void BackgroundCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		SaveAndApply();
	}

	private void WindowBehavior_Toggled(object sender, RoutedEventArgs e)
	{
		SaveAndApply();
		if (!_loading)
		{
			if (ReferenceEquals(sender, ShowTrayIconSwitch))
			{
				MainWindow.Instance?.ShowToast(ShowTrayIconSwitch.IsOn ? "已开启托盘小图标" : "已关闭托盘小图标");
			}
			else if (ReferenceEquals(sender, MinimizeToTraySwitch))
			{
				MainWindow.Instance?.ShowToast(MinimizeToTraySwitch.IsOn ? "最小化时将隐藏到托盘" : "最小化时显示在任务栏");
			}
			else if (ReferenceEquals(sender, CloseKeepsRunningSwitch))
			{
				MainWindow.Instance?.ShowToast(CloseKeepsRunningSwitch.IsOn ? "关闭窗口时保留进程（托盘）" : "关闭窗口时退出程序");
			}
			else if (ReferenceEquals(sender, AutoStartHiddenWindowSwitch))
			{
				SyncAutoStartRegistry();
				MainWindow.Instance?.ShowToast(AutoStartHiddenWindowSwitch.IsOn ? "开机自启时静默启动到托盘" : "开机自启时正常显示窗口");
			}
		}
	}

	private void AutoPlaySwitch_Toggled(object sender, RoutedEventArgs e)
	{
		SaveAndApply();
		if (!_loading)
		{
			MainWindow.Instance?.ShowToast(AutoPlaySwitch.IsOn ? "已开启：启动时自动播放音乐" : "已关闭：启动时不自动播放音乐");
		}
	}

	private void OpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
	{
		OpacityValueText.Text = $"{(int)Math.Round(e.NewValue)}%";
		SaveAndApply();
	}

	private void FontSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
	{
		FontSizeValueText.Text = $"{(int)Math.Round(e.NewValue)}";
		SaveAndApply();
		ScheduleFontRestartPrompt();
	}

	private void ScheduleFontRestartPrompt()
	{
		if (_loading)
		{
			return;
		}
		if (_fontRestartTimer == null)
		{
			_fontRestartTimer = base.DispatcherQueue.CreateTimer();
			_fontRestartTimer.Interval = TimeSpan.FromMilliseconds(800.0);
			_fontRestartTimer.Tick += delegate
			{
				_fontRestartTimer.Stop();
				ShowFontRestartPromptAsync();
			};
		}
		_fontRestartTimer.Stop();
		_fontRestartTimer.Start();
	}

	private async Task ShowFontRestartPromptAsync()
	{
		if (_fontRestartPromptShown || _loading)
		{
			return;
		}
		_fontRestartPromptShown = true;
		try
		{
			if (await new ContentDialog
			{
				Title = "字体大小已更改",
				Content = "字体大小调整需要重启程序才能完整生效。是否立即重启？\n\n若代理正在运行，重启后会自动恢复代理。",
				PrimaryButtonText = "立即重启",
				CloseButtonText = "暂不重启",
				DefaultButton = ContentDialogButton.Primary,
				XamlRoot = base.XamlRoot,
				RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default),
			}.ShowAsync() == ContentDialogResult.Primary)
			{
				MainWindow.RestartApplication();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "字体大小重启提示失败");
		}
	}

	private void FontBoldSwitch_Toggled(object sender, RoutedEventArgs e)
	{
		SaveAndApply();
	}

	private async void BrowseButton_Click(object sender, RoutedEventArgs e)
	{
		FileOpenPicker fileOpenPicker = new FileOpenPicker
		{
			FileTypeFilter = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".mp4", ".mkv", ".mov", ".wmv", ".webm" }
		};
		InitializeWithWindow.Initialize(fileOpenPicker, WindowNative.GetWindowHandle(MainWindow.Instance));
		StorageFile storageFile = await fileOpenPicker.PickSingleFileAsync();
		if (storageFile != null)
		{
			CustomPathBox.Text = storageFile.Path;
			BackgroundCombo.SelectedIndex = CustomIndex(_backgroundPaths.Count);
			SaveAndApply();
		}
	}

	private async void MusicBrowseButton_Click(object sender, RoutedEventArgs e)
	{
		FolderPicker folderPicker = new FolderPicker
		{
			FileTypeFilter = { "*" }
		};
		InitializeWithWindow.Initialize(folderPicker, WindowNative.GetWindowHandle(MainWindow.Instance));
		StorageFolder storageFolder = await folderPicker.PickSingleFolderAsync();
		if (!(storageFolder == null))
		{
			AppSettings instance = SettingsManager.Instance;
			instance.MusicFolder = storageFolder.Path;
			SettingsManager.Save(instance);
			if (MusicService.IsVideoSource)
			{
				MusicService.SetMusicSource("audio");
			}
			RefreshMusicSection();
			LogHelper.Log("选择音乐文件夹: " + storageFolder.Path);
			MainWindow.Instance?.ApplySettings();
			if (MusicService.KnownMusic.Count == 0)
			{
				MainWindow.Instance?.ShowToast("该文件夹下未检测到音乐文件");
			}
		}
	}

	private void ReloadMusic_Click(object sender, RoutedEventArgs e)
	{
		MusicService.ReloadLibrary();
		RefreshMusicSection();
		LogHelper.Log("手动重载音乐目录: " + MusicService.MusicFolder);
		MainWindow.Instance?.ApplySettings();
		if (MusicService.KnownMusic.Count == 0)
		{
			MainWindow.Instance?.ShowToast("目录下未检测到音乐/视频文件");
			return;
		}
		MainWindow.Instance?.ShowToast($"音乐库已重载: {MusicService.KnownMusic.Count} 首音乐/视频");
	}

	private void PlaybackModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_loading)
		{
			AppSettings instance = SettingsManager.Instance;
			instance.PlaybackMode = ((PlaybackModeCombo.SelectedItem as ComboBoxItem)?.Tag as string) ?? "random";
			_lastKnownPlaybackMode = instance.PlaybackMode;
			SettingsManager.Save(instance);
			// 立即应用到迷你播放器与背景视频循环状态（单曲循环不再等下一曲才生效）。
			MainWindow.Instance?.ApplyPlaybackMode();
			LogHelper.Log("播放模式: " + instance.PlaybackMode);
		}
	}

	/// <summary>
	/// 其它入口（迷你播放器按钮、命令行）改了播放模式时同步设置页下拉框。
	/// 用 _loading 保护，避免 SelectedIndex 变化再次触发 SelectionChanged 形成递归保存。
	/// </summary>
	private void OnSettingsSaved(AppSettings settings)
	{
		if (settings == null || PlaybackModeCombo == null)
		{
			return;
		}
		string mode = settings.PlaybackMode ?? "random";
		if (string.Equals(mode, _lastKnownPlaybackMode, StringComparison.Ordinal))
		{
			return;
		}
		_lastKnownPlaybackMode = mode;
		int idx = ((mode == "loop") ? 1 : ((mode == "sequential") ? 2 : 0));
		if (PlaybackModeCombo.SelectedIndex == idx)
		{
			return;
		}
		bool wasLoading = _loading;
		_loading = true;
		try
		{
			PlaybackModeCombo.SelectedIndex = idx;
		}
		finally
		{
			_loading = wasLoading;
		}
	}

	private void Combo_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
	{
		// 只在下拉展开/获得键盘焦点时才吞掉滚轮（此时滚轮用于改值）。
		// 否则光标扫过下拉框时整页滚不动，正是「滚轮列表不准」的手感来源之一。
		if (sender is ComboBox { IsDropDownOpen: true })
		{
			e.Handled = true;
			return;
		}
		if (sender is Control control && control.FocusState != FocusState.Unfocused)
		{
			e.Handled = true;
		}
	}

	private void MusicListBox_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (_reordering)
		{
			return;
		}
		PointerPoint currentPoint = e.GetCurrentPoint(MusicListBox);
		if (!currentPoint.Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
		{
			return;
		}
		_reorderPointerDown = true;
		_reorderIndex = -1;
		if (e.OriginalSource is DependencyObject start)
		{
			ListViewItem listViewItem = FindItemContainer(start);
			if (listViewItem != null)
			{
				for (int i = 0; i < MusicListBox.Items.Count; i++)
				{
					if ((object)MusicListBox.ContainerFromIndex(i) == listViewItem)
					{
						_reorderIndex = i;
						break;
					}
				}
			}
		}
		if (_reorderIndex < 0)
		{
			double itemHeight = GetItemHeight();
			if (itemHeight > 0.0)
			{
				_reorderIndex = Math.Clamp((int)(currentPoint.Position.Y / itemHeight), 0, MusicListBox.Items.Count - 1);
			}
		}
		if (IsNoMusicItem(_reorderIndex))
		{
			_reorderPointerDown = false;
			_reorderIndex = -1;
			return;
		}
		_reorderStartPos = currentPoint.Position;
		LogHelper.Log($"音乐列表按下: index={_reorderIndex}, items={MusicListBox.Items.Count}");
		e.Handled = true;
	}

	private double GetItemHeight()
	{
		for (int i = 0; i < MusicListBox.Items.Count; i++)
		{
			if (MusicListBox.ContainerFromIndex(i) is FrameworkElement { ActualHeight: var actualHeight } && actualHeight > 0.0)
			{
				return actualHeight;
			}
		}
		return 0.0;
	}

	private bool IsNoMusicItem(int index)
	{
		if (index == 0 && MusicListBox.Items.Count > 0 && MusicListBox.Items[0] is ListViewItem { Tag: string tag })
		{
			return tag == "<no-music>";
		}
		return false;
	}

	private static string? ResolveItemTag(object? clickedItem)
	{
		if (clickedItem is ListViewItem { Tag: string tag })
		{
			return tag;
		}
		if (clickedItem is DependencyObject start)
		{
			ListViewItem listViewItem2 = FindItemContainer(start);
			if ((object)listViewItem2 != null && listViewItem2.Tag is string result)
			{
				return result;
			}
		}
		return null;
	}

	private static ListViewItem? FindItemContainer(DependencyObject? start)
	{
		while (start != null)
		{
			if (start is ListViewItem result)
			{
				return result;
			}
			start = VisualTreeHelper.GetParent(start);
		}
		return null;
	}

	private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is T result)
			{
				return result;
			}
			T val = FindDescendant<T>(child);
			if (val != null)
			{
				return val;
			}
		}
		return null;
	}

	private static long SafeFileSize(string path)
	{
		try
		{
			return new FileInfo(path).Length;
		}
		catch
		{
			return 0L;
		}
	}

	private static string FormatSize(long bytes)
	{
		string[] array = new string[4] { "B", "KB", "MB", "GB" };
		double num = bytes;
		int num2 = 0;
		while (num >= 1024.0 && num2 < array.Length - 1)
		{
			num /= 1024.0;
			num2++;
		}
		return num.ToString("0.0") + " " + array[num2];
	}

	
	
	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	[DebuggerNonUserCode]
	public IComponentConnector GetBindingConnector(int connectionId, object target)
	{
		return null;
	}
}
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MhyunBox.Services;
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

	private Button? _exportLogButton;

	private ToggleSwitch? _settingsCleanupSwitch;

	private NumberBox? _settingsBackupDaysBox;

	private ToggleSwitch? _settingsProxyModeSwitch;

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
		};
		base.Loaded += delegate
		{
			// 页面是缓存复用的，Unloaded 会退订；重新进入时先退订再订阅，避免重复订阅或漏订阅。
			SettingsManager.Saved -= OnSettingsSaved;
			SettingsManager.Saved += OnSettingsSaved;
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
				_versionFooterText = new TextBlock
				{
					Text = "当前版本 " + AppVersion.Display,
					FontSize = 12.0,
					Opacity = 0.75,
					Margin = new Thickness(0.0, 18.0, 0.0, 0.0)
				};
				SettingsRoot.Children.Add(_versionFooterText);
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
				ToolTipService.SetToolTip(_exportLogButton, "把最近 3 天日志打包成 zip 保存到桌面，反馈问题时直接发送。需先开启调试模式。");
				_exportLogButton.Click += ExportLog_Click;
				SettingsRoot.Children.Add(_exportLogButton);
				// —— 更新 / 合并裁剪之后的自动清理（默认关闭；开启后删除的东西一律先进回收站）——
				_settingsCleanupSwitch = new ToggleSwitch
				{
					Header = "更新后自动清理（资源包 / 增量包 → 移入回收站，可还原）",
					OnContent = "开",
					OffContent = "关（保留文件）",
					IsOn = SettingsManager.Instance.AutoCleanupEnabled,
					Margin = new Thickness(0.0, 10.0, 0.0, 0.0)
				};
			_settingsCleanupSwitch.Toggled += SettingsCleanupSwitch_Toggled;
			SettingsRoot.Children.Add(_settingsCleanupSwitch);
			// —— 网易登录出口已固定为「跟随系统代理」（对齐 v3.8.0 行为，不再提供开关）；
			//    自动登录的保活机制已内置为账密每 6 小时重登，也不再提供开关。 ——
			// —— 代理模式（正常代理登录 / 防DDOS）：与首页 / 代理启动 / 快速启动共用同一组件 ——
			_settingsProxyModeSwitch = CreateProxyModeSelector();
			_settingsProxyModeSwitch.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
			SettingsRoot.Children.Add(_settingsProxyModeSwitch);
			SettingsRoot.Children.Add(new TextBlock
			{
				Text = "正常代理登录：单目标直连、不带连接节流与探测流量，避免被网易判为异常登录环境。防DDOS模式：节点轮换、预设 IP 池、健康探测与地址安全策略，适合被攻击时使用。切换后对新连接立即生效。",
				FontSize = 12.0,
				Opacity = 0.75,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
			});
			_settingsBackupDaysBox = new NumberBox
				{
					Header = "冗余备份保留天数（超期移入回收站，0 = 永不清理）",
					Value = SettingsManager.Instance.RedundantBackupKeepDays,
					Minimum = 0.0,
					Maximum = 365.0,
					SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
					Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
				};
				_settingsBackupDaysBox.ValueChanged += SettingsBackupDays_ValueChanged;
				SettingsRoot.Children.Add(_settingsBackupDaysBox);
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
	/// </summary>
	public static ToggleSwitch CreateProxyModeSelector()
	{
		ToggleSwitch toggle = new ToggleSwitch
		{
			Header = "代理流模式",
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

	private void SettingsDebugSwitch_Toggled(object sender, RoutedEventArgs e)
	{
		if (_exportLogButton != null)
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
			MainWindow.Instance?.ShowToast(instance.DebugMode ? "已开启调试模式（详细日志 + 抓包日志）" : "已关闭调试模式，冗余日志已停止产出并清空（仅保留错误日志）");
			SessionLogPage.SyncDebugMode();
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

	private void SettingsCleanupSwitch_Toggled(object sender, RoutedEventArgs e)
	{
		if (_loading || _settingsCleanupSwitch == null)
		{
			return;
		}
		try
		{
			AppSettings instance = SettingsManager.Instance;
			instance.AutoCleanupEnabled = _settingsCleanupSwitch.IsOn;
			SettingsManager.Save(instance);
			MainWindow.Instance?.ShowToast(_settingsCleanupSwitch.IsOn
				? "已开启自动清理：更新/合并后，资源包与增量包会移入回收站（可还原）"
				: "已关闭自动清理：更新/合并后保留资源包与增量包");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[设置页] 切换自动清理失败");
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
				num2 = 7.0;
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
			MainWindow.Instance?.ShowToast(instance.AutoStartProxyOnStartup ? "已开启：启动程序后按上次启动方式自动启动代理" : "已关闭：启动程序后自动启动代理");
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
				XamlRoot = base.XamlRoot
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

	private void MusicListBox_PointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (!_reorderPointerDown)
		{
			return;
		}
		Point position = e.GetCurrentPoint(MusicListBox).Position;
		if (!_reordering)
		{
			double value = position.X - _reorderStartPos.X;
			double value2 = position.Y - _reorderStartPos.Y;
			if (Math.Abs(value) + Math.Abs(value2) < 8.0)
			{
				return;
			}
			if (_reorderIndex < 0)
			{
				double itemHeight = GetItemHeight();
				if (itemHeight <= 0.0)
				{
					return;
				}
				_reorderIndex = Math.Clamp((int)(position.Y / itemHeight), 0, MusicListBox.Items.Count - 1);
			}
			if (IsNoMusicItem(_reorderIndex))
			{
				return;
			}
			_reordering = true;
			MusicListBox.CapturePointer(e.Pointer);
			LogHelper.Log($"音乐拖拽开始: index={_reorderIndex}, y={position.Y:0}, itemHeight={GetItemHeight():0}, items={MusicListBox.Items.Count}");
		}
		double itemHeight2 = GetItemHeight();
		if (!(itemHeight2 <= 0.0) && MusicListBox.Items.Count >= 2)
		{
			double num = (_listScrollViewer ?? (_listScrollViewer = FindDescendant<ScrollViewer>(MusicListBox)))?.VerticalOffset ?? 0.0;
			int num2 = Math.Max(1, Math.Clamp((int)((position.Y + num) / itemHeight2), 0, MusicListBox.Items.Count - 1));
			if (num2 != _reorderIndex)
			{
				object item = MusicListBox.Items[_reorderIndex];
				MusicListBox.Items.RemoveAt(_reorderIndex);
				MusicListBox.Items.Insert(num2, item);
				_reorderIndex = num2;
				MusicListBox.SelectedIndex = num2;
			}
			e.Handled = true;
		}
	}

	private void MusicListBox_PointerReleased(object sender, PointerRoutedEventArgs e)
	{
		FinishReorder(e);
	}

	private void MusicListBox_PointerCanceled(object sender, PointerRoutedEventArgs e)
	{
		FinishReorder(e);
	}

	private void FinishReorder(PointerRoutedEventArgs e)
	{
		MusicListBox.ReleasePointerCapture(e.Pointer);
		_reorderPointerDown = false;
		if (_reordering)
		{
			List<string> list = new List<string>();
			foreach (object item in MusicListBox.Items)
			{
				if (item is ListViewItem { Tag: string tag } && tag != "<no-music>")
				{
					list.Add(tag);
				}
			}
			if (list.Count > 0)
			{
				MusicService.ReorderKnownMusic(list);
				LogHelper.Log($"音乐拖拽完成: 新顺序 {list.Count} 首");
			}
		}
		_reordering = false;
		_reorderIndex = -1;
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

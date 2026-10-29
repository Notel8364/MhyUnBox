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

	private ToggleSwitch? _settingsCleanupSwitch;

	private NumberBox? _settingsBackupDaysBox;

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
		base.Unloaded += delegate
		{
			MusicService.StateChanged -= OnMusicStateChanged;
		};
		base.Loaded += delegate
		{
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
				// —— 本机锁（程序账号）——
				BuildAppLockSection();
				AppVersion.ApplyTo(SettingsRoot);
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[设置页] 添加版本信息/调试开关失败");
		}
	}

	private TextBlock? _lockStatusText;

	private TextBlock? _lockDetailText;

	private Button? _lockUnlockBtn;

	private Button? _lockChangeBtn;

	private Button? _lockExitBtn;

	private ToggleSwitch? _lockAutoSwitch;

	private bool _lockAutoSwitchUpdating;

	private void LockAutoSwitch_Toggled(object sender, RoutedEventArgs e)
	{
		if (_lockAutoSwitchUpdating || _lockAutoSwitch == null)
		{
			return;
		}
		try
		{
			_lockAutoSwitchUpdating = true;
			(bool Ok, string Message) = AppLock.SetAutoUnlock(_lockAutoSwitch.IsOn);
			MainWindow.Instance?.ShowToast(Message, Ok);
			if (!Ok)
			{
				_lockAutoSwitch.IsOn = AppLock.IsAutoUnlock;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[设置页] 切换自动登录程序账号失败");
		}
		finally
		{
			_lockAutoSwitchUpdating = false;
		}
	}

	private void BuildAppLockSection()
	{
		try
		{
			StackPanel card = new StackPanel
			{
				Spacing = 10.0,
				Margin = new Thickness(0.0, 22.0, 0.0, 0.0),
				Padding = new Thickness(16.0),
				BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
				BorderThickness = new Thickness(1.0),
				CornerRadius = new CornerRadius(8.0)
			};
			card.Children.Add(new TextBlock
			{
				Text = "本机锁（程序账号）",
				FontSize = 16.0,
				FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
			});
			_lockStatusText = new TextBlock
			{
				FontSize = 14.0,
				FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
			};
			card.Children.Add(_lockStatusText);
			_lockDetailText = new TextBlock
			{
				FontSize = 12.0,
				Opacity = 0.7,
				TextWrapping = TextWrapping.Wrap
			};
			card.Children.Add(_lockDetailText);
			StackPanel row = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Spacing = 8.0,
				Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
			};
			Button unlockBtn = new Button
			{
				Content = "验证解锁"
			};
			_lockUnlockBtn = unlockBtn;
			unlockBtn.Click += async delegate
			{
				if (AppLock.IsUnlocked)
				{
					AppLock.Lock();
					MainWindow.Instance?.ShowToast("已上锁，账密数据不可用");
				}
				else
				{
					await AppLockDialogs.ShowUnlockDialogAsync();
				}
			};
			Button netBtn = new Button
			{
				Content = "网易账号验证解锁",
				Visibility = Visibility.Collapsed
			};
			netBtn.Click += async delegate
			{
				await AppLockDialogs.ShowNeteaseUnlockDialogAsync();
			};
			Button changeBtn = new Button
			{
				Content = "更改密码"
			};
			_lockChangeBtn = changeBtn;
			changeBtn.Click += async delegate
			{
				await AppLockDialogs.ShowChangePasswordDialogAsync();
			};
			Button lockExitBtn = new Button
			{
				Content = "退出程序",
				Visibility = Visibility.Collapsed
			};
			_lockExitBtn = lockExitBtn;
			lockExitBtn.Click += async delegate
			{
				await AppLockDialogs.ShowExitProgramDialogAsync();
			};
			row.Children.Add(unlockBtn);
			row.Children.Add(changeBtn);
			row.Children.Add(lockExitBtn);
			card.Children.Add(row);
			// 默认启动自动登录：开启后启动不再要求输入程序账号，关闭则每次都要登录
			_lockAutoSwitch = new ToggleSwitch
			{
				Header = "启动时自动登录程序账号（默认启动解锁，仅本机）",
				OnContent = "开（启动不再要求输入）",
				OffContent = "关（每次启动要求登录程序账号 / 网易验证）",
				IsOn = AppLock.IsAutoUnlock,
				Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
			};
			_lockAutoSwitch.Toggled += LockAutoSwitch_Toggled;
			card.Children.Add(_lockAutoSwitch);
			card.Children.Add(new TextBlock
			{
				FontSize = 11.0,
				Opacity = 0.55,
				TextWrapping = TextWrapping.Wrap,
				Text = "程序密码在本机只存 MD5 单向哈希；网易账密/Cookie 未解锁时整体不可用，且 Cookie 落盘再经独立数据密钥二次加密。绑定网易账号后，可改用「网易账号验证解锁」或用它验证改密。"
			});
			SettingsRoot.Children.Add(card);
			// —— 底部栏：退出程序账号 ——
			Grid bottomBar = new Grid
			{
				Margin = new Thickness(0.0, 14.0, 0.0, 6.0)
			};
			bottomBar.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(1.0, GridUnitType.Star)
			});
			bottomBar.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			TextBlock exitTip = new TextBlock
			{
				Text = "退出程序账号后将立即锁定账密数据，下次使用需重新登录程序账号。",
				FontSize = 12.0,
				Opacity = 0.6,
				VerticalAlignment = VerticalAlignment.Center,
				TextWrapping = TextWrapping.Wrap
			};
			Grid.SetColumn(exitTip, 0);
			bottomBar.Children.Add(exitTip);
			Button exitBtn = new Button
			{
				Content = "退出程序账号",
				HorizontalAlignment = HorizontalAlignment.Right
			};
			exitBtn.Click += ExitProgramAccount_Click;
			Grid.SetColumn(exitBtn, 1);
			bottomBar.Children.Add(exitBtn);
			SettingsRoot.Children.Add(bottomBar);
			RefreshAppLockUi();
			AppLock.Unlocked += delegate
			{
				base.DispatcherQueue.TryEnqueue(RefreshAppLockUi);
			};
			AppLock.Locked += delegate
			{
				base.DispatcherQueue.TryEnqueue(RefreshAppLockUi);
			};
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[设置页] 本机锁区域构建失败");
		}
	}

	private void RefreshAppLockUi()
	{
		if (_lockStatusText == null || _lockDetailText == null)
		{
			return;
		}
		bool unlocked = AppLock.IsUnlocked;
		bool hasAccount = AppLock.HasAccount;
		_lockStatusText.Text = !hasAccount ? "⚠️ 尚未创建本机程序账号" : (unlocked ? "🔓 已解锁" : "🔒 已锁定");
		_lockDetailText.Text = hasAccount ? ("程序账号：" + AppLock.AccountNameMasked + "　·　" + (unlocked ? "账密 / Cookie 数据可用" : "点「验证解锁」登录程序账号或用网易/4399 账号验证；点「退出程序」直接退出")) : "启动本程序时会引导创建。";
		if (_lockUnlockBtn != null)
		{
			_lockUnlockBtn.Content = (unlocked ? "🔒 上锁" : "验证解锁");
			_lockUnlockBtn.Visibility = (hasAccount ? Visibility.Visible : Visibility.Collapsed);
		}
		if (_lockChangeBtn != null)
		{
			_lockChangeBtn.Visibility = (hasAccount && unlocked ? Visibility.Visible : Visibility.Collapsed);
		}
		if (_lockExitBtn != null)
		{
			_lockExitBtn.Visibility = (hasAccount && !unlocked ? Visibility.Visible : Visibility.Collapsed);
		}
	}

	private async void ExitProgramAccount_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			// 退出程序账号 = 锁定账密数据并一起关闭程序（与首页/导航栏「退出程序」一致）
			await AppLockDialogs.ShowExitProgramDialogAsync();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[设置页] 退出程序账号失败");
		}
	}

	private void SettingsDebugSwitch_Toggled(object sender, RoutedEventArgs e)
	{
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
			string themeMode = instance.ThemeMode;
			int selectedIndex = ((themeMode == "dark") ? 1 : ((themeMode == "light") ? 2 : 0));
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
			string playbackMode = instance.PlaybackMode;
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
		if (ReferenceEquals(sender, AutoStartHiddenWindowSwitch))
		{
			SyncAutoStartRegistry();
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
			SettingsManager.Save(instance);
			LogHelper.Log("播放模式: " + instance.PlaybackMode);
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

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using MhyunBox.Pages;
using MhyunBox.Services;
using MhyunBox.Settings;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;
using WinRT.Interop;
using Windows.Graphics;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace MhyunBox;

public sealed partial class MainWindow : Window
{
	private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

	private enum TrayCommand
	{
		None,
		Pause,
		Open,
		Settings,
		Exit
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NotifyIconData
	{
		public uint cbSize;

		public nint hWnd;

		public uint uID;

		public uint uFlags;

		public uint uCallbackMessage;

		public nint hIcon;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string szTip;

		public uint dwState;

		public uint dwStateMask;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string szInfo;

		public uint uTimeoutOrVersion;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string szInfoTitle;

		public uint dwInfoFlags;

		public Guid guidItem;

		public nint hBalloonIcon;
	}

	private struct POINT
	{
		public int X;

		public int Y;
	}

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct NotifyIconIdentifier
	{
		public uint cbSize;

		public nint hWnd;

		public uint uID;

		public Guid guidItem;
	}

	private static MainWindow? _instance;

	private AppWindow? _appWindow;

	private readonly List<FrameworkElement> _overlays = new List<FrameworkElement>();


	private bool _mainNavigationInitialized;

	private readonly Dictionary<Type, Page> _pageCache = new Dictionary<Type, Page>();

	private object? _lastNavSelection;

	private readonly List<string> _navHistory = new List<string>();

	private string _currentPageKey = "HomePage";

	private bool _appliedBefore;

	private bool _backgroundPathWarned;

	private string _lastTheme = "";

	private static readonly Dictionary<string, (Type Page, string Title)> Pages = new Dictionary<string, (Type, string)>
	{
		["HomePage"] = (typeof(HomePage), "首页"),
		["LoginPage"] = (typeof(LoginPage), "账号绑定"),
		["ProxyCapturePage"] = (typeof(ProxyCapturePage), "代理启动"),
		["GroupsQuickPage"] = (typeof(GroupsQuickPage), "快速启动"),
		["ServerListPage"] = (typeof(ServerListPage), "服务器列表"),
		["ShopPage"] = (typeof(ShopPage), "商城"),
		["ClientUpdatePage"] = (typeof(ClientUpdatePage), "更新客户端"),
		["SpeedTestPage"] = (typeof(SpeedTestPage), "网速测试"),
		["ToolsPage"] = (typeof(ToolsPage), "防护/工具"),
		["SessionLogPage"] = (typeof(SessionLogPage), "日志"),
		["AboutPage"] = (typeof(AboutPage), "关于")
	};

	private const double NavItemMinHeight = 48.0;

	private const int GwlWndProc = -4;

	private const int WmGetMinMaxInfo = 36;

	private const int MinWindowWidth = 720;

	private const int MinWindowHeight = 540;

	private static WndProcDelegate? _wndProcOverride;

	private static nint _originalWndProc;

	private const double PaneOpenWidth = 160.0;

	private static readonly string[] VideoExtensions = new string[5] { ".mp4", ".mkv", ".mov", ".wmv", ".webm" };

	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _centerRetryTimer;

	private int _centerAttempts;

	private nint _trayIcon;

	private bool _iconRefreshPending;

	private bool _wokeTopMost;

	private bool _trayHidden;

	private DateTime _lastTrayClickTime;

	private DateTime _lastHideUtc = DateTime.MinValue;

	private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _topMostTimer;

	private const uint WmSysCommand = 274u;

	private const int ScMinimize = 61472;

	private const uint WmActivate = 6u;

	private const uint WmClose = 16u;

	private const uint WmAppTray = 32768u;

	private static readonly uint _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

	private const uint WmDestroy = 2u;

	private const uint WmLButtonUp = 514u;

	private const uint WmLButtonDblClk = 515u;

	private const uint WmRButtonUp = 517u;

	private const uint TrayIconId = 1u;

	private const uint NimAdd = 0u;

	private const uint NimModify = 1u;

	private const uint NimDelete = 2u;

	private const uint NifMessage = 1u;

	private const uint NifIcon = 2u;

	private const uint NifTip = 4u;

	private const uint MfString = 0u;

	private const uint TpmRightButton = 2u;

	private const uint TpmNonNotify = 128u;

	private const uint TpmReturnCmd = 256u;

	private const uint ImageIcon = 1u;

	private const uint LrLoadFromFile = 16u;

	private const uint LrDefaultSize = 64u;

	private const int SwRestore = 9;

	private const uint SwpNoSize = 1u;

	private const uint SwpNoMove = 2u;

	private const uint SwpNoActivate = 16u;

	private const uint SwpShowWindow = 64u;

	private static readonly nint HwndTopMost = new IntPtr(-1);

	private static readonly nint HwndNotTopMost = new IntPtr(-2);

	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _toastTimer;

	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _titleRevertTimer;

	private string? _backgroundVideoPath;

	private readonly List<string> _bgVideoPlaylist = new List<string>();

	private string? _bgPlaylistKey;

	private string? _bgCyclePath;

	private int _bgIndex = -1;

	private bool _bgCycleMode;

	private double _bgVideoOpacity;

	private bool _bgEndedSubscribed;

	private bool _bgOpenedSubscribed;

	private bool _bgFailedSubscribed;

	private bool _bgStateSubscribed;

	private string? _bgPendingPath;

	private MediaSource? _bgPendingSource;

	private string? _appliedBackgroundVideo;

	private bool _bgSharingMainPlayer;

	private bool _exitRequested;

	private static readonly Random BgRandom = new Random();

	// 内容卡片背景：必须按当前主题实时解析。
	// 旧实现用 Lazy 缓存 Application.Current.Resources 的查找结果（启动时主题），
	// 用户中途切换深色后缓存不更新，整片内容区域保持浅色——这就是深色主题下发白的根因之一。
	private static Brush ResolveCardBackground()
	{
		try
		{
			return ThemeHelper.CardBackground();
		}
		catch
		{
			return new SolidColorBrush(ThemeHelper.IsDark
				? Color.FromArgb(byte.MaxValue, ThemeHelper.IsBlack ? (byte)12 : (byte)50, ThemeHelper.IsBlack ? (byte)12 : (byte)50, ThemeHelper.IsBlack ? (byte)12 : (byte)50)
				: Color.FromArgb(byte.MaxValue, 243, 243, 243));
		}
	}

	private MediaPlayerElement? _bgVideoElement;

	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _scrollTimer;

	private int _scrollDirection;

	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gameDetectionTimer;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Grid RootGrid;

	// Toast 最外层显示用的独立 Popup（窗口弹出层，位于一切窗口内容之上）
	private Microsoft.UI.Xaml.Controls.Primitives.Popup? _toastPopup;

	private Grid? _toastPopupRoot;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Grid ContentBackgroundHost;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Border PaneBackdrop;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Grid AppTitleBar;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Border ContentCard;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Frame ContentFrame;


	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button MiniLibraryButton;


	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button MiniPlayPauseButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button MiniPlayModeButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Slider MiniVolumeSlider;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock MiniVolumeText;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock MiniTrackText;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private NavigationView NavView;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Border ToastHost;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock ToastText;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Image AppTitleIcon;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock AppTitleTextBlock;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Border ContentBackgroundClipper;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Grid ContentBackgroundGrid;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Image ContentBackground;


	private static string BackgroundsFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds");

	public static MainWindow? Instance => _instance;

	/// <summary>主窗口的 AppWindow（供彩蛋效果移动/缩放窗口用）。</summary>
	public AppWindow? GetAppWindow() => _appWindow;

	/// <summary>窗口根容器（供彩蛋效果挂变换 / 覆盖彩虹层用）。</summary>
	public Grid? GetRootGrid() => RootGrid;

	public static Microsoft.UI.Dispatching.DispatcherQueue? UIQueue => _instance?.DispatcherQueue;

	public ElementTheme AppElementTheme
	{
		get
		{
			if (RootGrid.ActualTheme == ElementTheme.Dark)
			{
				return ElementTheme.Dark;
			}
			return ElementTheme.Light;
		}
	}

	public MainWindow()
	{
		InitializeComponent();
		_instance = this;
		// Toast 提到最外层：从 RootGrid 内部（内容卡片下层）改为独立 Popup（窗口弹出层），
		// 物理上位于一切窗口内容之上，浅色主题下不会再被内容卡片的半透明白底淡化遮挡
		try
		{
			Panel? toastParent = ToastHost.Parent as Panel;
			toastParent?.Children.Remove(ToastHost);
			_toastPopupRoot = new Grid
			{
				Background = null,
				IsHitTestVisible = false
			};
			_toastPopupRoot.Children.Add(ToastHost);
			_toastPopup = new Microsoft.UI.Xaml.Controls.Primitives.Popup
			{
				Child = _toastPopupRoot,
				IsLightDismissEnabled = false,
				IsHitTestVisible = false
			};
			RootGrid.SizeChanged += delegate(object? s, SizeChangedEventArgs e)
			{
				if (_toastPopupRoot != null)
				{
					_toastPopupRoot.Width = e.NewSize.Width;
					_toastPopupRoot.Height = e.NewSize.Height;
				}
			};
			LogHelper.Log("[界面] Toast 已提升为独立 Popup 最外层显示（原父级=" + (toastParent?.GetType().Name ?? "无") + "）");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[界面] Toast Popup 化失败，退回窗口内显示");
		}
		base.ExtendsContentIntoTitleBar = true;
		SetTitleBar(AppTitleBar);
		_appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
		_appWindow.Title = "BakaBox";
		AppTitleTextBlock.Text = "BakaBox";
		StartTitleRevertTimer();
		nint windowHandle = WindowNative.GetWindowHandle(this);
		_wndProcOverride = WndProc;
		_originalWndProc = SetWindowLongPtr(windowHandle, -4, Marshal.GetFunctionPointerForDelegate(_wndProcOverride));
		if (SettingsManager.Instance.ShowTrayIcon)
		{
			InitTrayIcon(windowHandle);
		}
		base.Closed += delegate
		{
			RemoveTrayIcon();
		};
		MusicService.StateChanged += OnMusicServiceStateChanged;
		base.Closed += delegate
		{
			MusicService.StateChanged -= OnMusicServiceStateChanged;
		};
		UpdateContentCardMargin(160.0);
		string path = Path.Combine(AppContext.BaseDirectory, "Assets");
		string text = Path.Combine(path, "app.ico");
		if (File.Exists(text))
		{
			_appWindow.SetIcon(text);
		}
		base.Activated += delegate
		{
			if (_iconRefreshPending)
			{
				_iconRefreshPending = false;
				RefreshAppIcon();
			}
		};
		_iconRefreshPending = true;
		string text2 = Path.Combine(path, "reimu.jpg");
		if (File.Exists(text2))
		{
			AppTitleIcon.Source = new BitmapImage(new Uri(text2));
		}
		ToolTipService.SetToolTip(AppTitleIcon, _appWindow.Title);
		ContentBackgroundHost.SizeChanged += ContentBackgroundHost_SizeChanged;
		RootGrid.Opacity = 0.4;
		LogHelper.Log("启动完成: AppDir=" + AppContext.BaseDirectory);
		LogCleanup.RunScheduledCleanup();
		InitializeMainNavigationIfNeeded();
	}

	private void RootGrid_Loaded(object sender, RoutedEventArgs e)
	{
		ApplySettings();
		CenterNavMenuItems();
		CompositionTarget.Rendering += OnFirstRender;
		base.DispatcherQueue.TryEnqueue(delegate
		{
			AccountAutoLoginService.Start();
			// 服务器列表自动发现：后台用已登录网易账号拉取服务器并自动注册预设（默认两个保留）
			ServerAutoDiscovery.Start(8);
			// 本机锁：开启「默认启动自动登录」时静默解锁；否则首次创建账号 / 每次要求登录程序账号
			if (!AppLock.TryAutoUnlock())
			{
				_ = AppLockDialogs.ShowStartupFlowAsync();
			}
		});
	}

	private void OnFirstRender(object? sender, object e)
	{
		CompositionTarget.Rendering -= OnFirstRender;
		Storyboard storyboard = new Storyboard();
		DoubleAnimation doubleAnimation = new DoubleAnimation
		{
			From = 0.4,
			To = 1.0,
			Duration = TimeSpan.FromMilliseconds(320.0),
			EnableDependentAnimation = true
		};
		Storyboard.SetTarget(doubleAnimation, RootGrid);
		Storyboard.SetTargetProperty(doubleAnimation, "Opacity");
		storyboard.Children.Add(doubleAnimation);
		storyboard.Begin();
	}

	public void ApplySettings()
	{
		AppSettings instance = SettingsManager.Instance;
		string themeMode = instance.ThemeMode;
		string text = ((!(themeMode == "dark")) ? ((!(themeMode == "light")) ? ((themeMode == "black") ? "black" : "system") : "light") : "dark");
		bool flag = text != _lastTheme;
		if (flag)
		{
			// black（黑底深色）按深色主题渲染（白字/深色控件），差异只在背景不透明度
			ElementTheme elementTheme = (RootGrid.RequestedTheme = ((text == "dark" || text == "black") ? ElementTheme.Dark : ((text == "light") ? ElementTheme.Light : ElementTheme.Default)));
			ElementTheme elementTheme3 = elementTheme;
			ElementTheme elementTheme4 = elementTheme3;
			try
			{
				if (elementTheme4 != ElementTheme.Default)
				{
					Application.Current.RequestedTheme = ((elementTheme4 == ElementTheme.Dark) ? ApplicationTheme.Dark : ApplicationTheme.Light);
				}
			}
			catch
			{
			}
			UpdateCaptionButtons();
			_lastTheme = text;
			// 主题切换后重建页面缓存：页面是缓存复用的，代码后置构建的卡片背景
			// 只在构造时取一次色，不重建的话切到深色内容页还是旧的浅色卡片
			if (_appliedBefore)
			{
				RefreshPagesForTheme();
			}
		}
		string text2 = ResolveBackgroundPath(instance.BackgroundPath);
		if (text2 != null && File.Exists(text2) && IsVideoPath(text2))
		{
			ContentBackground.Source = null;
			ContentBackground.Visibility = Visibility.Collapsed;
			_bgVideoOpacity = Math.Clamp(instance.BackgroundOpacity, 0.0, 1.0);
			EnsureBackgroundVideo().Opacity = _bgVideoOpacity;
			bool flag2 = text == "dark" || text == "black" || (text == "system" && RootGrid.ActualTheme == ElementTheme.Dark);
			// 黑底深色：内容卡也是实黑（不透明），与半透明壁纸彻底隔开，白色文字清晰
			ContentCard.Background = (text == "black")
				? new SolidColorBrush(Color.FromArgb(255, 12, 12, 12))
				: new SolidColorBrush(flag2 ? Color.FromArgb(192, 14, 14, 14) : Color.FromArgb(150, 247, 247, 247));
		}
		else if (text2 != null && File.Exists(text2))
		{
			ContentCard.Background = ResolveCardBackground();
			ContentBackground.Source = new BitmapImage(new Uri("file:///" + text2.Replace('\\', '/')));
			ContentBackground.Opacity = Math.Clamp(instance.BackgroundOpacity, 0.0, 1.0);
			ContentBackground.Visibility = Visibility.Visible;
		}
		else
		{
			ContentCard.Background = ResolveCardBackground();
			ContentBackground.Source = null;
			ContentBackground.Visibility = Visibility.Collapsed;
		}
		if (text2 == null && !string.IsNullOrWhiteSpace(instance.BackgroundPath) && !_backgroundPathWarned)
		{
			_backgroundPathWarned = true;
			string backgroundPath = instance.BackgroundPath;
			LogHelper.LogError(new FileNotFoundException("背景路径不存在: " + backgroundPath), "背景路径无效");
			ShowToast("背景路径无效：" + backgroundPath + "，请在设置里重新选择背景视频/图片");
		}
		if (MusicService.IsVideoSource && MusicService.ResolveBackgroundVideoPath() == null)
		{
			instance.MusicSource = "audio";
			SettingsManager.Save(instance);
		}
		if (_appliedBefore & flag)
		{
			AnimateThemeRender();
		}
		_appliedBefore = true;
		MusicService.ApplySettings();
		SyncBackgroundVideo();
		SyncTrayIcon();
		MiniVolumeSlider.Value = Math.Clamp(instance.MusicVolume, 0.0, 1.0) * 100.0;
		MiniVolumeText.Text = $"{(int)Math.Round(MiniVolumeSlider.Value)}%";
		ApplyFontSettings();
	}

	private void ApplyFontSettings()
	{
		try
		{
			double num = Math.Clamp(SettingsManager.Instance.FontSize, 12.0, 22.0);
			Application.Current.Resources["ControlContentThemeFontSize"] = num;
			if (SettingsManager.Instance.FontBold)
			{
				Style style = new Style(typeof(TextBlock));
				style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
				Application.Current.Resources[typeof(TextBlock)] = style;
			}
			else
			{
				Application.Current.Resources.Remove(typeof(TextBlock));
			}
		}
		catch
		{
		}
	}

	private void SyncBackgroundVideo()
	{
		try
		{
			string text = MusicService.ResolveBackgroundVideoPath();
			string currentPath = MusicService.CurrentPath;
			string appliedBackgroundVideo = _appliedBackgroundVideo;
			_appliedBackgroundVideo = text;
			if (currentPath != null && MusicService.IsVideoFile(currentPath) && text != null)
			{
				_bgCycleMode = false;
				if (appliedBackgroundVideo != null && !string.Equals(appliedBackgroundVideo, text, StringComparison.OrdinalIgnoreCase) && string.Equals(currentPath, appliedBackgroundVideo, StringComparison.OrdinalIgnoreCase))
				{
					MusicService.Play(text);
				}
				else
				{
					AttachBackgroundToMainPlayer();
				}
				return;
			}
			if (text == null)
			{
				_bgCycleMode = false;
				StopBackgroundVideo();
				return;
			}
			if (_bgSharingMainPlayer)
			{
				DetachBackgroundFromMainPlayer();
				_backgroundVideoPath = null;
			}
			_bgCycleMode = true;
			if (!string.Equals(_bgPlaylistKey, text, StringComparison.Ordinal))
			{
				BuildBackgroundVideoPlaylist(text);
				_bgPlaylistKey = text;
				_bgCyclePath = null;
			}
			string source = _bgCyclePath;
			if (source == null || !_bgVideoPlaylist.Any((string p) => string.Equals(p, source, StringComparison.OrdinalIgnoreCase)))
			{
				source = PickInitialBackgroundVideo(text);
			}
			bool loop = _bgVideoPlaylist.Count <= 1;
			bool restartVideoRequested = MusicService.RestartVideoRequested;
			MusicService.RestartVideoRequested = false;
			if (!string.Equals(_backgroundVideoPath, source, StringComparison.OrdinalIgnoreCase))
			{
				PlayBackgroundVideo(source, loop);
				return;
			}
			if (restartVideoRequested)
			{
				RestartBackgroundVideo(source, loop);
				return;
			}
			MediaPlayer mediaPlayer = EnsureBackgroundVideo().MediaPlayer;
			if ((object)mediaPlayer == null || mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
			{
				EnsureBackgroundVideo().MediaPlayer?.Play();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "同步背景视频失败");
		}
	}

	private void BuildBackgroundVideoPlaylist(string backgroundVideo)
	{
		_bgVideoPlaylist.Clear();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			Directory.CreateDirectory(BackgroundsFolder);
			foreach (string item in Directory.EnumerateFiles(BackgroundsFolder, "*", SearchOption.AllDirectories))
			{
				if (!IsVideoPath(item))
				{
					continue;
				}
				try
				{
					if (hashSet.Add(Path.GetFullPath(item)))
					{
						_bgVideoPlaylist.Add(Path.GetFullPath(item));
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		try
		{
			if (hashSet.Add(backgroundVideo))
			{
				_bgVideoPlaylist.Add(backgroundVideo);
			}
		}
		catch
		{
		}
		_bgVideoPlaylist.Sort(StringComparer.OrdinalIgnoreCase);
		LogHelper.Log($"背景视频轮播列表: {_bgVideoPlaylist.Count} 个");
	}

	private string? PickInitialBackgroundVideo(string preferred)
	{
		int num = _bgVideoPlaylist.FindIndex((string p) => string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase));
		if (num < 0)
		{
			num = ((SettingsManager.Instance.PlaybackMode == "random") ? BgRandom.Next(_bgVideoPlaylist.Count) : 0);
		}
		_bgIndex = num;
		return _bgVideoPlaylist[num];
	}

	private string? PickNextBackgroundVideo()
	{
		if (_bgVideoPlaylist.Count == 0)
		{
			return null;
		}
		if (SettingsManager.Instance.PlaybackMode == "sequential")
		{
			_bgIndex = (_bgIndex + 1) % _bgVideoPlaylist.Count;
		}
		else
		{
			_bgIndex = BgRandom.Next(_bgVideoPlaylist.Count);
		}
		return _bgVideoPlaylist[Math.Max(0, _bgIndex)];
	}

	private void OnBackgroundVideoEnded(MediaPlayer sender, object args)
	{
		if (sender.IsLoopingEnabled || !_bgCycleMode)
		{
			return;
		}
		base.DispatcherQueue.TryEnqueue(delegate
		{
			if (_bgCycleMode)
			{
				string text = PickNextBackgroundVideo();
				if (text != null)
				{
					PlayBackgroundVideo(text, _bgVideoPlaylist.Count <= 1);
				}
			}
		});
	}

	private void PlayBackgroundVideo(string path, bool loop)
	{
		try
		{
			MediaPlayer player = EnsureBackgroundVideo().MediaPlayer;
			if (player == null)
			{
				EnsureBackgroundVideo().Source = MediaSource.CreateFromUri(new Uri("file:///" + path.Replace('\\', '/')));
				player = EnsureBackgroundVideo().MediaPlayer;
			}
			if (player == null)
			{
				LogHelper.LogError(new InvalidOperationException("未获取到背景视频播放器"), "背景视频播放器不可用: " + path);
				return;
			}
			if ((object)player != MusicService.Player)
			{
				ConfigureBackgroundVideoTransport(player, path);
			}
			if (!_bgEndedSubscribed)
			{
				player.MediaEnded += OnBackgroundVideoEnded;
				_bgEndedSubscribed = true;
			}
			player.IsMuted = true;
			player.Volume = 0.0;
			if (!_bgOpenedSubscribed)
			{
				player.MediaOpened += OnBackgroundVideoOpened;
				_bgOpenedSubscribed = true;
			}
			if (!_bgFailedSubscribed)
			{
				player.MediaFailed += OnBackgroundVideoFailed;
				_bgFailedSubscribed = true;
			}
			if (!_bgStateSubscribed)
			{
				player.PlaybackSession.PlaybackStateChanged += delegate(MediaPlaybackSession s, object _)
				{
					LogHelper.Log($"背景视频状态: {s.PlaybackState}");
					try
					{
						if ((object)player != MusicService.Player)
						{
							SyncBackgroundVideoTransport();
						}
					}
					catch
					{
					}
				};
				_bgStateSubscribed = true;
			}
			EnsureBackgroundVideo().Visibility = Visibility.Visible;
			_bgPendingPath = path;
			_bgPendingSource = MediaSource.CreateFromUri(new Uri("file:///" + path.Replace('\\', '/')));
			player.Source = _bgPendingSource;
			player.IsLoopingEnabled = loop;
			player.Play();
			_backgroundVideoPath = path;
			if (_bgCycleMode)
			{
				_bgCyclePath = path;
			}
			FadeInBackgroundVideo();
			LogHelper.Log("背景视频(全窗): " + path);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "背景视频加载失败: " + path);
		}
	}

	private void ConfigureBackgroundVideoTransport(MediaPlayer player, string path)
	{
		try
		{
			SystemMediaTransportControls systemMediaTransportControls = player.SystemMediaTransportControls;
			if (!string.IsNullOrWhiteSpace(MusicService.CurrentPath))
			{
				systemMediaTransportControls.IsEnabled = false;
				systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Closed;
				LogHelper.Log("[SMTC诊断] 背景视频让位音乐: CurrentPath=" + MusicService.CurrentPath + " -> IsEnabled=false, Closed");
				return;
			}
			systemMediaTransportControls.IsEnabled = true;
			systemMediaTransportControls.IsPlayEnabled = false;
			systemMediaTransportControls.IsPauseEnabled = false;
			systemMediaTransportControls.IsNextEnabled = false;
			systemMediaTransportControls.IsPreviousEnabled = false;
			systemMediaTransportControls.PlaybackStatus = ((player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused);
			systemMediaTransportControls.DisplayUpdater.Type = MediaPlaybackType.Music;
			systemMediaTransportControls.DisplayUpdater.MusicProperties.Title = "视频背景播放";
			systemMediaTransportControls.DisplayUpdater.MusicProperties.Artist = "视频背景播放";
			systemMediaTransportControls.DisplayUpdater.Thumbnail = MusicService.CreateAppThumbnail();
			systemMediaTransportControls.DisplayUpdater.Update();
			LogHelper.Log("[SMTC诊断] 背景视频接管: path=" + path + " -> 显示'视频背景播放' IsEnabled=true");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "背景视频媒体浮层配置失败: " + path);
		}
	}

	private void SyncBackgroundVideoTransport()
	{
		try
		{
			MediaPlayer mediaPlayer = EnsureBackgroundVideo().MediaPlayer;
			if (!(mediaPlayer == null) && (object)mediaPlayer != MusicService.Player)
			{
				string text = _backgroundVideoPath;
				if (string.IsNullOrWhiteSpace(text))
				{
					text = _bgCyclePath;
				}
				if (string.IsNullOrWhiteSpace(text))
				{
					mediaPlayer.SystemMediaTransportControls.IsEnabled = false;
					mediaPlayer.SystemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Closed;
				}
				else
				{
					ConfigureBackgroundVideoTransport(mediaPlayer, text);
				}
			}
		}
		catch
		{
		}
	}

	private void OnBackgroundVideoOpened(MediaPlayer sender, object args)
	{
		MediaSource bgPendingSource = _bgPendingSource;
		if (!(bgPendingSource == null) && ReferenceEquals(sender.Source, bgPendingSource))
		{
			string bgPendingPath = _bgPendingPath;
			_bgPendingPath = null;
			_bgPendingSource = null;
			if (bgPendingPath != null && string.Equals(bgPendingPath, _backgroundVideoPath, StringComparison.OrdinalIgnoreCase) && sender.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
			{
				sender.Play();
				LogHelper.Log("背景视频(打开后补播): " + bgPendingPath);
			}
			if ((object)sender != MusicService.Player && !string.IsNullOrWhiteSpace(bgPendingPath))
			{
				ConfigureBackgroundVideoTransport(sender, bgPendingPath);
			}
		}
	}

	private void OnBackgroundVideoFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
	{
		LogHelper.Log($"背景视频打开失败: Error={args.Error}, 描述={args.ErrorMessage}");
		_bgPendingPath = null;
		_bgPendingSource = null;
	}

	private void RestartBackgroundVideo(string path, bool loop)
	{
		MediaPlayer mediaPlayer = EnsureBackgroundVideo().MediaPlayer;
		if (mediaPlayer == null || mediaPlayer.Source == null)
		{
			PlayBackgroundVideo(path, loop);
			return;
		}
		try
		{
			mediaPlayer.IsLoopingEnabled = loop;
			mediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
			mediaPlayer.Play();
			EnsureBackgroundVideo().Visibility = Visibility.Visible;
			LogHelper.Log("背景视频(重播): " + path);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "背景视频重播失败: " + path);
		}
	}

	private void FadeInBackgroundVideo()
	{
		double value = Math.Clamp(_bgVideoOpacity, 0.0, 1.0);
		DoubleAnimation doubleAnimation = new DoubleAnimation
		{
			From = 0.0,
			To = value,
			Duration = TimeSpan.FromMilliseconds(260.0),
			EasingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			}
		};
		Storyboard.SetTarget(doubleAnimation, EnsureBackgroundVideo());
		Storyboard.SetTargetProperty(doubleAnimation, "Opacity");
		Storyboard storyboard = new Storyboard();
		storyboard.Children.Add(doubleAnimation);
		storyboard.Begin();
	}

	private MediaPlayerElement EnsureBackgroundVideo()
	{
		if (_bgVideoElement == null)
		{
			MediaPlayerElement item = (_bgVideoElement = new MediaPlayerElement
			{
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Stretch,
				Stretch = Stretch.UniformToFill,
				Visibility = Visibility.Collapsed,
				IsHitTestVisible = false,
				AreTransportControlsEnabled = false,
				AutoPlay = false
			});
			ContentBackgroundGrid.Children.Insert(0, item);
		}
		return _bgVideoElement;
	}

	private void StopBackgroundVideo()
	{
		DetachBackgroundFromMainPlayer();
		_backgroundVideoPath = null;
		_bgCyclePath = null;
		_bgPendingPath = null;
		_bgPendingSource = null;
		if (_bgVideoElement != null)
		{
			_bgVideoElement.Visibility = Visibility.Collapsed;
			MediaPlayer mediaPlayer = _bgVideoElement.MediaPlayer;
			if (mediaPlayer != null)
			{
				mediaPlayer.Pause();
				mediaPlayer.Source = null;
			}
			ContentBackgroundGrid.Children.Remove(_bgVideoElement);
			_bgVideoElement = null;
		}
	}

	private void AttachBackgroundToMainPlayer()
	{
		if (!_bgSharingMainPlayer)
		{
			MediaPlayer player = MusicService.Player;
			MediaPlayer mediaPlayer = EnsureBackgroundVideo().MediaPlayer;
			if (mediaPlayer != null && (object)mediaPlayer != player)
			{
				mediaPlayer.Pause();
				mediaPlayer.Source = null;
			}
			EnsureBackgroundVideo().SetMediaPlayer(player);
			_bgSharingMainPlayer = true;
			_bgPendingPath = null;
			_bgPendingSource = null;
			_backgroundVideoPath = MusicService.CurrentPath;
			_bgCyclePath = null;
			EnsureBackgroundVideo().Visibility = Visibility.Visible;
			FadeInBackgroundVideo();
			LogHelper.Log("背景视频(跟随主播放器): " + MusicService.CurrentPath);
		}
	}

	private void DetachBackgroundFromMainPlayer()
	{
		if (_bgSharingMainPlayer)
		{
			MediaPlayer mediaPlayer = new MediaPlayer();
			mediaPlayer.SystemMediaTransportControls.IsEnabled = false;
			mediaPlayer.SystemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Closed;
			EnsureBackgroundVideo().SetMediaPlayer(mediaPlayer);
			_bgSharingMainPlayer = false;
			_bgEndedSubscribed = false;
			_bgOpenedSubscribed = false;
			_bgFailedSubscribed = false;
			_bgStateSubscribed = false;
		}
	}

	private void AnimateThemeRender()
	{
		Storyboard storyboard = new Storyboard();
		DoubleAnimation doubleAnimation = new DoubleAnimation
		{
			To = 0.55,
			Duration = TimeSpan.FromMilliseconds(140.0),
			EnableDependentAnimation = true
		};
		Storyboard.SetTarget(doubleAnimation, RootGrid);
		Storyboard.SetTargetProperty(doubleAnimation, "Opacity");
		storyboard.Children.Add(doubleAnimation);
		storyboard.Completed += delegate
		{
			Storyboard storyboard2 = new Storyboard();
			DoubleAnimation doubleAnimation2 = new DoubleAnimation
			{
				To = 1.0,
				Duration = TimeSpan.FromMilliseconds(260.0),
				EnableDependentAnimation = true
			};
			Storyboard.SetTarget(doubleAnimation2, RootGrid);
			Storyboard.SetTargetProperty(doubleAnimation2, "Opacity");
			storyboard2.Children.Add(doubleAnimation2);
			storyboard2.Begin();
		};
		storyboard.Begin();
	}

	private void UpdateCaptionButtons()
	{
		if ((object)_appWindow != null)
		{
			AppWindowTitleBar titleBar = _appWindow.TitleBar;
			if (RootGrid.ActualTheme == ElementTheme.Dark || RootGrid.RequestedTheme == ElementTheme.Dark)
			{
				titleBar.ButtonForegroundColor = Colors.White;
				titleBar.ButtonBackgroundColor = Color.FromArgb(38, 0, 0, 0);
				titleBar.ButtonHoverBackgroundColor = Color.FromArgb(77, 0, 0, 0);
				titleBar.ButtonHoverForegroundColor = Colors.White;
				titleBar.ButtonPressedBackgroundColor = Color.FromArgb(102, 0, 0, 0);
				titleBar.ButtonPressedForegroundColor = Colors.White;
				titleBar.ButtonInactiveForegroundColor = Color.FromArgb(179, byte.MaxValue, byte.MaxValue, byte.MaxValue);
			}
			else
			{
				titleBar.ButtonForegroundColor = Colors.Black;
				titleBar.ButtonBackgroundColor = Color.FromArgb(38, byte.MaxValue, byte.MaxValue, byte.MaxValue);
				titleBar.ButtonHoverBackgroundColor = Color.FromArgb(77, byte.MaxValue, byte.MaxValue, byte.MaxValue);
				titleBar.ButtonHoverForegroundColor = Colors.Black;
				titleBar.ButtonPressedBackgroundColor = Color.FromArgb(102, byte.MaxValue, byte.MaxValue, byte.MaxValue);
				titleBar.ButtonPressedForegroundColor = Colors.Black;
				titleBar.ButtonInactiveForegroundColor = Color.FromArgb(179, 0, 0, 0);
			}
		}
	}

	public static string? ResolveBackgroundPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		if (File.Exists(path))
		{
			return path;
		}
		string fileName = Path.GetFileName(path);
		string text = Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds", fileName);
		if (File.Exists(text))
		{
			return text;
		}
		string text2 = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
		if (File.Exists(text2))
		{
			return text2;
		}
		return null;
	}

	private void ShowPage(Type pageType)
	{
		if (!_pageCache.TryGetValue(pageType, out var value))
		{
			value = (Page)Activator.CreateInstance(pageType);
			_pageCache[pageType] = value;
		}
		if (!ReferenceEquals(ContentFrame.Content, value))
		{
			ContentFrame.Content = value;
		}
	}

	/// <summary>主题切换后刷新页面：清空页面缓存并按新主题重建当前页，使代码后置的界面立即跟随主题。</summary>
	private void RefreshPagesForTheme()
	{
		try
		{
			Type? currentType = null;
			if (ContentFrame?.Content is Page current)
			{
				foreach (KeyValuePair<Type, Page> kv in _pageCache)
				{
					if (kv.Value == current)
					{
						currentType = kv.Key;
						break;
					}
				}
			}
			_pageCache.Clear();
			if (currentType != null)
			{
				Page fresh = (Page)Activator.CreateInstance(currentType);
				_pageCache[currentType] = fresh;
				ContentFrame.Content = fresh;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "主题切换后刷新页面失败");
		}
	}

	private void InitializeMainNavigationIfNeeded()
	{
		if (_mainNavigationInitialized)
		{
			return;
		}
		_mainNavigationInitialized = true;
		NavView.MenuItems.Clear();
		AddNavItem(Symbol.Home, "HomePage");
		AddNavItem(Symbol.People, "LoginPage");
		AddNavItem(Symbol.Sync, "ProxyCapturePage");
		AddNavItem(Symbol.AllApps, "GroupsQuickPage");
		AddNavItem(Symbol.Library, "ServerListPage");
		AddNavItem(Symbol.Shop, "ShopPage");
		AddNavItem(Symbol.Download, "ClientUpdatePage");
		AddNavItem(Symbol.World, "SpeedTestPage");
		AddNavItem(Symbol.ContactInfo, "AboutPage");
		AddNavItem(Symbol.Repair, "ToolsPage");
		AddNavItem(Symbol.Document, "SessionLogPage");
		if (NavView.SettingsItem is NavigationViewItem navigationViewItem)
		{
			navigationViewItem.FontWeight = FontWeights.SemiBold;
			navigationViewItem.MinHeight = 48.0;
		}
		// 导航栏最底部：退出程序（锁定程序账号并关闭）
		NavigationViewItem exitItem = new NavigationViewItem
		{
			Icon = new FontIcon
			{
				Glyph = "\uE7E8"
			},
			Content = "退出程序",
			Tag = "ExitProgram",
			MinHeight = 48.0,
			FontWeight = FontWeights.SemiBold
		};
		exitItem.Tapped += async delegate
		{
			NavView.SelectedItem = _lastNavSelection;
			await AppLockDialogs.ShowExitProgramDialogAsync();
		};
		NavView.FooterMenuItems.Add(exitItem);
		foreach (object menuItem in NavView.MenuItems)
		{
			if (menuItem is NavigationViewItem { Tag: string tag } navigationViewItem2 && tag == "HomePage")
			{
				NavView.SelectedItem = navigationViewItem2;
				_lastNavSelection = navigationViewItem2;
				ShowPage(typeof(HomePage));
				break;
			}
		}
	}

	private void AddNavItem(Symbol icon, string key)
	{
		if (Pages.TryGetValue(key, out (Type, string) value))
		{
			NavView.MenuItems.Add(new NavigationViewItem
			{
				Icon = CreateBoldIcon(icon),
				Content = value.Item2,
				Tag = key,
				MinHeight = 48.0,
				FontWeight = FontWeights.SemiBold
			});
		}
	}

	private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
	{
		(Type, string) value;
		if (args.IsSettingsSelected)
		{
			RecordNavigation("Settings");
			_lastNavSelection = sender.SettingsItem;
			ShowPage(typeof(SettingsPage));
		}
		else if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Pages.TryGetValue(tag, out value))
		{
			RecordNavigation(tag);
			_lastNavSelection = args.SelectedItem;
			ShowPage(value.Item1);
		}
	}

	public void NavigateTo(string key)
	{
		if (!Pages.TryGetValue(key, out (Type, string) value))
		{
			return;
		}
		base.DispatcherQueue.TryEnqueue(delegate
		{
			RecordNavigation(key);
			ShowPage(value.Item1);
			foreach (object menuItem in NavView.MenuItems)
			{
				if (menuItem is NavigationViewItem { Tag: string tag } navigationViewItem && tag == key)
				{
					NavView.SelectedItem = navigationViewItem;
					_lastNavSelection = navigationViewItem;
					break;
				}
			}
		});
	}

	private void RecordNavigation(string newKey)
	{
		if (!(newKey == _currentPageKey))
		{
			if (_navHistory.Count >= 30)
			{
				_navHistory.RemoveAt(0);
			}
			_navHistory.Add(_currentPageKey);
			_currentPageKey = newKey;
		}
	}

	private void NavigateBack()
	{
		if (_navHistory.Count == 0)
		{
			return;
		}
		string text = _navHistory[_navHistory.Count - 1];
		_navHistory.RemoveAt(_navHistory.Count - 1);
		_currentPageKey = text;
		if (text == "Settings")
		{
			NavView.SelectedItem = NavView.SettingsItem;
			if (NavView.SelectedItem == null)
			{
				ShowPage(typeof(SettingsPage));
			}
		}
		else
		{
			if (!Pages.TryGetValue(text, out (Type, string) value))
			{
				return;
			}
			ShowPage(value.Item1);
			foreach (object menuItem in NavView.MenuItems)
			{
				if (menuItem is NavigationViewItem { Tag: string tag } navigationViewItem && tag == text)
				{
					NavView.SelectedItem = navigationViewItem;
					_lastNavSelection = navigationViewItem;
					break;
				}
			}
		}
	}

	private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Escape && _overlays.Count > 0)
		{
			CloseWindowOverlay(_overlays[_overlays.Count - 1]);
			e.Handled = true;
			return;
		}
		object focusedElement = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
		if (focusedElement is TextBox || focusedElement is PasswordBox || focusedElement is RichEditBox || focusedElement is AutoSuggestBox || focusedElement is ComboBox)
		{
			return;
		}
		if (e.Key == VirtualKey.Number1)
		{
			NavigateTo("HomePage");
			e.Handled = true;
		}
		else if (e.Key == VirtualKey.Number2)
		{
			NavigateTo("LoginPage");
			e.Handled = true;
		}
		else if (e.Key == VirtualKey.Number3)
		{
			NavigateTo("ProxyCapturePage");
			e.Handled = true;
		}
		else if (e.Key == VirtualKey.Number4)
		{
			NavigateTo("SessionLogPage");
			e.Handled = true;
		}
		else if (e.Key == VirtualKey.Number5)
		{
			NavigateTo("AboutPage");
			e.Handled = true;
		}
		else if (e.Key == VirtualKey.Number6)
		{
			NavigateTo("ToolsPage");
			e.Handled = true;
		}
		else if (e.Key == VirtualKey.W || e.Key == VirtualKey.A)
		{
			SetScrollDirection(-1);
			e.Handled = true;
		}
		else if (e.Key == VirtualKey.S || e.Key == VirtualKey.D)
		{
			SetScrollDirection(1);
			e.Handled = true;
		}
		else if (e.Key == VirtualKey.Escape)
		{
			if (_currentPageKey == "Settings")
			{
				if (SettingsManager.Instance.CloseKeepsRunning)
				{
					HideToTray();
				}
				else
				{
					ExitApp();
				}
			}
			else
			{
				OpenSettings();
			}
			e.Handled = true;
		}
		else if (e.Key == VirtualKey.Z && IsCtrlDown())
		{
			NavigateBack();
			e.Handled = true;
		}
	}

	private static bool IsCtrlDown()
	{
		return (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
	}

	private void ScrollCurrentPage(double delta)
	{
		if (ContentFrame.Content is DependencyObject node)
		{
			ScrollViewer scrollViewer = FindScrollableScrollViewer(node);
			if (scrollViewer != null)
			{
				double value = Math.Max(0.0, scrollViewer.VerticalOffset + delta);
				scrollViewer.ChangeView(null, value, null, disableAnimation: true);
			}
		}
	}

	private void SetScrollDirection(int direction)
	{
		if (_scrollDirection != direction || !(_scrollTimer != null))
		{
			_scrollDirection = direction;
			_scrollTimer?.Stop();
			_scrollTimer = base.DispatcherQueue.CreateTimer();
			_scrollTimer.Interval = TimeSpan.FromMilliseconds(24.0);
			_scrollTimer.IsRepeating = true;
			int dir = direction;
			_scrollTimer.Tick += delegate
			{
				ScrollCurrentPage(16.0 * (double)dir);
			};
			_scrollTimer.Start();
			ScrollCurrentPage(16.0 * (double)dir);
		}
	}

	private void RootGrid_KeyUp(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.W || e.Key == VirtualKey.A || e.Key == VirtualKey.S || e.Key == VirtualKey.D)
		{
			_scrollTimer?.Stop();
			_scrollTimer = null;
			_scrollDirection = 0;
		}
	}

	private static ScrollViewer? FindScrollableScrollViewer(DependencyObject node)
	{
		if (node is ScrollViewer { ScrollableHeight: >0.0 } scrollViewer)
		{
			return scrollViewer;
		}
		int childrenCount = VisualTreeHelper.GetChildrenCount(node);
		for (int i = 0; i < childrenCount; i++)
		{
			ScrollViewer scrollViewer2 = FindScrollableScrollViewer(VisualTreeHelper.GetChild(node, i));
			if (scrollViewer2 != null)
			{
				return scrollViewer2;
			}
		}
		return null;
	}

	public void ShowWindowOverlay(FrameworkElement overlay)
	{
		if (!(overlay == null))
		{
			Grid.SetRow(overlay, 0);
			Grid.SetRowSpan(overlay, 2);
			overlay.HorizontalAlignment = HorizontalAlignment.Stretch;
			overlay.VerticalAlignment = VerticalAlignment.Stretch;
			_overlays.Remove(overlay);
			RootGrid.Children.Remove(overlay);
			_overlays.Add(overlay);
			overlay.RenderTransform = new CompositeTransform
			{
				ScaleX = 0.94,
				ScaleY = 0.94,
				CenterX = RootGrid.ActualWidth / 2.0,
				CenterY = RootGrid.ActualHeight / 2.0
			};
			overlay.Opacity = 0.0;
			RootGrid.Children.Add(overlay);
			TimeSpan duration = TimeSpan.FromMilliseconds(180.0);
			Storyboard storyboard = new Storyboard();
			AddOverlayDoubleAnimation(storyboard, overlay, "Opacity", 0.0, 1.0, duration);
			AddOverlayDoubleAnimation(storyboard, overlay, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)", 0.94, 1.0, duration);
			AddOverlayDoubleAnimation(storyboard, overlay, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)", 0.94, 1.0, duration);
			storyboard.Begin();
		}
	}

	public void CloseWindowOverlay(FrameworkElement overlay)
	{
		if (!(overlay == null) && _overlays.Contains(overlay))
		{
			TimeSpan duration = TimeSpan.FromMilliseconds(160.0);
			Storyboard storyboard = new Storyboard();
			AddOverlayDoubleAnimation(storyboard, overlay, "Opacity", 1.0, 0.0, duration);
			AddOverlayDoubleAnimation(storyboard, overlay, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)", 1.0, 0.94, duration);
			AddOverlayDoubleAnimation(storyboard, overlay, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)", 1.0, 0.94, duration);
			storyboard.Completed += delegate
			{
				_overlays.Remove(overlay);
				RootGrid.Children.Remove(overlay);
				overlay.RenderTransform = null;
				overlay.Opacity = 1.0;
			};
			storyboard.Begin();
		}
	}

	private static void AddOverlayDoubleAnimation(Storyboard storyboard, FrameworkElement target, string property, double from, double to, TimeSpan duration)
	{
		DoubleAnimation doubleAnimation = new DoubleAnimation
		{
			From = from,
			To = to,
			Duration = duration,
			EnableDependentAnimation = true
		};
		Storyboard.SetTarget(doubleAnimation, target);
		Storyboard.SetTargetProperty(doubleAnimation, property);
		storyboard.Children.Add(doubleAnimation);
	}

	private static FontIcon CreateBoldIcon(Symbol symbol)
	{
		return new FontIcon
		{
			Glyph = ((char)symbol).ToString(),
			FontFamily = new FontFamily("Segoe MDL2 Assets"),
			FontWeight = FontWeights.Bold
		};
	}

	private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
	{
		switch (msg)
		{
		case 36u:
			Marshal.WriteInt32(lParam, 24, 720);
			Marshal.WriteInt32(lParam, 28, 540);
			break;
		case 274u:
		{
			nint num = wParam;
			if ((((IntPtr)num).ToInt64() & 0xFFF0) == 61472 && SettingsManager.Instance.MinimizeToTray && _instance?._trayIcon != IntPtr.Zero)
			{
				_instance?.HideToTray();
				return IntPtr.Zero;
			}
			break;
		}
		case 16u:
			if ((object)_instance != null && !_instance._exitRequested && SettingsManager.Instance.CloseKeepsRunning)
			{
				_instance.HideToTray();
				return IntPtr.Zero;
			}
			break;
		case 6u:
			if (wParam == IntPtr.Zero)
			{
				MainWindow instance = _instance;
				if ((object)instance != null && instance._wokeTopMost)
				{
					_instance._wokeTopMost = false;
					SetWindowPos(hWnd, HwndNotTopMost, 0, 0, 0, 0, 83u);
				}
			}
			break;
		case 32768u:
			_instance?.HandleTrayMessage(lParam);
			return IntPtr.Zero;
		default:
			if (msg == _taskbarCreatedMsg && _instance != null && SettingsManager.Instance.ShowTrayIcon)
			{
				_instance.RemoveTrayIcon();
				_instance.InitTrayIcon(hWnd);
			}
			break;
		case 2u:
			_instance?.RemoveTrayIcon();
			break;
		}
		return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern uint RegisterWindowMessage(string lpString);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

	[DllImport("user32.dll")]
	private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

	private void UpdateContentCardMargin(double paneLength)
	{
		double left = Math.Max(0.0, paneLength - 3.0);
		SetTranslateX(ContentCard, 0.0);
		ContentCard.Margin = new Thickness(left, -2.0, -1.0, -3.0);
		UpdateBackgroundSize(paneLength);
	}

	private void UpdateBackgroundSize(double paneLength)
	{
		double actualWidth = ContentBackgroundHost.ActualWidth;
		double actualHeight = ContentBackgroundHost.ActualHeight;
		if (!(actualWidth <= 0.0) && !(actualHeight <= 0.0))
		{
			double left = paneLength - 3.0;
			ContentBackgroundClipper.Margin = new Thickness(left, -2.0, -1.0, -3.0);
			PaneBackdrop.Width = paneLength;
		}
	}

	private void ContentBackgroundHost_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		UpdateContentCardMargin(NavView.OpenPaneLength);
	}

	private static void SetTranslateX(FrameworkElement element, double x)
	{
		if (element.RenderTransform is TranslateTransform translateTransform)
		{
			translateTransform.X = x;
		}
	}

	private static bool IsVideoPath(string path)
	{
		return VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
	}

	private void CenterNavMenuItems()
	{
		_centerAttempts = 0;
		TryCenterNavMenuItems();
	}

	private void TryCenterNavMenuItems()
	{
		try
		{
			List<object> list = new List<object>();
			CollectMenuHosts(NavView, list);
			if (_centerAttempts == 0)
			{
				LogHelper.Log("菜单居中树:\n" + DumpTree(NavView));
			}
			object obj = null;
			foreach (object item in list)
			{
				if (item is ListView listView && listView.Items.Count == NavView.MenuItems.Count)
				{
					obj = item;
					break;
				}
			}
			if (obj == null)
			{
				obj = ((list.Count > 0) ? list[0] : null);
			}
			if (obj is FrameworkElement frameworkElement)
			{
				frameworkElement.VerticalAlignment = VerticalAlignment.Center;
				LogHelper.Log($"菜单居中: 宿主={frameworkElement.GetType().Name} items={GetItemsCount(obj)} navChildren={VisualTreeHelper.GetChildrenCount(NavView)} 已居中");
			}
			else if (_centerAttempts < 20)
			{
				_centerAttempts++;
				if (_centerRetryTimer == null)
				{
					_centerRetryTimer = base.DispatcherQueue.CreateTimer();
					_centerRetryTimer.Interval = TimeSpan.FromMilliseconds(100.0);
					_centerRetryTimer.Tick += delegate
					{
						_centerRetryTimer.Stop();
						TryCenterNavMenuItems();
					};
				}
				_centerRetryTimer.Stop();
				_centerRetryTimer.Start();
			}
			else
			{
				LogHelper.Log($"菜单居中: 多次尝试仍未找到宿主 navChildren={VisualTreeHelper.GetChildrenCount(NavView)}");
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "菜单居中失败");
		}
	}

	private static void CollectMenuHosts(DependencyObject root, List<object> results)
	{
		int childrenCount = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < childrenCount; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			string name = child.GetType().Name;
			bool flag = child is ListView;
			if (!flag)
			{
				flag = ((name == "NavigationViewItemsControl" || name == "ItemsRepeaterScrollHost") ? true : false);
			}
			if (flag)
			{
				results.Add(child);
			}
			CollectMenuHosts(child, results);
		}
	}

	private static int GetItemsCount(object host)
	{
		if (!(host is ItemsControl itemsControl))
		{
			return 0;
		}
		return itemsControl.Items.Count;
	}

	private static string DumpTree(DependencyObject root)
	{
		StringBuilder stringBuilder = new StringBuilder();
		DumpTreeInner(root, stringBuilder, 0);
		return stringBuilder.ToString();
	}

	private static void DumpTreeInner(DependencyObject root, StringBuilder sb, int depth)
	{
		if (depth > 10)
		{
			return;
		}
		int childrenCount = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < childrenCount; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			string name = child.GetType().Name;
			if (name.Contains("Navigation") || name.Contains("Items") || name.Contains("List"))
			{
				sb.Append(new string(' ', depth * 2)).Append(name);
				if (child is ItemsControl itemsControl)
				{
					sb.Append(" items=").Append(itemsControl.Items.Count);
				}
				sb.AppendLine();
			}
			DumpTreeInner(child, sb, depth + 1);
		}
	}

	private void InitTrayIcon(nint hwnd)
	{
		try
		{
			string text = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
			nint num = LoadImage(IntPtr.Zero, text, 1u, 0, 0, 80u);
			if (num == IntPtr.Zero)
			{
				LogHelper.Log("托盘图标加载失败，使用默认图标: " + text);
				num = LoadIcon(IntPtr.Zero, new IntPtr(32512));
			}
			NotifyIconData lpdata = new NotifyIconData
			{
				cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
				hWnd = hwnd,
				uID = 1u,
				uFlags = 7u,
				uCallbackMessage = 32768u,
				hIcon = num,
				szTip = "N"
			};
			NotifyIconData lpdata2 = lpdata;
			Shell_NotifyIcon(2u, ref lpdata2);
			_trayIcon = num;
			bool flag = Shell_NotifyIcon(0u, ref lpdata);
			string[] array = new string[6]
			{
				"托盘图标初始化: added=",
				flag.ToString(),
				" hWnd=",
				null,
				null,
				null
			};
			nint num2 = hwnd;
			array[3] = ((IntPtr)num2).ToString();
			array[4] = " icon=";
			array[5] = text;
			LogHelper.Log(string.Concat(array));
			if (!flag)
			{
				base.DispatcherQueue.TryEnqueue(delegate
				{
					Microsoft.UI.Dispatching.DispatcherQueueTimer timer = base.DispatcherQueue.CreateTimer();
					int attempts = 0;
					timer.Interval = TimeSpan.FromSeconds(1.0);
					timer.Tick += delegate
					{
						attempts++;
						if (attempts >= 3 || Shell_NotifyIcon(0u, ref lpdata))
						{
							timer.Stop();
						}
					};
					timer.Start();
				});
			}
			NotifyIconData lpdata3 = new NotifyIconData
			{
				cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
				hWnd = hwnd,
				uID = 1u,
				uTimeoutOrVersion = 4u
			};
			Shell_NotifyIcon(4u, ref lpdata3);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "托盘图标初始化失败");
		}
	}

	private void RefreshAppIcon()
	{
		try
		{
			App.RegisterAppUserModelIdIcon();
			string text = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
			if (File.Exists(text))
			{
				_appWindow?.SetIcon(text);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "刷新窗口图标失败");
		}
	}

	private void RemoveTrayIcon()
	{
		if (_trayIcon != IntPtr.Zero)
		{
			try
			{
				NotifyIconData lpdata = new NotifyIconData
				{
					cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
					hWnd = WindowNative.GetWindowHandle(_instance),
					uID = 1u
				};
				Shell_NotifyIcon(2u, ref lpdata);
			}
			catch
			{
			}
			_trayIcon = IntPtr.Zero;
		}
	}

	private void SyncTrayIcon()
	{
		bool showTrayIcon = SettingsManager.Instance.ShowTrayIcon;
		if (showTrayIcon && _trayIcon == IntPtr.Zero)
		{
			InitTrayIcon(WindowNative.GetWindowHandle(this));
		}
		else if (!showTrayIcon && _trayIcon != IntPtr.Zero)
		{
			RemoveTrayIcon();
			if (_trayHidden)
			{
				BringToFront("syncTrayIcon");
			}
		}
	}

	private void HandleTrayMessage(nint lParam)
	{
		nint num = lParam;
		switch ((uint)((IntPtr)num).ToInt64())
		{
		case 514u:
		case 515u:
		{
			DateTime utcNow = DateTime.UtcNow;
			uint num2 = Math.Max(GetDoubleClickTime(), 650u);
			if (!((utcNow - _lastTrayClickTime).TotalMilliseconds < (double)num2))
			{
				if (_trayHidden && (utcNow - _lastHideUtc).TotalMilliseconds < 800.0)
				{
					LogHelper.Log("托盘点击已忽略（距隐藏过近）: " + (utcNow - _lastHideUtc).TotalMilliseconds.ToString("F0") + "ms");
					break;
				}
				if (!IsCursorOverTrayIcon())
				{
					LogHelper.Log("托盘点击已忽略（光标不在托盘图标上）");
					break;
				}
				_lastTrayClickTime = utcNow;
				ToggleTrayWindow();
			}
			break;
		}
		case 517u:
			ShowTrayMenu();
			break;
		}
	}

	private bool IsCursorOverTrayIcon()
	{
		try
		{
			NotifyIconIdentifier identifier = new NotifyIconIdentifier
			{
				cbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
				hWnd = WindowNative.GetWindowHandle(this),
				uID = 1u,
				guidItem = Guid.Empty
			};
			if (Shell_NotifyIconGetRect(ref identifier, out var iconLocation) != 0)
			{
				return true;
			}
			GetCursorPos(out var pt);
			return PtInRect(ref iconLocation, pt);
		}
		catch
		{
			return true;
		}
	}

	private void ToggleTrayWindow()
	{
		if (_trayHidden)
		{
			BringToFront("trayToggle");
		}
		else
		{
			HideToTray();
		}
	}

	public void HideToTrayOnStartup()
	{
		HideToTray();
	}

	private void HideToTray()
	{
		if ((object)_appWindow != null)
		{
			_appWindow.Hide();
			_trayHidden = true;
			_lastHideUtc = DateTime.UtcNow;
			LogHelper.Log("已最小化到托盘");
		}
	}

	private void ShowTrayMenu()
	{
		nint windowHandle = WindowNative.GetWindowHandle(this);
		nint hMenu = CreatePopupMenu();
		AppendMenuW(hMenu, 0u, 1u, "暂停");
		AppendMenuW(hMenu, 0u, 2u, "打开");
		AppendMenuW(hMenu, 0u, 3u, "设置");
		AppendMenuW(hMenu, 0u, 4u, "退出");
		SetForegroundWindow(windowHandle);
		GetCursorPos(out var pt);
		int num = (int)TrackPopupMenu(hMenu, 386u, pt.X, pt.Y, 0, windowHandle, IntPtr.Zero);
		DestroyMenu(hMenu);
		switch ((TrayCommand)num)
		{
		case TrayCommand.Pause:
			TogglePause();
			break;
		case TrayCommand.Open:
			BringToFront("trayMenuOpen");
			break;
		case TrayCommand.Settings:
			OpenSettings();
			break;
		case TrayCommand.Exit:
			ExitApp();
			break;
		}
	}

	private void OpenSettings()
	{
		BringToFront("openSettings");
		if (Environment.CurrentManagedThreadId == _uiThreadId)
		{
			NavView.SelectedItem = NavView.SettingsItem;
			if (NavView.SelectedItem == null)
			{
				ShowPage(typeof(SettingsPage));
			}
			return;
		}
		base.DispatcherQueue.TryEnqueue(delegate
		{
			NavView.SelectedItem = NavView.SettingsItem;
			if (NavView.SelectedItem == null)
			{
				ShowPage(typeof(SettingsPage));
			}
		});
	}

	private void TogglePause()
	{
		if (MusicService.IsPlaying)
		{
			MusicService.Pause();
		}
		else
		{
			MusicService.Resume();
		}
	}

	private void ExitApp()
	{
		_exitRequested = true;
		RemoveTrayIcon();
		try
		{
			Close();
		}
		catch
		{
			Environment.Exit(0);
		}
	}

	public static void RestartApplication()
	{
		try
		{
			string text = Environment.ProcessPath ?? "";
			if (string.IsNullOrWhiteSpace(text))
			{
				return;
			}
			string text2 = "";
			if (ProxyCaptureService.IsRunning && !string.IsNullOrWhiteSpace(ProxyCaptureService.TargetHost))
			{
				text2 = ServerPresets.All.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, ProxyCaptureService.TargetHost, StringComparison.OrdinalIgnoreCase))?.Name ?? "";
			}
			string text3 = (string.IsNullOrWhiteSpace(text2) ? "" : ("--capture \"" + text2 + "\""));
			if (!string.IsNullOrWhiteSpace(text3))
			{
				LogHelper.Log("[重启] 检测到代理运行中，重启后自动恢复: " + text3);
			}
			SingleInstance.Release();
			Process.Start(new ProcessStartInfo(text)
			{
				Arguments = text3,
				UseShellExecute = true
			});
			MainWindow instance = _instance;
			if ((object)instance != null)
			{
				instance._exitRequested = true;
			}
			_instance?.Close();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "重启程序失败");
			_instance?.ShowToast("重启程序失败：" + ex.Message);
		}
	}

	public void BringToFront(string source)
	{
		LogHelper.Log("唤醒窗口: 显示并置顶 [src=" + source + "]");
		nint windowHandle = WindowNative.GetWindowHandle(this);
		bool flag = !IsWindowVisible(windowHandle);
		if (flag)
		{
			_appWindow?.Show();
		}
		_trayHidden = false;
		ShowWindow(windowHandle, 9);
		SetForegroundWindow(windowHandle);
		if (flag)
		{
			FadeInWindow();
		}
		StartTopMostFlash(windowHandle);
	}

	private void FadeInWindow()
	{
		if (Environment.CurrentManagedThreadId != _uiThreadId)
		{
			base.DispatcherQueue.TryEnqueue(delegate
			{
				FadeInWindowCore();
			});
		}
		else
		{
			FadeInWindowCore();
		}
	}

	private void FadeInWindowCore()
	{
		try
		{
			RootGrid.Opacity = 0.0;
			Storyboard storyboard = new Storyboard();
			DoubleAnimation doubleAnimation = new DoubleAnimation
			{
				From = 0.0,
				To = 1.0,
				Duration = TimeSpan.FromMilliseconds(220.0),
				EnableDependentAnimation = true
			};
			Storyboard.SetTarget(doubleAnimation, RootGrid);
			Storyboard.SetTargetProperty(doubleAnimation, "Opacity");
			storyboard.Children.Add(doubleAnimation);
			storyboard.Begin();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "窗口淡入动画失败");
			RootGrid.Opacity = 1.0;
		}
	}

	public void WakeToFront()
	{
		LogHelper.Log("唤醒窗口: 置顶显示 [src=secondInstance]");
		nint windowHandle = WindowNative.GetWindowHandle(this);
		if (!IsWindowVisible(windowHandle))
		{
			_appWindow?.Show();
		}
		_trayHidden = false;
		_wokeTopMost = true;
		SetWindowPos(windowHandle, HwndTopMost, 0, 0, 0, 0, 83u);
	}

	private void StartTopMostFlash(nint hwnd)
	{
		SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, 83u);
		if ((object)_topMostTimer == null)
		{
			_topMostTimer = CreateTopMostTimer();
		}
		_topMostTimer.Stop();
		_topMostTimer.Start();
	}

	private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateTopMostTimer()
	{
		Microsoft.UI.Dispatching.DispatcherQueueTimer timer = base.DispatcherQueue.CreateTimer();
		timer.Interval = TimeSpan.FromMilliseconds(700.0);
		timer.Tick += delegate
		{
			timer.Stop();
			try
			{
				SetWindowPos(WindowNative.GetWindowHandle(_instance), HwndNotTopMost, 0, 0, 0, 0, 83u);
			}
			catch
			{
			}
		};
		return timer;
	}

	[DllImport("shell32.dll")]
	private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpdata);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint LoadImage(nint hInst, string lpszName, uint type, int cx, int cy, uint fuLoad);

	[DllImport("user32.dll")]
	private static extern nint LoadIcon(nint hInstance, nint lpIconName);

	[DllImport("user32.dll")]
	private static extern nint CreatePopupMenu();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool AppendMenuW(nint hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

	[DllImport("user32.dll")]
	private static extern bool DestroyMenu(nint hMenu);

	[DllImport("user32.dll")]
	private static extern nint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT pt);

	[DllImport("user32.dll")]
	private static extern uint GetDoubleClickTime();

	[DllImport("user32.dll")]
	private static extern bool PtInRect(ref RECT lprc, POINT pt);

	[DllImport("shell32.dll")]
	private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out RECT iconLocation);

	public void ShowToast(string message, bool? ok = null)
	{
		// 中性提示跟随主题：浅色=纯白底+灰边+深字、深色=深底白字；
		// 成功/失败固定绿/红底白字（两种主题下都高对比）
		byte bgA, bgR, bgG, bgB, fgR, fgG, fgB, bdR, bdG, bdB, bdA;
		if (ok == true)
		{
			(bgA, bgR, bgG, bgB) = (255, 21, 122, 57);
			(fgR, fgG, fgB) = (255, 255, 255);
			(bdA, bdR, bdG, bdB) = (90, 10, 70, 35);
		}
		else if (ok == false)
		{
			(bgA, bgR, bgG, bgB) = (255, 179, 38, 30);
			(fgR, fgG, fgB) = (255, 255, 255);
			(bdA, bdR, bdG, bdB) = (90, 110, 22, 18);
		}
		else if (RootGrid.ActualTheme == ElementTheme.Dark)
		{
			(bgA, bgR, bgG, bgB) = (250, 28, 28, 28);
			(fgR, fgG, fgB) = (255, 255, 255);
			(bdA, bdR, bdG, bdB) = (70, 255, 255, 255);
		}
		else
		{
			(bgA, bgR, bgG, bgB) = (255, 255, 255, 255);
			(fgR, fgG, fgB) = (10, 10, 10);
			(bdA, bdR, bdG, bdB) = (80, 0, 0, 0);
		}
		ToastHost.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(bgA, bgR, bgG, bgB));
		ToastHost.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(bdA, bdR, bdG, bdB));
		ToastText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, fgR, fgG, fgB));
		ToastText.Text = message;
		ToastHost.Visibility = Visibility.Visible;
		// Popup 最外层显示：确保 XamlRoot/尺寸并保持打开（ToastHost 自身控制显隐）
		try
		{
			if (_toastPopup != null && _toastPopupRoot != null)
			{
				if (_toastPopup.XamlRoot == null)
				{
					_toastPopup.XamlRoot = RootGrid.XamlRoot;
				}
				if (RootGrid.ActualWidth > 0)
				{
					_toastPopupRoot.Width = RootGrid.ActualWidth;
					_toastPopupRoot.Height = RootGrid.ActualHeight;
				}
				if (!_toastPopup.IsOpen)
				{
					_toastPopup.IsOpen = true;
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[界面] Toast Popup 打开失败");
		}
		if ((object)_toastTimer == null)
		{
			_toastTimer = CreateToastTimer();
		}
		_toastTimer.Stop();
		_toastTimer.Start();
	}

	private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateToastTimer()
	{
		Microsoft.UI.Dispatching.DispatcherQueueTimer timer = base.DispatcherQueue.CreateTimer();
		timer.Interval = TimeSpan.FromMilliseconds(2600.0);
		timer.Tick += delegate
		{
			timer.Stop();
			ToastHost.Visibility = Visibility.Collapsed;
		};
		return timer;
	}

	private void StartTitleRevertTimer()
	{
		_titleRevertTimer?.Stop();
		Microsoft.UI.Dispatching.DispatcherQueueTimer timer = base.DispatcherQueue.CreateTimer();
		timer.Interval = TimeSpan.FromMinutes(1.0);
		timer.Tick += delegate
		{
			timer.Stop();
			AppTitleTextBlock.Text = "MhyUnbox";
			_appWindow.Title = "MhyUnbox";
			ToolTipService.SetToolTip(AppTitleIcon, "MhyUnbox");
		};
		_titleRevertTimer = timer;
		timer.Start();
	}

	private void OnMusicServiceStateChanged()
	{
		try
		{
			base.DispatcherQueue.TryEnqueue(delegate
			{
				UpdateMiniPlayer();
				SyncBackgroundVideo();
				SyncBackgroundVideoTransport();
			});
		}
		catch
		{
		}
	}

	private void UpdateMiniPlayer()
	{
		MiniPlayPauseButton.Content = (MusicService.IsPlaying ? "暂停" : "播放");
		MiniPlayModeButton.Content = PlayModeLabel(SettingsManager.Instance.PlaybackMode);
		string currentPath = MusicService.CurrentPath;
		string text = (MusicService.NoMusicMode ? "不播放任何音乐（仅背景视频）" : ((!string.IsNullOrWhiteSpace(currentPath)) ? (MusicService.IsPlaying ? ("当前正在播放: " + Path.GetFileName(currentPath)) : ("已暂停: " + Path.GetFileName(currentPath))) : "未在播放"));
		if (MiniTrackText.Text != text)
		{
			MiniTrackText.Text = text;
			AnimateMiniTrackText();
		}
	}

	private void AnimateMiniTrackText()
	{
		try
		{
			MiniTrackText.Opacity = 0.0;
			DoubleAnimation doubleAnimation = new DoubleAnimation
			{
				From = 0.0,
				To = 1.0,
				Duration = TimeSpan.FromMilliseconds(280.0),
				EnableDependentAnimation = true,
				EasingFunction = new CubicEase
				{
					EasingMode = EasingMode.EaseOut
				}
			};
			Storyboard.SetTarget(doubleAnimation, MiniTrackText);
			Storyboard.SetTargetProperty(doubleAnimation, "Opacity");
			Storyboard storyboard = new Storyboard();
			storyboard.Children.Add(doubleAnimation);
			storyboard.Begin();
		}
		catch
		{
			MiniTrackText.Opacity = 1.0;
		}
	}

	private void MiniVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
	{
		MiniVolumeText.Text = $"{(int)Math.Round(e.NewValue)}%";
		MusicService.SetVolume(e.NewValue / 100.0);
	}

	private void MiniLibraryButton_Click(object sender, RoutedEventArgs e)
	{
		MusicService.InvalidateScanCache();
		MusicService.RefreshKnownMusic();
		ShowTrackFlyout(MiniLibraryButton, MusicService.KnownMusic, MusicService.IsVideoSource ? "未找到音乐或MP4" : "未找到音乐");
	}

	private void MiniReloadButton_Click(object sender, RoutedEventArgs e)
	{
		MusicService.ReloadLibrary();
		ShowToast($"已刷新音乐库: {MusicService.KnownMusic.Count} 首音乐/视频");
	}

	private void ShowTrackFlyout(Button anchor, IReadOnlyList<string> items, string emptyText)
	{
		Flyout flyout = new Flyout
		{
			Placement = FlyoutPlacementMode.Top
		};
		double num = anchor.XamlRoot?.Size.Width ?? 1000.0;
		double num2 = anchor.XamlRoot?.Size.Height ?? 700.0;
		ListView listView = new ListView
		{
			MinWidth = Math.Max(220.0, Math.Min(320.0, num - 120.0)),
			MaxWidth = Math.Max(320.0, num - 80.0),
			MaxHeight = Math.Max(180.0, num2 - 140.0),
			SelectionMode = ListViewSelectionMode.None,
			IsItemClickEnabled = true
		};
		foreach (string item in items)
		{
			listView.Items.Add(BuildLibraryItem(item));
		}
		if (items.Count == 0)
		{
			listView.Items.Add(new ListViewItem
			{
				Content = emptyText,
				IsEnabled = false
			});
		}
		listView.ItemClick += delegate(object _, ItemClickEventArgs args)
		{
			MusicService.LastFlyoutClickTicks = Environment.TickCount64;
			if (args.ClickedItem is ListViewItem { Tag: string tag })
			{
				MusicService.Play(tag);
			}
			flyout.Hide();
		};
		listView.RightTapped += delegate(object _, RightTappedRoutedEventArgs args)
		{
			ListViewItem listViewItem = FindLibraryItemContainer(args.OriginalSource as DependencyObject);
			if ((object)listViewItem != null && listViewItem.Tag is string path)
			{
				MusicService.ToggleSkip(path);
				UpdateLibraryItemVisual(listViewItem, path);
				args.Handled = true;
			}
		};
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 8.0
		};
		stackPanel.Children.Add(BuildFlyoutHeader(MusicService.IsVideoSource ? "MhyUnBox 音乐库 / 视频" : "MhyUnBox 音乐库"));
		stackPanel.Children.Add(new Border
		{
			Height = 1.0,
			Background = new SolidColorBrush(Colors.Gray),
			Opacity = 0.2
		});
		stackPanel.Children.Add(listView);
		flyout.Content = stackPanel;
		flyout.ShowAt(anchor);
	}

	private static FrameworkElement BuildFlyoutHeader(string title)
	{
		Grid grid = new Grid
		{
			ColumnSpacing = 8.0
		};
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		try
		{
			string text = Path.Combine(AppContext.BaseDirectory, "Assets", "reimu.jpg");
			if (File.Exists(text))
			{
				Image image = new Image
				{
					Source = new BitmapImage(new Uri(text)),
					Width = 22.0,
					Height = 22.0,
					Stretch = Stretch.UniformToFill,
					VerticalAlignment = VerticalAlignment.Center
				};
				Grid.SetColumn(image, 0);
				grid.Children.Add(image);
			}
		}
		catch
		{
		}
		TextBlock textBlock = new TextBlock
		{
			Text = title,
			FontSize = 14.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(textBlock, 1);
		grid.Children.Add(textBlock);
		return grid;
	}

	private static ListViewItem BuildLibraryItem(string path)
	{
		TextBlock textBlock = new TextBlock
		{
			Text = LibraryItemText(path),
			TextTrimming = TextTrimming.CharacterEllipsis,
			FontSize = 13.0
		};
		if (MusicService.IsSkipped(path))
		{
			textBlock.Foreground = new SolidColorBrush(Colors.Red);
		}
		return new ListViewItem
		{
			Content = textBlock,
			Tag = path
		};
	}

	private static void UpdateLibraryItemVisual(ListViewItem item, string path)
	{
		if (item.Content is TextBlock textBlock)
		{
			textBlock.Text = LibraryItemText(path);
			if (MusicService.IsSkipped(path))
			{
				textBlock.Foreground = new SolidColorBrush(Colors.Red);
			}
			else
			{
				textBlock.ClearValue(TextBlock.ForegroundProperty);
			}
		}
	}

	private static string LibraryItemText(string path)
	{
		return Path.GetFileName(path);
	}

	private static ListViewItem? FindLibraryItemContainer(DependencyObject? start)
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

	private void MiniPlayPauseButton_Click(object sender, RoutedEventArgs e)
	{
		if (MusicService.IsPlaying)
		{
			MusicService.Pause();
		}
		else
		{
			MusicService.Resume();
		}
	}

	private void MiniPlayModeButton_Click(object sender, RoutedEventArgs e)
	{
		AppSettings instance = SettingsManager.Instance;
		string playbackMode = instance.PlaybackMode;
		string playbackMode2 = ((!(playbackMode == "random")) ? ((!(playbackMode == "sequential")) ? "random" : "loop") : "sequential");
		instance.PlaybackMode = playbackMode2;
		SettingsManager.Save(instance);
		UpdateMiniPlayer();
		ShowToast("播放模式：" + PlayModeLabel(instance.PlaybackMode));
	}

	private static string PlayModeLabel(string mode)
	{
		if (!(mode == "sequential"))
		{
			if (mode == "loop")
			{
				return "单曲循环";
			}
			return "随机";
		}
		return "列表循环";
	}

	public void ScheduleGameProcessDetection()
	{
		if (!SettingsManager.Instance.DetectGameProcesses || SettingsManager.Instance.GameDetectedThisBoot)
		{
			LogHelper.Log("[进程检测] 调度跳过：开关=" + SettingsManager.Instance.DetectGameProcesses + " 本次开机已检测=" + SettingsManager.Instance.GameDetectedThisBoot);
			return;
		}
		LogHelper.Log("[进程检测] 已调度：8 秒后执行本次开机进程检测");
		_gameDetectionTimer?.Stop();
		_gameDetectionTimer = base.DispatcherQueue.CreateTimer();
		_gameDetectionTimer.Interval = TimeSpan.FromSeconds(8.0);
		_gameDetectionTimer.IsRepeating = false;
		_gameDetectionTimer.Tick += async delegate
		{
			await RunGameProcessDetectionAsync();
		};
		_gameDetectionTimer.Start();
	}

	private async Task RunGameProcessDetectionAsync()
	{
		try
		{
			AppSettings instance = SettingsManager.Instance;
			if (!instance.DetectGameProcesses || instance.GameDetectedThisBoot)
			{
				return;
			}
			instance.GameDetectedThisBoot = true;
			SettingsManager.Save(instance);
			List<GameProcessDetector.FoundGroup> found = await Task.Run((Func<List<GameProcessDetector.FoundGroup>>)GameProcessDetector.FindRunning);
			if (found.Count == 0)
			{
				LogHelper.Log("[进程检测] 未检测到发烧游戏平台 / 网易我的世界");
				return;
			}
			string names = string.Join("、", found.Select((GameProcessDetector.FoundGroup g) => g.Label + "（" + g.Processes.Count + " 个进程）"));
			LogHelper.Log("[进程检测] 检测到 " + names + "，准备弹窗询问");
			if (await new ContentDialog
			{
				Title = "检测到相关游戏进程",
				Content = "正在运行：" + names + "。\n\n是否立即结束这些进程？\n（每次开机仅提醒一次，可在「设置 - 启动行为」中关闭本检测）",
				PrimaryButtonText = "结束进程",
				CloseButtonText = "忽略",
				DefaultButton = ContentDialogButton.Primary,
				RequestedTheme = AppElementTheme,
				XamlRoot = RootGrid.XamlRoot
			}.ShowAsync() == ContentDialogResult.Primary)
			{
				await Task.Run(delegate
				{
					foreach (GameProcessDetector.FoundGroup item in found)
					{
						GameProcessDetector.Kill(item.Processes);
					}
				});
				LogHelper.Log("[进程检测] 用户选择结束进程：" + names);
				await Task.Delay(2000);
				List<GameProcessDetector.FoundGroup> list = await Task.Run((Func<List<GameProcessDetector.FoundGroup>>)GameProcessDetector.FindRunning);
				if (list.Count > 0)
				{
					string text = string.Join("、", list.Select((GameProcessDetector.FoundGroup g) => g.Label + "（" + g.Processes.Count + " 个进程）"));
					LogHelper.Log("[进程检测] 结束后仍存活：" + text + "（可能被守护/平台重新拉起，可稍后重试）");
					ShowToast("已结束部分进程，仍有存活：" + text);
				}
				else
				{
					LogHelper.Log("[进程检测] 相关进程已全部结束");
					ShowToast("已结束相关游戏进程");
				}
			}
			else
			{
				LogHelper.Log("[进程检测] 用户选择忽略：" + names);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			LogHelper.LogError(ex2, "[进程检测] 失败");
		}
	}

	
	
	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	[DebuggerNonUserCode]
	public IComponentConnector GetBindingConnector(int connectionId, object target)
	{
		return null;
	}
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Services;
using MhyunBox.Settings;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MhyunBox.Pages;

/// <summary>
/// 首页「盐值与版本」卡片（代码后置构建，不依赖 XAML 编译）。
/// 功能：盐值/版本填入框（默认 = 最早第一次成功会话的值）、Alane 地址框（首位默认地址）、
/// 404 授权服地址框 + 账号登录 + 三层加密原理、HWID 设备限制解除模式、官方启动器扫描状态、
/// 按优先级链立即获取盐值、各输入框下方可用性反馈。
/// </summary>
public sealed class SaltSourcePanel : StackPanel
{
	private readonly DispatcherQueue _queue;

	private TextBox _saltBox;

	private TextBox _versionBox;

	private TextBlock _saltFeedback;

	private TextBox _alaneBox;

	private TextBlock _alaneFeedback;

	private TextBox _auth404Box;

	private TextBox _auth404UserBox;

	private Button _auth404LoginButton;

	private PasswordBox _auth404PassBox;

	private TextBlock _auth404Feedback;

	private ComboBox _hwidModeBox;

	private string? _lastLauncherExe;

	private TextBlock _officialStatusText;

	private TextBlock _overallStatusText;

	private TextBlock _compareText;

	private Button _watchBtn;

	private Button _memScanBtn;

	private Button _memStopBtn;

	private ComboBox _memSecondsBox;

	private TextBlock _memStatusText;

	private TextBox _blacklistAddBox;

	private TextBlock _blacklistCountText;

	private Button _memLogBtn;

	private StackPanel _memCandidates;

	private StackPanel _memCandidateRows;

	private CancellationTokenSource _memScanCts;

	private Button _captureBtn;

	private Button _captureStopBtn;

	private ComboBox _captureSecondsBox;

	private TextBlock _captureText;

	private Button _fetchButton;

	private Border _card;

	public SaltSourcePanel()
	{
		_queue = DispatcherQueue.GetForCurrentThread();
		Orientation = Orientation.Vertical;
		Spacing = 12.0;
		Margin = new Thickness(0, 12, 0, 24);
		// 与代理功能一致：未激活（卡密）时不暴露盐值 / 版本这块内容
		try
		{
			ActivationService.StateChanged += OnActivationChanged;
		}
		catch
		{
		}
		base.Unloaded += OnUnloaded;
		RebuildForActivation();
	}

	/// <summary>按激活状态重建：未激活时只给一句提示，激活后才构建真正的面板。</summary>
	private void RebuildForActivation()
	{
		Children.Clear();
		if (!ActivationService.IsActivated)
		{
			Children.Add(new TextBlock
			{
				Text = "盐值与版本：未激活，无法使用。请先在首页右侧「代理入口 / 激活」输入卡密激活。",
				FontSize = 12.0,
				Opacity = 0.85,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 12, 0, 12)
			});
			return;
		}
		Build();
		LoadFromSettings();
		try
		{
			CrcSaltSourceManager.StateChanged += OnSourceStateChanged;
		}
		catch
		{
		}
	}

	private void OnActivationChanged()
	{
		try
		{
			_queue.TryEnqueue(RebuildForActivation);
		}
		catch
		{
		}
	}

	// ---------- UI 构建 ----------

	private void Build()
	{
		Border card = new Border
		{
			Background = TryThemeBrush("CardBackgroundFillColorDefaultBrush"),
			BorderBrush = TryThemeBrush("CardStrokeColorDefaultBrush"),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(12.0),
			Padding = new Thickness(20.0)
		};
		// 卡片背景/描边只在构建时取过一次色。若构建时主题尚未生效（如启动早期或激活态在主题切换后才刷新），
		// 会拿到浅色画笔且之后切深色不会自动刷新（XAML 用 {ThemeResource} 会自适应，代码后置的固定画笔不会）。
		// 这里订阅 ActualThemeChanged：主题一变立即按“当前生效主题”重新取色，避免深色下这张卡仍是白的。
		if (_card != null)
		{
			_card.ActualThemeChanged -= Card_ActualThemeChanged;
		}
		_card = card;
		card.ActualThemeChanged += Card_ActualThemeChanged;
		StackPanel root = new StackPanel
		{
			Spacing = 10.0
		};
		root.Children.Add(new TextBlock
		{
			Text = "盐值与版本（CrcSalt 获取）",
			FontSize = 14.0,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
		});
		root.Children.Add(new TextBlock
		{
			Text = "优先级链：手输盐值 → Alane（首位，不被覆盖）→ 404授权服（三层Fernet）→ 网易官方DLL（控制端口索取）。进游戏报 status=0x11（盐值对不上/版本号不对）时自动切换到下一个源重试；地址不可用自动回退之前可用地址。",
			FontSize = 12.0,
			Opacity = 0.8,
			TextWrapping = TextWrapping.Wrap
		});
		// —— 当前盐值 / 版本 ——
		root.Children.Add(SectionHeader("当前盐值 / 启动器版本"));
		Grid saltGrid = new Grid
		{
			ColumnSpacing = 8.0
		};
		saltGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(280.0)
		});
		saltGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(150.0)
		});
		saltGrid.ColumnDefinitions.Add(new ColumnDefinition());
		_saltBox = new TextBox
		{
			PlaceholderText = "32 位十六进制盐值（留空=自动获取）",
			MaxLength = 32
		};
		SetColumn(_saltBox, 0);
		_versionBox = new TextBox
		{
			PlaceholderText = "启动器版本",
			MaxLength = 24
		};
		SetColumn(_versionBox, 1);
		Button saveBtn = new Button
		{
			Content = "保存并启用"
		};
		saveBtn.Click += SaveSalt_Click;
		SetColumn(saveBtn, 2);
		saltGrid.Children.Add(_saltBox);
		saltGrid.Children.Add(_versionBox);
		saltGrid.Children.Add(saveBtn);
		root.Children.Add(saltGrid);
		_saltFeedback = FeedbackText();
		root.Children.Add(_saltFeedback);
		// —— Alane ——
		root.Children.Add(SectionHeader("Alane 盐值请求地址（首位）"));
		_alaneBox = new TextBox
		{
			PlaceholderText = CrcSaltSourceManager.DefaultAlaneAddress
		};
		_alaneBox.LostFocus += AlaneBox_LostFocus;
		root.Children.Add(_alaneBox);
		_alaneFeedback = FeedbackText();
		root.Children.Add(_alaneFeedback);
		// —— 404 ——
		root.Children.Add(SectionHeader("404盐值请求"));
		_auth404Box = new TextBox
		{
			PlaceholderText = Auth404Client.DefaultBaseUrl + "（备用 " + Auth404Client.DefaultBackupUrl + " 自动回退）"
		};
		_auth404Box.LostFocus += Auth404Box_LostFocus;
		root.Children.Add(_auth404Box);
		Grid loginGrid = new Grid
		{
			ColumnSpacing = 8.0,
			Margin = new Thickness(0, 4, 0, 0)
		};
		loginGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(160.0)
		});
		loginGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(160.0)
		});
		loginGrid.ColumnDefinitions.Add(new ColumnDefinition());
		_auth404UserBox = new TextBox
		{
			PlaceholderText = "404 账号（回车 → 密码）"
		};
		// 与账号绑定那里一致：输完账号按回车跳到密码框
		_auth404UserBox.KeyDown += Auth404UserBox_KeyDown;
		SetColumn(_auth404UserBox, 0);
		_auth404PassBox = new PasswordBox
		{
			PlaceholderText = "密码（回车 → 登录）"
		};
		// 密码框里按回车直接响应登录按钮
		_auth404PassBox.KeyDown += Auth404PassBox_KeyDown;
		SetColumn(_auth404PassBox, 1);
		Button loginBtn = new Button
		{
			Content = "登录 404"
		};
		loginBtn.Click += Auth404Login_Click;
		_auth404LoginButton = loginBtn;
		SetColumn(loginBtn, 2);
		loginGrid.Children.Add(_auth404UserBox);
		loginGrid.Children.Add(_auth404PassBox);
		loginGrid.Children.Add(loginBtn);
		root.Children.Add(loginGrid);
		_auth404Feedback = FeedbackText();
		root.Children.Add(_auth404Feedback);
		// —— HWID 设备限制解除 ——
		Grid hwidGrid = new Grid
		{
			ColumnSpacing = 8.0
		};
		hwidGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		hwidGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(150.0)
		});
		hwidGrid.ColumnDefinitions.Add(new ColumnDefinition());
		hwidGrid.Children.Add(new TextBlock
		{
			Text = "HWID 设备限制解除：",
			VerticalAlignment = VerticalAlignment.Center,
			FontSize = 12.0
		});
		_hwidModeBox = new ComboBox
		{
			// 默认 = 真实 HWID
			SelectedIndex = 0
		};
		ThemeHelper.SolidComboPopup(_hwidModeBox);
		_hwidModeBox.Items.Add(new ComboBoxItem
		{
			Content = "真实 HWID"
		});
		_hwidModeBox.Items.Add(new ComboBoxItem
		{
			Content = "按账号伪装(推荐)"
		});
		_hwidModeBox.Items.Add(new ComboBoxItem
		{
			Content = "每次随机"
		});
		_hwidModeBox.Items.Add(new ComboBoxItem
		{
			Content = "固定值"
		});
		_hwidModeBox.SelectionChanged += HwidMode_SelectionChanged;
		SetColumn(_hwidModeBox, 1);
		hwidGrid.Children.Add(_hwidModeBox);
		root.Children.Add(hwidGrid);
		root.Children.Add(new TextBlock
		{
			Text = "官方 HWID=sha256(\"404-launcher-machine-guid-v2|\"+MachineGuid)。伪装/随机模式向服务端呈现虚拟设备指纹，绕过按设备的绑定与数量限制；伪装模式同一账号保持同一虚拟设备，避免设备数暴涨。",
			FontSize = 12.0,
			Opacity = 0.8,
			TextWrapping = TextWrapping.Wrap
		});
		// —— 官方启动器 ——
		root.Children.Add(SectionHeader("网易官方启动器（DLL 逆向索取，无效）"));
		// 三个按钮合并为一个：内部按「拉起发烧平台 → 拉起我的世界启动器 → 取盐」顺序自动走
		Button officialBtn = new Button
		{
			Content = "一键：平台→启动器→取盐"
		};
		officialBtn.Click += OfficialOneClick_Click;
		ToolTipService.SetToolTip(officialBtn, "游戏已在跑就直接取盐；否则依次拉起发烧游戏平台与我的世界启动器");
		root.Children.Add(officialBtn);
		// 守候抢连：控制端口只在游戏启动瞬间监听，必须在进程出现的第一时间去连
		_watchBtn = new Button
		{
			Content = "守候+抓包取盐（先点我，再去启动游戏）"
		};
		_watchBtn.Click += WatchOfficial_Click;
		ToolTipService.SetToolTip(_watchBtn, "同时做两件事：守候抢连 + 抓全回环流量。点完马上去启动游戏；即使抢连失败，也能从抓到的真实报文里看到游戏发的登录帧");
		root.Children.Add(_watchBtn);
		_officialStatusText = new TextBlock
		{
			Text = "尚未扫描",
			FontSize = 12.0,
			Opacity = 0.85,
			TextWrapping = TextWrapping.Wrap
		};
		root.Children.Add(_officialStatusText);

		// ---- 内存扫描（独立于调试模式，随时可用）----
		root.Children.Add(SectionHeader("内存扫盐值(需下载网易发烧我的世界启动器启动游戏)"));
		root.Children.Add(new TextBlock
		{
			Text = "※ 网易启动器普遍会掉线（一般只能存活 2~3 分钟），但这段时间**足够扫到盐值**——"
				+ "只要游戏已经完全进入服务器、角色可以活动，就立刻点「开始扫描」。"
				+ "还在加载/排队阶段扫到的不算。",
			FontSize = 12.0,
			Opacity = 0.85,
			TextWrapping = TextWrapping.Wrap,
			Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 183, 77)),
			Margin = new Thickness(0, 0, 0, 4)
		});
		// 用 StackPanel 而不是 Grid：Grid 的 Auto 列在父容器变窄时会挤压按钮，
		// 导致文字被裁掉（此前「扫描游戏内存取盐」就糊成一片）。
		// 另外按钮文案不要和分区标题重复——标题已经说了"内存扫描"，按钮就该短。
		StackPanel memBar = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		_memScanBtn = new Button
		{
			Content = "开始扫描",
			MinWidth = 96.0
		};
		_memScanBtn.Click += MemScan_Click;
		ToolTipService.SetToolTip(_memScanBtn, "扫描正在运行的 javaw.exe 内存，找 byte[16] 形态的 CrcSalt（不需要开调试模式）");
		_memStopBtn = new Button
		{
			Content = "停止",
			MinWidth = 64.0,
			IsEnabled = false
		};
		_memStopBtn.Click += MemStop_Click;
		ToolTipService.SetToolTip(_memStopBtn, "立即中止扫描并输出已扫到的部分");
		_memSecondsBox = new ComboBox
		{
			MinWidth = 108.0
		};
		ThemeHelper.SolidComboPopup(_memSecondsBox);
		_memLogBtn = new Button
		{
			Content = "打开扫描日志",
			MinWidth = 116.0
		};
		_memLogBtn.Click += OpenMemLog_Click;
		ToolTipService.SetToolTip(_memLogBtn, "打开最近一次内存扫描保存的候选清单");
		memBar.Children.Add(_memScanBtn);
		memBar.Children.Add(_memStopBtn);
		memBar.Children.Add(_memSecondsBox);
		memBar.Children.Add(_memLogBtn);
		root.Children.Add(memBar);
		_memStatusText = new TextBlock
		{
			Text = "",
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 4, 0, 0)
		};
		root.Children.Add(_memStatusText);
		// 扫描命中的候选会渲染到这里，每条带一个「应用」按钮 ——
		// 扫出来不能只是看着，得能直接填进程序里生效。
		_memCandidates = new StackPanel
		{
			Spacing = 4.0,
			Margin = new Thickness(0, 6, 0, 0)
		};
		root.Children.Add(_memCandidates);

		// ---- 盐值名单（黑名单 / 待试盐值）----
		root.Children.Add(new TextBlock
		{
			Text = "盐值名单：扫出来的候选会先过这里。用途是**没有 Alane/404 可对比时**，"
				+ "把这些候选逐个拿去进服试，试过了不好使就加进来排除掉，下次不再试。",
			FontSize = 12.0,
			Opacity = 0.8,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 6, 0, 0)
		});
		StackPanel blBar = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0,
			Margin = new Thickness(0, 4, 0, 0)
		};
		_blacklistAddBox = new TextBox
		{
			PlaceholderText = "粘贴 32 位盐值后按回车加入名单",
			MinWidth = 250.0
		};
		_blacklistAddBox.KeyDown += BlacklistAddBox_KeyDown;
		Button blOpenBtn = new Button
		{
			Content = "查看名单",
			MinWidth = 92.0
		};
		blOpenBtn.Click += OpenBlacklist_Click;
		ToolTipService.SetToolTip(blOpenBtn, "打开名单弹窗：只显示首尾几位，可逐条删除");
		_blacklistCountText = new TextBlock
		{
			Text = "共 0 条",
			FontSize = 12.0,
			Opacity = 0.8,
			VerticalAlignment = VerticalAlignment.Center
		};
		blBar.Children.Add(_blacklistAddBox);
		blBar.Children.Add(blOpenBtn);
		blBar.Children.Add(_blacklistCountText);
		root.Children.Add(blBar);

		// 抓包功能已停用（控制端口已被证实不下发盐值，见逆向文档 §37/§38）
		/*
		Grid capGrid = new Grid
		{
			ColumnSpacing = 8.0
		};
		capGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		capGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		capGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		_captureBtn = new Button
		{
			Content = "调试：抓回环流量"
		};
		_captureBtn.Click += CaptureTraffic_Click;
		SetColumn(_captureBtn, 0);
		ToolTipService.SetToolTip(_captureBtn, "仅调试模式可用：抓 Npcap 回环适配器上的报文。想抓首条登录帧就先开抓、再启动游戏进服务器");
		_captureStopBtn = new Button
		{
			Content = "停止抓取",
			IsEnabled = false
		};
		_captureStopBtn.Click += StopCapture_Click;
		SetColumn(_captureStopBtn, 1);
		ToolTipService.SetToolTip(_captureStopBtn, "立即结束抓取并解析已抓到的部分（不必等满时长）");
		_captureSecondsBox = new ComboBox
		{
			MinWidth = 110.0
		};
		ThemeHelper.SolidComboPopup(_captureSecondsBox);
		_captureSecondsBox.Items.Add(new ComboBoxItem
		{
			Content = "1 分钟",
			Tag = 60
		});
		_captureSecondsBox.Items.Add(new ComboBoxItem
		{
			Content = "3 分钟",
			Tag = 180
		});
		_captureSecondsBox.Items.Add(new ComboBoxItem
		{
			Content = "5 分钟",
			Tag = 300
		});
		_captureSecondsBox.Items.Add(new ComboBoxItem
		{
			Content = "10 分钟",
			Tag = 600
		});
		// 默认 5 分钟：官方启动器常在启动后数分钟才走完整流程，短了抓不全
		_captureSecondsBox.SelectedIndex = 2;
		SetColumn(_captureSecondsBox, 2);
		ToolTipService.SetToolTip(_captureSecondsBox, "抓取时长。游戏启动/更新的流程较长，建议 5 分钟起；随时可点「停止抓取」提前结束");
		capGrid.Children.Add(_captureBtn);
		capGrid.Children.Add(_captureStopBtn);
		capGrid.Children.Add(_captureSecondsBox);
		root.Children.Add(capGrid);
		_captureText = new TextBlock
		{
			Text = "",
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 4, 0, 0)
		};
		root.Children.Add(_captureText);
		*/
		root.Children.Add(new TextBlock
		{
			Text = "取盐前提：" + LauncherScanner.NeedGameProcessHint,
			FontSize = 12.0,
			Opacity = 0.8,
			TextWrapping = TextWrapping.Wrap,
			Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 183, 77)),
			Margin = new Thickness(0, 4, 0, 0)
		});
		// —— 操作区 ——
		Grid opGrid = new Grid
		{
			ColumnSpacing = 8.0
		};
		opGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		opGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		opGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		// 原来「立即获取盐值」（应用）与「对比三源盐值」（只读诊断）合并成一个：
		// 一次跑完三个源，既给出对比诊断，也把可用的盐值应用生效。
		_fetchButton = new Button
		{
			Content = "获取盐值（含三源对比）"
		};
		_fetchButton.Click += FetchSalt_Click;
		ToolTipService.SetToolTip(_fetchButton, "依次请求 Alane / 404 / 官方DLL，展示各自结果并把可用的盐值应用生效");
		SetColumn(_fetchButton, 0);
		Button resetBtn = new Button
		{
			Content = "重置失效标记"
		};
		resetBtn.Click += ResetFlags_Click;
		ToolTipService.SetToolTip(resetBtn, "清除各盐值源的本会话失效标记（status=0x11 轮换后重新按链尝试）");
		SetColumn(resetBtn, 1);
		opGrid.Children.Add(_fetchButton);
		opGrid.Children.Add(resetBtn);
		root.Children.Add(opGrid);
		_compareText = new TextBlock
		{
			Text = "",
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap
		};
		root.Children.Add(_compareText);
		_overallStatusText = new TextBlock
		{
			Text = "",
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap
		};
		root.Children.Add(_overallStatusText);
		card.Child = root;
		Children.Add(card);
	}

	// 主题切换时按当前主题重新取卡片背景/描边色（见 Build() 内订阅处说明）
	private void Card_ActualThemeChanged(FrameworkElement sender, object args)
	{
		try
		{
			if (sender is Border b)
			{
				b.Background = TryThemeBrush("CardBackgroundFillColorDefaultBrush");
				b.BorderBrush = TryThemeBrush("CardStrokeColorDefaultBrush");
			}
		}
		catch
		{
		}
	}

	private static TextBlock SectionHeader(string text)
	{
		return new TextBlock
		{
			Text = text,
			FontSize = 13.0,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			Margin = new Thickness(0, 6, 0, 0)
		};
	}

	private static TextBlock FeedbackText()
	{
		return new TextBlock
		{
			Text = "",
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap
		};
	}

	private static void SetColumn(FrameworkElement e, int col)
	{
		Grid.SetColumn(e, col);
	}

	// 主题笔刷统一走 ThemeHelper（App.xaml 未定义 ThemeDictionaries，代码查找在深色主题下会拿到浅色画笔）
	private static Brush TryThemeBrush(string key)
	{
		try
		{
			return ThemeHelper.Resolve(key);
		}
		catch
		{
		}
		return new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
	}

	private static void SetFeedback(TextBlock block, string text, bool ok)
	{
		try
		{
			block.Text = text;
			block.Foreground = new SolidColorBrush(ok ? Windows.UI.Color.FromArgb(255, 129, 199, 132) : Windows.UI.Color.FromArgb(255, 229, 115, 115));
		}
		catch
		{
		}
	}

	// ---------- 加载 / 保存 ----------

	private void LoadFromSettings()
	{
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			// 内存扫描的时长档位 + 黑名单
			FillDurations(_memSecondsBox, 1);
			ApplyBlacklist();
			RefreshBlacklistCount();
			// 默认 = 最早第一次成功连接会话的盐值与版本号
			string saltDefault = !string.IsNullOrWhiteSpace(cfg.LastGoodCrcSalt) ? cfg.LastGoodCrcSalt : (cfg.CrcSalt ?? "");
			string versionDefault = !string.IsNullOrWhiteSpace(cfg.LastGoodLauncherVersion) ? cfg.LastGoodLauncherVersion : (NeteaseYggdrasil.LauncherVersion ?? "");
			_saltBox.Text = saltDefault;
			_versionBox.Text = versionDefault;
			_alaneBox.Text = CrcSaltSourceManager.AlaneAddress;
			_auth404Box.Text = CrcSaltSourceManager.Auth404Address;
			string mode = cfg.Auth404HwidMode ?? Auth404Client.DefaultHwidMode;
			_hwidModeBox.SelectedIndex = ((mode == "real") ? 0 : ((mode == "random") ? 2 : ((mode == "fixed") ? 3 : 1)));
			// 本机长期保存的 404 账号：自动回填账号与密码（SecureStore 加密，与账号绑定同一套机制）
			// 已登录（有 token）时**不回填密码**，避免明文长期留在界面上；
			// 只有未登录状态才回填账密，方便直接按回车登录。
			bool loggedIn = !string.IsNullOrWhiteSpace(cfg.Auth404Token);
			Auth404Client.Auth404Account? saved = Auth404Client.LoadAccount();
			if (loggedIn)
			{
				if (saved != null && !string.IsNullOrWhiteSpace(saved!.Username))
				{
					_auth404UserBox.Text = saved!.Username;
				}
				else if (!string.IsNullOrWhiteSpace(cfg.Auth404Username))
				{
					_auth404UserBox.Text = cfg.Auth404Username;
				}
				_auth404PassBox.Password = "";
			}
			else if (saved != null && !string.IsNullOrWhiteSpace(saved!.Username))
			{
				_auth404UserBox.Text = saved!.Username;
				if (!string.IsNullOrWhiteSpace(saved!.Password))
				{
					_auth404PassBox.Password = saved!.Password;
				}
			}
			else if (!string.IsNullOrWhiteSpace(cfg.Auth404Username))
			{
				_auth404UserBox.Text = cfg.Auth404Username;
			}
			if (loggedIn)
			{
				SetFeedback(_auth404Feedback, "已登录 404 账号 " + cfg.Auth404Username
					+ "（账密已加密保存到本机" + ((saved != null && saved!.SavedAt.Length > 0) ? ("，存档于 " + saved!.SavedAt) : "")
					+ "；密码框留空，需要重登时再填）", ok: true);
			}
			RefreshOverallStatus();
		}
		catch
		{
		}
	}

	private void SaveSalt_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string salt = (_saltBox.Text ?? "").Trim().ToUpperInvariant();
			string version = (_versionBox.Text ?? "").Trim();
			if (salt.Length == 0)
			{
				// 清空 = 交回自动获取链
				AppSettings cfg = SettingsManager.Instance;
				cfg.CrcSalt = "";
				SettingsManager.Save(cfg);
				SetFeedback(_saltFeedback, "已清空手输盐值，恢复按优先级链自动获取", ok: true);
				return;
			}
			if (salt.Length != 32 || !salt.All((char c) => Uri.IsHexDigit(c)))
			{
				SetFeedback(_saltFeedback, "盐值格式错误：需要 32 位十六进制（0-9A-F）", ok: false);
				return;
			}
			AppSettings cfg2 = SettingsManager.Instance;
			cfg2.CrcSalt = salt;
			if (!string.IsNullOrWhiteSpace(version))
			{
				cfg2.LastGoodLauncherVersion = version;
				NeteaseYggdrasil.LauncherVersion = version;
			}
			SettingsManager.Save(cfg2);
			NeteaseYggdrasil.CrcSalt = salt;
			if (string.IsNullOrWhiteSpace(cfg2.LastGoodCrcSalt))
			{
				cfg2.LastGoodCrcSalt = salt;
				SettingsManager.Save(cfg2);
			}
			SetFeedback(_saltFeedback, "已启用手输盐值 " + CrcSaltSourceManager.Mask(salt) + (string.IsNullOrWhiteSpace(version) ? "" : ("，版本 " + version)), ok: true);
			CrcSaltSourceManager.SetStatus("使用手输盐值 " + CrcSaltSourceManager.Mask(salt));
		}
		catch (Exception ex)
		{
			SetFeedback(_saltFeedback, "保存失败: " + ex.Message, ok: false);
		}
	}

	private async void AlaneBox_LostFocus(object sender, RoutedEventArgs e)
	{
		string url = (_alaneBox.Text ?? "").Trim();
		if (url.Length == 0)
		{
			// 清空 = 恢复 Alane 首位的默认地址
			_alaneBox.Text = CrcSaltSourceManager.DefaultAlaneAddress;
			SetFeedback(_alaneFeedback, "已恢复默认地址 " + CrcSaltSourceManager.DefaultAlaneAddress, ok: true);
			url = CrcSaltSourceManager.DefaultAlaneAddress;
		}
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			cfg.AlaneSaltUrl = url;
			SettingsManager.Save(cfg);
		}
		catch
		{
		}
		// 地址有效性实时探测：不可用就在输入框下方报出原因，并提示可回退的历史可用地址
		SetFeedback(_alaneFeedback, "正在探测地址可用性…", ok: true);
		try
		{
			var (ok, salt, msg, fallback) = await CrcSaltSourceManager.ValidateAlaneAddressAsync(url, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: true);
			if (ok && salt != null)
			{
				SetFeedback(_alaneFeedback, msg + "（" + CrcSaltSourceManager.Mask(salt) + "，可在下方点「获取盐值（含三源对比）」应用）", ok: true);
			}
			else
			{
				string text = msg;
				if (!string.IsNullOrEmpty(fallback))
				{
					text = text + "。将自动回退到之前可用地址 " + fallback;
				}
				SetFeedback(_alaneFeedback, text, ok: false);
			}
		}
		catch (Exception ex)
		{
			SetFeedback(_alaneFeedback, "地址探测异常: " + ex.Message, ok: false);
		}
	}

	private async void Auth404Box_LostFocus(object sender, RoutedEventArgs e)
	{
		string url = (_auth404Box.Text ?? "").Trim();
		if (url.Length == 0)
		{
			_auth404Box.Text = Auth404Client.DefaultBaseUrl;
			SetFeedback(_auth404Feedback, "已恢复默认地址 " + Auth404Client.DefaultBaseUrl + "（备用 " + Auth404Client.DefaultBackupUrl + " 自动回退）", ok: true);
			url = Auth404Client.DefaultBaseUrl;
		}
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			cfg.Auth404BaseUrl = url;
			SettingsManager.Save(cfg);
		}
		catch
		{
		}
		// 未登录时只做格式+可达性校验（/api/crc-salt 需要 Bearer token，401 也说明服务端在）
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u) || (u.Scheme != "http" && u.Scheme != "https"))
		{
			SetFeedback(_auth404Feedback, "地址格式不正确：须为 http:// 或 https:// 开头的绝对地址", ok: false);
			return;
		}
		SetFeedback(_auth404Feedback, "正在探测地址可达性…", ok: true);
		try
		{
			using System.Net.Http.HttpClient http = new System.Net.Http.HttpClient
			{
				Timeout = TimeSpan.FromSeconds(8.0)
			};
			http.DefaultRequestHeaders.UserAgent.ParseAdd("404Launcher/" + Auth404Client.AppVersion);
			using System.Net.Http.HttpResponseMessage resp = await http.GetAsync(new Uri(url.TrimEnd('/') + "/api/crc-salt")).ConfigureAwait(continueOnCapturedContext: true);
			int code = (int)resp.StatusCode;
			if (code == 401 || code == 403)
			{
				SetFeedback(_auth404Feedback, "地址可达（HTTP " + code + " 未授权，属正常）—— 请先登录 404 账号再取盐", ok: true);
			}
			else if (code == 404)
			{
				SetFeedback(_auth404Feedback, "地址可达但不存在 /api/crc-salt 接口（HTTP 404），请检查地址是否正确", ok: false);
			}
			else if (code >= 200 && code < 300)
			{
				SetFeedback(_auth404Feedback, "地址可达（HTTP " + code + "）", ok: true);
			}
			else
			{
				SetFeedback(_auth404Feedback, "地址响应异常：HTTP " + code, ok: false);
			}
		}
		catch (Exception ex)
		{
			SetFeedback(_auth404Feedback, "地址不可用：" + ex.GetType().Name + ": " + ex.Message + "（将自动回退 " + Auth404Client.DefaultBackupUrl + "）", ok: false);
		}
	}

	private void HwidMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		try
		{
			string mode = ((_hwidModeBox.SelectedIndex == 0) ? "real" : ((_hwidModeBox.SelectedIndex == 2) ? "random" : ((_hwidModeBox.SelectedIndex == 3) ? "fixed" : "spoof")));
			AppSettings cfg = SettingsManager.Instance;
			cfg.Auth404HwidMode = mode;
			SettingsManager.Save(cfg);
			if (mode == "real")
			{
				string hwid = Auth404Client.ComputeHwid("real");
				SetFeedback(_auth404Feedback, "HWID 模式=真实（" + CrcSaltSourceManager.Mask(hwid) + " 位:" + hwid.Length + "）", ok: true);
			}
		}
		catch
		{
		}
	}

	// ---------- 异步操作 ----------

	/// <summary>404 账号框里按回车 → 焦点跳到密码框。</summary>
	private void Auth404UserBox_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == Windows.System.VirtualKey.Enter)
		{
			e.Handled = true;
			_auth404PassBox.Focus(FocusState.Programmatic);
		}
	}

	/// <summary>404 密码框里按回车 → 直接触发登录按钮（等价于点「登录 404」）。</summary>
	private void Auth404PassBox_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == Windows.System.VirtualKey.Enter)
		{
			e.Handled = true;
			Auth404Login_Click(_auth404LoginButton, new RoutedEventArgs());
		}
	}

	private async void Auth404Login_Click(object sender, RoutedEventArgs e)
	{
		Button btn = (Button)sender;
		string user = (_auth404UserBox.Text ?? "").Trim();
		string pass = _auth404PassBox.Password ?? "";
		if (user.Length == 0 || pass.Length == 0)
		{
			SetFeedback(_auth404Feedback, "请输入 404 账号和密码", ok: false);
			return;
		}
		btn.IsEnabled = false;
		SetFeedback(_auth404Feedback, "登录中…", ok: true);
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			string mode = cfg.Auth404HwidMode ?? Auth404Client.DefaultHwidMode;
			string baseUrl = CrcSaltSourceManager.Auth404Address;
			var (ok, msg, token, hwid) = await Auth404Client.LoginAsync(baseUrl, user, pass, mode, cfg.Auth404FixedHwid, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: true);
			if (ok)
			{
				cfg.Auth404Token = token;
				cfg.Auth404Username = user;
				SettingsManager.Save(cfg);
				Auth404Client.SaveSession(token, user);
				// 长期保存：账号+密码落本机（加密），重启后自动回填
				Auth404Client.SaveAccount(user, pass, token, mode);
				// 已存到本机后就清掉输入框里的明文账密，避免在界面上长期暴露
				_auth404UserBox.Text = "";
				_auth404PassBox.Password = "";
				SetFeedback(_auth404Feedback, msg + "，HWID=" + hwid.Substring(0, 8) + "…（账号已加密保存到本机，输入框已清空）", ok: true);
				CrcSaltSourceManager.MarkSuccess(SaltSourceKind.Auth404, "", "已登录");
			}
			else
			{
				SetFeedback(_auth404Feedback, msg, ok: false);
			}
		}
		catch (Exception ex)
		{
			SetFeedback(_auth404Feedback, "登录异常: " + ex.Message, ok: false);
		}
		finally
		{
			btn.IsEnabled = true;
		}
	}

	/// <summary>
	/// 官方 DLL 取盐的一键流程：游戏已在跑就直接取盐；没在跑则按网易现在的启动顺序
	/// 「发烧游戏平台 → 我的世界启动器」依次拉起，并提示用户从游戏中心进游戏。
	/// </summary>
	private async void OfficialOneClick_Click(object sender, RoutedEventArgs e)
	{
		Button btn = (Button)sender;
		btn.IsEnabled = false;
		_officialStatusText.Text = "扫盘中（扫盘 + 进程 + patchlist）…";
		try
		{
			LauncherScanResult scan = await LauncherScanner.ScanAsync(CancellationToken.None).ConfigureAwait(continueOnCapturedContext: true);
			_lastLauncherExe = scan.FirstLauncher?.LauncherExe;
			StringBuilder sb = new StringBuilder();
			// 游戏进程还没起来 → 先按顺序把平台和启动器拉起来
			if (scan.FirstGameWithPort == null)
			{
				sb.Append("未发现游戏进程，正在按顺序拉起：\n");
				if (!LauncherScanner.IsFeverPlatformRunning())
				{
					var (fok, fmsg, _) = LauncherScanner.LaunchFeverPlatform();
					sb.Append("· ").Append(fmsg).Append('\n');
					if (fok)
					{
						await Task.Delay(3000).ConfigureAwait(continueOnCapturedContext: true);
					}
				}
				else
				{
					sb.Append("· 发烧平台已在运行\n");
				}
				if (!LauncherScanner.IsLauncherRunning())
				{
					var (lok, lmsg, _) = LauncherScanner.LaunchOfficialLauncher(_lastLauncherExe);
					sb.Append("· ").Append(lmsg);
					if (lok)
					{
						await Task.Delay(2000).ConfigureAwait(continueOnCapturedContext: true);
					}
				}
				else
				{
					sb.Append("· 我的世界启动器已在运行");
				}
				sb.Append('\n').Append(LauncherScanner.NeedGameProcessHint);
				_officialStatusText.Text = sb.ToString();
				return;
			}
			sb.Append("启动器 ").Append(scan.Launchers.Count).Append(" 处");
			foreach (OfficialLauncherInfo l in scan.Launchers)
			{
				sb.Append("\n· ").Append(l.LauncherExe).Append("  版本=").Append(string.IsNullOrEmpty(l.Version) ? "未知" : l.Version);
			}
			// 已安装版本落后于 patchlist 最新版本 → 提示更新（版本不配对会直接 status=0x11）
			if (scan.FirstLauncher != null && !string.IsNullOrEmpty(scan.PatchlistVersion)
				&& !string.Equals(scan.FirstLauncher!.Version, scan.PatchlistVersion, StringComparison.Ordinal))
			{
				sb.Append("\n⚠ 已安装版本 ").Append(scan.FirstLauncher!.Version).Append(" 落后于最新 ")
					.Append(scan.PatchlistVersion).Append("，建议先更新官方启动器");
			}
			sb.Append("\n运行中相关进程 ").Append(scan.RunningGames.Count).Append(" 个");
			foreach (RunningGameInfo g in scan.RunningGames)
			{
				sb.Append("\n· pid=").Append(g.Pid).Append(' ').Append(g.ProcessName)
					.Append(" 游戏端口=").Append(g.LauncherPort)
					.Append(" 控制端口=").Append(g.ControlPort)
					.Append(" gameid=").Append(g.GameIdShort);
			}
			if (!string.IsNullOrEmpty(scan.PatchlistVersion))
			{
				sb.Append("\npatchlist 最新版本=").Append(scan.PatchlistVersion);
			}
			OfficialSaltResult official = await OfficialLauncherSaltClient.TryAcquireAsync(CancellationToken.None).ConfigureAwait(continueOnCapturedContext: true);
			sb.Append("\nDLL 算法自检：").Append(official.AlgorithmSelfCheck ? "通过" : "失败");
			if (official.HandshakeDone)
			{
				sb.Append("\n控制端口握手：已连接 127.0.0.1:").Append(official.ControlPort);
			}
			if (!string.IsNullOrEmpty(official.Salt))
			{
				sb.Append("\n索取到盐值：").Append(CrcSaltSourceManager.Mask(official.Salt!));
			}
			else
			{
				sb.Append("\n未索取到盐值：").Append(official.Message);
			}
			if (!string.IsNullOrEmpty(official.Version))
			{
				sb.Append("\n启动器版本（配对用）：").Append(official.Version);
			}
			_officialStatusText.Text = sb.ToString();
		}
		catch (Exception ex)
		{
			_officialStatusText.Text = "扫描异常: " + ex.Message;
		}
		finally
		{
			btn.IsEnabled = true;
		}
	}

	/// <summary>
	/// 调试模式专属：抓取游戏 ↔ 启动器控制端口上的真实报文。
	/// 目的：我们已能连上并发出登录帧但启动器不回包，需要看到真实交互才能对齐（见逆向文档 §28）。
	/// </summary>
	private async void CaptureTraffic_Click(object sender, RoutedEventArgs e)
	{
		if (!LogHelper.DebugEnabled)
		{
			_captureText.Text = "该功能仅在调试模式下可用：请先到设置里打开调试模式，再点此按钮。";
			return;
		}
		_captureBtn.IsEnabled = false;
		_captureStopBtn.IsEnabled = true;
		int seconds = 300;
		if (_captureSecondsBox.SelectedItem is ComboBoxItem ci && ci.Tag is int s && s > 0)
		{
			seconds = s;
		}
		_captureText.Text = "正在扫盘定位控制端口…";
		int port = 0;
		try
		{
			LauncherScanResult scan = await LauncherScanner.ScanAsync(CancellationToken.None).ConfigureAwait(continueOnCapturedContext: true);
			RunningGameInfo? game = scan.FirstGameWithPort;
			if (game != null)
			{
				port = (game!.LauncherPort > 0) ? game!.LauncherPort : game!.ControlPort;
			}
			if (port > 0)
			{
				_captureText.Text = $"已定位控制端口 {port}（pid={game!.Pid}），正在抓取 {seconds / 60} 分钟…\n"
					+ "现在可以让游戏跑起来；随时点「停止抓取」提前结束并查看。";
			}
			else
			{
				// 关键：启动/更新阶段游戏还没起来，这时不能拒绝抓取 ——
				// 抓全部回环流量，让用户「先开抓、再启动游戏」，才能抓到首条登录帧。
				_captureText.Text = $"未发现游戏进程，将抓取**全部** 127.0.0.1 回环流量（{seconds / 60} 分钟）。\n"
					+ "现在请去启动官方启动器 → 游戏中心 → 选服务器 → 跳转运行游戏。\n"
					+ "抓满后自动出结果，也可随时点「停止抓取」。";
			}
			CaptureResult r = await OfficialTrafficCapture.CaptureAsync(port, seconds, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: true);
			_captureText.Text = OfficialTrafficCapture.BuildReport(r);
			LogHelper.Salt($"[抓包] 面板抓取完成 ok={r.Ok} 报文={r.Payloads.Count} 文件={r.PcapPath}");
		}
		catch (Exception ex)
		{
			_captureText.Text = "抓取异常: " + ex.Message;
		}
		finally
		{
			_captureBtn.IsEnabled = true;
			_captureStopBtn.IsEnabled = false;
		}
	}

	/// <summary>
	/// 守候抢连：控制端口随机且只在游戏启动瞬间监听，所以流程必须反过来——
	/// 先守候，用户再去启动游戏，进程一出现就抢连。
	/// </summary>
	private async void WatchOfficial_Click(object sender, RoutedEventArgs e)
	{
		_watchBtn.IsEnabled = false;
		_officialStatusText.Text = "守候 + 抓包 已同时启动（最长 180 秒）…\n现在请去：官方启动器 → 游戏中心 → 选服务器 → 跳转运行游戏。\n"
			+ "游戏进程一出现就会自动抢连；即使抢连失败，也会把抓到的真实报文列出来。";
		try
		{
			string report = await OfficialLauncherSaltClient.WatchWithCaptureAsync(180, delegate (string s)
			{
				LogHelper.Log(s);
			}, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: true);
			_officialStatusText.Text = report;
		}
		catch (Exception ex)
		{
			_officialStatusText.Text = "守候异常: " + ex.Message;
		}
		finally
		{
			_watchBtn.IsEnabled = true;
		}
	}

	// ---------- 游戏进程内存扫描 ----------

	/// <summary>构造时长下拉（内存扫描与抓包共用同样的档位）。</summary>
	private static void FillDurations(ComboBox box, int defaultIndex)
	{
		string[] labels = new string[4] { "1 分钟", "3 分钟", "5 分钟", "10 分钟" };
		int[] secs = new int[4] { 60, 180, 300, 600 };
		for (int i = 0; i < labels.Length; i++)
		{
			box.Items.Add(new ComboBoxItem
			{
				Content = labels[i],
				Tag = secs[i]
			});
		}
		box.SelectedIndex = defaultIndex;
	}

	private static int SelectedSeconds(ComboBox box, int fallback)
	{
		if (box.SelectedItem is ComboBoxItem ci && ci.Tag is int s && s > 0)
		{
			return s;
		}
		return fallback;
	}

	private async void MemScan_Click(object sender, RoutedEventArgs e)
	{
		_memScanBtn.IsEnabled = false;
		_memStopBtn.IsEnabled = true;
		_memScanCts = new CancellationTokenSource();
		int seconds = SelectedSeconds(_memSecondsBox, 300);
		try
		{
			// 先找游戏进程
			LauncherScanResult scan = await LauncherScanner.ScanProcessesOnlyAsync(_memScanCts.Token).ConfigureAwait(continueOnCapturedContext: true);
			RunningGameInfo? game = scan.FirstGameWithPort;
			if (game == null)
			{
				_memStatusText.Text = "未发现游戏进程。请先让游戏**完全进入服务器、角色可活动**后再扫描。";
				return;
			}
			int pid = game!.Pid;
			_memStatusText.Text = $"正在扫描 pid={pid} {game!.ProcessName}（最多 {seconds / 60} 分钟）…\n"
				+ "确认游戏已完全进入服务器并可活动，否则扫到的不算数。";

			GameMemoryScanner.FilteredCount = 0;
			GameMemoryScanner.FilterReasons.Clear();
			List<string> known = OfficialLauncherSaltClient.GetKnownSalts();

			// 文本 + 二进制两条同时跑（二进制必须在进程还活着时发起）
			Task<List<MemoryHit>> textTask = GameMemoryScanner.ScanForSaltAsync(pid, ct: _memScanCts.Token);
			Task<List<MemoryHit>> binTask = GameMemoryScanner.ScanForBinarySaltAsync(pid, known, ct: _memScanCts.Token);

			List<MemoryHit> textHits = await textTask.ConfigureAwait(continueOnCapturedContext: true);
			List<MemoryHit> binHits = await binTask.ConfigureAwait(continueOnCapturedContext: true);

			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			sb.Append("扫描完成（pid=").Append(pid).Append("）\n");
			sb.Append("文本候选 ").Append(textHits.Count).Append(" 个（已过滤 ").Append(GameMemoryScanner.FilteredCount).Append(" 个）\n");
			if (GameMemoryScanner.FilterReasons.Count > 0)
			{
				sb.Append("过滤原因：");
				foreach (KeyValuePair<string, int> kv in GameMemoryScanner.FilterReasons)
				{
					sb.Append(kv.Key).Append('×').Append(kv.Value).Append("  ");
				}
				sb.Append('\n');
			}
			List<MemoryHit> matched = GameMemoryScanner.MatchKnown(textHits, known);
			if (matched.Count > 0)
			{
				sb.Append("\n★★ 文本形态命中已知盐值：\n");
				foreach (MemoryHit m in matched)
				{
					sb.Append("  ").Append(m.ToString()).Append('\n');
				}
			}
			sb.Append('\n');
			if (binHits.Count > 0)
			{
				sb.Append("★★★ 二进制形态命中：\n");
				foreach (MemoryHit b in binHits.Take(10))
				{
					sb.Append("  ").Append(b.ToString()).Append('\n');
				}
			}
			else
			{
				sb.Append("二进制形态未命中（已试 ").Append(known.Count).Append(" 个已知盐值）\n");
			}
			string dump = GameMemoryScanner.SaveHits(textHits, pid);
			sb.Append("\n已保存：").Append(dump);
			_memStatusText.Text = sb.ToString();

			// 把候选渲染成可点「应用」的行：只加载二进制解码扫出来的 32B 盐值——
			// 二进制命中是按已知盐值的原始字节比对出来的，能对上才列出来；
			// 文本候选大多是十六进制纯噪声，不再进候选列表（完整清单仍保存到文件备查）。
			RenderCandidates(binHits, matched, textHits);
		}
		catch (OperationCanceledException)
		{
			_memStatusText.Text = "已停止扫描。";
		}
		catch (Exception ex)
		{
			_memStatusText.Text = "扫描异常: " + ex.GetType().Name + ": " + ex.Message;
		}
		finally
		{
			// 释放上一轮遗留的取消源，避免反复扫描累积
			try
			{
				_memScanCts?.Dispose();
			}
			catch
			{
			}
			_memScanCts = null;
			_memScanBtn.IsEnabled = true;
			_memStopBtn.IsEnabled = false;
		}
	}

	private void MemStop_Click(object sender, RoutedEventArgs e)
	{
		_memStopBtn.IsEnabled = false;
		_memStatusText.Text = "正在停止…";
		try
		{
			_memScanCts?.Cancel();
		}
		catch
		{
		}
	}

	/// <summary>
	/// 把扫描命中的候选渲染成一行行可点「应用」的条目。
	/// 只加载二进制解码扫出来的 32B 盐值（binHits，按已知盐值字节比对命中、能对上的唯一可信值）；
	/// 文本候选是十六进制纯噪声，不再渲染（完整清单已保存到文件备查）。
	/// </summary>
	private void RenderCandidates(List<MemoryHit> binHits, List<MemoryHit> matched, List<MemoryHit> textHits)
	{
		_memCandidates.Children.Clear();
		List<MemoryHit> ordered = new List<MemoryHit>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (MemoryHit h in binHits)
		{
			if (seen.Add(h.Value))
			{
				ordered.Add(h);
			}
		}
		if (ordered.Count == 0)
		{
			_memCandidates.Children.Add(new TextBlock
			{
				Text = "没有二进制解码命中的 32B 盐值（文本十六进制候选均为噪声，已不加载；完整清单见保存的文件）。",
				FontSize = 12.0,
				Opacity = 0.7,
				TextWrapping = TextWrapping.Wrap
			});
			return;
		}
		_memCandidates.Children.Add(new TextBlock
		{
			Text = "候选（点「应用」直接填进程序生效；试过不好使可点「排除」）：",
			FontSize = 12.0,
			Opacity = 0.85,
			Margin = new Thickness(0, 0, 0, 4)
		});
		// 用一个带边框的容器把候选套住，视觉上跟上面的说明文字分开
		Border box = new Border
		{
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(4.0),
			Padding = new Thickness(8.0, 6.0, 8.0, 6.0),
			BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 128, 128, 128))
		};
		_memCandidateRows = new StackPanel
		{
			Spacing = 4.0
		};
		box.Child = _memCandidateRows;
		_memCandidates.Children.Add(box);

		foreach (MemoryHit h in ordered.Take(12))
		{
			// BIN(16B) = 按 16 字节原始字节命中，说明这个盐值**确实以 byte[16] 就躺在游戏内存里**。
			// 它只在二进制形态命中时才出现，可信度最高，标为推荐。
			bool isBinary = h.Encoding.StartsWith("BIN", StringComparison.OrdinalIgnoreCase);
			Grid row = new Grid
			{
				ColumnSpacing = 8.0,
				Margin = new Thickness(0, 2, 0, 2)
			};
			row.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(1.0, GridUnitType.Star)
			});
			row.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			row.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			TextBlock tb = new TextBlock
			{
				Text = (isBinary ? "★ " : "   ") + h.Value.Substring(0, 8) + "…" + h.Value.Substring(28)
					+ "   [" + h.Encoding + "]" + (isBinary ? "   ← 推荐，直接应用" : ""),
				FontSize = 12.0,
				VerticalAlignment = VerticalAlignment.Center,
				FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
				Foreground = isBinary ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 175, 80)) : null
			};
			ToolTipService.SetToolTip(tb, h.Value + "  " + h.Address
				+ (isBinary ? "\n按 16 字节原始字节命中：该盐值确实驻留在游戏内存中，可信度最高。" : ""));
			SetColumn(tb, 0);
			Button apply = new Button
			{
				Content = "应用",
				Tag = h.Value
			};
			apply.Click += ApplyScannedSalt_Click;
			SetColumn(apply, 1);
			Button exclude = new Button
			{
				Content = "排除",
				Tag = h.Value
			};
			exclude.Click += ExcludeSalt_Click;
			SetColumn(exclude, 2);
			row.Children.Add(tb);
			row.Children.Add(apply);
			row.Children.Add(exclude);
			// 记住这行属于哪个盐值，点「排除」后能立刻把它从界面上抹掉
			row.Tag = h.Value;
			_memCandidateRows.Children.Add(row);
		}
	}

	/// <summary>把扫到的盐值真正写进程序并落盘。</summary>
	private void ApplyScannedSalt_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (((Button)sender).Tag is not string salt || salt.Length != 32)
			{
				return;
			}
			// 1) 让认证链路立即用上
			NeteaseYggdrasil.CrcSalt = salt;
			// 2) 落盘，重启后仍然有效
			AppSettings cfg = SettingsManager.Instance;
			cfg.CrcSalt = salt;
			cfg.LastGoodCrcSalt = salt;
			SettingsManager.Save(cfg);
			// 3) 界面同步
			_saltBox.Text = salt;
			SetFeedback(_saltFeedback, "已应用扫描到的盐值 " + CrcSaltSourceManager.Mask(salt)
				+ "，并写入设置（下次进游戏生效）", ok: true);
			LogHelper.Salt("[内存] 已应用扫描盐值 " + salt);
			CrcSaltSourceManager.MarkSuccess(SaltSourceKind.Official, salt, "游戏进程内存扫描命中");
		}
		catch (Exception ex)
		{
			SetFeedback(_saltFeedback, "应用失败: " + ex.Message, ok: false);
		}
	}

	/// <summary>试过不好使的候选 → 加入名单，下次扫描直接排除。</summary>
	private void ExcludeSalt_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (((Button)sender).Tag is not string salt)
			{
				return;
			}
			List<string> items = ParseBlacklist(SettingsManager.Instance.SaltBlacklist);
			if (!items.Contains(salt, StringComparer.OrdinalIgnoreCase))
			{
				items.Add(salt.ToUpperInvariant());
			}
			SaveBlacklist(items);
			RefreshBlacklistCount();
			// 立刻从候选列表里抹掉这一行，不用重扫才消失
			for (int i = _memCandidateRows.Children.Count - 1; i >= 0; i--)
			{
				if (_memCandidateRows.Children[i] is FrameworkElement fe
					&& fe.Tag is string tag
					&& string.Equals(tag, salt, StringComparison.OrdinalIgnoreCase))
				{
					_memCandidateRows.Children.RemoveAt(i);
				}
			}
			SetFeedback(_saltFeedback, "已排除 " + CrcSaltSourceManager.Mask(salt) + "（下次扫描不再列出）", ok: true);
		}
		catch
		{
		}
	}

	/// <summary>名单里按回车 → 直接把这条盐值收进名单（省得先点按钮）。</summary>
	private async void BlacklistAddBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
	{
		if (e.Key != Windows.System.VirtualKey.Enter)
		{
			return;
		}
		e.Handled = true;
		string raw = (_blacklistAddBox.Text ?? "").Trim();
		// 容忍用户粘贴整行（可能带上下文），只取其中的 32 位十六进制
		System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
			raw, "(?<![0-9A-Za-z])([0-9A-Fa-f]{32})(?![0-9A-Za-z])");
		if (!m.Success)
		{
			await ShowMsgAsync("没识别到 32 位盐值", "请输入或粘贴一个 32 位十六进制盐值，例如：\n387090BD9CE8C8143F952E397153890A");
			return;
		}
		string salt = m.Groups[1].Value.ToUpperInvariant();
		AppSettings cfg = SettingsManager.Instance;
		List<string> items = ParseBlacklist(cfg.SaltBlacklist);
		if (!items.Contains(salt, StringComparer.OrdinalIgnoreCase))
		{
			items.Add(salt);
		}
		cfg.SaltBlacklist = string.Join(",", items);
		SettingsManager.Save(cfg);
		ApplyBlacklist();
		RefreshBlacklistCount();
		_blacklistAddBox.Text = "";
	}

	/// <summary>把设置里的名单文本解析成列表。</summary>
	private static List<string> ParseBlacklist(string text)
	{
		List<string> items = new List<string>();
		foreach (string part in (text ?? "").Split(new char[3] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
		{
			string t = part.Trim().ToUpperInvariant();
			if (t.Length == 32 && !items.Contains(t, StringComparer.OrdinalIgnoreCase))
			{
				items.Add(t);
			}
		}
		return items;
	}

	private static void SaveBlacklist(List<string> items)
	{
		AppSettings cfg = SettingsManager.Instance;
		cfg.SaltBlacklist = string.Join(",", items);
		SettingsManager.Save(cfg);
		ApplyBlacklist();
	}

	private void RefreshBlacklistCount()
	{
		try
		{
			int n = ParseBlacklist(SettingsManager.Instance.SaltBlacklist).Count;
			_blacklistCountText.Text = "共 " + n + " 条";
		}
		catch
		{
		}
	}

	/// <summary>名单弹窗：只露出首尾几位，方便一眼区分，可逐条删除。</summary>
	private async void OpenBlacklist_Click(object sender, RoutedEventArgs e)
	{
		List<string> items = ParseBlacklist(SettingsManager.Instance.SaltBlacklist);
		if (items.Count == 0)
		{
			await ShowMsgAsync("名单是空的", "在上面输入框里粘贴一个 32 位盐值，按回车即可加入。\n\n"
				+ "用途：没有 Alane/404 可对比时，扫到的候选逐个拿去进服试；\n"
				+ "试过不好使的就加进来，下次扫描直接排除。");
			return;
		}
		StackPanel panel = new StackPanel
		{
			Spacing = 6.0
		};
		panel.Children.Add(new TextBlock
		{
			Text = "首尾各露 6 位，中间省略。点右侧删除可从名单移除：",
			FontSize = 12.0,
			Opacity = 0.8,
			TextWrapping = TextWrapping.Wrap
		});
		foreach (string it in items)
		{
			Grid row = new Grid
			{
				ColumnSpacing = 8.0
			};
			row.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(1.0, GridUnitType.Star)
			});
			row.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			TextBlock tb = new TextBlock
			{
				Text = it.Substring(0, 6) + "…" + it.Substring(26),
				FontSize = 13.0,
				VerticalAlignment = VerticalAlignment.Center,
				FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
			};
			ToolTipService.SetToolTip(tb, it);
			SetColumn(tb, 0);
			Button del = new Button
			{
				Content = "删除",
				Tag = it
			};
			del.Click += BlacklistDelete_Click;
			SetColumn(del, 1);
			row.Children.Add(tb);
			row.Children.Add(del);
			panel.Children.Add(row);
		}
		ContentDialog dlg = new ContentDialog
		{
			Title = "盐值名单（" + items.Count + " 条）",
			Content = new ScrollViewer
			{
				Content = panel,
				MaxHeight = 360.0
			},
			CloseButtonText = "关闭",
			XamlRoot = XamlRoot
		};
		await dlg.ShowAsync();
	}

	private void BlacklistDelete_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (((Button)sender).Tag is string victim)
			{
				List<string> items = ParseBlacklist(SettingsManager.Instance.SaltBlacklist);
				items.RemoveAll((string x) => string.Equals(x, victim, StringComparison.OrdinalIgnoreCase));
				SaveBlacklist(items);
				RefreshBlacklistCount();
			}
		}
		catch
		{
		}
	}

	/// <summary>打开最近一次内存扫描保存的候选清单。</summary>
	private void OpenMemLog_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string dir = Path.Combine(Path.GetTempPath(), "MhyunBoxCapture");
			if (!Directory.Exists(dir))
			{
				_memStatusText.Text = "还没有扫描记录（目录不存在）：" + dir;
				return;
			}
			FileInfo[] files = new DirectoryInfo(dir).GetFiles("memsalt_*.txt");
			if (files.Length == 0)
			{
				_memStatusText.Text = "还没有内存扫描候选清单。先跑一次扫描再点这里。";
				return;
			}
			FileInfo latest = files.OrderByDescending((FileInfo f) => f.LastWriteTime).First();
			Process.Start(new ProcessStartInfo
			{
				FileName = latest.FullName,
				UseShellExecute = true
			});
			_memStatusText.Text = "已打开：" + latest.FullName;
		}
		catch (Exception ex)
		{
			_memStatusText.Text = "打开失败: " + ex.Message;
		}
	}

	private async Task ShowMsgAsync(string title, string body)
	{
		try
		{
			ContentDialog dlg = new ContentDialog
			{
				Title = title,
				Content = body,
				CloseButtonText = "知道了",
				XamlRoot = XamlRoot
			};
			await dlg.ShowAsync();
		}
		catch
		{
			// XamlRoot 还没挂上时弹窗无法显示，退化为写状态栏
			try
			{
				_memStatusText.Text = title + "：" + body;
			}
			catch
			{
			}
		}
	}

	/// <summary>把设置里的黑名单灌进扫描器的自定义黑名单集合。</summary>
	private static void ApplyBlacklist()
	{
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string part in (cfg.SaltBlacklist ?? "").Split(new char[3] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string t = part.Trim().ToUpperInvariant();
				if (t.Length == 32)
				{
					set.Add(t);
				}
			}
			GameMemoryScanner.CustomBlacklist = set;
		}
		catch
		{
		}
	}

	private void StopCapture_Click(object sender, RoutedEventArgs e)
	{
		_captureStopBtn.IsEnabled = false;
		_captureText.Text = "正在停止抓取并解析已抓到的部分…";
		OfficialTrafficCapture.StopCapture();
	}

	private async void FetchSalt_Click(object sender, RoutedEventArgs e)
	{
		Button btn = (Button)sender;
		btn.IsEnabled = false;
		SetFeedback(_saltFeedback, "正在请求三个盐值源（Alane → 404 → 官方DLL）…", ok: true);
		_compareText.Text = "";
		try
		{
			// 一次跑完三个源：既拿到完整诊断，也拿到可用的盐值
			List<SaltSourceComparison> rows = await CrcSaltSourceManager.CompareSourcesAsync(CancellationToken.None).ConfigureAwait(continueOnCapturedContext: true);

			// 1) 展示三源对比（诊断）
			StringBuilder sb = new StringBuilder();
			foreach (SaltSourceComparison r in rows)
			{
				sb.Append(r.StatusLine);
				if (!string.IsNullOrEmpty(r.Address))
				{
					sb.Append("\n    地址 ").Append(r.Address);
				}
				if (!string.IsNullOrEmpty(r.Version))
				{
					sb.Append("\n    版本 ").Append(r.Version);
				}
				sb.Append('\n');
			}
			sb.Append('\n').Append(CrcSaltSourceManager.BuildComparisonVerdict(rows));
			_compareText.Text = sb.ToString();

			// 2) 把第一个可用源的盐值应用生效（rows 顺序即优先级链：Alane → 404 → 官方）
			SaltSourceComparison? hit = rows.FirstOrDefault((SaltSourceComparison r) => r.Ok && r.Salt != null);
			if (hit != null)
			{
				string salt = hit!.Salt!;
				string? version = string.IsNullOrWhiteSpace(hit!.Version) ? null : hit!.Version;
				NeteaseYggdrasil.CrcSalt = salt;
				if (version != null)
				{
					NeteaseYggdrasil.LauncherVersion = version;
					_versionBox.Text = version;
				}
				_saltBox.Text = salt;
				AppSettings cfg = SettingsManager.Instance;
				cfg.CrcSalt = salt;
				if (version != null)
				{
					cfg.LastGoodLauncherVersion = version;
				}
				if (string.IsNullOrWhiteSpace(cfg.LastGoodCrcSalt))
				{
					cfg.LastGoodCrcSalt = salt;
				}
				SettingsManager.Save(cfg);
				CrcSaltSourceManager.MarkSuccess(hit!.Kind, salt, "面板应用");
				SetFeedback(_saltFeedback, "已应用盐值（来源 " + hit!.Name + "）：" + CrcSaltSourceManager.Mask(salt) + ((version == null) ? "" : ("，版本 " + version)), ok: true);
			}
			else
			{
				SetFeedback(_saltFeedback, "三个源均不可用，保持当前盐值", ok: false);
			}
			RefreshOverallStatus();
		}
		catch (Exception ex)
		{
			SetFeedback(_saltFeedback, "获取异常: " + ex.Message, ok: false);
		}
		finally
		{
			btn.IsEnabled = true;
		}
	}

	private void ResetFlags_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			CrcSaltSourceManager.ResetSessionFlags();
			RefreshOverallStatus();
			SetFeedback(_saltFeedback, "已重置各盐值源失效标记（0x11 后可重新按链尝试）", ok: true);
		}
		catch
		{
		}
	}

	private void RefreshOverallStatus()
	{
		try
		{
			_overallStatusText.Text = CrcSaltSourceManager.BuildStatusLines();
		}
		catch
		{
		}
	}

	private void OnSourceStateChanged()
	{
		_queue.TryEnqueue(RefreshOverallStatus);
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		try
		{
			CrcSaltSourceManager.StateChanged -= OnSourceStateChanged;
		}
		catch
		{
		}
	}
}
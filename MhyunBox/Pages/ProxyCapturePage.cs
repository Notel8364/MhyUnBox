using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Services;
using MhyunBox.Services.BjdProxy;
using MhyunBox.Settings;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace MhyunBox.Pages;

public sealed partial class ProxyCapturePage : Page
{
	private sealed class SessionItem : INotifyPropertyChanged
	{
		private string _idLabel = "";

		private string _status = "";

		private string _summary = "";

		public long Id;

		public string IdLabel
		{
			get
			{
				return _idLabel;
			}
			set
			{
				if (_idLabel != value)
				{
					_idLabel = value;
					PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IdLabel"));
				}
			}
		}

		public string Status
		{
			get
			{
				return _status;
			}
			set
			{
				if (_status != value)
				{
					_status = value;
					PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Status"));
				}
			}
		}

		public string Summary
		{
			get
			{
				return _summary;
			}
			set
			{
				if (_summary != value)
				{
					_summary = value;
					PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Summary"));
				}
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;
	}

	private sealed class StatusCardItem
	{
		public string Header { get; set; } = "";

		public List<StatusRowItem> Rows { get; } = new List<StatusRowItem>();
	}

	private Microsoft.UI.Dispatching.DispatcherQueue _queue;

	private readonly List<SessionItem> _items = new List<SessionItem>();

	private const int MaxTailChars = 20000;

	private string _fallbackHost = "";

	private int _fallbackPort;

	private readonly List<RadioButton> _presetRadios = new List<RadioButton>();

	private bool _portsClosing;

	private PointerEventHandler? _statusWheelHandler;

	/// <summary>滚轮一格对应的滚动像素数（对齐 WinUI 默认的「一格 3 行」手感）。</summary>
	private const double WheelStepPixels = 48.0;

	private bool _statusUiReady;

	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusRefreshTimer;

	private static readonly string[] OneClickFlavor = new string[5] { "万事俱备，只欠开玩，冲鸭！", "代理已就位，接下来交给你的手速啦～", "一切准备妥当，去游戏里大杀四方吧！", "搞定收工，剩下的就是快乐！", "线路已通，快乐起航，玩得尽兴！" };

	private static readonly nint HWND_TOPMOST = new IntPtr(-1);

	private const uint SWP_NOSIZE = 1u;

	private const uint SWP_NOMOVE = 2u;

	private const uint SWP_SHOWWINDOW = 64u;

	private bool _suppressPclOption;

	private TextBox? _targetAddressBox;

	private bool _suppressTargetChanged;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Grid PortsOverlayHost;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel ProxyRoot;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock ActivationHint;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button GoHomeButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock AutoStartHint;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Grid ProxyCardGrid;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Border ControlCard;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel StatusSection;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel PclSection;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel SessionSection;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel LogSection;


	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ListView SessionList;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock PclPathHint;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private CheckBox PclAutoLaunchCheck;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private CheckBox PclAutoStartCheck;

	private CheckBox PclInjectCptdCheck;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button PclMoreLinkButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button PclCopyCptdButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBox PclPathBox;


	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button PclFindButton;


	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ScrollViewer StatusCardsScroller;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ItemsControl StatusCardsList;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock StatusEmptyHint;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ListView PresetListView;






	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button ViewSolutionButton;


	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button RestartButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button StopAllButton;



	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBox ListenPortBox;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button StartButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button StopButton;


	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button OneClickStartButton;

	// —— 布吉岛专用 A/B 会话共享代理（仅布吉岛预设可见，代码构建，不依赖 XBF）——
	private Border _bjdSection;
	private Button _bjdInjectButton;
	private Button _bjdReInjectButton;
	private TextBlock _bjdStatusText;
	private TextBlock _bjdHintText;
	private Button _bjdLogButton;
	private bool _bjdBusy;
	/// <summary>布吉岛区块的状态刷新定时器（看 B 是否接入 / A 是否重连）。</summary>
	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _bjdTimer;
	/// <summary>最近一次按钮操作的可视反馈（"HH:mm:ss 结果"），避免用户觉得点了没反应。</summary>
	private string _bjdLastAction = "";

	public ProxyCapturePage()
	{
		InitializeComponent();
		RemoveDisconnectHintBanner();
		RemoveSessionAndLogSections();
		// 「打开教程 / 查看解决方案」按钮随教程/解决页一并移除，隐藏 XBF 里遗留的按钮
		if (XamlScope.Find(this, "OpenTutorialButton") is FrameworkElement tutorialButton)
		{
			tutorialButton.Visibility = Visibility.Collapsed;
		}
		if (XamlScope.Find(this, "ViewSolutionButton") is FrameworkElement solutionButton)
		{
			solutionButton.Visibility = Visibility.Collapsed;
		}
		RebuildPresetChips();
		// 代理流模式开关改为跟随「目标地址」放在控制卡片内（见 BuildTargetAddressBox），
		// 不再挂在页面底部，避免用户要滚到最下面才能切模式。
		BuildTrapButton();
		PclPathBox.TextChanged += PclPath_TextChanged;
		base.Unloaded += OnUnloaded;
		base.Loaded += OnLoaded;
		BuildTargetAddressBox();
		// 旧「布吉岛专用代理（A/B 会话共享）」与旧「官方客户端脱盒启动」区块已合并为顶部
		// 统一的「客户端配置与启动」卡片（见 RoleLauncherPanel），不再重复构建。
		BuildClientLauncherSection();
	}

	private void RemoveSessionAndLogSections()
	{
		try
		{
			ProxyCardGrid.Children.Remove(SessionSection);
			ProxyCardGrid.Children.Remove(LogSection);
			SessionSection.Visibility = Visibility.Collapsed;
			LogSection.Visibility = Visibility.Collapsed;
			LogHelper.Log("[代理启动] 会话/日志区块已移出代理页（改由日志页按调试模式显示）");
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[代理启动] 移除会话/日志区块失败");
		}
	}

	private void BuildTrapButton()
	{
		try
		{
			Button button = new Button
			{
				Name = "GenshinTrapButton",
				Content = "千！万！不！要！点！",
				HorizontalAlignment = HorizontalAlignment.Stretch,
				HorizontalContentAlignment = HorizontalAlignment.Center,
				Background = new SolidColorBrush(Color.FromArgb(byte.MaxValue, byte.MaxValue, 45, 45)),
				Foreground = new SolidColorBrush(Colors.White),
				FontWeight = FontWeights.Bold,
				FontSize = 16.0,
				Padding = new Thickness(0.0, 12.0, 0.0, 12.0),
				Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
				CornerRadius = new CornerRadius(8.0)
			};
			button.Click += GenshinTrap_Click;
			ProxyRoot.Children.Add(button);
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[代理启动] 恶搞按钮构建失败");
		}
	}

	private void GenshinTrap_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Process.Start(new ProcessStartInfo("https://ys.mihoyo.com/")
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "打开原神官网失败");
			MainWindow.Instance?.ShowToast("打开官网失败：" + ex.Message);
		}
	}

	private void BuildTargetAddressBox()
	{
		try
		{
			StackPanel stackPanel = ControlCard?.Child as StackPanel;
			if (!(stackPanel == null))
			{
				StackPanel stackPanel2 = new StackPanel
				{
					Orientation = Orientation.Horizontal,
					Spacing = 8.0
				};
				stackPanel2.Children.Add(new TextBlock
				{
					Text = "目标地址",
					VerticalAlignment = VerticalAlignment.Center,
					Opacity = 0.8
				});
				_suppressTargetChanged = true;
				_targetAddressBox = new TextBox
				{
					Width = 320.0,
					VerticalAlignment = VerticalAlignment.Center,
					PlaceholderText = "目标服务器地址（格式 host:port，留空使用所选预设默认）",
					Text = (SettingsManager.Instance.ProxyTargetOverride ?? "")
				};
				_suppressTargetChanged = false;
				_targetAddressBox.TextChanged += TargetAddressBox_TextChanged;
				stackPanel2.Children.Add(_targetAddressBox);
				stackPanel.Children.Add(stackPanel2);
				// 代理流模式开关紧跟在「目标地址」下面（与设置页 / 首页 / 快速启动 / 账号绑定共用同一份设置）
				try
				{
					stackPanel.Children.Add(SettingsPage.CreateProxyModeSelector());
				}
				catch (Exception exMode)
				{
					LogHelper.DebugError(exMode, "[代理启动] 代理模式切换构建失败");
				}
				UpdateTargetPlaceholder();
				LogHelper.Log("[代理启动] 已添加目标服务器地址输入框");
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[代理启动] 添加目标地址输入框失败");
		}
	}

	private void TargetAddressBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_suppressTargetChanged)
		{
			return;
		}
		try
		{
			string text = _targetAddressBox?.Text?.Trim() ?? "";
			AppSettings instance = SettingsManager.Instance;
			if (!string.Equals(instance.ProxyTargetOverride ?? "", text, StringComparison.Ordinal))
			{
				instance.ProxyTargetOverride = text;
				SettingsManager.Save(instance);
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[代理启动] 保存目标地址失败");
		}
	}

	// =====================================================================================
	// 布吉岛专用 A/B 会话共享代理
	//
	// 与「启动代理」按钮那条通用链路（ProxyCaptureService，按账号重走网易云认证）互相独立：
	// 这里是把 WtfNel/SakuraTools 的 payload 反射注入 A 客户端 JVM，由 payload 把 A 的
	// 已登录会话复制给 B，B 连本机端口即可，不需要第二个账号。
	//
	// 门禁：只有选中布吉岛预设（BjdGate.GameId）时区块才可见，点击时再判一次。
	// =====================================================================================

	/// <summary>「服务器预设 · 启动客户端」区块（选预设直接脱盒启动官方客户端）。</summary>
	/// <summary>代理启动页布局整理（2026-11-20）：
	/// 「客户端配置与启动」卡已按需求从代理启动页移除（只在快速启动页显示）；
	/// 「PCL 启动器」块从 ProxyCardGrid 里单独分出来，作为独立卡放在角色端口卡区域中间
	/// （角色端口卡下方、「千!万!不!要!点!」按钮上方）。</summary>
	private void BuildClientLauncherSection()
	{
		try
		{
			// 1) PCL 启动器块单独分出来：从 ProxyCardGrid 移到 ProxyRoot，紧跟在角色端口卡（ProxyCardGrid）后面
			if (XamlScope.Find(this, "ProxyCardGrid") is Panel cardGrid
				&& cardGrid.Children.Contains(PclSection)
				&& ProxyRoot != null && !ProxyRoot.Children.Contains(PclSection))
			{
				cardGrid.Children.Remove(PclSection);
				int gi = ProxyRoot.Children.IndexOf(cardGrid);
				ProxyRoot.Children.Insert((gi >= 0) ? (gi + 1) : ProxyRoot.Children.Count, PclSection);
				LogHelper.Log("[代理启动] PCL 启动器块已从端口卡网格分出，独立置于其下方");
			}
			// 2) 去掉「角色端口栏 / 状态」标题（XBF 预编译，运行时摘除）
			if (XamlScope.Find(this, "StatusSection") is StackPanel status)
			{
				for (int i = status.Children.Count - 1; i >= 0; i--)
				{
					if (status.Children[i] is TextBlock tb && (tb.Text ?? "").StartsWith("角色端口栏", StringComparison.Ordinal))
					{
						status.Children.RemoveAt(i);
					}
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[代理启动] PCL 区块整理失败");
		}
	}

	/// <summary>
	/// 区块常驻显示（用户要求）：不再随预设切换隐藏。
	/// 但目标服务器恒为布吉岛内置预设 —— 当前选中的预设若不是布吉岛，状态行会说明。
	/// </summary>
	private void UpdateBjdSectionVisibility()
	{
		try
		{
			if (_bjdSection == null)
			{
				return;
			}
			_bjdSection.Visibility = Visibility.Visible;
			if (_bjdHintText != null)
			{
				bool selectedIsBjd = BjdGate.IsBjd(SelectedPreset());
				_bjdHintText.Text = "把 A 已登录的会话共享给 B，不用第二个账号。"
					+ "⚠ 必须先用 A 进入布吉岛服务器（看到游戏世界）再点注入："
					+ "若 A 还没进服，payload 会挂起游戏主线程等待 B，画面会卡死，且按 DEL 也解不开。"
					+ (selectedIsBjd
						? ""
						: "\n（本区块常驻：注入目标固定为「" + BjdGate.PresetName + "」，与上面选中的服务器预设无关。）");
			}
			SyncBjdStatus();
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[布吉岛代理] 更新区块可见性失败");
		}
	}

	private void OnBjdStateChanged()
	{
		_queue?.TryEnqueue(SyncBjdStatus);
	}

	private void SyncBjdStatus()
	{
		try
		{
			if (_bjdStatusText == null)
			{
				return;
			}
			// 程序重启后接管仍存活的注入会话（payload 常驻游戏进程，端口可能还在）
			BjdProxyService.AdoptExistingSession();
			BjdProxyService.Refresh();
			string actionLine = string.IsNullOrWhiteSpace(_bjdLastAction)
				? ""
				: (Environment.NewLine + "上次操作：" + _bjdLastAction);
			if (BjdProxyService.IsActive)
			{
				string pid = (BjdProxyService.ActivePid > 0) ? ("（A 客户端 pid " + BjdProxyService.ActivePid + "）") : "";
				if (BjdProxyService.IsGameFrozen)
				{
					_bjdStatusText.Text = "状态：⚠ 已注入，但游戏主线程被挂起（画面卡住）· "
						+ "让 B 连 127.0.0.1:" + BjdProxyService.ActivePort + " 即可恢复；按 DEL 无效" + pid;
				}
				else
				{
					_bjdStatusText.Text = "状态：运行中（未阻塞游戏）· 端口 127.0.0.1:" + BjdProxyService.ActivePort + pid;
				}
				// B 是否接入：这是「注入成功却没效果」的最常见原因 —— 没有 B 就没有任何共享
				if (BjdProxyService.BConnected)
				{
					_bjdStatusText.Text += Environment.NewLine + "B：已接入";
				}
				else
				{
					_bjdStatusText.Text += Environment.NewLine
						+ "B：TCP 已连接，但尚未到达 PLAY —— 当前仍是握手未完成状态；"
						+ "不要重复注入。检查第二个客户端是否卡在“正在连接至服务器”，"
						+ "若出现 ServerboundHelloPacket / 12 bytes extra 异常，请重启两个客户端后重试";
				}
				// A 重连会让 payload 在新连接上重挂 Relay，实测会破坏 Forge 的进服流程
				int aConn = BjdProxyService.AConnectionCount;
				if (BjdProxyService.CapturedLiveSession && aConn > 0)
				{
					_bjdStatusText.Text += Environment.NewLine
						+ "⚠ 检测到 A 已重新建连 " + aConn + " 次：payload 会在新连接上重挂 Relay，"
						+ "实测可能触发 Forge 重复注册（Duplicate handler name: forge:vanilla_filter）、"
						+ "进服失败或服务端反作弊验证超时。建议重启客户端后按流程重来。";
				}
				_bjdStatusText.Text += actionLine;
				if (_bjdInjectButton != null)
				{
					_bjdInjectButton.Content = "复制 B 连接地址（已在运行）";
				}
				if (_bjdReInjectButton != null)
				{
					// 二次注入会重复挂 Hook/Relay 并破坏连接，固定禁用
					_bjdReInjectButton.IsEnabled = false;
				}
			}
			else
			{
				string last = string.IsNullOrWhiteSpace(BjdProxyService.LastMessage) ? "" : ("。" + BjdProxyService.LastMessage);
				_bjdStatusText.Text = "状态：未运行" + last + actionLine;
				if (_bjdInjectButton != null)
				{
					_bjdInjectButton.Content = "注入布吉岛代理";
				}
				if (_bjdReInjectButton != null)
				{
					_bjdReInjectButton.IsEnabled = false;
				}
			}
			if (_bjdLogButton != null)
			{
				_bjdLogButton.IsEnabled = BjdPayloadLog.CurrentPath != null;
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[布吉岛代理] 刷新状态失败");
		}
	}

	private void UpdateTargetPlaceholder()
	{
		try
		{
			if (_targetAddressBox != null)
			{
				_targetAddressBox.PlaceholderText = ((string.IsNullOrWhiteSpace(_fallbackHost) || _fallbackPort <= 0) ? "目标服务器地址（格式 host:port，留空使用所选预设默认）" : (_fallbackHost + ":" + _fallbackPort + "（留空使用此预设默认）"));
			}
		}
		catch
		{
		}
	}

	private void RemoveDisconnectHintBanner()
	{
		try
		{
			int num = 0;
			for (int num2 = ProxyRoot.Children.Count - 1; num2 >= 0; num2--)
			{
				if (ProxyRoot.Children[num2] is Border { Child: TextBlock { Text: not null } child } && child.Text.StartsWith("提示：如遇代理断连", StringComparison.Ordinal))
				{
					ProxyRoot.Children.RemoveAt(num2);
					num++;
				}
			}
			LogHelper.Debug("[代理启动] 断连提示横幅已移除 " + num + " 个");
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[代理启动] 断连提示横幅清理失败");
		}
	}

	private void RebuildPresetChips()
	{
		try
		{
			_presetRadios.Clear();
			PresetListView.Items.Clear();
			PresetListView.Visibility = Visibility.Collapsed;
			StackPanel stackPanel = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Spacing = 12.0
			};
			foreach (ServerPreset item in ServerPresets.All)
			{
				RadioButton radioButton = new RadioButton
				{
					Content = item.Name,
					Tag = item.Name,
					GroupName = "preset",
					MinWidth = 196.0,
					MinHeight = 52.0,
					Padding = new Thickness(16.0, 10.0, 16.0, 10.0),
					Margin = new Thickness(0.0, 0.0, 12.0, 6.0),
					VerticalContentAlignment = VerticalAlignment.Center,
					HorizontalContentAlignment = HorizontalAlignment.Stretch
				};
				radioButton.Checked += Preset_Checked;
				_presetRadios.Add(radioButton);
				stackPanel.Children.Add(radioButton);
			}
			if (ControlCard.Child is Panel panel)
			{
				// 2026-11-20 修复：预设多时横排会溢出边缘——套横向滑栏（与角色端口栏同款样式）
				ScrollViewer presetScroller = new ScrollViewer
				{
					Content = stackPanel,
					HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
					HorizontalScrollMode = ScrollMode.Enabled,
					VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
					VerticalScrollMode = ScrollMode.Disabled,
					HorizontalAlignment = HorizontalAlignment.Stretch,
					Padding = new Thickness(0.0, 0.0, 0.0, 4.0)
				};
				int num = panel.Children.IndexOf(PresetListView);
				panel.Children.Insert((num < 0) ? panel.Children.Count : num, presetScroller);
				LogHelper.Debug("[代理启动] 预设胶囊已插入控制卡（横向滑栏, 位置=" + num + ", 预设数=" + _presetRadios.Count + "）");
			}
			else
			{
				ControlCard.Child = stackPanel;
				LogHelper.Debug("[代理启动] 预设胶囊替换控制卡内容（原内容=" + (ControlCard.Child?.GetType().Name ?? "null") + "）");
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[代理启动] 服务器预设构建失败");
		}
	}

	private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		RelayoutProxy(e.NewSize.Width);
	}

	private void RelayoutProxy(double width)
	{
		bool flag = width >= 1200.0;
		ProxyRoot.HorizontalAlignment = (flag ? HorizontalAlignment.Center : HorizontalAlignment.Left);
		ProxyRoot.MaxWidth = (flag ? 1180 : 900);
		ProxyCardGrid.RowDefinitions.Clear();
		ProxyCardGrid.ColumnDefinitions.Clear();
		if (flag)
		{
			ProxyCardGrid.ColumnDefinitions.Add(new ColumnDefinition());
			ProxyCardGrid.ColumnDefinitions.Add(new ColumnDefinition());
			ProxyCardGrid.RowDefinitions.Add(new RowDefinition());
			ProxyCardGrid.RowDefinitions.Add(new RowDefinition());
			SetCell(ControlCard, 0, 0, 2);
			SetCell(StatusSection, 1, 0, 2);
			// PclSection 已分出网格（见 BuildClientLauncherSection），不再占网格行
		}
		else
		{
			ProxyCardGrid.ColumnDefinitions.Add(new ColumnDefinition());
			ProxyCardGrid.RowDefinitions.Add(new RowDefinition());
			ProxyCardGrid.RowDefinitions.Add(new RowDefinition());
			SetCell(ControlCard, 0, 0, 1);
			SetCell(StatusSection, 1, 0, 1);
		}
	}

	private static void SetCell(FrameworkElement element, int row, int column, int span)
	{
		Grid.SetRow(element, row);
		Grid.SetColumn(element, column);
		Grid.SetColumnSpan(element, span);
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		try
		{
			base.KeyDown += PageRoot_KeyDown;
			_queue = base.DispatcherQueue;
			StartBjdStatusTimer();
			RestorePresetSelection();
			ApplySelectedPreset();
			if (SettingsManager.Instance.ProxyListenPort > 0)
			{
				ListenPortBox.Text = SettingsManager.Instance.ProxyListenPort.ToString();
			}
			ProxyCaptureService.StateChanged += OnStateChanged;
			ProxyCaptureService.SessionOpened += OnSessionOpened;
			ProxyCaptureService.SessionClosed += OnSessionClosed;
			ElsewhereLoginNotice.Changed += OnElsewhereChanged;
			ActivationService.StateChanged += OnActivationChanged;
			// 2026-11-20：「允许局域网设备连接」UI 已移除；设置值仍然生效（AllowLanListen），仅不再提供界面开关。
			ProxyCaptureService.AllowLanBindings = SettingsManager.Instance.AllowLanListen;
			RefreshSessionList();
			SyncUiState();
			InitPclUi();
			_statusUiReady = true;
			StartStatusRefreshTimer();
			if (_statusWheelHandler == null)
			{
				_statusWheelHandler = delegate(object s, PointerRoutedEventArgs e2)
				{
					// 光标落在内部可纵向滚动的区域时交给它自己处理，不要抢
					if (((e2.OriginalSource is DependencyObject source) ? FindScrollableScroller(source, StatusCardsScroller) : null) != null)
					{
						return;
					}
					int mouseWheelDelta = e2.GetCurrentPoint(StatusCardsScroller).Properties.MouseWheelDelta;
					if (mouseWheelDelta == 0)
					{
						return;
					}
					// 修正「滚轮不准」：MouseWheelDelta 一格是 120（WHEEL_DELTA），
					// 早前直接把它当像素用（一格跳 120px）且开着过渡动画，
					// 连续滚动时动画排队错帧，看起来就是滚过头 / 滚不到位。
					// 现按「一格 ≈ 3 行 ≈ 48 有效像素」换算并关闭动画，做到一格一格精确跟手。
					double step = WheelStepPixels * (0.0 - (double)mouseWheelDelta / 120.0);
					double value = StatusCardsScroller.HorizontalOffset + step;
					double max = StatusCardsScroller.ScrollableWidth;
					if (value < 0.0)
					{
						value = 0.0;
					}
					else if (max > 0.0 && value > max)
					{
						value = max;
					}
					StatusCardsScroller.ChangeView(value, null, null, disableAnimation: true);
					e2.Handled = true;
				};
			}
			StatusCardsScroller.AddHandler(UIElement.PointerWheelChangedEvent, _statusWheelHandler, handledEventsToo: true);
			RelayoutProxy(base.ActualWidth);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "代理抓包页加载失败");
		}
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		ProxyCaptureService.StateChanged -= OnStateChanged;
		ProxyCaptureService.SessionOpened -= OnSessionOpened;
		ProxyCaptureService.SessionClosed -= OnSessionClosed;
		ElsewhereLoginNotice.Changed -= OnElsewhereChanged;
		ActivationService.StateChanged -= OnActivationChanged;
		BjdProxyService.StateChanged -= OnBjdStateChanged;
		base.KeyDown -= PageRoot_KeyDown;
		if (_statusWheelHandler != null)
		{
			StatusCardsScroller.RemoveHandler(UIElement.PointerWheelChangedEvent, _statusWheelHandler);
			_statusWheelHandler = null;
		}
		if (_statusRefreshTimer != null)
		{
			_statusRefreshTimer.Stop();
			_statusRefreshTimer = null;
		}
		if (_bjdTimer != null)
		{
			_bjdTimer.Stop();
			_bjdTimer = null;
		}
	}

	/// <summary>
	/// 布吉岛区块每 2 秒刷新一次：主要看两件运行期事实 ——
	/// B 是否已接入 13337（未接入 = 没有任何共享效果）、A 是否重连（会重挂 Relay 破坏进服）。
	/// </summary>
	private void StartBjdStatusTimer()
	{
		try
		{
			if (_bjdTimer != null)
			{
				_bjdTimer.Stop();
			}
			if (_queue == null)
			{
				return;
			}
			_bjdTimer = _queue.CreateTimer();
			_bjdTimer.Interval = TimeSpan.FromSeconds(2.0);
			_bjdTimer.Tick += delegate
			{
				if (_bjdBusy || _bjdSection == null || _bjdSection.Visibility != Visibility.Visible)
				{
					return;
				}
				SyncBjdStatus();
			};
			_bjdTimer.Start();
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[布吉岛代理] 启动状态定时器失败");
		}
	}

	private void OnActivationChanged()
	{
		_queue.TryEnqueue(UpdateActivationGate);
	}

	private void UpdateActivationGate()
	{
		if (!(ViewSolutionButton == null))
		{
			bool isActivated = ActivationService.IsActivated;
			StartButton.IsEnabled = isActivated;
			ActivationHint.Text = "";
			ActivationHint.Visibility = Visibility.Collapsed;
			GoHomeButton.Visibility = (isActivated ? Visibility.Collapsed : Visibility.Visible);
			ViewSolutionButton.Visibility = ((!isActivated) ? Visibility.Collapsed : Visibility.Visible);
		}
	}

	private void GoHome_Click(object sender, RoutedEventArgs e)
	{
		MainWindow.Instance?.NavigateTo("HomePage");
	}

	private void OnStateChanged(bool running)
	{
		_queue.TryEnqueue(SyncUiState);
	}

	private void OnElsewhereChanged()
	{
		_queue?.TryEnqueue(SyncUiState);
	}

	private void SyncUiState()
	{
		bool isRunning = ProxyCaptureService.IsRunning;
		string text = SelectedPreset()?.Name ?? "";
		List<string> runningPresets = RunningPresetNamesInOrder();
		StopButton.IsEnabled = isRunning && runningPresets.Contains(text);
		StopAllButton.IsEnabled = isRunning;
		StartButton.Content = ((isRunning && !ShouldAllowStartAnother()) ? "查看端口" : "启动代理");
		RestartButton.IsEnabled = ActivationService.IsActivated && !string.IsNullOrWhiteSpace(text);
		UpdatePresetRunningLabels();
		UpdateActivationGate();
		UpdateStatus();
		RefreshStatusCards();
	}

	private void UpdatePresetRunningLabels()
	{
		foreach (RadioButton presetRadio in _presetRadios)
		{
			SetPresetRunningLabel(presetRadio);
		}
	}

	private static void SetPresetRunningLabel(RadioButton button)
	{
		string text = (button.Tag as string) ?? "";
		bool flag = !string.IsNullOrWhiteSpace(text) && ProxyCaptureService.IsRunning && RunningPresetNamesInOrder().Contains(text);
		button.Content = text + (flag ? "（运行中）" : "");
	}

	private bool ShouldAllowStartAnother()
	{
		if (!ProxyCaptureService.IsRunning)
		{
			return false;
		}
		string text = SelectedPreset()?.Name ?? "";
		if (!string.IsNullOrWhiteSpace(text))
		{
			return !RunningPresetNamesInOrder().Contains(text);
		}
		return false;
	}

	private void OnProxyStarted()
	{
		string text = SelectedPreset()?.Name ?? "";
		AppendTail($"[{DateTime.Now:HH:mm:ss.fff}] 已开启「{text}」服务器的代理（端口自动向上递增）\n");
		SyncUiState();
	}

	private void OnSessionOpened(ProxyCaptureService.SessionInfo info)
	{
		_queue.TryEnqueue(delegate
		{
			_items.Add(new SessionItem
			{
				IdLabel = $"S{info.Id:000}",
				Status = info.Status,
				Summary = $"{info.Client} -> {info.Target}  起于 {info.Started:HH:mm:ss}",
				Id = info.Id
			});
			SessionList.ItemsSource = _items.ToArray();
			AppendTail($"[{DateTime.Now:HH:mm:ss.fff}] 会话 S{info.Id:000} 开启 {info.Client} -> {info.Target} 日志: {ProxyCaptureService.SessionLogLabel(info.LogPath)}\n");
		});
	}

	private void OnSessionClosed(ProxyCaptureService.SessionInfo info)
	{
		_queue.TryEnqueue(delegate
		{
			SessionItem sessionItem = _items.FirstOrDefault((SessionItem i) => i.Id == info.Id);
			if (sessionItem != null)
			{
				sessionItem.Status = info.Status;
				sessionItem.Summary = $"{info.Client} -> {info.Target}  ↑{info.UpBytes}B ↓{info.DownBytes}B  {info.Started:HH:mm:ss}~{info.Ended:HH:mm:ss} 日志: {ProxyCaptureService.SessionLogLabel(info.LogPath)}";
			}
			SessionList.ItemsSource = _items.ToArray();
			AppendTail($"[{DateTime.Now:HH:mm:ss.fff}] 会话 S{info.Id:000} 关闭  ↑{info.UpBytes}B ↓{info.DownBytes}B\n");
		});
	}

	private void RefreshSessionList()
	{
		_items.Clear();
		foreach (ProxyCaptureService.SessionInfo item in ProxyCaptureService.SessionsSnapshot())
		{
			_items.Add(new SessionItem
			{
				IdLabel = $"S{item.Id:000}",
				Status = item.Status,
				Summary = $"{item.Client} -> {item.Target}  ↑{item.UpBytes}B ↓{item.DownBytes}B  {item.Started:HH:mm:ss} 日志: {ProxyCaptureService.SessionLogLabel(item.LogPath)}",
				Id = item.Id
			});
		}
		SessionList.ItemsSource = _items.ToArray();
	}

	private void UpdateStatus()
	{
		if (ProxyCaptureService.IsRunning)
		{
			if (!string.IsNullOrEmpty(_fallbackHost))
			{
				_ = $"{_fallbackHost}:{_fallbackPort}（回环地址时回退）";
			}
			string text = string.Join(", ", ProxyCaptureService.ActivePorts.Select((int p) => $"127.0.0.1:{p}"));
			if (ProxyCaptureService.AllowLanBindings)
			{
				List<string> lanIps = GetLanIps();
				if (lanIps.Count > 0)
				{
					text = text + "   局域网: " + string.Join(" 或 ", lanIps.Select((string ip) => string.Join(", ", ProxyCaptureService.ActivePorts.Select((int p) => $"{ip}:{p}"))));
				}
			}
			if (RunningPresetNamesInOrder().Count != 0)
			{
				_ = " | 服务器: " + string.Join("、", RunningPresetNamesInOrder());
			}
		}
		AutoStartHint.Text = "";
		AutoStartHint.Visibility = Visibility.Collapsed;
	}

	private static List<string> GetLanIps()
	{
		List<string> list = new List<string>();
		try
		{
			IPAddress[] hostAddresses = Dns.GetHostAddresses(Dns.GetHostName());
			IPAddress[] array = hostAddresses;
			foreach (IPAddress iPAddress in array)
			{
				if (iPAddress.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(iPAddress))
				{
					list.Add(iPAddress.ToString());
				}
			}
		}
		catch
		{
		}
		return list;
	}

	private void AppendTail(string text)
	{
		LogHelper.Log("[代理页] " + (text ?? "").TrimEnd());
	}

	private ServerPreset? SelectedPreset()
	{
		string text = _presetRadios.FirstOrDefault((RadioButton r) => r.IsChecked == true)?.Tag as string;
		if (!string.IsNullOrWhiteSpace(text))
		{
			return ServerPresets.Find(text);
		}
		return null;
	}

	private void ApplySelectedPreset()
	{
		ServerPreset serverPreset = SelectedPreset();
		if (serverPreset == null || serverPreset.IsPending)
		{
			_fallbackHost = "";
			_fallbackPort = 0;
		}
		else
		{
			_fallbackHost = serverPreset.Host;
			_fallbackPort = serverPreset.Port;
		}
		UpdateTargetPlaceholder();
		// 布吉岛专用区块跟随预设切换显隐（唯一门禁入口）
		UpdateBjdSectionVisibility();
	}

	private void RestorePresetSelection()
	{
		string saved = SettingsManager.Instance.ProxyPresetName?.Trim() ?? "";
		RadioButton radioButton = _presetRadios.FirstOrDefault((RadioButton r) => string.Equals(r.Tag as string, saved, StringComparison.Ordinal));
		if ((object)radioButton == null)
		{
			radioButton = _presetRadios.FirstOrDefault();
		}
		if (!(radioButton == null))
		{
			radioButton.IsChecked = true;
		}
	}

	private void Preset_Checked(object sender, RoutedEventArgs e)
	{
		ApplySelectedPreset();
		if (sender is RadioButton { Tag: string tag } && !string.IsNullOrWhiteSpace(tag))
		{
			AppSettings instance = SettingsManager.Instance;
			instance.ProxyPresetName = tag;
			SettingsManager.Save(instance);
		}
		SyncUiState();
	}

	private async void StartButton_Click(object sender, RoutedEventArgs e)
	{
		bool flag = ShouldAllowStartAnother();
		if (!ProxyCaptureService.IsRunning | flag)
		{
			await OpenProxyStartAsync();
		}
		else
		{
			ShowPortsDialog();
		}
	}

	private async void RestartButton_Click(object sender, RoutedEventArgs e)
	{
		ServerPreset serverPreset = SelectedPreset();
		if (serverPreset == null)
		{
			return;
		}
		if (!ActivationService.IsActivated)
		{
			AppendTail("状态: 未激活，代理重启不可用\n");
			return;
		}
		bool flag = ProxyCaptureService.IsRunning && RunningPresetNamesInOrder().Contains(serverPreset.Name);
		if (flag)
		{
			AppendTail($"[{DateTime.Now:HH:mm:ss.fff}] 重启「{serverPreset.Name}」：当前代理保持运行，确认启动新批次后再切换（取消则维持原状）\n");
		}
		await OpenProxyStartAsync(flag);
	}

	private async Task OpenProxyStartAsync(bool stopOnStart = false)
	{
		if (!ActivationService.IsActivated)
		{
			AppendTail("状态: 未激活，代理启动不可用\n");
			return;
		}
		if (AccountStore.All.Count == 0)
		{
			if (await new ContentDialog
			{
				Title = "请先登录账号",
				Content = "启动代理需要先绑定网易账号（用于角色认证）。\n是否前往「账号绑定」页登录？",
				PrimaryButtonText = "前往登录",
				CloseButtonText = "取消",
				DefaultButton = ContentDialogButton.Primary,
				RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default),
				XamlRoot = base.XamlRoot,
			}.ShowAsync() == ContentDialogResult.Primary)
			{
				MainWindow.Instance?.NavigateTo("LoginPage");
			}
			return;
		}
		if (!int.TryParse(ListenPortBox.Text.Trim(), out var result) || result < 1 || result > 65535)
		{
			AppendTail("状态: 监听端口无效\n");
			return;
		}
		if (!ProxyCaptureService.HasEarliestRecord())
		{
			SettingsManager.Instance.ProxyListenPort = result;
			SettingsManager.Save(SettingsManager.Instance);
		}
		ApplySelectedPreset();
		ProxyStartDialog dialog = new ProxyStartDialog(presetName: SelectedPreset()?.Name, basePort: result, targetHost: _fallbackHost, targetPort: _fallbackPort, stopOnStart: stopOnStart)
		{
			XamlRoot = base.XamlRoot
		};
		bool started = false;
		dialog.ProxyStarted += delegate
		{
			started = true;
		};
		dialog.ProxyStarted += OnProxyStarted;
		await DialogGate.ShowAsync(dialog);
		UpdateStatus();
		if (started && dialog.AutoLaunchPcl)
		{
			await HandleAutoPclAsync();
		}
	}

	private void StopButton_Click(object sender, RoutedEventArgs e)
	{
		// 「停止」= 停掉当前预设下的全部代理监听（服务器代理 + 机器人代理）。
		// 机器人自启会先用 nexus- 标记占住同一批端口，旧实现只停服务器侧 → 按钮看起来完全没反应。
		ServerPreset? preset = SelectedPreset();
		if (preset == null || string.IsNullOrWhiteSpace(preset.Host))
		{
			AppendTail($"[{DateTime.Now:HH:mm:ss.fff}] 未选择服务器预设，无法停止\n");
			return;
		}
		List<int> targetPorts = ProxyCaptureService.ActiveBindingsSnapshot()
			.Where(b => string.Equals(b.TargetHost, preset.Host, StringComparison.OrdinalIgnoreCase))
			.Select(b => b.ListenPort)
			.ToList();
		int stopped = ProxyCaptureService.StopTargetProxies(preset.Host);
		int cards = NexusEndpointService.DisableBotCardsForPorts(targetPorts);
		AppendTail($"[{DateTime.Now:HH:mm:ss.fff}] 已停止「{preset.Name}」全部代理（{stopped} 个监听"
			+ (cards > 0 ? $"，含 {cards} 个机器人代理（其卡片已停用，重新启动请到会话多开页）" : "")
			+ "）\n");
		SyncUiState();
	}

	private void StopAllButton_Click(object sender, RoutedEventArgs e)
	{
		List<int> allPorts = ProxyCaptureService.ActiveBindingsSnapshot()
			.Select(b => b.ListenPort)
			.ToList();
		int stopped = ProxyCaptureService.StopAllProxies();
		int cards = NexusEndpointService.DisableBotCardsForPorts(allPorts);
		// 「停止全部代理」= 机器人也全部关停：顺带清掉客户端里可能残留的机器人代理卡片
		//（包括已停用/旧版本留下的、当前没有监听的那些）。
		int clearedCards = NexusEndpointService.ClearAllBotProxyCards();
		AppendTail($"[{DateTime.Now:HH:mm:ss.fff}] 已停止全部代理（{stopped} 个监听"
			+ (cards > 0 ? $"，含 {cards} 个机器人代理（其卡片已停用，重新启动请到会话多开页）" : "")
			+ (clearedCards > 0 ? $"，已清理游戏内代理卡片 {clearedCards} 条" : "")
			+ "）\n");
		SyncUiState();
	}

	private void StartStatusRefreshTimer()
	{
		if (_statusRefreshTimer != null)
		{
			_statusRefreshTimer.Stop();
		}
		_statusRefreshTimer = _queue.CreateTimer();
		_statusRefreshTimer.Interval = TimeSpan.FromMilliseconds(600.0);
		_statusRefreshTimer.Tick += delegate
		{
			if (_statusUiReady)
			{
				RefreshStatusCards();
				if (StatusCardsList.ItemsSource != null)
				{
					_statusRefreshTimer?.Stop();
				}
			}
		};
		_statusRefreshTimer.Start();
	}

	private void RefreshStatusCards()
	{
		if (!_statusUiReady)
		{
			return;
		}
		try
		{
			PopulateStatusCards();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "刷新角色端口栏失败");
		}
	}

	private sealed class RoleStartTag
	{
		public AccountStore.LocalAccount? Account;

		public int AccountIndex = -1;

		public string? RoleName;

		public string? TargetHost;

		public int TargetPort;
	}

	private void PopulateStatusCards()
	{
		List<ProxyCaptureService.ActiveBindingInfo> activeBindings = ProxyCaptureService.ActiveBindingsSnapshot().ToList();
		List<FrameworkElement> list = new List<FrameworkElement>();
		List<AccountStore.LocalAccount> accounts = AccountStore.All.ToList();
		List<string> presetNames = ServerPresets.All.Select((ServerPreset p) => p.Name).ToList();
		foreach (AccountStore.LocalAccount account in accounts)
		{
			int accountIndex = accounts.IndexOf(account);
			StackPanel card = new StackPanel
			{
				Spacing = 6.0
			};
			card.Children.Add(new TextBlock
			{
				Text = "■ " + AccountLabel(accountIndex) + "（" + account.Channel + "）" + ((ElsewhereLoginNotice.Get(account.Channel, account.Account) != null) ? "（⚠ 别处登录）" : ""),
				FontSize = 13.5,
				FontWeight = FontWeights.Bold,
				Opacity = 0.95
			});
			bool anyRole = false;
			List<string> serverKeys = new List<string>();
			ServerPreset? activePreset = SelectedPreset();
			if (account.ServerRoles != null)
			{
				if (activePreset != null)
				{
					if (account.ServerRoles.TryGetValue(activePreset.Name, out var pre) && pre != null && pre.Count > 0)
					{
						serverKeys.Add(activePreset.Name);
					}
				}
				else
				{
					foreach (string presetName in presetNames)
					{
						if (account.ServerRoles.TryGetValue(presetName, out var pre2) && pre2 != null && pre2.Count > 0)
						{
							serverKeys.Add(presetName);
						}
					}
				}
			}
			foreach (string serverKey in serverKeys)
			{
				List<string> roles = ((account.ServerRoles != null && account.ServerRoles.TryGetValue(serverKey, out var value) && value != null) ? value.Where((string r) => !string.IsNullOrWhiteSpace(r)).Distinct().OrderBy((string r) => r, StringComparer.Ordinal).ToList() : new List<string>());
				if (roles.Count == 0)
				{
					continue;
				}
				ServerPreset serverPreset = ServerPresets.Find(serverKey);
				card.Children.Add(new TextBlock
				{
					Text = "◆ " + serverKey + ((serverPreset == null) ? "" : $"（{serverPreset.Host}:{serverPreset.Port}）"),
					FontSize = 13.0,
					FontWeight = FontWeights.SemiBold,
					Opacity = 0.9,
					Margin = new Thickness(14.0, 4.0, 0.0, 0.0)
				});
				foreach (string role in roles)
				{
					string targetHost;
					int targetPort;
					if (serverPreset != null)
					{
						targetHost = serverPreset.Host;
						targetPort = serverPreset.Port;
					}
					else
						{
							ServerPreset? current = SelectedPreset();
							targetHost = (current?.Host) ?? _fallbackHost;
							targetPort = ((current != null && current.Port > 0) ? current.Port : _fallbackPort);
						}
					ProxyCaptureService.ActiveBindingInfo activeBinding = activeBindings.FirstOrDefault(delegate(ProxyCaptureService.ActiveBindingInfo b)
					{
						if (b.AuthAccountIndex != accountIndex || !string.Equals(b.LoginName, role, StringComparison.Ordinal))
						{
							return false;
						}
						if (!string.Equals(b.TargetHost ?? "", targetHost ?? "", StringComparison.OrdinalIgnoreCase))
						{
							return false;
						}
						return b.TargetPort == targetPort;
					});
					Grid grid = new Grid
					{
						ColumnSpacing = 8.0,
						Margin = new Thickness(14.0, 0.0, 0.0, 0.0)
					};
					grid.ColumnDefinitions.Add(new ColumnDefinition
					{
						Width = new GridLength(1.0, GridUnitType.Star)
					});
					grid.ColumnDefinitions.Add(new ColumnDefinition
					{
						Width = GridLength.Auto
					});
					grid.ColumnDefinitions.Add(new ColumnDefinition
					{
						Width = GridLength.Auto
					});
					grid.ColumnDefinitions.Add(new ColumnDefinition
					{
						Width = GridLength.Auto
					});
					TextBlock roleText = new TextBlock
					{
						Text = role,
						VerticalAlignment = VerticalAlignment.Center,
						FontSize = 12.5,
						TextTrimming = TextTrimming.CharacterEllipsis
					};
					Grid.SetColumn(roleText, 0);
					grid.Children.Add(roleText);
					TextBlock portText = new TextBlock
					{
						Text = ((activeBinding != null) ? ("127.0.0.1:" + activeBinding.ListenPort) : "未启动"),
						VerticalAlignment = VerticalAlignment.Center,
						FontSize = 12.0,
						Opacity = ((activeBinding != null) ? 0.95 : 0.55)
					};
					Grid.SetColumn(portText, 1);
					grid.Children.Add(portText);
					Button startButton = new Button
					{
						Content = ((activeBinding != null) ? "停止" : "启动"),
						FontSize = 12.0,
						Padding = new Thickness(12.0, 3.0, 12.0, 3.0),
						MinHeight = 0.0,
						VerticalAlignment = VerticalAlignment.Center
					};
					if (activeBinding != null)
					{
						startButton.Tag = activeBinding;
						startButton.Click += StopRoleProxy_Click;
					}
					else
					{
						startButton.Tag = new RoleStartTag
						{
							Account = account,
							AccountIndex = accountIndex,
							RoleName = role,
							TargetHost = targetHost,
							TargetPort = targetPort
						};
						startButton.Click += StartRoleProxy_Click;
					}
					Grid.SetColumn(startButton, 2);
					grid.Children.Add(startButton);
					if (activeBinding != null)
					{
						Button copyButton = new Button
						{
							Content = "复制",
							FontSize = 12.0,
							Padding = new Thickness(10.0, 3.0, 10.0, 3.0),
							MinHeight = 0.0,
							VerticalAlignment = VerticalAlignment.Center
						};
						copyButton.Tag = activeBinding;
						copyButton.Click += CopyRoleAddress_Click;
						Grid.SetColumn(copyButton, 3);
						grid.Children.Add(copyButton);
					}
					card.Children.Add(grid);
					anyRole = true;
				}
			}
			if (!anyRole)
			{
				card.Children.Add(new TextBlock
				{
					Text = "暂无角色（请在「账号绑定」页刷新角色）",
					FontSize = 12.0,
					Opacity = 0.7,
					TextWrapping = TextWrapping.Wrap
				});
			}
			ServerPreset? selectedPreset = SelectedPreset();
			string groupServer = (selectedPreset?.Name) ?? serverKeys.FirstOrDefault();
			Button groupButton = new Button
			{
				Content = "分组…",
				FontSize = 12.0,
				Padding = new Thickness(10.0, 3.0, 10.0, 3.0),
				MinHeight = 0.0,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
			};
			groupButton.Tag = new Tuple<AccountStore.LocalAccount?, string?>(account, groupServer);
			groupButton.Click += AccountGroup_Click;
			card.Children.Add(groupButton);
			list.Add(new Border
			{
				Background = (ThemeBrush("CardBackgroundFillColorSecondaryBrush") ?? ThemeBrush("CardBackgroundFillColorDefaultBrush")),
				BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
				BorderThickness = new Thickness(1.0),
				CornerRadius = new CornerRadius(10.0),
				Padding = new Thickness(12.0, 10.0, 12.0, 10.0),
				Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
				MinWidth = 340.0,
				Child = card
			});
		}
		StatusCardsList.ItemTemplate = null;
		StatusCardsList.ItemsSource = list;
		StatusEmptyHint.Text = "暂无账号：请先在「账号绑定」页登录账号并刷新角色";
		StatusEmptyHint.Visibility = ((list.Count > 0) ? Visibility.Collapsed : Visibility.Visible);
	}

	private void StartRoleProxy_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (!(sender is FrameworkElement frameworkElement) || !(frameworkElement.Tag is RoleStartTag roleStartTag) || roleStartTag.Account == null)
			{
				return;
			}
			if (!ActivationService.IsActivated)
			{
				MainWindow.Instance?.ShowToast("未激活，代理启动不可用", false);
				return;
			}
			string targetHost = roleStartTag.TargetHost;
			int targetPort = roleStartTag.TargetPort;
			if (string.IsNullOrWhiteSpace(targetHost) || targetPort <= 0)
			{
				ServerPreset? current = SelectedPreset();
				targetHost = (current?.Host) ?? _fallbackHost;
				targetPort = ((current != null && current.Port > 0) ? current.Port : _fallbackPort);
			}
			if (string.IsNullOrWhiteSpace(targetHost) || targetPort <= 0)
			{
				MainWindow.Instance?.ShowToast("无法确定目标服务器，请先选择服务器预设", false);
				return;
			}
			int num = SettingsManager.Instance.ProxyListenPort;
			if (num < 1 || num > 65535)
			{
				num = 25565;
			}
			int listenPort = ProxyCaptureService.NextFreePort(num);
			if (listenPort < 1)
			{
				MainWindow.Instance?.ShowToast("没有可用监听端口", false);
				return;
			}
			List<ProxyCaptureService.ProxyBinding> list = new List<ProxyCaptureService.ProxyBinding>
			{
				new ProxyCaptureService.ProxyBinding
				{
					ListenPort = listenPort,
					LoginName = roleStartTag.RoleName,
					TargetHost = targetHost,
					TargetPort = targetPort,
					AuthAccountIndex = roleStartTag.AccountIndex
				}
			};
			ProxyCaptureService.TargetFromHandshake = true;
			ProxyCaptureService.StartBatch(list, targetHost, targetPort);
			if (!ProxyCaptureService.IsRunning)
			{
				MainWindow.Instance?.ShowToast("代理启动失败（端口可能被占用）", false);
				return;
			}
			ServerPreset serverPreset = ServerPresets.Find(targetHost);
			if (!ProxyCaptureService.HasEarliestRecord())
			{
				ProxyStarter.RememberStarted(serverPreset, list);
			}
			ProxyStarter.RememberLastLaunch("角色启动", ((serverPreset != null) ? serverPreset.Name : targetHost) + " 角色「" + (roleStartTag.RoleName ?? "默认") + "」共 1 个角色端口");
			MainWindow.Instance?.ShowToast("已启动「" + (roleStartTag.RoleName ?? "默认") + "」代理入口：127.0.0.1:" + listenPort, true);
			SyncUiState();
			RefreshStatusCards();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "单角色启动代理失败");
		}
	}

	private void StopRoleProxy_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (!(sender is FrameworkElement frameworkElement) || !(frameworkElement.Tag is ProxyCaptureService.ActiveBindingInfo binding) || binding.ListenPort < 1)
			{
				return;
			}
			// 「停止这一行角色」：服务器代理与机器人代理都按这个端口停。
			// 机器人自启占住同一端口时，旧实现只拒绝、不停，按钮看起来完全没反应。
			bool ok = ProxyCaptureService.StopPort(binding.ListenPort);
			int cards = ok ? NexusEndpointService.DisableBotCardsForPorts(new int[1] { binding.ListenPort }) : 0;
			MainWindow.Instance?.ShowToast(ok
				? ("已停止 127.0.0.1:" + binding.ListenPort + (cards > 0 ? "（机器人卡片已停用）" : ""))
				: ("127.0.0.1:" + binding.ListenPort + " 没有可停止的代理"), ok);
			SyncUiState();
			RefreshStatusCards();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "停止单角色代理失败");
		}
	}

	private void CopyRoleAddress_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (!(sender is FrameworkElement frameworkElement) || !(frameworkElement.Tag is ProxyCaptureService.ActiveBindingInfo binding) || binding.ListenPort < 1)
			{
				return;
			}
			string text = "127.0.0.1:" + binding.ListenPort;
			global::Windows.ApplicationModel.DataTransfer.DataPackage dataPackage = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
			dataPackage.SetText(text);
			global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
			MainWindow.Instance?.ShowToast("已复制代理入口 " + text, true);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "复制代理地址失败");
		}
	}

	private async void AccountGroup_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (!(sender is FrameworkElement frameworkElement) || !(frameworkElement.Tag is Tuple<AccountStore.LocalAccount?, string?> tuple) || tuple.Item1 == null)
			{
				return;
			}
			AccountStore.LocalAccount localAccount = tuple.Item1;
			string? text = tuple.Item2;
			List<string> list = new List<string>();
			if (!string.IsNullOrWhiteSpace(text) && localAccount.ServerRoles != null && localAccount.ServerRoles.TryGetValue(text, out var value) && value != null)
			{
				list.AddRange(value);
			}
			foreach (ProxyCaptureService.ActiveBindingInfo item in ProxyCaptureService.ActiveBindingsSnapshot())
			{
				if (item.AuthAccountIndex >= 0 && item.AuthAccountIndex < AccountStore.All.Count && string.Equals(AccountStore.All[item.AuthAccountIndex].Account, localAccount.Account, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.LoginName) && !list.Contains(item.LoginName))
				{
					list.Add(item.LoginName);
				}
			}
			if (list.Count == 0)
			{
				MainWindow.Instance?.ShowToast("该账号在当前服务器下暂无角色", false);
				return;
			}
			AppSettings appSettings = SettingsManager.Instance;
			List<ProxyGroup> list2 = appSettings.ProxyGroups ?? new List<ProxyGroup>();
			ComboBox comboBox = new ComboBox
			{
				Header = "加入分组",
				MinWidth = 220.0,
				HorizontalAlignment = HorizontalAlignment.Stretch
			};
			ThemeHelper.SolidComboPopup(comboBox);
			foreach (ProxyGroup item2 in list2)
			{
				comboBox.Items.Add(item2.Name);
			}
			comboBox.Items.Add("＋ 新建分组…");
			comboBox.SelectedIndex = ((list2.Count > 0) ? 0 : list2.Count);
			TextBox textBox = new TextBox
			{
				Header = "新分组名称（选择新建时生效）",
				PlaceholderText = "输入分组名称"
			};
			StackPanel stackPanel = new StackPanel
			{
				Spacing = 6.0
			};
			Dictionary<string, CheckBox> dictionary = new Dictionary<string, CheckBox>();
			foreach (string item3 in list)
			{
				CheckBox checkBox = new CheckBox
				{
					Content = item3,
					IsChecked = false
				};
				dictionary[item3] = checkBox;
				stackPanel.Children.Add(checkBox);
			}
			StackPanel stackPanel2 = new StackPanel
			{
				Spacing = 10.0,
				MinWidth = 380.0
			};
			stackPanel2.Children.Add(comboBox);
			stackPanel2.Children.Add(textBox);
			stackPanel2.Children.Add(new TextBlock
			{
				Text = "选择要加入该分组的角色（仅当前账号下的角色）：",
				FontSize = 12.5,
				Opacity = 0.8
			});
			stackPanel2.Children.Add(new ScrollViewer
			{
				MaxHeight = 260.0,
				Content = stackPanel,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto
			});
			ContentDialog contentDialog = new ContentDialog
			{
				Title = "账号角色分组",
				Content = stackPanel2,
				PrimaryButtonText = "保存",
				CloseButtonText = "取消",
				DefaultButton = ContentDialogButton.Primary,
				RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default),
				XamlRoot = base.XamlRoot
			};
			if (await DialogGate.ShowAsync(contentDialog) != ContentDialogResult.Primary)
			{
				return;
			}
			appSettings = SettingsManager.Instance;
			appSettings.ProxyGroups ??= new List<ProxyGroup>();
			ProxyGroup proxyGroup;
			bool flag = comboBox.SelectedIndex >= list2.Count || string.IsNullOrWhiteSpace(comboBox.SelectedItem as string);
			if (flag)
			{
				string text2 = textBox.Text.Trim();
				if (string.IsNullOrEmpty(text2))
				{
					text2 = "新分组" + DateTime.Now.ToString("MMdd-HHmmss");
				}
				proxyGroup = appSettings.ProxyGroups!.FirstOrDefault((ProxyGroup g) => string.Equals(g.Name, text2, StringComparison.Ordinal));
				if (proxyGroup == null)
				{
					proxyGroup = new ProxyGroup
					{
						Name = text2,
						ServerName = (text ?? "")
					};
					appSettings.ProxyGroups!.Add(proxyGroup);
				}
			}
			else
			{
				proxyGroup = appSettings.ProxyGroups![Math.Max(0, comboBox.SelectedIndex)];
			}
			int num = Math.Max(20000, appSettings.GroupPortBase);
			int num2 = 0;
			foreach (string item4 in list)
			{
				if (dictionary[item4].IsChecked != true)
				{
					continue;
				}
				bool flag2 = proxyGroup.Roles.Any((ProxyGroupRole r) => string.Equals(r.Channel, localAccount.Channel, StringComparison.Ordinal) && string.Equals(r.Account, localAccount.Account, StringComparison.Ordinal) && string.Equals(r.RoleName, item4, StringComparison.Ordinal) && string.Equals(r.ServerName, text ?? "", StringComparison.Ordinal));
				if (!flag2)
				{
					int num3 = ProxyCaptureService.NextFreePort(num);
					if (num3 < 1)
					{
						num3 = num;
					}
					num = num3 + 1;
					proxyGroup.Roles.Add(new ProxyGroupRole
					{
						Channel = (localAccount.Channel ?? ""),
						Account = (localAccount.Account ?? ""),
						RoleName = item4,
						ServerName = (text ?? ""),
						ListenPort = num3
					});
					num2++;
				}
			}
			SettingsManager.Save(appSettings);
			MainWindow.Instance?.ShowToast((num2 > 0) ? $"已把 {num2} 个角色加入分组「{proxyGroup.Name}」" : "所选角色已在分组中", false);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "账号角色分组失败");
		}
	}

	private void ShowPortsDialog()
	{
		try
		{
			ShowPortsDialogCore();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "查看端口弹窗打开失败");
			MainWindow.Instance?.ShowToast("查看端口打开失败：" + ex.Message, false);
		}
	}

	private void ShowPortsDialogCore()
	{
		List<ProxyCaptureService.ActiveBindingInfo> list = ProxyCaptureService.ActiveBindingsSnapshot().ToList();
		if (list.Count == 0)
		{
			UpdateStatus();
			MainWindow.Instance?.ShowToast("当前没有运行中的角色端口", false);
			return;
		}
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 10.0,
			MinWidth = 420.0
		};
		StackPanel rowsRoot = new StackPanel
		{
			Spacing = 10.0
		};
		foreach (IGrouping<string, ProxyCaptureService.ActiveBindingInfo> item in GroupBindingsByServer(list))
		{
			ServerPreset serverPreset = (string.IsNullOrWhiteSpace(item.Key) ? null : ServerPresets.Find(item.Key));
			rowsRoot.Children.Add(new TextBlock
			{
				Text = "◆ " + ((string.IsNullOrWhiteSpace(item.Key)) ? "未知服务器" : (item.Key + ((serverPreset == null) ? "" : $"（{serverPreset.Host}:{serverPreset.Port}）"))),
				FontSize = 13.5,
				FontWeight = FontWeights.SemiBold,
				Opacity = 0.95
			});
			foreach (IGrouping<int, ProxyCaptureService.ActiveBindingInfo> item2 in from b in item
					group b by b.AuthAccountIndex)
			{
				rowsRoot.Children.Add(new TextBlock
				{
					Text = "■ " + AccountLabel(item2.Key),
					FontSize = 12.5,
					FontWeight = FontWeights.SemiBold,
					Opacity = 0.9,
					Margin = new Thickness(14.0, 0.0, 0.0, 0.0)
				});
				foreach (ProxyCaptureService.ActiveBindingInfo binding in item2)
				{
					string address = "127.0.0.1:" + binding.ListenPort;
					string label = (string.IsNullOrWhiteSpace(binding.LoginName) ? "默认" : binding.LoginName);
					rowsRoot.Children.Add(BuildPortRow(label, address));
				}
			}
		}
		ScrollViewer scrollViewer = new ScrollViewer
		{
			Content = rowsRoot,
			MaxHeight = 440.0,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Padding = new Thickness(0.0, 4.0, 4.0, 4.0)
		};
		stackPanel.Children.Add(scrollViewer);
		stackPanel.Children.Add(new TextBlock
		{
			Text = "抓取目录：" + ProxyCaptureService.CaptureDir,
			Opacity = 0.75,
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap
		});
		ContentDialog contentDialog = new ContentDialog
		{
			Title = "查看端口",
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default),
			XamlRoot = base.XamlRoot
		};
		Button button2 = new Button
		{
			Content = "关闭",
			MinHeight = 36.0,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		button2.Click += delegate
		{
			contentDialog.Hide();
		};
		stackPanel.Children.Add(button2);
		contentDialog.Content = stackPanel;
		_ = DialogGate.ShowAsync(contentDialog);
	}

	private FrameworkElement BuildPortRow(string label, string address)
	{
		Grid grid = new Grid
		{
			ColumnSpacing = 8.0,
			Margin = new Thickness(14.0, 0.0, 0.0, 0.0)
		};
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		TextBlock textBlock = new TextBlock
		{
			Text = label,
			FontSize = 13.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		Border border = new Border
		{
			Background = (ThemeBrush("ControlFillColorSecondaryBrush") ?? ThemeBrush("CardBackgroundFillColorSecondaryBrush")),
			BorderBrush = (ThemeBrush("ControlStrokeColorDefaultBrush") ?? ThemeBrush("CardStrokeColorDefaultBrush")),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(4.0),
			Padding = new Thickness(10.0, 4.0, 10.0, 4.0),
			Child = new TextBlock
			{
				Text = address,
				FontSize = 13.0,
				FontFamily = new FontFamily("Consolas"),
				VerticalAlignment = VerticalAlignment.Center
			}
		};
		Button button = new Button
		{
			Content = "复制",
			MinWidth = 64.0,
			MinHeight = 32.0,
			Tag = address
		};
		button.Click += async delegate(object s, RoutedEventArgs e)
		{
			if (s is Button button2 && button2.Tag is string text)
			{
				try
				{
					DataPackage dataPackage = new DataPackage();
					dataPackage.SetText(text);
					Clipboard.SetContent(dataPackage);
					Clipboard.Flush();
					button2.Content = "已复制";
					await Task.Delay(1500);
					button2.Content = "复制";
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "复制端口失败");
				}
			}
		};
		Grid.SetColumn(textBlock, 0);
		Grid.SetColumn(border, 1);
		Grid.SetColumn(button, 2);
		grid.Children.Add(textBlock);
		grid.Children.Add(border);
		grid.Children.Add(button);
		return grid;
	}

	private void ClosePortsPanel()
	{
		if (!_portsClosing && PortsOverlayHost.Visibility == Visibility.Visible)
		{
			_portsClosing = true;
			TimeSpan duration = TimeSpan.FromMilliseconds(160.0);
			Storyboard storyboard = new Storyboard();
			AddPortsAnimation(storyboard, "Opacity", 1.0, 0.0, duration);
			AddPortsAnimation(storyboard, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)", 1.0, 0.94, duration);
			AddPortsAnimation(storyboard, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)", 1.0, 0.94, duration);
			storyboard.Completed += delegate
			{
				PortsOverlayHost.Children.Clear();
				PortsOverlayHost.Visibility = Visibility.Collapsed;
				PortsOverlayHost.RenderTransform = null;
				PortsOverlayHost.Opacity = 1.0;
				_portsClosing = false;
			};
			storyboard.Begin();
		}
	}

	private void AddPortsAnimation(Storyboard storyboard, string property, double from, double to, TimeSpan duration)
	{
		DoubleAnimation doubleAnimation = new DoubleAnimation
		{
			From = from,
			To = to,
			Duration = duration,
			EnableDependentAnimation = true
		};
		Storyboard.SetTarget(doubleAnimation, PortsOverlayHost);
		Storyboard.SetTargetProperty(doubleAnimation, property);
		storyboard.Children.Add(doubleAnimation);
	}

	private void PageRoot_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Escape && PortsOverlayHost.Visibility == Visibility.Visible)
		{
			ClosePortsPanel();
			e.Handled = true;
		}
	}

	private static ScrollViewer? FindScrollableScroller(DependencyObject source, DependencyObject boundary)
	{
		DependencyObject dependencyObject = source;
		while (dependencyObject != null && dependencyObject != boundary)
		{
			if (dependencyObject is ScrollViewer { ScrollableHeight: >0.0 } scrollViewer)
			{
				return scrollViewer;
			}
			dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
		}
		return null;
	}

	private static IEnumerable<IGrouping<string, ProxyCaptureService.ActiveBindingInfo>> GroupBindingsByServer(List<ProxyCaptureService.ActiveBindingInfo> bindings)
	{
		return bindings.GroupBy(delegate(ProxyCaptureService.ActiveBindingInfo b)
		{
			object obj;
			if (!string.IsNullOrWhiteSpace(b.TargetHost))
			{
				obj = ServerPresets.All.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, b.TargetHost, StringComparison.OrdinalIgnoreCase))?.Name;
				if (obj == null)
				{
					return b.TargetHost;
				}
			}
			else
			{
				obj = "";
			}
			return (string)obj;
		});
	}

	private static List<string> RunningPresetNamesInOrder()
	{
		List<string> list = new List<string>();
		foreach (ProxyCaptureService.ActiveBindingInfo b in ProxyCaptureService.ActiveBindingsSnapshot())
		{
			if (!string.IsNullOrWhiteSpace(b.TargetHost))
			{
				string text = ServerPresets.All.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, b.TargetHost, StringComparison.OrdinalIgnoreCase))?.Name ?? "";
				if (!string.IsNullOrWhiteSpace(text) && !list.Contains(text))
				{
					list.Add(text);
				}
			}
		}
		return list;
	}

	private static string AccountLabel(int accountIndex)
	{
		IReadOnlyList<AccountStore.LocalAccount> all = AccountStore.All;
		if (accountIndex < 0 || accountIndex >= all.Count)
		{
			return "(默认)";
		}
		AccountStore.LocalAccount localAccount = all[accountIndex];
		if (!string.IsNullOrWhiteSpace(localAccount.Nickname))
		{
			return localAccount.Nickname;
		}
		return localAccount.Account ?? "";
	}

	private void OpenCaptureDir_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Directory.CreateDirectory(ProxyCaptureService.CaptureDir);
			Process.Start(new ProcessStartInfo("explorer.exe", ProxyCaptureService.CaptureDir)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "打开抓取目录失败");
		}
	}

	private void ClearSessions_Click(object sender, RoutedEventArgs e)
	{
		_items.Clear();
		SessionList.ItemsSource = null;
	}

	private async void CleanLogs_Click(object sender, RoutedEventArgs e)
	{
		int num = LogCleanup.CleanNow();
		await new ContentDialog
		{
			Title = "清理日志",
			Content = ((num > 0) ? $"已清理 {num} 个日志文件(保留今天的应用日志)" : "没有可清理的日志"),
			CloseButtonText = "确定",
			XamlRoot = base.XamlRoot,
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default),
		}.ShowAsync();
	}

	private void InitPclUi()
	{
		ReconcilePclSection();
		AddPclCptdToggle();
		string text = ResolvePclPath();
		PclPathBox.Text = text;
		_suppressPclOption = true;
		PclAutoLaunchCheck.IsChecked = SettingsManager.Instance.PclAutoLaunchGame;
		PclAutoStartCheck.IsChecked = SettingsManager.Instance.AutoStartPcl;
		if (PclInjectCptdCheck != null)
		{
			PclInjectCptdCheck.IsChecked = SettingsManager.Instance.PclInjectCptd;
		}
		_suppressPclOption = false;
		UpdatePclUiState();
		UpdatePclHint(text);
	}

	private string ResolvePclPath()
	{
		string text = SettingsManager.Instance.PclPath?.Trim() ?? "";
		if (!string.IsNullOrWhiteSpace(text) && File.Exists(text))
		{
			return text;
		}
		if (File.Exists("G:\\PCL\\PCL2.exe"))
		{
			return "G:\\PCL\\PCL2.exe";
		}
		List<string> list = FindPclCandidatesFast();
		if (list.Count > 0)
		{
			return list[0];
		}
		return "";
	}

	/// <summary>快速候选：注册表卸载项 + 各固定盘根目录两层浅扫（深度扫描留给手动「查找」按钮）。实现已抽到 PclLocator 供更新页复用。</summary>
	private static List<string> FindPclCandidatesFast()
	{
		return PclLocator.FindPclCandidatesFast();
	}

	private void UpdatePclHint(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			PclPathHint.Text = "未找到 PCL 启动器，可手动填写路径、点击「浏览…」选择，或点击「查找此电脑上的PCL」自动搜索。";
			return;
		}
		string text = FindModsFolder(path);
		List<string> list = FindMinecraftVersions(path);
		string text2 = ((list.Count > 0) ? ("游戏版本：" + string.Join("、", list.Take(12)) + ((list.Count > 12) ? (" 等" + list.Count + " 个") : "")) : "未检测到游戏版本（请确认 .minecraft 文件夹与 PCL 启动器在同一目录）");
		ClientDetection clientDetection = ModInjectService.DetectClient(path);
		string text3 = (clientDetection.Matched ? ("已识别：" + clientDetection.VersionDir + "（模组 " + clientDetection.Rule.SourceFileName + " -> " + clientDetection.Rule.TargetFileName + " 就绪）") : ("模组注入：" + clientDetection.Detail));
		string text4 = (SettingsManager.Instance.PclInjectCptd ? "MhyCPTD 注入：开启" : "MhyCPTD 注入：关闭（客户端内已停用）");
		PclPathHint.Text = "启动器：" + path + (string.IsNullOrEmpty(text) ? "" : ("\nMod 文件夹：" + text)) + "\n" + text2 + "\n" + text3 + "\n" + text4;
	}

	private void PclPath_TextChanged(object sender, TextChangedEventArgs e)
	{
		UpdatePclUiState();
		UpdatePclHint(PclPathBox.Text.Trim());
	}

	private void UpdatePclUiState()
	{
		bool isEnabled = File.Exists(PclPathBox.Text.Trim());
		PclAutoLaunchCheck.IsEnabled = isEnabled;
		PclAutoStartCheck.IsEnabled = isEnabled;
		if (PclInjectCptdCheck != null)
		{
			PclInjectCptdCheck.IsEnabled = isEnabled;
		}
	}

	private void PclOption_Changed(object sender, RoutedEventArgs e)
	{
		if (_suppressPclOption)
		{
			return;
		}
		AppSettings instance = SettingsManager.Instance;
		bool flag = instance.PclInjectCptd;
		instance.PclAutoLaunchGame = PclAutoLaunchCheck.IsChecked == true;
		instance.AutoStartPcl = PclAutoStartCheck.IsChecked == true;
		instance.PclInjectCptd = PclInjectCptdCheck?.IsChecked == true;
		SettingsManager.Save(instance);
		if (instance.PclInjectCptd != flag)
		{
			_ = ApplyCptdToggleAsync();
		}
	}

	private void PortBox_LostFocus(object sender, RoutedEventArgs e)
	{
		if (!(sender is TextBox textBox))
		{
			return;
		}
		string text = textBox.Text.Trim();
		if (text.Length != 0 && int.TryParse(text, out var result))
		{
			int num = Math.Clamp(result, 1, 65535);
			if (num != result)
			{
				textBox.Text = num.ToString();
			}
		}
	}

	private int CurrentDirectConnectPort()
	{
		int pclJoinPort = SettingsManager.Instance.PclJoinPort;
		if (pclJoinPort >= 1 && pclJoinPort <= 65535)
		{
			return pclJoinPort;
		}
		int listenPort = ProxyCaptureService.ListenPort;
		if (listenPort >= 1 && listenPort <= 65535)
		{
			return listenPort;
		}
		int proxyListenPort = SettingsManager.Instance.ProxyListenPort;
		if (proxyListenPort < 1 || proxyListenPort > 65535)
		{
			return 25565;
		}
		return proxyListenPort;
	}

	/// <summary>打开「更多显示」模组下载页（2026-11-20：玩家自主下载/更换模组，注入按钮已移除）。</summary>
	private void PclMoreLink_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			using Process p = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = ModInjectService.MoreShowDownloadPage,
					UseShellExecute = true
				}
			};
			p.Start();
			LogHelper.Log("[PCL] 已打开更多显示模组下载页：" + ModInjectService.MoreShowDownloadPage);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[PCL] 打开模组下载页失败");
			MainWindow.Instance?.ShowToast("打开链接失败：" + ex.Message);
		}
	}

	/// <summary>把内置 MhyCPTD 模组（程序 Mods 目录里最新的 MhyCPTD-*.jar）复制到桌面（2026-11-20 新增）。</summary>
	private void PclCopyCptd_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string mods = ModInjectService.ModsDirectory();
			string src = Directory.GetFiles(mods, "MhyCPTD-*.jar")
				.OrderByDescending(File.GetLastWriteTimeUtc)
				.FirstOrDefault() ?? "";
			if (src.Length == 0 || !File.Exists(src))
			{
				MainWindow.Instance?.ShowToast("未找到内置 CPTD 模组（程序 Mods 目录无 MhyCPTD-*.jar）");
				return;
			}
			string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			string dest = Path.Combine(desktop, Path.GetFileName(src));
			File.Copy(src, dest, overwrite: true);
			LogHelper.Log("[PCL] 内置 CPTD 已复制到桌面：" + dest);
			MainWindow.Instance?.ShowToast("已复制到桌面：" + Path.GetFileName(src) + "，请自行放入客户端 mods 文件夹", ok: true);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[PCL] 复制内置 CPTD 到桌面失败");
			MainWindow.Instance?.ShowToast("复制到桌面失败：" + ex.Message);
		}
	}

	private async Task TryAutoInjectAsync()
	{
		string path = PclPathBox.Text.Trim();
		var (injectResultKind, text) = await Task.Run(() => ModInjectService.InjectAll(path, SettingsManager.Instance.PclInjectCptd));
		LogHelper.Log("[模组注入] " + text);
		switch (injectResultKind)
		{
		case InjectResultKind.Injected:
		case InjectResultKind.AlreadyLatest:
			MainWindow.Instance?.ShowToast(text);
			break;
		case InjectResultKind.Failed:
			MainWindow.Instance?.ShowToast(text);
			break;
		}
	}

	/// <summary>
	/// 2026-11-20 PCL 区版式重建（兼容旧预编译 XBF）：
	/// 移除「启动PCL我的世界」「注入元素之诗模组」「允许局域网设备连接」与「指定端口」行，
	/// 在原按钮行位置插入「打开链接」（更多显示模组下载页）与「复制到桌面」（内置 CPTD 复制）。
	/// 新 XBF 已按新版式编译：此时旧控件找不到、新按钮字段已绑定，本方法自动跳过重建只做兜底。
	/// </summary>
	private void ReconcilePclSection()
	{
		try
		{
			// 1) 允许局域网设备连接：摘除勾选框（设置值 AllowLanListen 仍生效，只是不再提供界面开关）
			if (XamlScope.Find(this, "LanListenCheck") is FrameworkElement lan)
			{
				(lan.Parent as Panel)?.Children.Remove(lan);
			}
			// 2) 指定端口行（文字 + 输入框）整行摘除
			if (XamlScope.Find(this, "PclJoinPortBox") is FrameworkElement portBox && portBox.Parent is Panel portRow)
			{
				if (portRow.Parent is Panel portHost)
				{
					portHost.Children.Remove(portRow);
				}
				else
				{
					portRow.Children.Remove(portBox);
				}
			}
			// 3) 旧按钮行整行移除，原位插入新按钮
			Panel? btnRowHost = null;
			int btnRowIndex = -1;
			if (XamlScope.Find(this, "PclGameLaunchButton") is FrameworkElement oldBtn && oldBtn.Parent is Panel oldRow && oldRow.Parent is Panel oldHost)
			{
				btnRowIndex = oldHost.Children.IndexOf(oldRow);
				oldHost.Children.Remove(oldRow);
				btnRowHost = oldHost;
			}
			if (btnRowHost != null && btnRowIndex >= 0)
			{
				Button moreLink = PclMoreLinkButton ??= CreatePclActionButton("下载更多显示", PclMoreLink_Click);
				Button copyCptd = PclCopyCptdButton ??= CreatePclActionButton("复制到桌面", PclCopyCptd_Click);
				StackPanel newRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.0 };
				newRow.Children.Add(moreLink);
				newRow.Children.Add(copyCptd);
				btnRowHost.Children.Insert(Math.Min(btnRowIndex, btnRowHost.Children.Count), newRow);
			}
			else if (PclMoreLinkButton != null && PclCopyCptdButton != null)
			{
				// 新 XBF：按钮由 XAML 提供，只做事件兜底挂接（幂等）
				PclMoreLinkButton.Click -= PclMoreLink_Click;
				PclMoreLinkButton.Click += PclMoreLink_Click;
				PclCopyCptdButton.Click -= PclCopyCptd_Click;
				PclCopyCptdButton.Click += PclCopyCptd_Click;
			}
			LogHelper.Log("[PCL] 区块版式已重建（移除局域网/指定端口/启动PCL/注入按钮，新增打开链接/复制到桌面）");
		}
		catch (Exception exRecon)
		{
			LogHelper.LogError(exRecon, "[PCL] 区块版式重建失败");
		}
	}

	private static Button CreatePclActionButton(string text, RoutedEventHandler onClick)
	{
		Button b = new Button { Content = text };
		b.Click += onClick;
		return b;
	}

	private void AddPclCptdToggle()
	{
		if (PclInjectCptdCheck != null || !(PclAutoStartCheck?.Parent is Panel row) || !(row.Parent is Panel section))
		{
			return;
		}
		PclInjectCptdCheck = new CheckBox
		{
			Content = "注入 MhyCPTD 模组（关闭时自动禁用）",
			Margin = PclAutoStartCheck.Margin,
			IsChecked = SettingsManager.Instance.PclInjectCptd
		};
		StackPanel cptdRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 16.0,
			Margin = row.Margin
		};
		cptdRow.Children.Add(PclInjectCptdCheck);
		section.Children.Insert(section.Children.IndexOf(row) + 1, cptdRow);
		PclInjectCptdCheck.Checked += PclOption_Changed;
		PclInjectCptdCheck.Unchecked += PclOption_Changed;
	}

	private async Task ApplyCptdToggleAsync()
	{
		string path = PclPathBox.Text.Trim();
		if (!File.Exists(path))
		{
			return;
		}
		bool injectCptd = SettingsManager.Instance.PclInjectCptd;
		try
		{
			var (_, text) = await Task.Run(() => ModInjectService.InjectAll(path, injectCptd));
			LogHelper.Log("[模组注入] CPTD 开关切换为" + (injectCptd ? "启用" : "禁用") + "：" + text);
			MainWindow.Instance?.ShowToast((injectCptd ? "已启用 MhyCPTD 注入" : "已禁用 MhyCPTD（模组已停用）") + "：" + text);
			UpdatePclHint(path);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[模组注入] CPTD 开关切换应用失败");
			MainWindow.Instance?.ShowToast("CPTD 开关应用失败：" + ex.Message);
		}
	}

	private bool WriteDirectConnectAddress()
	{
		string pclExePath = PclPathBox.Text.Trim();
		int port = CurrentDirectConnectPort();
		int.TryParse(ListenPortBox.Text.Trim(), out var result);
		IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> readOnlyList = ProxyCaptureService.ActiveBindingsSnapshot();
		if (readOnlyList.Count > 0 && port == result && readOnlyList.All((ProxyCaptureService.ActiveBindingInfo b) => b.ListenPort != port))
		{
			port = readOnlyList[0].ListenPort;
		}
		port = Math.Clamp(port, 1, 65535);
		AppSettings instance = SettingsManager.Instance;
		instance.PclJoinPort = port;
		SettingsManager.Save(instance);
		string text = "127.0.0.1:" + port;
		string text2 = PclLauncherService.WriteServerAddress(pclExePath, text, out var written);
		LogHelper.Log("[PCL] " + text2);
		MainWindow.Instance?.ShowToast(written ? ("已写入直连服务器 " + text) : ("直连写入失败：" + text2));
		return written;
	}

	private void LaunchPclProcess()
	{
		string text = PclPathBox.Text.Trim();
		string jvmMsg = PclLauncherService.WriteJvmArgs(text, out var jvmWritten);
		LogHelper.Log("[PCL] " + jvmMsg);
		if (!PclLauncherService.Launch(text))
		{
			MainWindow.Instance?.ShowToast("启动 PCL 失败：" + text);
		}
	}

	private static string FindModsFolder(string pclExePath)
	{
		try
		{
			string text = Path.Combine(Path.GetDirectoryName(pclExePath) ?? "", ".minecraft");
			if (!Directory.Exists(text))
			{
				return "";
			}
			string text2 = "";
			string path = Path.Combine(text, "PCL.ini");
			if (File.Exists(path))
			{
				string[] array = File.ReadAllLines(path);
				string[] array2 = array;
				foreach (string text3 in array2)
				{
					if (text3.StartsWith("Version:"))
					{
						text2 = text3.Substring("Version:".Length).Trim();
						break;
					}
				}
			}
			if (string.IsNullOrWhiteSpace(text2))
			{
				text2 = "1.12.2-Forge_UO";
			}
			string text4 = Path.Combine(text, "versions", text2, "mods");
			return Directory.Exists(text4) ? text4 : Path.Combine(text, "versions");
		}
		catch
		{
			return "";
		}
	}

	private void SavePclPath()
	{
		AppSettings instance = SettingsManager.Instance;
		instance.PclPath = PclPathBox.Text.Trim();
		SettingsManager.Save(instance);
	}

	private void PclMods_Click(object sender, RoutedEventArgs e)
	{
		string text = PclPathBox.Text.Trim();
		if (!File.Exists(text))
		{
			MainWindow.Instance?.ShowToast("未找到 PCL 启动器：" + text);
			return;
		}
		SavePclPath();
		string text2 = FindModsFolder(text);
		if (string.IsNullOrEmpty(text2))
		{
			MainWindow.Instance?.ShowToast("未找到 PCL 的 .minecraft 目录");
			return;
		}
		try
		{
			Directory.CreateDirectory(text2);
			Process.Start(new ProcessStartInfo("explorer.exe", text2)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "打开 PCL Mod 文件夹失败");
			MainWindow.Instance?.ShowToast("打开 Mod 文件夹失败：" + ex.Message);
		}
	}

	// 主题笔刷统一走 ThemeHelper（App.xaml 未定义 ThemeDictionaries，本地查表在深色主题下会拿到浅色画笔）
	private static Brush? ThemeBrush(string key)
	{
		return ThemeHelper.Resolve(key);
	}

	private static List<string> FindPclLaunchers()
	{
		List<string> list = new List<string>();
		DriveInfo[] drives = DriveInfo.GetDrives();
		DriveInfo[] array = drives;
		foreach (DriveInfo driveInfo in array)
		{
			if (driveInfo.IsReady && (driveInfo.DriveType == DriveType.Fixed || driveInfo.DriveType == DriveType.Removable))
			{
				try
				{
					ScanFolderForPcl(new DirectoryInfo(driveInfo.RootDirectory.FullName), 0, list);
				}
				catch
				{
				}
			}
		}
		return list.Distinct().ToList();
	}

	private static void ScanFolderForPcl(DirectoryInfo dir, int depth, List<string> result)
	{
		if (depth > 5)
		{
			return;
		}
		try
		{
			foreach (DirectoryInfo item in dir.EnumerateDirectories())
			{
				string name = item.Name;
				if (!name.Equals("Windows", StringComparison.OrdinalIgnoreCase) && !name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) && !name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) && !name.StartsWith(".git", StringComparison.OrdinalIgnoreCase))
				{
					try
					{
						ScanFolderForPcl(item, depth + 1, result);
					}
					catch
					{
					}
				}
			}
			foreach (FileInfo item2 in dir.EnumerateFiles("*.exe"))
			{
				if (item2.Name.StartsWith("PCL", StringComparison.OrdinalIgnoreCase))
				{
					result.Add(item2.FullName);
				}
			}
		}
		catch
		{
		}
	}

	private static List<string> FindMinecraftVersions(string pclPath)
	{
		List<string> list = new List<string>();
		try
		{
			string path = Path.Combine(Path.Combine(Path.GetDirectoryName(pclPath) ?? "", ".minecraft"), "versions");
			if (Directory.Exists(path))
			{
				foreach (DirectoryInfo item in new DirectoryInfo(path).EnumerateDirectories())
				{
					list.Add(item.Name);
				}
				list.Sort(StringComparer.Ordinal);
			}
		}
		catch
		{
		}
		return list;
	}

	private async void PclFind_Click(object sender, RoutedEventArgs e)
	{
		PclFindButton.IsEnabled = false;
		PclFindButton.Content = "查找中…";
		try
		{
			switch (await PickPclLauncherAsync())
			{
			case -1:
				MainWindow.Instance?.ShowToast("未在本机找到 PCL 启动器，请手动点击「浏览…」选择");
				break;
			case 1:
				MainWindow.Instance?.ShowToast("已选择 PCL：" + PclPathBox.Text.Trim());
				break;
			}
		}
		finally
		{
			PclFindButton.IsEnabled = true;
			PclFindButton.Content = "查找此电脑上的PCL";
		}
	}

	private async Task<int> PickPclLauncherAsync()
	{
		List<string> found = await Task.Run(() => FindPclLaunchers());
		if (found.Count == 0)
		{
			return -1;
		}
		List<string> list = new List<string>();
		foreach (string item in found)
		{
			List<string> list2 = FindMinecraftVersions(item);
			string text = item;
			if (list2.Count > 0)
			{
				text = text + "    [游戏版本: " + string.Join("、", list2.Take(8)) + ((list2.Count > 8) ? "…" : "") + "]";
			}
			list.Add(text);
		}
		ListView list3 = new ListView
		{
			ItemsSource = list,
			SelectionMode = ListViewSelectionMode.Single,
			MaxHeight = 360.0,
			MinWidth = 520.0,
			Background = ThemeHelper.ListSurface()
		};
		if (await new ContentDialog
		{
			Title = "选择 PCL 启动器（已在本机找到 " + found.Count + " 个）",
			Content = list3,
			PrimaryButtonText = "使用所选 PCL",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default),
			XamlRoot = base.XamlRoot,
		}.ShowAsync() == ContentDialogResult.Primary && list3.SelectedIndex >= 0 && list3.SelectedIndex < found.Count)
		{
			PclPathBox.Text = found[list3.SelectedIndex];
			SavePclPath();
			UpdatePclUiState();
			UpdatePclHint(PclPathBox.Text.Trim());
			await TryAutoInjectAsync();
			return 1;
		}
		return 0;
	}

	private async void OneClickStart_Click(object sender, RoutedEventArgs e)
	{
		if (!ActivationService.IsActivated)
		{
			AppendTail("状态: 未激活，一键启动不可用\n");
			return;
		}
		if (AccountStore.All.Count == 0)
		{
			if (await new ContentDialog
			{
				Title = "请先登录账号",
				Content = "一键启动前需要先绑定账号（用于角色认证）。\n是否前往「账号绑定」页登录？",
				PrimaryButtonText = "前往登录",
				CloseButtonText = "取消",
				DefaultButton = ContentDialogButton.Primary,
				RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default),
				XamlRoot = base.XamlRoot,
			}.ShowAsync() == ContentDialogResult.Primary)
			{
				MainWindow.Instance?.NavigateTo("LoginPage");
			}
			return;
		}
		if (!int.TryParse(ListenPortBox.Text.Trim(), out var port) || port < 1 || port > 65535)
		{
			AppendTail("状态: 监听端口无效\n");
			return;
		}
		if (!ProxyCaptureService.HasEarliestRecord())
		{
			SettingsManager.Instance.ProxyListenPort = port;
			SettingsManager.Save(SettingsManager.Instance);
		}
		ApplySelectedPreset();
		MenuFlyout menuFlyout = new MenuFlyout();
		foreach (ServerPreset item in ServerPresets.All)
		{
			MenuFlyoutItem menuFlyoutItem2 = new MenuFlyoutItem
			{
				Text = (item.IsPending ? (item.Name + "（暂不可用）") : (item.Name + "（" + item.Host + ":" + item.Port + "）")),
				IsEnabled = !item.IsPending
			};
			ServerPreset captured = item;
			menuFlyoutItem2.Click += async delegate
			{
				await OneClickStartWithPresetAsync(captured, port);
			};
			menuFlyout.Items.Add(menuFlyoutItem2);
		}
		menuFlyout.ShowAt(OneClickStartButton);
	}

	private async Task OneClickStartWithPresetAsync(ServerPreset preset, int port)
	{
		RadioButton radioButton = _presetRadios.FirstOrDefault((RadioButton r) => string.Equals(r.Tag as string, preset.Name, StringComparison.Ordinal));
		if (radioButton != null && radioButton.IsChecked != true)
		{
			radioButton.IsChecked = true;
		}
		ApplySelectedPreset();
		if (preset.IsPending)
		{
			AppendTail("状态: 该服务器暂不可用\n");
			return;
		}
		bool started = false;
		ProxyStartDialog dialog = new ProxyStartDialog(port, preset.Host, preset.Port, preset.Name, stopOnStart: false, enableAutoPcl: true)
		{
			XamlRoot = base.XamlRoot
		};
		dialog.ProxyStarted += delegate
		{
			started = true;
		};
		dialog.ProxyStarted += OnProxyStarted;
		await DialogGate.ShowAsync(dialog);
		UpdateStatus();
		if (started && dialog.AutoLaunchPcl)
		{
			await HandleAutoPclAsync();
		}
	}

	private async Task HandleAutoPclAsync()
	{
		string text = PclPathBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(text) || !File.Exists(text))
		{
			switch (await PickPclLauncherAsync())
			{
			case -1:
				MainWindow.Instance?.ShowToast("未找到 PCL 启动器，已跳过自动启动（可稍后点「启动PCL我的世界」）");
				await ShowOneClickDoneDialog("没找到 PCL，先手动开游戏吧～");
				return;
			case 0:
				await ShowOneClickDoneDialog("你没选 PCL，先去游戏里手动填 127.0.0.1:端口 吧～");
				return;
			}
		}
		if (!WriteDirectConnectAddress())
		{
			await ShowOneClickDoneDialog("直连地址写入失败，请检查 PCL 的 .minecraft 目录～");
			return;
		}
		await TryAutoInjectAsync();
		LaunchPclProcess();
		await BringPclToFrontAsync();
		await ShowOneClickDoneDialog("PCL 已就位并自动置顶，进游戏前记得填 127.0.0.1:端口，玩得开心！");
	}

	private async Task BringPclToFrontAsync()
	{
		await Task.Run(delegate
		{
			try
			{
				for (int i = 0; i < 25; i++)
				{
					Thread.Sleep(300);
					try
					{
						Process[] processes = Process.GetProcesses();
						Process[] array = processes;
						foreach (Process process in array)
						{
							if (process.ProcessName.StartsWith("PCL", StringComparison.OrdinalIgnoreCase) && process.MainWindowHandle != IntPtr.Zero)
							{
								SetWindowPos(process.MainWindowHandle, HWND_TOPMOST, 0, 0, 0, 0, 67u);
								SetForegroundWindow(process.MainWindowHandle);
								return;
							}
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
		});
	}

	private async Task ShowOneClickDoneDialog(string message)
	{
		await new ContentDialog
		{
			Title = "一键启动完成",
			Content = message + "\n\n" + OneClickFlavor[Random.Shared.Next(OneClickFlavor.Length)],
			CloseButtonText = "好的",
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default),
			XamlRoot = base.XamlRoot,
		}.ShowAsync();
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(nint hWnd);

	private async void PclBrowse_Click(object sender, RoutedEventArgs e)
	{
		FileOpenPicker fileOpenPicker = new FileOpenPicker
		{
			FileTypeFilter = { ".exe" }
		};
		InitializeWithWindow.Initialize(fileOpenPicker, WindowNative.GetWindowHandle(MainWindow.Instance));
		StorageFile storageFile = await fileOpenPicker.PickSingleFileAsync();
		if (storageFile != null)
		{
			PclPathBox.Text = storageFile.Path;
			SavePclPath();
			UpdatePclHint(storageFile.Path);
			await TryAutoInjectAsync();
		}
	}

	
	
	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	[DebuggerNonUserCode]
	public IComponentConnector GetBindingConnector(int connectionId, object target)
	{
		return null;
	}
}
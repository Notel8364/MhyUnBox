using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Services;
using MhyunBox.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using WinRT;
using WinRT.Interop;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MhyunBox.Pages;

public sealed partial class ToolsPage : Page
{
	private const string HiBitLocalPath = "F:\\Notel文件\\Fixtools\\HiBitUninstaller_87005\\HiBitUninstaller-Portable.exe";

	private const int MaxLogChars = 24000;

	// 卡片显示顺序（值为 ToolCardGrid.Children 的索引）：
	// 2=恶意木马与威胁清理（扫描/应急，通栏置顶）、7=在线查杀与扫描工具折叠分区（通栏，紧随其后）、
	// 1=电脑布防、6=自启动工具、0=安全防护工具下载、3=卸载工具、4=其他常用工具下载、5=硬件驱动下载。
	private static readonly int[] DisplayOrder = new int[8] { 2, 7, 1, 6, 0, 3, 4, 5 };

	private bool _killBusy;

	private Button _antivirusScanButton;

	private Button _rescueKitButton;

	private int _antivirusThreatCount;

	private bool _defenseBusy;

	private int _guardAlertCursor;

	private bool _miningIsolationBusy;

	// 四张「在线工具」动态卡片（在线查杀 / 在线沙箱 / 开源工具 / 超级工具箱），
	// 由 BuildOnlineToolsSection 收进一个可折叠分区。
	private Border? _onlineScannerCard;

	private Border? _onlineSandboxCard;

	private Border? _openSourceCard;

	private Border? _superToolkitCard;

	private ProgressBar KillScanProgress;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel ToolRoot;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Grid ToolCardGrid;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock AutoStartFolderText;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock NapCatFilesText;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ToggleButton AutoStartHiddenToggle;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button SilverFoxKillButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button SilverFoxScanButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ScrollViewer KillLogScroll;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock KillStatusText;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBox KillLogBox;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock MiningIsolationStatusText;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock DefenseStatusText;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ScrollViewer DefenseLogScroll;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBox DefenseLogBox;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button MiningIsolationOnButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private ToggleButton GuardAutoScanToggle;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button StartMonitorButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button StopMonitorButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private bool _contentLoaded;

	public ToolsPage()
	{
		InitializeComponent();
		AddOnlineScannerCard();
		AddOnlineSandboxCard();
		AddOpenSourceToolCard();
		AddSuperToolkitCard();
		BuildOnlineToolsSection();
		base.Unloaded += delegate
		{
			DefenseService.Instance.FileModified -= OnHoneyPotTouched;
			DefenseService.Instance.TamperDetected -= OnTamperDetected;
		};
		base.Loaded += delegate
		{
			DefenseService.Instance.FileModified += OnHoneyPotTouched;
			DefenseService.Instance.TamperDetected += OnTamperDetected;
			// 依赖父级的初始化必须推迟到 Loaded：构造阶段 XAML 字段虽已连接，但元素尚未挂上父级
			// （SilverFoxScanButton.Parent == null），在构造函数里按 Parent 插入按钮/进度条会全部静默失败。
			EnsureParentDependentInit();
			RelayoutTools(base.ActualWidth);
			base.DispatcherQueue.TryEnqueue(delegate
			{
				RefreshDefenseUi();
				LoadGuardAlerts();
			});
		};
	}

	private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		RelayoutTools(e.NewSize.Width);
	}

	private void AddAntivirusButtons()
	{
		try
		{
			if (SilverFoxScanButton?.Parent is StackPanel panel)
			{
				Button button = new Button
				{
					Content = "一键杀毒扫描（主力·发现威胁自动联动清理）",
					HorizontalAlignment = HorizontalAlignment.Stretch,
					MinHeight = 40
				};
				button.Click += AntivirusScan_Click;
				_antivirusScanButton = button;
				Button button2 = new Button
				{
					Content = "360 系统急救箱（SuperKiller）",
					HorizontalAlignment = HorizontalAlignment.Stretch,
					MinHeight = 40
				};
				button2.Click += RescueKit_Click;
				_rescueKitButton = button2;
				int index = panel.Children.IndexOf(SilverFoxScanButton) + 1;
				panel.Children.Insert(index, button);
				panel.Children.Insert(index + 1, button2);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "初始化杀毒扫描/急救箱按钮失败");
		}
	}

	private Button? _dragonTrimButton;

	private Button? _dragonMergeButton;

	private void AddDragonCoreButtons()
	{
		try
		{
			if (SilverFoxScanButton?.Parent is StackPanel panel)
			{
				Button button = new Button
				{
					Content = "DragonCore 裁剪增量包（元素之诗服务器官方资源包：对比两个大体积文件夹 基准包 vs 最新包 → 产出小体积增量 zip）",
					HorizontalAlignment = HorizontalAlignment.Stretch,
					MinHeight = 40
				};
				button.Click += DragonCoreTrim_Click;
				_dragonTrimButton = button;
				Button button2 = new Button
				{
					Content = "DragonCore 合并增量包（选小体积增量 zip → 解压覆盖进本地的元素之诗服务器官方资源包）",
					HorizontalAlignment = HorizontalAlignment.Stretch,
					MinHeight = 40
				};
				button2.Click += DragonCoreMerge_Click;
				_dragonMergeButton = button2;
				int index = panel.Children.IndexOf(SilverFoxScanButton) + 3;
				panel.Children.Insert(index, button);
				panel.Children.Insert(index + 1, button2);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "初始化 DragonCore 裁剪按钮失败");
		}
	}

	private async void DragonCoreTrim_Click(object sender, RoutedEventArgs e)
	{
		if (DragonCoreTrimDialogs.Busy || _dragonTrimButton == null || _dragonTrimButton.IsEnabled == false)
		{
			return;
		}
		SetDragonCoreBusy(true);
		try
		{
			await DragonCoreTrimDialogs.ShowTrimFlowAsync(XamlRoot);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "DragonCore 裁剪流程异常");
			MainWindow.Instance?.ShowToast("裁剪流程异常：" + ex.Message, false);
		}
		finally
		{
			SetDragonCoreBusy(false);
		}
	}

	private async void DragonCoreMerge_Click(object sender, RoutedEventArgs e)
	{
		if (DragonCoreTrimDialogs.Busy || _dragonMergeButton == null || _dragonMergeButton.IsEnabled == false)
		{
			return;
		}
		SetDragonCoreBusy(true);
		try
		{
			await DragonCoreTrimDialogs.ShowMergeFlowAsync(XamlRoot);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "DragonCore 合并流程异常");
			MainWindow.Instance?.ShowToast("合并流程异常：" + ex.Message, false);
		}
		finally
		{
			SetDragonCoreBusy(false);
		}
	}

	private void SetDragonCoreBusy(bool busy)
	{
		if (_dragonTrimButton != null)
		{
			_dragonTrimButton.IsEnabled = !busy;
		}
		if (_dragonMergeButton != null)
		{
			_dragonMergeButton.IsEnabled = !busy;
		}
	}

	// TRAE 推荐在线查杀与排查工具卡片（插入到「安全防护工具下载」之后）
	private void AddOnlineScannerCard()
	{
		try
		{
		Border card = new Border();
		card.BorderThickness = new Thickness(1.0);
		card.CornerRadius = new CornerRadius(12.0);
		card.Padding = new Thickness(20.0);
		// 主题切换时（即使页面实例被复用未重建）也自行刷新配色，避免深色下仍是白卡。
		// 注意：不能对 Border 设置 TextElement.Foreground（那是 TextElement 拥有的附加属性，
		// 在 Border 上走原生 ABI SetValue 会触发 AccessViolation 导致进程直接崩溃）。
		// 文本前景统一交给 ApplyCardTheme 对卡片内的 TextBlock 设置。
		card.ActualThemeChanged += (s, e) =>
		{
			if (s is Border b)
			{
				ApplyCardTheme(b);
			}
		};
			card.Margin = new Thickness(0.0, 12.0, 0.0, 0.0);
			StackPanel body = new StackPanel
			{
				Spacing = 10
			};
			body.Children.Add(new TextBlock
			{
				Text = "在线查杀与排查工具（TRAE 推荐）",
				FontSize = 13.0,
				FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
				Opacity = 0.8
			});
			body.Children.Add(new TextBlock
			{
				Text = "以下均为安全厂商官方免费查杀/排查工具：绿色版引擎独立、无需安装，适合杀毒软件被木马禁用后交叉查杀；Autoruns / Process Explorer 用于排查银狐类木马的可疑启动项与隐藏进程。点击按钮跳转官网下载。",
				FontSize = 12.0,
				Opacity = 0.7,
				TextWrapping = TextWrapping.Wrap
			});
			Grid grid = new Grid
			{
				ColumnSpacing = 10.0
			};
			grid.ColumnDefinitions.Add(new ColumnDefinition());
			grid.ColumnDefinitions.Add(new ColumnDefinition());
			StackPanel left = new StackPanel
			{
				Spacing = 6.0
			};
			Grid.SetColumn(left, 0);
			StackPanel right = new StackPanel
			{
				Spacing = 6.0
			};
			Grid.SetColumn(right, 1);
			AddOnlineScanButton(left, "微软 Safety Scanner（官方绿色查杀）", "https://learn.microsoft.com/defender-endpoint/safety-scanner-download");
			AddOnlineScanButton(left, "卡巴斯基 KVRT 病毒清除工具", "https://www.kaspersky.com.cn/downloads/free-virus-removal-tool");
			AddOnlineScanButton(left, "Dr.Web CureIt! 绿色专杀", "https://free.drweb.com/cureit/");
			AddOnlineScanButton(left, "ESET Online Scanner 在线扫描", "https://www.eset.com/int/home/online-scanner/");
			AddOnlineScanButton(right, "Malwarebytes AdwCleaner（广告劫持清理）", "https://www.malwarebytes.com/adwcleaner");
			AddOnlineScanButton(right, "Trellix Stinger 轻量专杀（原 McAfee）", "https://www.trellix.com/products/stinger/");
			AddOnlineScanButton(right, "Autoruns 启动项排查（微软官方）", "https://learn.microsoft.com/sysinternals/downloads/autoruns");
			AddOnlineScanButton(right, "Process Explorer 进程排查（微软官方）", "https://learn.microsoft.com/sysinternals/downloads/process-explorer");
			grid.Children.Add(left);
			grid.Children.Add(right);
			body.Children.Add(grid);
		card.Child = body;
		ApplyCardTheme(card);
		ToolCardGrid.Children.Add(card);
		_onlineScannerCard = card;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "初始化在线查杀工具卡片失败");
		}
	}

	private void AddOnlineScanButton(StackPanel panel, string label, string url)
	{
		Button button = new Button
		{
			Content = label,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			MinHeight = 40.0
		};
		button.Click += delegate
		{
			OpenUrl(url);
		};
		panel.Children.Add(button);
	}

	// 在线扫描与行为分析沙箱卡片（插入到「在线查杀与排查工具」之后）
	private void AddOnlineSandboxCard()
	{
		try
		{
		Border card = new Border();
		card.BorderThickness = new Thickness(1.0);
		card.CornerRadius = new CornerRadius(12.0);
		card.Padding = new Thickness(20.0);
		// 主题切换时（即使页面实例被复用未重建）也自行刷新配色，避免深色下仍是白卡。
		// 文本前景统一交给 ApplyCardTheme 对卡片内的 TextBlock 设置（不应对 Border 设 TextElement.Foreground）。
		card.ActualThemeChanged += (s, e) =>
		{
			if (s is Border b)
			{
				ApplyCardTheme(b);
			}
		};
			card.Margin = new Thickness(0.0, 12.0, 0.0, 0.0);
			StackPanel body = new StackPanel
			{
				Spacing = 10
			};
			body.Children.Add(new TextBlock
			{
				Text = "在线扫描与行为分析沙箱",
				FontSize = 13.0,
				FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
				Opacity = 0.8
			});
			body.Children.Add(new TextBlock
			{
				Text = "拿不准的文件先上传沙箱/多引擎扫描再运行：沙箱会记录程序的完整行为链（用了哪些命令、改了哪些注册表、连了哪些地址），是识别未知新木马的关键手段。单文件建议 ≤100MB，切勿上传含隐私的文档。",
				FontSize = 12.0,
				Opacity = 0.7,
				TextWrapping = TextWrapping.Wrap
			});
			Grid grid = new Grid
			{
				ColumnSpacing = 10.0
			};
			grid.ColumnDefinitions.Add(new ColumnDefinition());
			grid.ColumnDefinitions.Add(new ColumnDefinition());
			StackPanel left = new StackPanel
			{
				Spacing = 6.0
			};
			Grid.SetColumn(left, 0);
			StackPanel right = new StackPanel
			{
				Spacing = 6.0
			};
			Grid.SetColumn(right, 1);
			AddOnlineScanButton(left, "VirusTotal 多引擎在线扫描", "https://www.virustotal.com/");
			AddOnlineScanButton(left, "MetaDefender Cloud 在线扫描", "https://metadefender.opswat.com/");
			AddOnlineScanButton(left, "ANY.RUN 交互式沙箱（看行为）", "https://any.run/");
			AddOnlineScanButton(right, "Hybrid Analysis 沙箱", "https://www.hybrid-analysis.com/");
			AddOnlineScanButton(right, "微软威胁情报中心", "https://www.microsoft.com/en-us/wdsi/threats");
			AddOnlineScanButton(right, "微步在线威胁情报社区", "https://x.threatbook.com/");
			grid.Children.Add(left);
			grid.Children.Add(right);
			body.Children.Add(grid);
		card.Child = body;
		ApplyCardTheme(card);
		ToolCardGrid.Children.Add(card);
		_onlineSandboxCard = card;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "初始化在线沙箱卡片失败");
		}
	}

	// 开源 / GitHub 安全工具卡片（免费、可携带、杀软被禁用时的最后手段）
	private void AddOpenSourceToolCard()
	{
		try
		{
		Border card = new Border();
		card.BorderThickness = new Thickness(1.0);
		card.CornerRadius = new CornerRadius(12.0);
		card.Padding = new Thickness(20.0);
		// 主题切换时（即使页面实例被复用未重建）也自行刷新配色，避免深色下仍是白卡。
		// 文本前景统一交给 ApplyCardTheme 对卡片内的 TextBlock 设置（不应对 Border 设 TextElement.Foreground）。
		card.ActualThemeChanged += (s, e) =>
		{
			if (s is Border b)
			{
				ApplyCardTheme(b);
			}
		};
			card.Margin = new Thickness(0.0, 12.0, 0.0, 0.0);
			StackPanel body = new StackPanel
			{
				Spacing = 10
			};
			body.Children.Add(new TextBlock
			{
				Text = "开源 / GitHub 安全工具（免费 · 可携带）",
				FontSize = 13.0,
				FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
				Opacity = 0.8
			});
			body.Children.Add(new TextBlock
			{
				Text = "开源社区出品的查杀与排查工具，引擎独立、免安装、无需注册。木马把杀毒软件关掉之后，这些绿色工具就是最后的排查防线；PE-sieve 专抓「披着正常进程外衣」的注入木马，IOC 扫描器靠特征规则找已知家族，Tron 是一键自动化清理修复流水线。",
				FontSize = 12.0,
				Opacity = 0.7,
				TextWrapping = TextWrapping.Wrap
			});
			Grid grid = new Grid
			{
				ColumnSpacing = 10.0
			};
			grid.ColumnDefinitions.Add(new ColumnDefinition());
			grid.ColumnDefinitions.Add(new ColumnDefinition());
			StackPanel left = new StackPanel
			{
				Spacing = 6.0
			};
			Grid.SetColumn(left, 0);
			StackPanel right = new StackPanel
			{
				Spacing = 6.0
			};
			Grid.SetColumn(right, 1);
			AddOnlineScanButton(left, "System Informer（进程/驱动排查）", "https://systeminformer.com/");
			AddOnlineScanButton(left, "PE-sieve（进程注入/伪装检测）", "https://github.com/hasherezade/pe-sieve/releases");
			AddOnlineScanButton(left, "PE-bear（PE 文件剖析）", "https://github.com/hasherezade/pe-bear/releases");
			AddOnlineScanButton(left, "Loki IOC 扫描器（GitHub）", "https://github.com/Neo23x0/Loki");
			AddOnlineScanButton(left, "THOR Lite 免费扫描器", "https://www.nextron-systems.com/thor-lite/");
			AddOnlineScanButton(left, "ClamAV 开源杀毒引擎", "https://www.clamav.net/");
			AddOnlineScanButton(right, "YARA 特征规则库（GitHub）", "https://github.com/Yara-Rules/rules");
			AddOnlineScanButton(right, "Tron Script 一键清理修复", "https://github.com/bmrf/tron");
			AddOnlineScanButton(right, "Simplewall 开源防火墙", "https://github.com/henrypp/simplewall");
			AddOnlineScanButton(right, "Portmaster 出站管控", "https://safing.io/portmaster/");
			AddOnlineScanButton(right, "WindowsSpyBlocker 反外联", "https://github.com/crazy-max/WindowsSpyBlocker");
			AddOnlineScanButton(right, "GMER 内核 Rootkit 扫描", "http://www.gmer.net/");
			grid.Children.Add(left);
			grid.Children.Add(right);
			body.Children.Add(grid);
		card.Child = body;
		ApplyCardTheme(card);
		ToolCardGrid.Children.Add(card);
		_openSourceCard = card;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "初始化开源安全工具卡片失败");
		}
	}

	// 下一代威胁检测与命令守卫按钮（插入到「恶意木马与威胁清理」卡片）
	private Button? _evoScanButton;

	private Button? _guardOnButton;

	private Button? _guardOffButton;

	private Button? _emergencyStopButton;

	private Button? _emergencyStandardButton;

	private Button? _emergencyDeepButton;

	private Button? _emergencyRestoreButton;

	private void AddEvolutionGuardButtons()
	{
		try
		{
			if (SilverFoxScanButton?.Parent is StackPanel panel)
			{
				Button button = new Button
				{
					Content = "下一代威胁扫描（LOLBins/进程伪装/WMI 订阅/BYOVD 驱动）",
					HorizontalAlignment = HorizontalAlignment.Stretch,
					MinHeight = 40
				};
				button.Click += EvoScan_Click;
				_evoScanButton = button;
				Button button2 = new Button
				{
					Content = "异常命令监控 - 开启（检测到危险指令即置顶弹窗并击杀）",
					HorizontalAlignment = HorizontalAlignment.Stretch,
					MinHeight = 40
				};
				button2.Click += CommandGuardOn_Click;
				_guardOnButton = button2;
				Button button3 = new Button
				{
					Content = "异常命令监控 - 关闭",
					HorizontalAlignment = HorizontalAlignment.Stretch,
					MinHeight = 40
				};
				button3.Click += CommandGuardOff_Click;
				_guardOffButton = button3;
				int index = panel.Children.IndexOf(SilverFoxScanButton) + 5;
				panel.Children.Insert(index, button);
				panel.Children.Insert(index + 1, button2);
				panel.Children.Insert(index + 2, button3);

				// 应急响应（病毒泛滥/被入侵/未装防护软件时的一键止血）
				Button stopBtn = new Button
				{
					Content = "🚨 一键应急止血（断网 + 强杀可疑进程 + 冻结自启动）",
					HorizontalAlignment = HorizontalAlignment.Stretch,
					MinHeight = 40
				};
				stopBtn.Click += EmergencyStop_Click;
				_emergencyStopButton = stopBtn;
				Button stdBtn = new Button
				{
					Content = "应急响应 - 标准（止血 + 全项威胁扫描）",
					HorizontalAlignment = HorizontalAlignment.Stretch,
					MinHeight = 40
				};
				stdBtn.Click += EmergencyStandard_Click;
				_emergencyStandardButton = stdBtn;
				Button deepBtn = new Button
				{
					Content = "应急响应 - 深度（+ 投放目录排查 + 取证打包）",
					HorizontalAlignment = HorizontalAlignment.Stretch,
					MinHeight = 40
				};
				deepBtn.Click += EmergencyDeep_Click;
				_emergencyDeepButton = deepBtn;
				Button restoreBtn = new Button
				{
					Content = "应急恢复（恢复网络与启动项，停止守护）",
					HorizontalAlignment = HorizontalAlignment.Stretch,
					MinHeight = 40
				};
				restoreBtn.Click += EmergencyRestore_Click;
				_emergencyRestoreButton = restoreBtn;
				panel.Children.Insert(index + 3, stopBtn);
				panel.Children.Insert(index + 4, stdBtn);
				panel.Children.Insert(index + 5, deepBtn);
				panel.Children.Insert(index + 6, restoreBtn);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "初始化下一代威胁扫描/命令守卫按钮失败");
		}
	}

	// 依赖父级的初始化统一推迟到页面 Loaded 后执行。
	// 背景：本工程 XAML 走预编译 .xbf，InitializeComponent 只连接字段；构造阶段元素虽已实例化
	// 但尚未挂上父级（SilverFoxScanButton.Parent == null），原作者按 Parent 插入按钮的代码
	// （一键杀毒/急救箱/DragonCore/下一代扫描/命令守卫/应急响应/扫描进度条）在构造函数里会全部
	// 静默失败——按钮与进度条从来不会出现。改为 Loaded 后执行一次（页面实例复用/主题重建均有守卫）。
	private bool _parentDependentInitDone;

	private void EnsureParentDependentInit()
	{
		if (_parentDependentInitDone)
		{
			return;
		}
		_parentDependentInitDone = true;
		AddAntivirusButtons();
		AddDragonCoreButtons();
		EnsureScanProgressBar();
		AddEvolutionGuardButtons();
		BuildScanToolSections();
	}

	// 在线查杀/在线沙箱/开源工具/超级工具箱四张动态卡片 → 收进一个可折叠分区（默认收起），
	// 作为一张通栏卡片插到 ToolCardGrid 末尾（DisplayOrder 第 2 位，宽屏通栏、窄屏第二张）。
	private void BuildOnlineToolsSection()
	{
		try
		{
			List<Border> cards = new List<Border>();
			if (_onlineScannerCard != null)
			{
				cards.Add(_onlineScannerCard);
			}
			if (_onlineSandboxCard != null)
			{
				cards.Add(_onlineSandboxCard);
			}
			if (_openSourceCard != null)
			{
				cards.Add(_openSourceCard);
			}
			if (_superToolkitCard != null)
			{
				cards.Add(_superToolkitCard);
			}
			if (cards.Count == 0)
			{
				return;
			}
			int totalTools = 0;
			foreach (Border card in cards)
			{
				if (card.Child is Panel body)
				{
					totalTools += CountButtonsInPanel(body);
				}
				ToolCardGrid.Children.Remove(card);
			}
			Button header = new Button
			{
				Content = "▶  在线查杀与扫描工具（" + totalTools + " 项，点击展开）",
				HorizontalAlignment = HorizontalAlignment.Stretch,
				HorizontalContentAlignment = HorizontalAlignment.Left,
				MinHeight = 46.0,
				Padding = new Thickness(16.0, 0.0, 16.0, 0.0),
				FontSize = 14.0,
				FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
			};
			StackPanel content = new StackPanel
			{
				Visibility = Visibility.Collapsed
			};
			foreach (Border card in cards)
			{
				card.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
				content.Children.Add(card);
			}
			header.Click += delegate
			{
				bool expanded = content.Visibility == Visibility.Collapsed;
				content.Visibility = (expanded ? Visibility.Visible : Visibility.Collapsed);
				header.Content = (expanded ? "▼  " : "▶  ") + "在线查杀与扫描工具（" + totalTools + " 项，" + (expanded ? "点击收起" : "点击展开") + "）";
			};
			StackPanel container = new StackPanel
			{
				Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
			};
			container.Children.Add(header);
			container.Children.Add(content);
			ToolCardGrid.Children.Add(container);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "初始化在线工具折叠分区失败");
		}
	}

	private static int CountButtonsInPanel(Panel panel)
	{
		int num = 0;
		foreach (var child in panel.Children)
		{
			if (child is Button)
			{
				num++;
			}
			else if (child is Panel panel2)
			{
				num += CountButtonsInPanel(panel2);
			}
		}
		return num;
	}

	// 扫描与应急工具折叠分区：
	// 「恶意木马与威胁清理」卡片里代码后置动态插入的按钮（一键杀毒/急救箱/DragonCore/下一代扫描/
	// 命令守卫/应急响应共 11 项）原先因构造阶段元素未挂父级（Parent == null）而全部静默失败、
	// 从未显示；现改为 Loaded 后插入，并按功能分组、各组带小标题，收进一个可折叠分区（默认收起）。
	// XAML 原有的 4 个主按钮（火绒专杀/威胁清理/仅扫描/HEU）保持可见，主要入口不折叠；
	// 扫描进度条与实时日志保留在折叠区外面，折叠状态下也能看到扫描进度。
	private bool _scanSectionExpanded;

	private void BuildScanToolSections()
	{
		try
		{
			if (SilverFoxScanButton?.Parent is not StackPanel panel)
			{
				return;
			}
			// 只收纳代码后置动态插入的按钮；XAML 原有的 4 个主按钮保持可见
			Button? heuButton = FindButtonByContent(panel, "HEU KMS");
			List<(string Title, List<Button> Buttons)> groups = new List<(string, List<Button>)>();
			groups.Add(MakeToolGroup("威胁扫描与清理", _antivirusScanButton, _rescueKitButton, _evoScanButton));
			groups.Add(MakeToolGroup("应急响应（被入侵 / 病毒泛滥时一键止血）", _emergencyStopButton, _emergencyStandardButton, _emergencyDeepButton, _emergencyRestoreButton));
			groups.Add(MakeToolGroup("异常命令监控", _guardOnButton, _guardOffButton));
			groups.Add(MakeToolGroup("其他工具", _dragonTrimButton, _dragonMergeButton));
			int total = 0;
			foreach (var group in groups)
			{
				foreach (Button button in group.Buttons)
				{
					panel.Children.Remove(button);
					total++;
				}
			}
			if (total == 0)
			{
				return;
			}
			Button header = new Button
			{
				Content = "▶  扫描与应急工具（" + total + " 项，点击展开）",
				HorizontalAlignment = HorizontalAlignment.Stretch,
				HorizontalContentAlignment = HorizontalAlignment.Left,
				MinHeight = 44.0,
				Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
				FontSize = 13.0,
				FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
			};
			StackPanel content = new StackPanel
			{
				Spacing = 8.0,
				Margin = new Thickness(0.0, 2.0, 0.0, 0.0),
				Visibility = Visibility.Collapsed
			};
			foreach (var group in groups)
			{
				if (group.Buttons.Count == 0)
				{
					continue;
				}
				content.Children.Add(new TextBlock
				{
					Text = group.Title,
					FontSize = 12.0,
					FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
					Opacity = 0.85,
					Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
				});
				foreach (Button button in group.Buttons)
				{
					content.Children.Add(button);
				}
			}
			header.Click += delegate
			{
				_scanSectionExpanded = !_scanSectionExpanded;
				AnimateScanSection(content, _scanSectionExpanded);
				header.Content = (_scanSectionExpanded ? "▼  " : "▶  ") + "扫描与应急工具（" + total + " 项，" + (_scanSectionExpanded ? "点击收起" : "点击展开") + "）";
			};
			// 插到 4 个主按钮之后、进度条/日志之前
			int insertAt = panel.Children.Count;
			if (heuButton != null)
			{
				int idx = panel.Children.IndexOf(heuButton);
				if (idx >= 0)
				{
					insertAt = idx + 1;
				}
			}
			panel.Children.Insert(insertAt, header);
			panel.Children.Insert(insertAt + 1, content);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "初始化扫描工具折叠分区失败");
		}
	}

	// 收缩栏展开/收起过渡动画：淡入淡出 + 轻微上下滑动（200ms，EaseOut）
	private void AnimateScanSection(StackPanel content, bool expand)
	{
		try
		{
			content.Visibility = Visibility.Visible;
			Microsoft.UI.Xaml.Media.TranslateTransform tt = content.RenderTransform as Microsoft.UI.Xaml.Media.TranslateTransform;
			if (tt == null)
			{
				tt = new Microsoft.UI.Xaml.Media.TranslateTransform();
				content.RenderTransform = tt;
			}
			Microsoft.UI.Xaml.Media.Animation.Storyboard sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
			Microsoft.UI.Xaml.Media.Animation.DoubleAnimation fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
			{
				From = (expand ? 0.0 : 1.0),
				To = (expand ? 1.0 : 0.0),
				Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(200.0)),
				EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CircleEase
				{
					EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
				}
			};
			Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, content);
			Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
			Microsoft.UI.Xaml.Media.Animation.DoubleAnimation slide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
			{
				From = (expand ? -12.0 : 0.0),
				To = (expand ? 0.0 : -8.0),
				Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(200.0)),
				EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CircleEase
				{
					EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
				}
			};
			Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, content);
			Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");
			sb.Children.Add(fade);
			sb.Children.Add(slide);
			if (!expand)
			{
				sb.Completed += delegate
				{
					content.Visibility = Visibility.Collapsed;
				};
			}
			sb.Begin();
		}
		catch
		{
			// 动画异常时退回直接显隐，不影响功能
			content.Visibility = (expand ? Visibility.Visible : Visibility.Collapsed);
		}
	}

	private static (string Title, List<Button> Buttons) MakeToolGroup(string title, params Button?[] buttons)
	{
		List<Button> list = new List<Button>();
		foreach (Button? button in buttons)
		{
			if (button != null)
			{
				list.Add(button);
			}
		}
		return (title, list);
	}

	private static Button? FindButtonByContent(StackPanel panel, string keyword)
	{
		foreach (UIElement child in panel.Children)
		{
			if (child is Button button && button.Content is string text && text.Contains(keyword))
			{
				return button;
			}
		}
		return null;
	}

	private async void EvoScan_Click(object sender, RoutedEventArgs e)
	{
		if (_killBusy)
		{
			return;
		}
		if (await new ContentDialog
		{
			Title = "下一代威胁扫描 (MhyunBox)",
			Content = "将执行下一代威胁扫描（仅检测，不修改系统）：\n1. LOLBins 滥用（powershell/mshta/rundll32/certutil 等被木马拿来用的合法程序）\n2. 进程伪装与异常亲缘（svchost 不在 System32、Office 拉起 powershell 等）\n3. WMI 持久化订阅、计划任务隐藏载荷、可疑启动项\n4. BYOVD 恶意驱动（带漏洞的第三方驱动被用来打穿内核防护）\n5. 系统 DLL 签名/注入、启动配置（bcdedit）与 hosts/DNS 劫持\n\n需要管理员权限（将弹出 UAC 授权）。是否继续？",
			PrimaryButtonText = "开始扫描",
			CloseButtonText = "取消",
			XamlRoot = base.XamlRoot
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			await RunEvoScanFlowAsync();
		}
	}

	private async Task RunEvoScanFlowAsync()
	{
		if (_killBusy)
		{
			return;
		}
		if (!File.Exists(SilverFoxTool.EvoScanScriptPath))
		{
			MainWindow.Instance?.ShowToast("未找到下一代威胁扫描脚本：" + SilverFoxTool.EvoScanScriptPath);
			return;
		}
		_killBusy = true;
		SetKillBusy(busy: true);
		KillLogBox.Text = string.Empty;
		_antivirusThreatCount = 0;
		AppendLog("== 开始下一代威胁扫描（仅检测，不修改系统）==");
		Action<string> onLine = delegate(string s)
		{
			base.DispatcherQueue.TryEnqueue(delegate
			{
				if (s.Contains("[威胁] "))
				{
					_antivirusThreatCount++;
				}
				AppendLog(s);
			});
		};
		bool flag;
		try
		{
			flag = await SilverFoxTool.RunEvoScanAsync(onLine);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "下一代威胁扫描执行失败");
			AppendLog("【错误】" + ex.Message);
			flag = false;
		}
		finally
		{
			_killBusy = false;
			SetKillBusy(busy: false);
		}
		if (_antivirusThreatCount > 0)
		{
			MainWindow.Instance?.ShowToast($"下一代威胁扫描结束：发现 {_antivirusThreatCount} 项可疑，详见下方日志。");
		}
		else if (flag)
		{
			MainWindow.Instance?.ShowToast("下一代威胁扫描完成：未发现威胁。");
		}
	}

	private async void CommandGuardOn_Click(object sender, RoutedEventArgs e)
	{
		if (await new ContentDialog
		{
			Title = "开启异常命令监控 (MhyunBox)",
			Content = "将注册开机自启的「命令守卫」计划任务（隐藏后台运行）：\n1. 实时监控全系统的进程创建与命令行\n2. 发现危险指令（加密/编码的 powershell、mshta 远程脚本、杀软对抗、删除卷影副本、高危 LOLBins 等）立即击杀进程树、隔离样本、防火墙阻断出网\n3. 同时弹出置顶警示对话框，需要你确认\n\n需要管理员权限（UAC）。监控日志会写入本机日志目录，可在「布防日志」查看告警。",
			PrimaryButtonText = "开启监控",
			CloseButtonText = "取消",
			XamlRoot = base.XamlRoot
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			await RunCommandGuardAsync("install");
		}
	}

	private async void CommandGuardOff_Click(object sender, RoutedEventArgs e)
	{
		if (await new ContentDialog
		{
			Title = "关闭异常命令监控 (MhyunBox)",
			Content = "将注销「命令守卫」计划任务并停止已在运行的监控进程。\n\n确定关闭吗？",
			PrimaryButtonText = "关闭监控",
			CloseButtonText = "取消",
			XamlRoot = base.XamlRoot
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			await RunCommandGuardAsync("uninstall");
		}
	}

	private async Task RunCommandGuardAsync(string mode)
	{
		if (_defenseBusy)
		{
			return;
		}
		if (!File.Exists(SilverFoxTool.CommandGuardScriptPath))
		{
			MainWindow.Instance?.ShowToast("未找到命令守卫脚本：" + SilverFoxTool.CommandGuardScriptPath);
			return;
		}
		_defenseBusy = true;
		try
		{
			AppendDefenseLog((mode == "install") ? "== 开启异常命令监控（置顶弹窗 + 自动拦截）==" : "== 关闭异常命令监控 ==");
			await SilverFoxTool.RunCommandGuardAsync(mode, delegate(string s)
			{
				base.DispatcherQueue.TryEnqueue(delegate
				{
					AppendDefenseLog(s);
				});
			});
			RefreshDefenseUi();
			LoadGuardAlerts();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "命令守卫执行失败");
			AppendDefenseLog("【错误】" + ex.Message);
		}
		finally
		{
			_defenseBusy = false;
		}
	}

	// ================= 应急响应（病毒泛滥 / 系统被入侵 / 未装防护软件） =================

	private async void EmergencyStop_Click(object sender, RoutedEventArgs e)
	{
		if (await new ContentDialog
		{
			Title = "🚨 一键应急止血 (MhyunBox)",
			Content = "用于「已经中毒 / 木马正在运行 / 被入侵」的紧急场景，将立即执行：\n1. 防火墙阻断全部出站与入站（断网止血，阻止外联回传与横向扩散）\n2. 强杀全部可疑进程（随机名 / 伪装系统进程 / 诱导名 / 编码命令等），并隔离样本\n3. 冻结可疑自启动项与计划任务（之后可一键恢复）\n4. 挂载系统守护进程，防止桌面与关键服务被木马连带打崩导致系统崩溃\n\n安全说明：系统关键进程、微软签名文件、杀毒软件进程均有白名单保护，不会误杀；未安装任何杀毒软件的电脑同样可用。\n应急结束后请点「应急恢复」还原网络。需要管理员权限（UAC）。",
			PrimaryButtonText = "立即止血",
			CloseButtonText = "取消",
			XamlRoot = base.XamlRoot,
			RequestedTheme = MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			await RunEmergencyFlowAsync("stop", "一键应急止血");
		}
	}

	private async void EmergencyStandard_Click(object sender, RoutedEventArgs e)
	{
		if (await new ContentDialog
		{
			Title = "应急响应 - 标准 (MhyunBox)",
			Content = "在「一键止血」（断网 + 强杀 + 冻结自启动）之后追加：\n1. 全项下一代威胁扫描（LOLBins / 进程伪装 / WMI 订阅 / 计划任务 / 启动项 / BYOVD 驱动 / hosts 劫持）\n2. 若系统自带 Windows 安全中心可用，同时触发 Defender 快速扫描\n\n适合：怀疑中毒但不确定根因，想一次性查干净的场景。需要管理员权限（UAC）。",
			PrimaryButtonText = "开始应急",
			CloseButtonText = "取消",
			XamlRoot = base.XamlRoot,
			RequestedTheme = MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			await RunEmergencyFlowAsync("standard", "应急响应-标准");
		}
	}

	private async void EmergencyDeep_Click(object sender, RoutedEventArgs e)
	{
		if (await new ContentDialog
		{
			Title = "应急响应 - 深度 (MhyunBox)",
			Content = "在「标准」全部内容之外追加：\n1. 常见病毒投放目录（Temp / AppData / Public / Downloads / 桌面等）全枚举，列出所有未签名可执行与脚本\n2. 引导层检查（bcdedit 测试模式 / 关闭完整性检查）\n3. 取证信息打包：进程列表 / 网络连接 / 服务 / 启动项 / 计划任务，导出到\n   %LOCALAPPDATA%\\MhyunBox\\Incident_时间戳\\\n\n耗时较长（数分钟），适合感染严重、需要留痕排查的场景。需要管理员权限（UAC）。",
			PrimaryButtonText = "开始深度应急",
			CloseButtonText = "取消",
			XamlRoot = base.XamlRoot,
			RequestedTheme = MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			await RunEmergencyFlowAsync("deep", "应急响应-深度");
		}
	}

	private async void EmergencyRestore_Click(object sender, RoutedEventArgs e)
	{
		if (await new ContentDialog
		{
			Title = "应急恢复 (MhyunBox)",
			Content = "将还原应急响应期间的改动：\n1. 删除应急防火墙阻断规则，恢复原始出站/入站策略（恢复上网）\n2. 解冻被冻结的自启动项\n3. 停止后台系统守护进程\n\n注意：被强杀和隔离的样本不会自动恢复（这是有意为之，避免病毒复活）。确定恢复吗？",
			PrimaryButtonText = "恢复",
			CloseButtonText = "取消",
			XamlRoot = base.XamlRoot,
			RequestedTheme = MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			await RunEmergencyFlowAsync("restore", "应急恢复");
		}
	}

	private async Task RunEmergencyFlowAsync(string mode, string title)
	{
		if (_defenseBusy)
		{
			return;
		}
		if (!File.Exists(SilverFoxTool.EmergencyScriptPath))
		{
			MainWindow.Instance?.ShowToast("未找到应急响应脚本：" + SilverFoxTool.EmergencyScriptPath);
			return;
		}
		_defenseBusy = true;
		try
		{
			AppendDefenseLog("== " + title + " 开始（后台静默执行，仅回传关键结果）==");
			await SilverFoxTool.RunEmergencyAsync(mode, delegate(string s)
			{
				base.DispatcherQueue.TryEnqueue(delegate
				{
					AppendDefenseLog(s);
				});
			});
			RefreshDefenseUi();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "应急响应执行失败");
			AppendDefenseLog("【错误】" + ex.Message);
		}
		finally
		{
			_defenseBusy = false;
		}
		MainWindow.Instance?.ShowToast(title + "执行结束，详见布防日志。");
	}

	private void RelayoutTools(double width)
	{
		bool wide = width >= 1200.0;
		ToolRoot.HorizontalAlignment = (wide ? HorizontalAlignment.Center : HorizontalAlignment.Left);
		ToolRoot.MaxWidth = (wide ? 1250 : 680);
		ToolCardGrid.RowDefinitions.Clear();
		ToolCardGrid.ColumnDefinitions.Clear();

		// 按 DisplayOrder 收集卡片；未列入的（在线沙箱、开源工具等动态新增卡片）按自然顺序追加到末尾，
		// 保证每一张卡片都被显式放置，在窄屏下也不会因为未分配行而叠到 Row0/Col0 上造成「混在一起」。
		List<FrameworkElement> ordered = new List<FrameworkElement>();
		HashSet<int> placed = new HashSet<int>();
		for (int i = 0; i < DisplayOrder.Length && i < ToolCardGrid.Children.Count; i++)
		{
			int idx = DisplayOrder[i];
			if (idx >= 0 && idx < ToolCardGrid.Children.Count && placed.Add(idx))
			{
				ordered.Add((FrameworkElement)ToolCardGrid.Children[idx]);
			}
		}
		for (int i = 0; i < ToolCardGrid.Children.Count; i++)
		{
			if (placed.Add(i))
			{
				ordered.Add((FrameworkElement)ToolCardGrid.Children[i]);
			}
		}

	if (wide)
	{
		ToolCardGrid.ColumnDefinitions.Add(new ColumnDefinition());
		ToolCardGrid.ColumnDefinitions.Add(new ColumnDefinition());
		for (int r = 0; r < 7; r++)
		{
			ToolCardGrid.RowDefinitions.Add(new RowDefinition());
		}
		// 布局（下标为 DisplayOrder 中的位置）：
		// pos0 恶意木马与威胁清理（通栏）、pos1 在线工具折叠分区（通栏）、
		// pos2 电脑布防 + pos3 自启动工具（两列一行）、
		// pos4 安全防护工具下载、pos5 卸载工具、pos6 其他常用工具下载、pos7 硬件驱动下载（各通栏）
		int[] array = new int[8] { 0, 1, 2, 2, 3, 4, 5, 6 };
		int[] array2 = new int[8] { 0, 0, 0, 1, 0, 0, 0, 0 };
		int[] array3 = new int[8] { 2, 2, 1, 1, 2, 2, 2, 2 };
		int n = Math.Min(ordered.Count, 8);
		for (int j = 0; j < n; j++)
		{
			FrameworkElement element = ordered[j];
			Grid.SetRow(element, array[j]);
			Grid.SetColumn(element, array2[j]);
			Grid.SetColumnSpan(element, array3[j]);
		}
		// 超出固定布局顺序的新增卡片：追加到新行并跨两列平铺
		int extraRow = 7;
			for (int k = n; k < ordered.Count; k++)
			{
				while (extraRow >= ToolCardGrid.RowDefinitions.Count)
				{
					ToolCardGrid.RowDefinitions.Add(new RowDefinition());
				}
				FrameworkElement element2 = ordered[k];
				Grid.SetRow(element2, extraRow);
				Grid.SetColumn(element2, 0);
				Grid.SetColumnSpan(element2, 2);
				extraRow++;
			}
		}
		else
		{
			ToolCardGrid.ColumnDefinitions.Add(new ColumnDefinition());
			for (int k = 0; k < ordered.Count; k++)
			{
				ToolCardGrid.RowDefinitions.Add(new RowDefinition());
				FrameworkElement element2 = ordered[k];
				Grid.SetRow(element2, k);
				Grid.SetColumn(element2, 0);
				Grid.SetColumnSpan(element2, 1);
			}
		}
	}

	// 给代码后置动态卡片按当前主题统一配色（背景/描边 + 卡片内文本前景）。
	// 关键：绝不能用 TextElement.ForegroundProperty 给 Border 设前景——那是 TextElement 拥有的附加属性，
	// 在 Border 上走原生 ABI SetValue 会触发 AccessViolation 直接崩进程（MhyunBox 打开工具页闪退的根因）。
	// 正确做法：Border 自身只设 Background/BorderBrush，文本前景逐个设到 TextBlock 上（按钮由框架自动跟随主题）。
	private static void ApplyCardTheme(Border card)
	{
		card.Background = ThemeHelper.Resolve("CardBackgroundFillColorSecondaryBrush");
		card.BorderBrush = ThemeHelper.Resolve("CardStrokeColorDefaultBrush");
		if (card.Child is Panel root)
		{
			SetTextForeground(root, ThemeHelper.Resolve("TextFillColorPrimaryBrush"));
		}
	}

	private static void SetTextForeground(Panel panel, Brush brush)
	{
		foreach (FrameworkElement child in panel.Children.OfType<FrameworkElement>())
		{
			if (child is TextBlock tb)
			{
				tb.Foreground = brush;
			}
			else if (child is Panel p)
			{
				SetTextForeground(p, brush);
			}
		}
	}

	// 超级工具箱：内置「压缩版」开源小工具，按需解压到本地再启动（清单驱动，见 SuperToolkit / BundledTools\tools.json）
	private void AddSuperToolkitCard()
	{
		Border card = new Border();
		card.BorderThickness = new Thickness(1.0);
		card.CornerRadius = new CornerRadius(12.0);
		card.Padding = new Thickness(20.0);
		card.ActualThemeChanged += (s, e) =>
		{
			if (s is Border b)
			{
				ApplyCardTheme(b);
			}
		};
		StackPanel body = new StackPanel
		{
			Spacing = 8.0
		};
		body.Children.Add(new TextBlock
		{
			Text = "超级工具箱（内置 · 按需解压）",
			FontSize = 14.0,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
		});
		body.Children.Add(new TextBlock
		{
			Text = "以下开源工具已压缩随程序分发：首次点按需解压到本地后直接运行，无需联网、无需单独下载；解压过一次后可离线复用。需要管理员权限的工具会弹一次 UAC 授权。",
			FontSize = 12.0,
			Opacity = 0.8,
			TextWrapping = TextWrapping.Wrap
		});
		IReadOnlyList<SuperTool> tools = SuperToolkit.ListTools();
		if (tools.Count == 0)
		{
			body.Children.Add(new TextBlock
			{
				Text = "（未发现内置工具，请确认 BundledTools 已随程序分发）",
				FontSize = 12.0,
				Opacity = 0.7
			});
		}
		else
		{
			foreach (SuperTool tool in tools)
			{
				SuperTool captured = tool;
				Button btn = new Button
				{
					Content = "▶ " + captured.Name + (captured.Admin ? "（管理员）" : ""),
					HorizontalAlignment = HorizontalAlignment.Stretch,
					MinHeight = 40.0
				};
				ToolTipService.SetToolTip(btn, captured.Desc + "\n许可证：" + captured.License + "　来源：" + captured.Home);
				TextBlock status = new TextBlock
				{
					Text = "",
					FontSize = 12.0,
					Opacity = 0.85,
					TextWrapping = TextWrapping.Wrap,
					Margin = new Thickness(0, 2, 0, 6)
				};
				btn.Click += async (s, e) =>
				{
					btn.IsEnabled = false;
					try
					{
						await SuperToolkit.RunToolAsync(captured, delegate(string msg)
						{
							status.Text = msg;
						});
					}
					finally
					{
						btn.IsEnabled = true;
					}
				};
				StackPanel row = new StackPanel
				{
					Spacing = 2.0
				};
				row.Children.Add(btn);
				row.Children.Add(status);
				body.Children.Add(row);
			}
		}
		card.Child = body;
		ApplyCardTheme(card);
		ToolCardGrid.Children.Add(card);
		_superToolkitCard = card;
	}

	// 扫描进度条：本工程运行时加载随工程预编译的 .xbf、.xaml 不参与编译（字段连接靠 XamlGenerated\*.g.cs），
	// 因此不能在 XAML 里新增命名元素。这里在代码后置创建一个不确定(IsIndeterminate)进度条，
	// 插入到「清理实时日志」滚动框之前，由 SetKillBusy 控制显隐——扫描时显示、结束隐藏。
	private void EnsureScanProgressBar()
	{
		try
		{
			if (KillScanProgress != null || KillLogScroll == null)
			{
				return;
			}
			ProgressBar bar = new ProgressBar
			{
				IsIndeterminate = true,
				Visibility = Visibility.Collapsed,
				Margin = new Thickness(0, 2, 0, 0),
				Height = 4.0
			};
			KillScanProgress = bar;
			if (KillLogScroll.Parent is Panel parent)
			{
				int index = parent.Children.IndexOf(KillLogScroll);
				if (index >= 0)
				{
					parent.Children.Insert(index, bar);
				}
				else
				{
					parent.Children.Add(bar);
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "初始化扫描进度条失败");
		}
	}

	private static void OpenUrl(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo(url)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "打开下载链接失败: " + url);
		}
	}

	private void Open360_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://www.360.cn/");
	}

	private void Open360Jijiu_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://jijiu.360.cn/");
	}

	private void OpenQQGuanjia_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://guanjia.qq.com/");
	}

	private void OpenKaspersky_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://www.kaspersky.com.cn/downloads");
	}

	private void OpenHiBitDownload_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://hibitsoft.ir/");
	}

	private void Open7Zip_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://www.7-zip.org/");
	}

	private void OpenBandizip_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://www.bandisoft.com/bandizip/");
	}

	private void OpenRadminLan_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://www.radmin-lan.cn/");
	}

	private void OpenSteam_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://store.steampowered.com/about/");
	}

	private void LaunchHiBit_Click(object sender, RoutedEventArgs e)
	{
		if (!File.Exists("F:\\Notel文件\\Fixtools\\HiBitUninstaller_87005\\HiBitUninstaller-Portable.exe"))
		{
			MainWindow.Instance?.ShowToast("未找到本机 HiBit 卸载工具：F:\\Notel文件\\Fixtools\\HiBitUninstaller_87005\\HiBitUninstaller-Portable.exe");
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo("F:\\Notel文件\\Fixtools\\HiBitUninstaller_87005\\HiBitUninstaller-Portable.exe")
			{
				WorkingDirectory = Path.GetDirectoryName("F:\\Notel文件\\Fixtools\\HiBitUninstaller_87005\\HiBitUninstaller-Portable.exe"),
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "启动本机 HiBit 卸载工具失败");
		}
	}

	private void OpenHuoRong_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://www.huorong.cn/");
	}

	private void OpenDiskGenius_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://www.diskgenius.cn/");
	}

	private void OpenNvidiaDriver_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://www.nvidia.cn/drivers/");
	}

	private void OpenAmdDriver_Click(object sender, RoutedEventArgs e)
	{
		OpenUrl("https://www.amd.com/zh-cn/support/downloads/drivers.html");
	}

	private void LaunchAutoStartToolsNow()
	{
		int started = 0;
		AppSettings instance = SettingsManager.Instance;
		try
		{
			List<string> list = new List<string>();
			string text = instance.AutoStartFolder?.Trim() ?? "";
			if (!string.IsNullOrWhiteSpace(text))
			{
				if (File.Exists(text))
				{
					list.Add(text);
				}
				else if (Directory.Exists(text))
				{
					list.AddRange(Directory.GetFiles(text, "*.bat"));
					list.AddRange(Directory.GetFiles(text, "*.exe"));
				}
			}
			if (instance.AutoStartNapCatFiles != null)
			{
				foreach (string autoStartNapCatFile in instance.AutoStartNapCatFiles)
				{
					if (!string.IsNullOrWhiteSpace(autoStartNapCatFile) && File.Exists(autoStartNapCatFile))
					{
						list.Add(autoStartNapCatFile);
					}
				}
			}
			foreach (string item in list.Distinct())
			{
				try
				{
					ProcessStartInfo processStartInfo = new ProcessStartInfo(item)
					{
						WorkingDirectory = Path.GetDirectoryName(item),
						UseShellExecute = true
					};
					if (instance.AutoStartHidden)
					{
						processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
					}
					Process.Start(processStartInfo);
					started++;
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "自启动工具启动失败: " + item);
				}
			}
		}
		catch (Exception ex2)
		{
			LogHelper.LogError(ex2, "自启动工具执行失败");
		}
		base.DispatcherQueue.TryEnqueue(delegate
		{
			MainWindow.Instance?.ShowToast((started > 0) ? $"已启动 {started} 个自启动程序" : "没有可启动的自启动程序，请先设置");
			RefreshAutoStartUi();
		});
	}

	private void LaunchAutoStart_Click(object sender, RoutedEventArgs e)
	{
		Task.Run((Action)LaunchAutoStartToolsNow);
	}

	private void ClearAutoStart_Click(object sender, RoutedEventArgs e)
	{
		AppSettings instance = SettingsManager.Instance;
		instance.AutoStartFolder = "";
		instance.AutoStartNapCatFiles = new List<string>();
		instance.AutoStartFired = false;
		SettingsManager.Save(instance);
		RefreshAutoStartUi();
		MainWindow.Instance?.ShowToast("已清除自启动配置（下次启动可再次自动触发）");
	}

	private void RefreshAutoStartUi()
	{
		AppSettings instance = SettingsManager.Instance;
		AutoStartFolderText.Text = (string.IsNullOrWhiteSpace(instance.AutoStartFolder) ? "未设置自启动程序。" : ("自启动程序：" + instance.AutoStartFolder));
		List<string> list = instance.AutoStartNapCatFiles ?? new List<string>();
		NapCatFilesText.Text = ((list.Count == 0) ? "未选择 NapCat 启动器。" : ("NapCat 启动器（" + list.Count + " 个）：" + Environment.NewLine + string.Join(Environment.NewLine, list)));
		AutoStartHiddenToggle.IsChecked = instance.AutoStartHidden;
	}

	private void AutoStartHiddenToggle_Checked(object sender, RoutedEventArgs e)
	{
		AppSettings instance = SettingsManager.Instance;
		instance.AutoStartHidden = true;
		SettingsManager.Save(instance);
	}

	private void AutoStartHiddenToggle_Unchecked(object sender, RoutedEventArgs e)
	{
		AppSettings instance = SettingsManager.Instance;
		instance.AutoStartHidden = false;
		SettingsManager.Save(instance);
	}

	private async void PickAutoStartFolder_Click(object sender, RoutedEventArgs e)
	{
		FileOpenPicker fileOpenPicker = new FileOpenPicker
		{
			FileTypeFilter = { ".bat", ".cmd", ".exe" }
		};
		InitializeWithWindow.Initialize(fileOpenPicker, WindowNative.GetWindowHandle(MainWindow.Instance));
		StorageFile storageFile = await fileOpenPicker.PickSingleFileAsync();
		if (!(storageFile == null))
		{
			AppSettings instance = SettingsManager.Instance;
			instance.AutoStartFolder = storageFile.Path;
			SettingsManager.Save(instance);
			RefreshAutoStartUi();
			MainWindow.Instance?.ShowToast("已设置自启动程序：" + instance.AutoStartFolder);
		}
	}

	private async void PickNapCat_Click(object sender, RoutedEventArgs e)
	{
		FileOpenPicker fileOpenPicker = new FileOpenPicker
		{
			FileTypeFilter = { ".bat", ".cmd", ".exe" }
		};
		InitializeWithWindow.Initialize(fileOpenPicker, WindowNative.GetWindowHandle(MainWindow.Instance));
		IReadOnlyList<StorageFile> readOnlyList = await fileOpenPicker.PickMultipleFilesAsync();
		if (readOnlyList != null && readOnlyList.Count != 0)
		{
			AppSettings instance = SettingsManager.Instance;
			instance.AutoStartNapCatFiles = readOnlyList.Select((StorageFile f) => f.Path).ToList();
			SettingsManager.Save(instance);
			RefreshAutoStartUi();
			MainWindow.Instance?.ShowToast($"已选择 {readOnlyList.Count} 个 NapCat 启动器");
		}
	}

	private void LaunchHuoRongKiller_Click(object sender, RoutedEventArgs e)
	{
		if (!File.Exists(SilverFoxTool.HuorongKillerPath))
		{
			MainWindow.Instance?.ShowToast("未找到内置火绒专杀工具：" + SilverFoxTool.HuorongKillerPath);
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo(SilverFoxTool.HuorongKillerPath)
			{
				WorkingDirectory = SilverFoxTool.ToolsDir,
				UseShellExecute = true
			});
			AppendLog("已启动内置火绒恶性木马专杀工具，请在弹窗中点击扫描。");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "启动火绒专杀工具失败");
			AppendLog("启动火绒专杀工具失败：" + ex.Message);
		}
	}

	private void LaunchKms_Click(object sender, RoutedEventArgs e)
	{
		if (!File.Exists(SilverFoxTool.KmsActivatorPath))
		{
			MainWindow.Instance?.ShowToast("未找到内置系统激活工具：" + SilverFoxTool.KmsActivatorPath);
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo(SilverFoxTool.KmsActivatorPath)
			{
				WorkingDirectory = SilverFoxTool.ToolsDir,
				UseShellExecute = true
			});
			AppendLog("已启动 HEU KMS Activator 系统激活工具。");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "启动系统激活工具失败");
			AppendLog("启动系统激活工具失败：" + ex.Message);
		}
	}

	private void SilverFoxScan_Click(object sender, RoutedEventArgs e)
	{
		RunSilverFoxAsync("scan");
	}

	private async void SilverFoxKill_Click(object sender, RoutedEventArgs e)
	{
		if (await new ContentDialog
		{
			Title = "确认执行威胁清理 (MhyunBox)",
			Content = "即将执行银狐木马综合威胁清理（需要管理员权限，将弹出 UAC 授权）：\n\n1. 清空被恶意启用的 WDAC 策略，解除杀毒软件禁用\n2. 删除旧版银狐驱动与 wow64log.dll\n3. 禁用根目录计划任务\n4. 劫持并终止隐藏进程\n5. 禁用可疑服务\n\n本工具会修改系统，请确认你的电脑感染了银狐木马。",
			PrimaryButtonText = "继续清理",
			CloseButtonText = "取消",
			XamlRoot = base.XamlRoot
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			await RunSilverFoxAsync("kill");
		}
	}

	private async Task RunSilverFoxAsync(string mode)
	{
		if (_killBusy)
		{
			return;
		}
		if (!File.Exists(SilverFoxTool.ScriptPath))
		{
			MainWindow.Instance?.ShowToast("未找到内置威胁清理脚本：" + SilverFoxTool.ScriptPath);
			return;
		}
		_killBusy = true;
		SetKillBusy(busy: true);
		KillLogBox.Text = string.Empty;
		AppendLog((mode == "kill") ? "== 开始银狐综合威胁清理 ==" : "== 开始扫描（仅检测，不修改系统）==");
		Action<string> onLine = delegate(string s)
		{
			base.DispatcherQueue.TryEnqueue(delegate
			{
				AppendLog(s);
			});
		};
		try
		{
			if (mode == "kill")
			{
				await SilverFoxTool.RunKillAsync(onLine);
			}
			else
			{
				await SilverFoxTool.RunScanAsync(onLine);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			LogHelper.LogError(ex2, "银狐威胁清理执行失败");
			AppendLog("【错误】" + ex2.Message);
		}
		finally
		{
			_killBusy = false;
			SetKillBusy(busy: false);
		}
		if (mode == "kill")
		{
			MainWindow.Instance?.ShowToast("威胁清理已结束，建议运行「一键杀毒扫描」复查系统。");
		}
	}

	private void SetKillBusy(bool busy)
	{
		SilverFoxKillButton.IsEnabled = !busy;
		SilverFoxScanButton.IsEnabled = !busy;
		if (_antivirusScanButton != null)
		{
			_antivirusScanButton.IsEnabled = !busy;
		}
		if (_rescueKitButton != null)
		{
			_rescueKitButton.IsEnabled = !busy;
		}
		if (_evoScanButton != null)
		{
			_evoScanButton.IsEnabled = !busy;
		}
		KillStatusText.Text = (busy ? "正在执行中，请稍候..." : "");
		// 扫描进行中在日志框上方显示不确定进度条，结束后隐藏（IsIndeterminate 由脚本驱动，无法取真实百分比）
		if (KillScanProgress != null)
		{
			KillScanProgress.Visibility = (busy ? Visibility.Visible : Visibility.Collapsed);
		}
	}

	private async void AntivirusScan_Click(object sender, RoutedEventArgs e)
	{
		if (_killBusy)
		{
			return;
		}
		if (!File.Exists(SilverFoxTool.AntivirusScanScriptPath))
		{
			MainWindow.Instance?.ShowToast("未找到内置杀毒扫描脚本：" + SilverFoxTool.AntivirusScanScriptPath);
			return;
		}
		if (await new ContentDialog
		{
			Title = "开始主力杀毒扫描 (MhyunBox)",
			Content = "将执行主力杀毒扫描：\n1. Defender 病毒库更新 + 快速扫描（MpCmdRun）\n2. 自研特征扫描（随机名/乱码名进程、高占用与外联、可疑启动项与计划任务、载荷目录、特征库样本比对）\n\n发现威胁时会自动调用「威胁清理」脚本清理，清理环节需要管理员权限（将弹出 UAC 授权框）。是否继续？",
			PrimaryButtonText = "开始扫描",
			CloseButtonText = "取消",
			XamlRoot = base.XamlRoot
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			await RunAntivirusScanFlowAsync();
		}
	}

	private async Task RunAntivirusScanFlowAsync()
	{
		if (_killBusy)
		{
			return;
		}
		_killBusy = true;
		SetKillBusy(busy: true);
		KillLogBox.Text = string.Empty;
		_antivirusThreatCount = 0;
		AppendLog("== 开始主力杀毒扫描（发现威胁将自动联动威胁清理）==");
		Action<string> onLine = delegate(string s)
		{
			base.DispatcherQueue.TryEnqueue(delegate
			{
				if (s.Contains("[威胁] "))
				{
					_antivirusThreatCount++;
				}
				AppendLog(s);
			});
		};
		bool flag;
		try
		{
			flag = await SilverFoxTool.RunAntivirusScanAsync(onLine);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "杀毒扫描执行失败");
			AppendLog("【错误】" + ex.Message);
			flag = false;
		}
		finally
		{
			_killBusy = false;
			SetKillBusy(busy: false);
		}
		if (_antivirusThreatCount > 0)
		{
			MainWindow.Instance?.ShowToast($"杀毒扫描结束：发现 {_antivirusThreatCount} 项威胁，已自动联动威胁清理，详见下方日志。");
		}
		else if (flag)
		{
			MainWindow.Instance?.ShowToast("杀毒扫描完成：未发现威胁。");
		}
	}

	private async void RescueKit_Click(object sender, RoutedEventArgs e)
	{
		if (_killBusy)
		{
			return;
		}
		if (!File.Exists(SilverFoxTool.RescueKitScriptPath))
		{
			MainWindow.Instance?.ShowToast("未找到系统急救箱脚本：" + SilverFoxTool.RescueKitScriptPath);
			return;
		}
		_killBusy = true;
		SetKillBusy(busy: true);
		KillLogBox.Text = string.Empty;
		AppendLog("== 启动 360 系统急救箱（SuperKiller）==");
		AppendLog("未以管理员运行时脚本会自动请求 UAC 提权，急救箱下载完成后窗口将自行打开。");
		Action<string> onLine = delegate(string s)
		{
			base.DispatcherQueue.TryEnqueue(delegate
			{
				AppendLog(s);
			});
		};
		try
		{
			await SilverFoxTool.RunRescueKitAsync(onLine);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "启动360系统急救箱失败");
			AppendLog("【错误】" + ex.Message);
		}
		finally
		{
			_killBusy = false;
			SetKillBusy(busy: false);
		}
		MainWindow.Instance?.ShowToast("360 系统急救箱流程已结束，如未弹出急救箱窗口请查看下方日志。");
	}

	private void ClearKillLog_Click(object sender, RoutedEventArgs e)
	{
		KillLogBox.Text = string.Empty;
	}

	private void AppendLog(string line)
	{
		AppendLogText(KillLogBox, KillLogScroll, line);
	}

	private void AppendDefenseLog(string line)
	{
		AppendLogText(DefenseLogBox, DefenseLogScroll, line);
	}

	private static void AppendLogText(TextBox box, ScrollViewer scroll, string line)
	{
		if (string.IsNullOrEmpty(line))
		{
			line = " ";
		}
		string text = box.Text + DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine;
		if (text.Length > 24000)
		{
			text = text.Substring(text.Length - 24000);
			int num = text.IndexOf('\n');
			if (num >= 0 && num < text.Length - 1)
			{
				text = text.Substring(num + 1);
			}
		}
		box.Text = text;
		scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
	}

	private void RefreshDefenseUi()
	{
		int count = DefenseService.Instance.DeployedFiles().Count;
		StartMonitorButton.IsEnabled = count > 0;
		StopMonitorButton.IsEnabled = DefenseService.Instance.IsMonitoring;
		GuardAutoScanToggle.IsChecked = SettingsManager.Instance.GuardAutoScan;
		DefenseStatusText.Text = ((count > 0) ? ($"已部署 {count} 个蜜罐诱饵文件。" + (DefenseService.Instance.IsMonitoring ? "布防监控运行中，文件异动将自动触发扫描。" : "点击「开始布防监控」启用异动监控。")) : "尚未部署蜜罐文件，点击「部署/重建蜜罐诱饵」开始布防。");
	}

	private void GuardAutoScanToggle_Checked(object sender, RoutedEventArgs e)
	{
		SetGuardAutoScan(on: true);
	}

	private void GuardAutoScanToggle_Unchecked(object sender, RoutedEventArgs e)
	{
		SetGuardAutoScan(on: false);
	}

	private void SetGuardAutoScan(bool on)
	{
		AppSettings instance = SettingsManager.Instance;
		instance.GuardAutoScan = on;
		SettingsManager.Save(instance);
		AppendDefenseLog(DefenseService.Instance.SetGuardAutoScan(on));
	}

	private void DeployHoneyPots_Click(object sender, RoutedEventArgs e)
	{
		var (num, num2) = DefenseService.Instance.DeployHoneyPots();
		if (num == 0)
		{
			MainWindow.Instance?.ShowToast((num2 > 0) ? "目标位置存在真实同名文件，已跳过，未覆盖任何数据" : "部署失败，请查看日志");
		}
		else
		{
			MainWindow.Instance?.ShowToast($"已部署 {num} 个蜜罐诱饵文件");
		}
		AppendDefenseLog($"== 部署蜜罐诱饵：成功 {num} 个，跳过 {num2} 个 ==");
		RefreshDefenseUi();
	}

	private void LoadGuardAlerts()
	{
		(List<string>, int) tuple = DefenseService.Instance.ReadGuardAlerts(_guardAlertCursor);
		List<string> item = tuple.Item1;
		int item2 = tuple.Item2;
		_guardAlertCursor = item2;
		if (item.Count == 0)
		{
			return;
		}
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append(DateTime.Now.ToString("HH:mm:ss")).Append("  == 布防守护计划任务（程序未运行时）记录到 ").Append(item.Count)
			.Append(" 条告警 ==")
			.AppendLine();
		foreach (string item3 in item)
		{
			stringBuilder.Append(DateTime.Now.ToString("HH:mm:ss")).Append("  ").Append(item3)
				.AppendLine();
		}
		string text = DefenseLogBox.Text + stringBuilder.ToString();
		if (text.Length > 24000)
		{
			text = text.Substring(text.Length - 24000);
			int num = text.IndexOf('\n');
			if (num >= 0 && num < text.Length - 1)
			{
				text = text.Substring(num + 1);
			}
		}
		DefenseLogBox.Text = text;
		DefenseLogScroll.ChangeView(null, DefenseLogScroll.ScrollableHeight, null, disableAnimation: true);
	}

	private async void StartMonitor_Click(object sender, RoutedEventArgs e)
	{
		int num = DefenseService.Instance.StartMonitoring();
		if (num == 0)
		{
			MainWindow.Instance?.ShowToast("没有可监控的蜜罐文件，请先部署");
			return;
		}
		string line = DefenseService.Instance.RegisterGuardTask();
		string line2 = DefenseService.Instance.SetGuardAutoScan(SettingsManager.Instance.GuardAutoScan);
		AppendDefenseLog($"== 布防监控已开启：蜜罐文件 {num} 个 + 防护篡改监控（Defender 排除项/实时保护、IFEO 劫持、DisallowRun、hosts、系统DLL签名）==");
		AppendDefenseLog(line);
		AppendDefenseLog(line2);
		AppendDefenseLog("提示：守护计划任务每 30 分钟运行一次，即使 MhyunBox 未打开也会检查并自动联动扫描，下次打开时可在本日志查看告警。");
		RefreshDefenseUi();
		LoadGuardAlerts();
		try
		{
			List<(string, bool)> list = await DefenseService.Instance.GetInstalledAntivirusAsync();
			if (list.Count == 0)
			{
				AppendDefenseLog("⚠ 未检测到任何已安装的杀毒软件（防护空窗）！若防护软件此前被木马禁用/安装失败，请断网运行威胁清理后再安装火绒或 360。");
				DefenseStatusText.Text = "监控中，但未检测到杀毒软件（防护空窗）！建议尽快安装/开启防护。";
				return;
			}
			string text = string.Join("、", from a in list
				where !a.Item2
				select a.Item1);
			AppendDefenseLog("已检测到杀毒软件：" + string.Join("、", list.Select<(string, bool), string>(((string Item1, bool Item2) a) => a.Item1)) + (string.IsNullOrEmpty(text) ? "" : ("；注意：" + text + " 处于禁用状态，请立即开启")));
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "检测杀毒软件失败");
		}
	}

	private void StopMonitor_Click(object sender, RoutedEventArgs e)
	{
		DefenseService.Instance.StopMonitoring();
		string line = DefenseService.Instance.UnregisterGuardTask();
		AppendDefenseLog("== 布防监控已停止 ==");
		AppendDefenseLog(line);
		RefreshDefenseUi();
	}

	private async void CheckDefenseStatus_Click(object sender, RoutedEventArgs e)
	{
		if (_defenseBusy)
		{
			return;
		}
		_defenseBusy = true;
		DefenseStatusText.Text = "正在检查防护状态（需要几秒）...";
		try
		{
			string text = await DefenseService.Instance.CheckDefenseStatusAsync();
			DefenseStatusText.Text = text;
			AppendDefenseLog("== 已刷新防护状态 ==");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			LogHelper.LogError(ex2, "检查防护状态失败");
			DefenseStatusText.Text = "检查防护状态失败：" + ex2.Message;
		}
		finally
		{
			_defenseBusy = false;
		}
	}

	private void ApplyHardening_Click(object sender, RoutedEventArgs e)
	{
		string line = DefenseService.Instance.ApplyHardening();
		AppendDefenseLog(line);
		MainWindow.Instance?.ShowToast("已发起一键加固");
	}

	private async void DisconnectNetwork_Click(object sender, RoutedEventArgs e)
	{
		if (await new ContentDialog
		{
			Title = "紧急断网 (MhyunBox)",
			Content = "将禁用所有当前已连接的网卡（有线/无线），彻底断开网络，阻止木马继续下载、对抗或外传数据。\n\n需要管理员权限（UAC）。断网后可通过「恢复网络」重新启用。\n\n确定要立即断网吗？",
			PrimaryButtonText = "立即断网",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = base.XamlRoot
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			string line = DefenseService.Instance.DisconnectNetwork();
			AppendDefenseLog(line);
			MainWindow.Instance?.ShowToast("已发起紧急断网");
		}
	}

	private async void ReconnectNetwork_Click(object sender, RoutedEventArgs e)
	{
		if (await new ContentDialog
		{
			Title = "恢复网络 (MhyunBox)",
			Content = "将重新启用所有被禁用的网卡。需要管理员权限（UAC）。\n\n确定要恢复网络吗？",
			PrimaryButtonText = "恢复网络",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = base.XamlRoot
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			string line = DefenseService.Instance.ReconnectNetwork();
			AppendDefenseLog(line);
			MainWindow.Instance?.ShowToast("已发起恢复网络");
		}
	}

	private void ClearDefenseLog_Click(object sender, RoutedEventArgs e)
	{
		DefenseLogBox.Text = string.Empty;
	}

	private async void MiningIsolationOn_Click(object sender, RoutedEventArgs e)
	{
		if (await new ContentDialog
		{
			Title = "开启挖矿断网防护 (MhyunBox)",
			Content = "将启用「默认阻断全部出站 + 白名单放行」策略：木马无论把矿池换成哪个 IP/域名都无法联网（不再按 IP 封）。\n\n开启前请确认 KillTools\\断网白名单.txt 中已加入你日常要用的软件（浏览器、游戏等，program: 格式），否则这些程序将无法联网。Windows 更新与 Defender 已默认放行。\n\n需要管理员权限（UAC）。确定开启吗？",
			PrimaryButtonText = "开启",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = base.XamlRoot
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			await RunMiningIsolationAsync("on");
		}
	}

	private async void MiningIsolationOff_Click(object sender, RoutedEventArgs e)
	{
		if (await new ContentDialog
		{
			Title = "关闭挖矿断网防护 (MhyunBox)",
			Content = "将移除全部「默认阻断出站」规则，本机恢复联网。\n\n注意：特征库命中时自动添加的「MhyunBox_阻断_*」程序级阻断规则仍会保留（可在防火墙高级设置中单独删除）。\n\n需要管理员权限（UAC）。确定关闭吗？",
			PrimaryButtonText = "关闭",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = base.XamlRoot
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			await RunMiningIsolationAsync("off");
		}
	}

	private async void MiningIsolationStatus_Click(object sender, RoutedEventArgs e)
	{
		await RunMiningIsolationAsync("status");
	}

	private async Task RunMiningIsolationAsync(string mode)
	{
		if (_miningIsolationBusy)
		{
			return;
		}
		_miningIsolationBusy = true;
		MiningIsolationOnButton.IsEnabled = false;
		string line = ((!(mode == "on")) ? ((!(mode == "off")) ? "== 查看挖矿断网防护状态 ==" : "== 开始关闭挖矿断网防护 ==") : "== 开始开启挖矿断网防护（默认阻断全部出站）==");
		AppendDefenseLog(line);
		try
		{
			await SilverFoxTool.RunNetGuardAsync(mode, delegate(string s)
			{
				base.DispatcherQueue.TryEnqueue(delegate
				{
					AppendDefenseLog(s);
				});
			});
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			LogHelper.LogError(ex2, "挖矿断网防护执行失败");
			AppendDefenseLog("【错误】" + ex2.Message);
		}
		finally
		{
			_miningIsolationBusy = false;
			MiningIsolationOnButton.IsEnabled = true;
			await RefreshMiningIsolationStatusAsync();
		}
	}

	private async Task RefreshMiningIsolationStatusAsync()
	{
		try
		{
			string text = await DefenseService.Instance.GetMiningIsolationStatusAsync();
			MiningIsolationStatusText.Text = text;
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			LogHelper.LogError(ex2, "读取挖矿断网防护状态失败");
			MiningIsolationStatusText.Text = "读取挖矿断网防护状态失败：" + ex2.Message;
		}
	}

	private void OnHoneyPotTouched(string path, string kind)
	{
		base.DispatcherQueue.TryEnqueue(async delegate
		{
			if (_defenseBusy)
			{
				AppendDefenseLog("[忽略] 布防流程执行中，本次蜜罐告警稍后复核：" + path);
				return;
			}
			_defenseBusy = true;
			try
			{
				AppendDefenseLog("⚠ 布防告警：蜜罐诱饵文件被" + kind + "：" + path);
				DefenseStatusText.Text = "已捕获蜜罐异动，正在恢复文件并自动扫描...";
				await DefenseService.Instance.RestoreFileAsync(path);
				AppendDefenseLog("已自动恢复蜜罐诱饵文件：" + path);
				await RunDefenseScanAsync();
			}
			finally
			{
				_defenseBusy = false;
				RefreshDefenseUi();
			}
		});
	}

	private void OnTamperDetected(string title, string detail)
	{
		base.DispatcherQueue.TryEnqueue(async delegate
		{
			if (_defenseBusy)
			{
				AppendDefenseLog("[忽略] 布防流程执行中，本次防护篡改告警稍后复核：" + title);
				return;
			}
			_defenseBusy = true;
			try
			{
				AppendDefenseLog("⚠ 防护篡改告警：" + title);
				AppendDefenseLog("详情：" + detail);
				DefenseStatusText.Text = "检测到防护被篡改，正在自动扫描...";
				await RunDefenseScanAsync();
			}
			finally
			{
				_defenseBusy = false;
				RefreshDefenseUi();
			}
		});
	}

	private async Task RunDefenseScanAsync()
	{
		int suspicious = 0;
		bool scanOk = true;
		try
		{
			AppendDefenseLog("== 自动启动银狐扫描（仅检测，不修改系统）==");
			await SilverFoxTool.RunScanAsync(delegate(string s)
			{
				base.DispatcherQueue.TryEnqueue(delegate
				{
					AppendDefenseLog(s);
					if (s.Contains("-可疑]", StringComparison.Ordinal) || s.Contains("木马特征", StringComparison.Ordinal))
					{
						int num = suspicious;
						suspicious = num + 1;
					}
				});
			});
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			scanOk = false;
			LogHelper.LogError(ex2, "布防自动扫描失败");
			AppendDefenseLog("【错误】自动扫描失败：" + ex2.Message);
		}
		if (!scanOk)
		{
			DefenseStatusText.Text = "自动扫描失败，请手动运行「仅扫描检测」复核。";
			return;
		}
		if (suspicious <= 0)
		{
			DefenseStatusText.Text = "扫描未发现异常，布防监控继续运行。";
			AppendDefenseLog("== 自动扫描完成，未发现可疑项 ==");
			return;
		}
		DefenseStatusText.Text = $"扫描发现 {suspicious} 项可疑，请选择处置方式。";
		MainWindow.Instance?.BringToFront("布防自动扫描发现可疑项");
		if (await new ContentDialog
		{
			Title = "检测到可疑项 (MhyunBox)",
			Content = $"布防告警触发的扫描发现了 {suspicious} 项可疑内容（隐藏进程 / 隐藏启动项 / 可疑服务），电脑可能已被木马入侵。\n\n是否立即运行内置威胁清理（扫描+清理，需要管理员权限）？",
			PrimaryButtonText = "运行威胁清理",
			CloseButtonText = "暂不处理",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = base.XamlRoot
		}.ShowAsync() != ContentDialogResult.Primary)
		{
			AppendDefenseLog("用户选择暂不处理，可稍后在「恶意木马与威胁清理」手动运行。");
			DefenseStatusText.Text = "已跳过清理，布防监控继续运行。";
		}
		else if (await new ContentDialog
		{
			Title = "二次确认 (MhyunBox)",
			Content = "再确认一次：威胁清理会修改系统（解除杀软禁用、清理旧版驱动、劫持隐藏进程、禁用可疑服务）。\n\n若当前电脑没有任何可用的防护软件（Defender/360/火绒均已失效，安装防护软件也会失败），强烈建议：\n1. 先断开网络，避免木马继续下载、对抗或外传数据；\n2. 清理完成后立即安装/开启火绒或 360，并做全盘扫描；\n3. 防护软件仍无法安装时，查看「布防日志」中的 IFEO 劫持与 DisallowRun 告警，先清理这些拦截项。\n\n确定要继续执行威胁清理吗？",
			PrimaryButtonText = "我已了解，继续清理",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = base.XamlRoot
		}.ShowAsync() == ContentDialogResult.Primary)
		{
			AppendDefenseLog("用户已二次确认，开始运行威胁清理...");
			await RunSilverFoxAsync("kill");
		}
		else
		{
			AppendDefenseLog("已取消清理（未二次确认），布防监控继续运行。");
		}
	}

	
	
	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	[DebuggerNonUserCode]
	public IComponentConnector GetBindingConnector(int connectionId, object target)
	{
		return null;
	}
}

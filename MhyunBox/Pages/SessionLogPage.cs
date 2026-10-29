using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MhyunBox.Services;
using MhyunBox.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace MhyunBox.Pages;

public sealed class SessionLogPage : Page
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

	private readonly DispatcherQueue _queue;

	private readonly List<SessionItem> _items = new List<SessionItem>();

	private bool _loading = true;

	private TextBlock _changelogBox;

	private TextBlock _logTailBox;

	private ScrollViewer _logTailScroller;

	private ListView _sessionList;

	private ToggleSwitch _debugModeSwitch;

	private FrameworkElement _logPanel;

	private FrameworkElement _sessionPanel;

	private static SessionLogPage? _instance;

	private static readonly string ChangelogText = "【7.8.0 本次更新】\n· 移除程序侧全部自设门槛：不再有风控冷静期、重试上限、程序级暂停——会话失效就照常重登，进不去也照常请求，程序不再替服务器和网易猜限制（此前反馈的\"进不去\"多为这些本地门槛锁死所致）。\n· 游戏进入（GameStart）阶段返回 login abnormally 时接入自动登录兜底；修复 login-start 返回 HTTP 200 但业务失败被误判为登录成功的问题。\n· 新增代理节点自动轮换：同一节点 10 分钟内 2 次认证失败自动临时停用并切换其它可用节点，认证成功即恢复候选；断开原因附带线路自检（直连/IP 池）。\n· 程序锁：锁定态支持「取消验证」验证账号（「设为验证」仍需解锁）；修复验证账号取消后被自动补回的问题；取消时同步移除该账号的验证包裹，立即生效。\n· 更新客户端：修复资源包地址复用错位——切换预设或地址与当前服务器不匹配时自动清空并重新获取。\n· 界面修复：列表/下拉背景透明穿透全面修复（应用级+逐控件兜底）；顶部 Toast 提示改为独立弹出层（物理最外层，不再被任何内容挡住），浅色主题高对比配色，绑定成功/失败以绿/红底色显示。\n· 移除「教程/解决」页面及其文字文档（教程.md/解决方案.txt 等）；防护/工具页「扫描与应急工具」收缩栏增加展开/收起过渡动画。\n· 版本号统一为 v7.8.0。\n\n【7.7.0 本次更新】\n· 修复 Auth Fail / 会话失效时自动账密重登不生效：重试计数锁死兜底、重登后账号下标错位两处根因，重登成功即用新会话原地重试进服。\n· 游戏内防 DDoS 掉线强化：上游被打挂时立即复测并热切换到 IP 池可用线路，正在游戏的会话不动，玩家重连直接走新线路；主域名恢复后连续 2 轮在线才切回。\n· 修复高 DPI 下窗口发虚：声明 PerMonitorV2，窗口按缩放比换算尺寸，原生分辨率清晰渲染。\n· 修复列表滚轮滚动不准：改为固定像素步长并关闭滚动动画。\n· 新增 GitHub 联动：首页检查更新、提交合并请求（PR）、打开仓库。\n· 编辑分组的服务器预设改为下拉列表（含新添加的额外预设）；移除账号会话「改账密」按钮与首页退出入口。\n· 关于页署名更新为 Powered By OpenCode & TRAE & WorkBuddy。\n· 清理冗余代码与重复实现，警告数量大幅下降。\n\n【7.5.0 本次更新】\n· 更新客户端页一键化：「检查 URL / 开始更新 / 自动获取最新地址」合并为「更新资源包」，输入框留空即自动向网易取最新地址。\n· 内置 7-Zip 独立版 7zr.exe，解压不再依赖本机安装 7-Zip。\n· 移除「强制覆盖」勾选框（更新一律按强制覆盖执行）。\n· 新增「第二合并路径」：失落世界自动拼出 PCL 客户端实例目录并同步合并一份；元素之诗显示 DragonCore 目录；可手动修改。\n· 元素之诗裁剪更新升级：对比升级为大小+MD5 双重校验；自动清理客户端里最新包已不存在的冗余文件（移入备份目录可回滚）；增量资源包自动导出到桌面可直接分享。\n· 托盘图标悬停提示改为 Notel。\n· 版本号更新为 v7.5.0。\n\n【7.1.0 本次更新】\n· 新增「游戏进程内存扫描」：直接扫 javaw.exe 内存找 CrcSalt（byte[16] 形态），不依赖 Alane/404，也不需要开调试模式。\n· 扫描改为在线程池执行，不再把界面卡成未响应；支持主动停止与时长选择。\n· 扫描命中结果可直接「应用」生效，或「排除」进名单；二进制命中（BIN 16B）标为推荐。\n· 新增盐值名单：输入框按回车即可加入，弹窗内只露首尾便于分辨，试过不好使的值下次不再列出。\n· 代理新增上游热切换：切换目标时不动正在转发的会话，解决换线路导致游戏掉线的问题。\n· 代理新增全局连接上限（256），超限时直接拒绝，避免被大量并发连接拖垮。\n· 首页新增会躲鼠标的彩蛋按钮（机制参考 PCL 愚人节）。\n· 版本号更新为 v7.1.0。\n\n【6.8.2 本次更新】\n· 代理启动页移除顶部橙色断连提示横幅与「已激活，剩余…」「已启动自动代理」提示，页面更清爽。\n· 角色端口栏更名「账号列表」：按账号展示其各服务器的全部角色（来自账号数据，不再只显示已运行绑定）。\n· 账号卡片内每个角色按钮改为「启动」，点击即单独启动该角色的代理入口（内网地址 127.0.0.1 + 端口，toast 回显），无需再走弹窗。\n· 已运行的角色显示实际监听端口并置灰（已运行），未占用端口。\n· 版本号更新为 v6.8.2。\n\n【6.5.0 本次更新】\n· 自启动代理改为「记住上一次启动方式」：在快速启动或代理启动实际启动过的角色与端口会被记下。\n· 程序自启动时按这份记忆恢复同样的角色端口，不再只按单一预设或固定起始端口推算。\n· 快速启动页新增「自启动代理」开关与启动记忆提示，与设置页共用同一个开关状态。\n· 「启动分组」对话框内也能直接开关自启动代理，无需再去设置页。\n· 快速启动页新增「清除启动记忆」，可让下次启动不自动恢复代理。\n· 设置页改动会实时同步到快速启动页开关，两处不再显示不一致。\n· 关闭调试模式后不再产出抓包日志（ProxyCaptures 不再新建会话日志文件）。\n· 关闭调试模式会立即关闭进行中会话的日志流并清空抓包日志目录。\n· 会话列表在未记录时显示「未记录（调试模式已关闭）」，不再显示空路径。\n· 修复「代理启动可复用旧 token 登录，但快速启动显示账号失效且锁死」的自相矛盾。\n· 快速启动与代理启动统一判定口径：只要本地还有 Cookie 就能启动，不再要求本次启动已登录。\n· 会话失效改为「游戏内第二次重复进入会话失败」才触发：单次失败仅记录，不再弹窗。\n· 代理启动页刷新角色列表失败不再标记账号失效（刷新失败 ≠ 游戏内会话失败）。\n· 设置页新增「会话失效提醒」开关；关闭后仅静默标记失效标记，不再弹出提示框。\n· 快速启动页失效提示文案区分「未保存登录凭证」与「游戏内连续两次进入会话失败」。\n· 版本号更新为 v6.5.0。\n\n【6.4.0 本次更新】\n· 快速启动页精简为「已保存分组」，去掉代理快速启动卡片与运行日志。\n· 代理启动页预设顺序改为「元素之诗」第一、「失落世界」第二。\n· 代理启动页底部恢复「千！万！不！要！点！」彩蛋按钮。\n· 代理页的会话/日志区块移到本页，并跟随调试模式显示。\n· 关闭调试模式时自动清空冗余日志文件与本页日志内容。\n· 账号页「全部登录」升级为「自动登录」：启动时按 0.1~1 秒间隔逐个拉取会话。\n· 进服发现会话失效时自动重登（失败再重试一次），成功后重置计数。\n· 重登仍失败时保留账号、备注与分组角色，只把该账号标记为「会话失效」，不再自动删除。\n· 账号凭证明文只落 DPAPI 加密存储，保存前自动留一份 local_accounts.bin.bak。\n· 退出程序时自动登出全部账号。\n· 版本号更新为 v6.4.0。\n\n【端口分组】\n· 可将同账户下多个角色端口编成一组，一键启动整组代理。\n· 分组内端口可单独修改，并自动排除已占用端口。\n\n【会话/日志】\n· 本页大标题为「更新日志」，左侧为更新内容，右侧为程序日志，底部会话占满整宽。\n· 右侧程序日志与底部会话仅在调试模式开启时显示。";

	private static readonly DataTemplate SessionTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><StackPanel Orientation=\"Vertical\" Spacing=\"4\"><Grid ColumnSpacing=\"12\" Padding=\"4,2,4,2\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\"/><ColumnDefinition Width=\"100\"/><ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions><TextBlock FontWeight=\"Bold\" VerticalAlignment=\"Center\" Text=\"{Binding IdLabel}\"/><TextBlock Opacity=\"0.85\" VerticalAlignment=\"Center\" Grid.Column=\"1\" Text=\"{Binding Status}\"/><TextBlock TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\" Grid.Column=\"2\" Text=\"{Binding Summary}\"/></Grid></StackPanel></DataTemplate>");

	public SessionLogPage()
	{
		_queue = base.DispatcherQueue;
		base.Content = BuildUi();
		base.Unloaded += OnUnloaded;
		base.Loaded += delegate
		{
			ProxyCaptureService.SessionOpened += OnSessionOpened;
			ProxyCaptureService.SessionClosed += OnSessionClosed;
			SessionLogStore.TailChanged += OnTailChanged;
			_changelogBox.Text = ChangelogText;
			_debugModeSwitch.IsOn = SettingsManager.Instance.DebugMode;
			_loading = false;
			ApplyDebugVisibility();
		};
		_instance = this;
	}

	public static void SyncDebugMode()
	{
		SessionLogPage instance = _instance;
		if (instance == null)
		{
			return;
		}
		try
		{
			instance._loading = true;
			instance._debugModeSwitch.IsOn = SettingsManager.Instance.DebugMode;
			instance._loading = false;
			instance.ApplyDebugVisibility();
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[日志页] 同步调试模式失败");
		}
	}

	private void ApplyDebugVisibility()
	{
		bool debugMode = SettingsManager.Instance.DebugMode;
		if (_logPanel != null)
		{
			_logPanel.Visibility = ((!debugMode) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (_sessionPanel != null)
		{
			_sessionPanel.Visibility = ((!debugMode) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (debugMode)
		{
			RefreshSessionList();
			RefreshLog();
		}
		else if (_logTailBox != null)
		{
			_logTailBox.Text = "";
		}
	}

	private FrameworkElement BuildUi()
	{
		Grid grid = new Grid
		{
			Padding = new Thickness(40.0, 28.0, 40.0, 28.0),
			ColumnSpacing = 16.0,
			RowSpacing = 12.0,
			MaxWidth = 1180.0,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			RowDefinitions = 
			{
				new RowDefinition
				{
					Height = new GridLength(0.0, GridUnitType.Auto)
				},
				new RowDefinition
				{
					Height = new GridLength(1.0, GridUnitType.Star)
				},
				new RowDefinition
				{
					Height = new GridLength(0.0, GridUnitType.Auto)
				}
			},
			ColumnDefinitions = 
			{
				new ColumnDefinition
				{
					Width = new GridLength(1.0, GridUnitType.Star)
				},
				new ColumnDefinition
				{
					Width = new GridLength(1.0, GridUnitType.Star)
				}
			}
		};
		TextBlock textBlock = new TextBlock
		{
			Text = "更新日志",
			FontSize = 28.0,
			FontWeight = FontWeights.Bold,
			Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
		};
		Grid.SetRow(textBlock, 0);
		Grid.SetColumnSpan(textBlock, 2);
		grid.Children.Add(textBlock);
		Grid grid2 = new Grid
		{
			RowSpacing = 4.0
		};
		grid2.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(0.0, GridUnitType.Auto)
		});
		grid2.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(1.0, GridUnitType.Star)
		});
		grid2.Children.Add(new TextBlock
		{
			Text = "更新内容",
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center
		});
		_changelogBox = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13.0,
			Opacity = 0.9,
			IsTextSelectionEnabled = true,
			LineHeight = 22.0
		};
		ScrollViewer child = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = _changelogBox
		};
		Border border = new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(16, 128, 128, 128)),
			BorderBrush = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128)),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(12.0),
			Padding = new Thickness(12.0)
		};
		border.Child = child;
		Grid.SetRow(border, 1);
		grid2.Children.Add(border);
		Grid.SetRow(grid2, 1);
		Grid.SetColumn(grid2, 0);
		grid.Children.Add(grid2);
		Grid grid3 = new Grid
		{
			RowSpacing = 4.0
		};
		grid3.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(0.0, GridUnitType.Auto)
		});
		grid3.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(1.0, GridUnitType.Star)
		});
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 12.0
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "日志",
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center
		});
		Button button = new Button
		{
			Content = "复制日志",
			VerticalAlignment = VerticalAlignment.Center,
			MinHeight = 28.0,
			Padding = new Thickness(10.0, 2.0, 10.0, 2.0),
			FontSize = 12.0
		};
		button.Click += CopyLog_Click;
		stackPanel.Children.Add(button);
		Button button2 = new Button
		{
			Content = "清空日志",
			VerticalAlignment = VerticalAlignment.Center,
			MinHeight = 28.0,
			Padding = new Thickness(10.0, 2.0, 10.0, 2.0),
			FontSize = 12.0
		};
		button2.Click += ClearLog_Click;
		stackPanel.Children.Add(button2);
		_debugModeSwitch = new ToggleSwitch
		{
			Header = "调试模式",
			OnContent = "开",
			OffContent = "关",
			VerticalAlignment = VerticalAlignment.Center,
			MinWidth = 140.0,
			Margin = new Thickness(12.0, 0.0, 0.0, 0.0)
		};
		_debugModeSwitch.Toggled += DebugModeSwitch_Toggled;
		stackPanel.Children.Add(_debugModeSwitch);
		grid3.Children.Add(stackPanel);
		_logTailBox = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			IsTextSelectionEnabled = true,
			FontSize = 12.0,
			Opacity = 0.9
		};
		_logTailScroller = new ScrollViewer
		{
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = _logTailBox
		};
		Border border2 = new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(16, 128, 128, 128)),
			BorderBrush = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128)),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(12.0),
			Padding = new Thickness(8.0)
		};
		border2.Child = _logTailScroller;
		Grid.SetRow(border2, 1);
		grid3.Children.Add(border2);
		Grid.SetRow(grid3, 1);
		Grid.SetColumn(grid3, 1);
		grid.Children.Add(grid3);
		_logPanel = grid3;
		Grid grid4 = new Grid
		{
			RowSpacing = 4.0
		};
		grid4.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(0.0, GridUnitType.Auto)
		});
		grid4.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(0.0, GridUnitType.Auto)
		});
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 12.0
		};
		stackPanel2.Children.Add(new TextBlock
		{
			Text = "会话",
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center
		});
		Button button3 = new Button
		{
			Content = "打开抓取目录",
			VerticalAlignment = VerticalAlignment.Center,
			MinHeight = 28.0,
			Padding = new Thickness(10.0, 2.0, 10.0, 2.0),
			FontSize = 12.0
		};
		button3.Click += OpenCaptureDir_Click;
		stackPanel2.Children.Add(button3);
		grid4.Children.Add(stackPanel2);
		_sessionList = new ListView
		{
			Height = 220.0,
			Background = ThemeHelper.ListSurface(),
			IsItemClickEnabled = false,
			SelectionMode = ListViewSelectionMode.None,
			ItemTemplate = SessionTemplate
		};
		Border border3 = new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(16, 128, 128, 128)),
			BorderBrush = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128)),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(12.0),
			Padding = new Thickness(8.0)
		};
		border3.Child = _sessionList;
		Grid.SetRow(border3, 1);
		grid4.Children.Add(border3);
		Grid.SetRow(grid4, 2);
		Grid.SetColumnSpan(grid4, 2);
		grid.Children.Add(grid4);
		_sessionPanel = grid4;
		return grid;
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		ProxyCaptureService.SessionOpened -= OnSessionOpened;
		ProxyCaptureService.SessionClosed -= OnSessionClosed;
		SessionLogStore.TailChanged -= OnTailChanged;
	}

	private void OnTailChanged()
	{
		_queue.TryEnqueue(RefreshLog);
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
			_sessionList.ItemsSource = _items.ToArray();
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
			_sessionList.ItemsSource = _items.ToArray();
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
		_sessionList.ItemsSource = _items.ToArray();
	}

	private void RefreshLog()
	{
		_logTailBox.Text = SessionLogStore.Snapshot();
		_queue.TryEnqueue(delegate
		{
			try
			{
				double scrollableHeight = _logTailScroller.ScrollableHeight;
				_logTailScroller.ChangeView(null, scrollableHeight, null, disableAnimation: true);
			}
			catch
			{
			}
		});
	}

	private void CopyLog_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string text = SessionLogStore.Snapshot();
			DataPackage dataPackage = new DataPackage();
			dataPackage.SetText(text);
			Clipboard.SetContent(dataPackage);
			MainWindow.Instance?.ShowToast((text.Length == 0) ? "暂无日志内容" : "日志已复制到剪贴板", text.Length > 0);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "复制日志失败");
		}
	}

	private void ClearLog_Click(object sender, RoutedEventArgs e)
	{
		SessionLogStore.Clear();
	}

	private void DebugModeSwitch_Toggled(object sender, RoutedEventArgs e)
	{
		if (!_loading)
		{
			AppSettings instance = SettingsManager.Instance;
			instance.DebugMode = _debugModeSwitch.IsOn;
			SettingsManager.Save(instance);
			ProxyCaptureService.SetCaptureLogEnabled(instance.DebugMode);
			if (instance.DebugMode)
			{
				MainWindow.Instance?.ShowToast("已开启调试模式（详细日志 + 抓包日志）");
			}
			else
			{
				LogHelper.ClearLogs();
				SessionLogStore.Clear();
				MainWindow.Instance?.ShowToast("已关闭调试模式，冗余日志已停止产出并清空（仅保留错误日志）");
			}
			ApplyDebugVisibility();
		}
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
}

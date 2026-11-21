using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MhyunBox.Services;
using MhyunBox.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;

namespace MhyunBox.Pages;

/// <summary>
/// 实例多开主程序内容页：全屏展示各角色（不同本地端口）的视角画面占位。
/// 卡片按服务器分组、可整组收起；角色与端口来自「快速启动」的分组配置。
/// </summary>
public sealed class InstancesViewPage : Page
{
	private readonly DispatcherQueue _queue;
	private readonly DispatcherTimer _timer;
	/// <summary>卡片区容器：按服务器分组纵向堆叠，每组一个可收缩横栏 + 一组卡片。</summary>
	private StackPanel _tiles = new StackPanel { Spacing = 10 };
	private TextBlock _summary = new TextBlock();
	/// <summary>高级统计行（监听数 / 上下行包），「高级信息」勾选才显示。</summary>
	private TextBlock _summaryDetail = new TextBlock();
	/// <summary>各服务器已进服机器人数量，如「0 失落世界 | 2 元素之诗」。</summary>
	private TextBlock _serverSummary = new TextBlock();
	private CheckBox? _advancedCheck;
	private string _signature = "";
	/// <summary>被收起的服务器分组名（横栏点一下收起/展开，重建后按它恢复）。</summary>
	private readonly HashSet<string> _collapsedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	/// <summary>正在跑收起/展开动画的分组：动画没结束前忽略重复点击，避免 MaxHeight 被算乱。</summary>
	private readonly HashSet<string> _sectionAnimating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	private bool _entryHandled;
	private CheckBox? _autoStartCheck;
	/// <summary>「自启动全选」：一次勾选/取消所有卡片的「自启动」。</summary>
	private CheckBox? _autoAllCheck;
	/// <summary>「全显日志」：不满时全展开，已全展开时全部收起。</summary>
	private CheckBox? _logAllCheck;
	/// <summary>哪些端口（卡片）的日志区是展开的。重建卡片时按它恢复，
	/// 否则删一张卡/任何刷新都会把其它卡已展开的日志一起收起来。</summary>
	private readonly HashSet<int> _logExpanded = new HashSet<int>();
	/// <summary>静默保存中：不让 SettingsManager.Saved 触发整页重建。</summary>
	private bool _muteSavedRefresh;
	private ComboBox? _marketCombo;
	private string _roleComboSignature = "";
	// 行情角色跟随显示组：记录上一次已经同步过行情角色的组名，避免 2 秒刷新反复改设置。
	private string _marketGroupSignature = "\u0002";
	// 只在用户真正展开并选择后保存，程序化重填（换组/定时刷新）不触发保存。
	private string _marketKeyOnOpen = "";
	// 程序化重填下拉项期间置位：Items.Clear() 会强制关闭下拉框并触发 DropDownClosed，不能当成用户选择。
	private bool _fillingRoleCombo;
	private string _gameDir = "";
	private FrameworkElement? _contentScroll;
	private Border? _lockPanel;
	private ComboBox? _groupCombo;
	private bool _groupComboSyncing;
	private string _groupSignature = "";
	private string _groupListSignature = "";
	private string _groupItemsSignature = "";
	private bool _groupApplyQueued;
	/// <summary>端口 → 各卡片区里对应的控件引用（上下两区可能同时显示同一张卡，所以是列表）。</summary>
	private readonly Dictionary<int, List<LiveRefs>> _live = new Dictionary<int, List<LiveRefs>>();

	/// <summary>每张卡片的批量勾选框（端口 → CheckBox），重建时清空。</summary>
	private readonly Dictionary<int, List<CheckBox>> _headChecks = new Dictionary<int, List<CheckBox>>();

	/// <summary>「全选角色卡片」勾选框：随子卡片勾选状态自动同步。</summary>
	private Button? _selectAllButton;

	/// <summary>被勾选的卡片端口。卡片每 2 秒可能整批重建，勾选状态必须放在这里，
	/// 否则重建一次用户勾的就全没了（「全选只生效一次」的根因之一）。</summary>
	private readonly HashSet<int> _checkedPorts = new HashSet<int>();

	/// <summary>最近一次 Refresh 得到的可见卡片（批量操作按勾选取）。</summary>
	private List<NexusEndpointService.Endpoint> _lastVisible = new List<NexusEndpointService.Endpoint>();

	/// <summary>卡片里需要随 2 秒刷新更新的文本控件。</summary>
	private sealed class LiveRefs
	{
		public TextBlock Stats = new TextBlock();
		public TextBlock Event = new TextBlock();
		public TextBlock Error = new TextBlock();
		public TextBlock Status = new TextBlock();
		public TextBlock Chat = new TextBlock();
		public Border StatusBadge = new Border();
		/// <summary>该端口当前是否有机器人代理在监听（决定右上角是否显示「监听中」）。</summary>
		public bool Listening;

		/// <summary>是否已经收到过聊天栏内容（收到即视为已进服，右上角改显示「已进服」）。</summary>
		public bool SawChat;

		/// <summary>这张卡是否处于「高级信息」模式：统计行与错误行只在高级信息下显示。</summary>
		public bool Advanced;
	}

	public InstancesViewPage()
	{
		_queue = DispatcherQueue.GetForCurrentThread();
		Content = BuildShell();
		_timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
		_timer.Tick += (_, _) => Refresh();
		Loaded += async (_, _) =>
		{
			_timer.Start();
			SettingsManager.Saved += OnSettingsSaved;
			Refresh();
			await HandleEntryAsync();
		};
		Unloaded += (_, _) =>
		{
			_timer.Stop();
			SettingsManager.Saved -= OnSettingsSaved;
		};
		Refresh();
	}

	/// <summary>填一条显示组下拉（全部组 + 各快速启动组），并同步选中项。</summary>
	private void FillGroupCombo(ComboBox? combo, List<ProxyGroup> groups, string current, ref string itemsSignature)
	{
		if (combo == null)
		{
			return;
		}
		string signature = "全部组\u0001" + string.Join("\u0001", groups.Select(g => g.Name));
		if (!string.Equals(itemsSignature, signature, StringComparison.Ordinal) || combo.Items.Count == 0)
		{
			combo.Items.Clear();
			combo.Items.Add(new ComboBoxItem { Content = "全部组", Tag = "" });
			foreach (ProxyGroup group in groups)
			{
				combo.Items.Add(new ComboBoxItem { Content = group.Name, Tag = group.Name });
			}
			itemsSignature = signature;
		}
		int index = 0;
		for (int i = 0; i < combo.Items.Count; i++)
		{
			string tag = ((combo.Items[i] as ComboBoxItem)?.Tag as string) ?? "";
			if (string.Equals(tag, current, StringComparison.OrdinalIgnoreCase))
			{
				index = i;
				break;
			}
		}
		if (combo.SelectedIndex != index)
		{
			combo.SelectedIndex = index;
		}
	}

	/// <summary>快速启动里分组/角色/端口一变，实例多开的卡片跟着刷新一次。</summary>
	private void OnSettingsSaved(AppSettings settings)
	{
		// 批量操作 / 「自启动」这类只改标记不改卡片的保存，不要触发整页重建：
		// 以前每张卡保存一次就 RebuildTiles 一次，15 张卡就是 15 次全量重建，
		// 表现就是「点一下自启动，卡片闪一下并卡顿」。
		if (_batchRunning || _muteSavedRefresh)
		{
			return;
		}
		_signature = "";
		_queue.TryEnqueue(Refresh);
	}

	/// <summary>静默保存：保存期间不让 OnSettingsSaved 触发整页重建。</summary>
	private void SaveSettingsQuiet()
	{
		_muteSavedRefresh = true;
		try
		{
			SettingsManager.Save(SettingsManager.Instance);
		}
		finally
		{
			_muteSavedRefresh = false;
		}
	}

	/// <summary>高级信息两行的显隐（默认关闭）。</summary>
	private void ApplyAdvancedVisibility()
	{
		bool on = SettingsManager.Instance.ShowAdvancedSessionInfo;
		if (_summaryDetail != null)
		{
			_summaryDetail.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
		}
		// 「0 失落世界 | 0 元素之诗」已经并进主摘要行，这一行不再单独显示。
		if (_serverSummary != null)
		{
			_serverSummary.Visibility = Visibility.Collapsed;
		}
	}

	/// <summary>各服务器已进服的机器人数量，如「0 失落世界 | 2 元素之诗」。</summary>
	private static string BuildServerSummary(
		List<NexusEndpointService.Endpoint> visible,
		Dictionary<int, ProxyCaptureService.TrafficInfo> traffic)
	{
		var order = new List<string>();
		var connected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (NexusEndpointService.Endpoint e in visible)
		{
			// 优先用服务器预设名（元素之诗 / 失落世界）；ServerHint 在分组角色上常为空，
			// 直接回退 Host 会把一长串域名显示在摘要里，看起来像乱码/错位。
			string name = ServerPresets.All
				.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host)
					&& string.Equals(p.Host, e.Host, StringComparison.OrdinalIgnoreCase))?.Name ?? "";
			if (string.IsNullOrWhiteSpace(name))
			{
				name = (e.ServerHint ?? "").Trim();
			}
			if (string.IsNullOrWhiteSpace(name))
			{
				name = string.IsNullOrWhiteSpace(e.Host) ? "未知服务器" : e.Host;
			}
			if (!connected.ContainsKey(name))
			{
				connected[name] = 0;
				order.Add(name);
			}
			if (traffic.TryGetValue(e.ListenPort, out ProxyCaptureService.TrafficInfo? t) && t.Sessions > 0)
			{
				connected[name]++;
			}
		}
		if (order.Count == 0)
		{
			return "";
		}
		return string.Join(" | ", order.Select(n => connected[n] + " " + n));
	}

	/// <summary>摘要行：各服务器已进服数 + 总进服数，如「0 失落世界 | 2 元素之诗 | 2/5 已进服」。</summary>
	private static string BuildSummaryLine(
		List<NexusEndpointService.Endpoint> cards,
		Dictionary<int, ProxyCaptureService.TrafficInfo> traffic,
		int connected)
	{
		string servers = BuildServerSummary(cards, traffic);
		string total = connected + "/" + cards.Count + " 已进服";
		return servers.Length == 0 ? total : servers + " | " + total;
	}

	/// <summary>
	/// 第一次点「实例多开」：未同意过责任声明就弹一次协议（全局唯一入口）。
	/// 不同意时右侧内容页保持锁定。
	/// </summary>
	private async Task HandleEntryAsync()
	{
		if (_entryHandled)
		{
			return;
		}
		_entryHandled = true;
		if (SettingsManager.Instance.NexusRiskAccepted)
		{
			ApplyLockState(true);
			return;
		}
		await ShowAgreementAsync(false);
	}

	/// <summary>第一次打开「会话多开」时弹一次的说明（文案见 <see cref="NexusNotice"/>）。</summary>
	private async Task<bool> ShowAgreementAsync(bool force)
	{
		if (!force && SettingsManager.Instance.NexusRiskAccepted)
		{
			return true;
		}
		if (XamlRoot == null)
		{
			return false;
		}
		var dialog = new ContentDialog
		{
			Title = "使用会话多开 / 机器人号代理",
			Content = NexusNotice.BuildAgreementContent(),
			PrimaryButtonText = "我已了解，继续",
			CloseButtonText = "暂不使用",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = XamlRoot
		};
		ContentDialogResult result = await DialogGate.ShowAsync(dialog);
		if (result != ContentDialogResult.Primary)
		{
			ApplyLockState(false);
			return false;
		}
		AppSettings cfg = SettingsManager.Instance;
		cfg.NexusRiskAccepted = true;
		SettingsManager.Save(cfg);
		ApplyLockState(true);
		_signature = "";
		Refresh();
		return true;
	}

	/// <summary>未同意协议时锁定右侧内容区。</summary>
	private void ApplyLockState(bool accepted)
	{
		if (_contentScroll != null)
		{
			_contentScroll.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
		}
		if (_lockPanel != null)
		{
			_lockPanel.Visibility = accepted ? Visibility.Collapsed : Visibility.Visible;
		}
		if (!accepted)
		{
			_summary.Text = "尚未同意使用前提协议：右侧内容页已隐藏。";
		}
	}

	private Border BuildLockPanel()
	{
		var panel = new StackPanel
		{
			Spacing = 10,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			MaxWidth = 560
		};
		panel.Children.Add(new TextBlock
		{
			Text = "使用前提协议未同意",
			FontSize = 20,
			FontWeight = FontWeights.SemiBold,
			TextAlignment = TextAlignment.Center
		});
		panel.Children.Add(new TextBlock
		{
			Text = "会话多开与机器人号代理需要先阅读并同意使用前提。"
				+ "同意后才会显示角色卡片。",
			FontSize = 13,
			Opacity = 0.72,
			TextWrapping = TextWrapping.Wrap,
			TextAlignment = TextAlignment.Center
		});
		var agree = Button("阅读并同意使用前提", () => _ = ShowAgreementAsync(true));
		agree.HorizontalAlignment = HorizontalAlignment.Center;
		panel.Children.Add(agree);
		return new Border
		{
			CornerRadius = new CornerRadius(10),
			BorderThickness = new Thickness(1),
			BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
			Background = CardBrush(),
			Padding = new Thickness(24),
			MinHeight = 260,
			Visibility = Visibility.Collapsed,
			Child = panel
		};
	}

	private Grid BuildShell()
	{
		var root = new Grid { Padding = new Thickness(18, 10, 14, 10), RowSpacing = 8 };
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

		var header = new Grid { RowSpacing = 8 };
		header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		var topRow = new Grid();
		topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

		// 标题去掉：窗口变窄时星号列会被压到 0，28px 的「会话多开」会和右侧
		// 控件重叠成一片（截图里标题被勾选框压住就是这个原因）。页面由左侧
		// 导航标识位置，这里只留一行操作控件，窄窗口也不会再叠字。
		var spacer = new Border { Height = 1 };
		Grid.SetColumn(spacer, 0);
		topRow.Children.Add(spacer);

		// 第一行：批量按钮 + 行情角色/显示组（原来全塞一行，最右侧「显示组」被卡片区
		// 裁掉一半，就是截图里的右侧沉边）。勾选类控件下移到第二行，第一行留足空间。
		var batchBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };

		var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
		// 第二行左侧：勾选类控件（与右侧刷新/复制按钮同一行）
		var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
		_autoStartCheck = new CheckBox
		{
			Content = "自启动机器人",
			FontSize = 13,
			IsChecked = SettingsManager.Instance.AutoStartBotOnStartup,
			VerticalAlignment = VerticalAlignment.Center
		};
		_autoStartCheck.Click += (_, _) =>
		{
			AppSettings cfg = SettingsManager.Instance;
			// 机器人代理自启总开关（与快速启动/代理启动页的服务器代理自启完全独立）；
			// 具体恢复哪些机器人由每张卡片上的「自启动」勾选决定。
			// 这里只落一个布尔值，不涉及卡片数据 —— 用静默保存，避免整页重建卡一下。
			cfg.AutoStartBotOnStartup = _autoStartCheck?.IsChecked == true;
			SaveSettingsQuiet();
			int cleared = 0;
			if (!cfg.AutoStartBotOnStartup)
			{
				// 关闭总开关 = 用户明确不要机器人：立刻清掉游戏内模组里的代理卡片，
				// 不能只改一个布尔值、让客户端下次启动还挂着一堆指向本地端口的账号。
				try
				{
					cleared = NexusEndpointService.ClearAllBotProxyCards();
				}
				catch (Exception exClean)
				{
					LogHelper.Log("[实例多开] 关闭自启动时清理游戏内代理卡片失败：" + exClean.Message);
				}
			}
			LogHelper.Log("[实例多开] 程序启动时自动机器人代理 = " + cfg.AutoStartBotOnStartup
				+ (cfg.AutoStartBotOnStartup ? "（按每张卡片的「自启动」勾选恢复）" : "（已关闭，并清理游戏内代理卡片 " + cleared + " 条）"));
			MainWindow.Instance?.ShowToast(cfg.AutoStartBotOnStartup
				? "已开启：程序启动时按卡片「自启动」勾选恢复机器人"
				: ("已关闭：下次启动不自动恢复机器人"
					+ (cleared > 0 ? "，已清理游戏内代理卡片 " + cleared + " 条" : "")), true);
			Refresh();
		};
		// 批量操作：勾选卡片后可一次启动 / 停止 / 重启 / 设置自启动
		_selectAllButton = new Button
		{
			Content = "全选角色",
			FontSize = 13,
			MinHeight = 0,
			Padding = new Thickness(12, 4, 12, 4),
			VerticalAlignment = VerticalAlignment.Center
		};
		ToolTipService.SetToolTip(_selectAllButton, "全选当前显示的角色卡片；已全选时再点一次取消全选");
		_selectAllButton.Click += (_, _) =>
		{
			bool all = _lastVisible.Count > 0 && _lastVisible.All(e => _checkedPorts.Contains(e.ListenPort));
			bool on = !all;
			_checkedPorts.Clear();
			if (on)
			{
				foreach (NexusEndpointService.Endpoint e in _lastVisible)
				{
					_checkedPorts.Add(e.ListenPort);
				}
			}
			foreach (CheckBox box in _headChecks.Values.SelectMany(v => v))
			{
				box.IsChecked = on;
			}
			SyncSelectAllCheckState();
		};
		checks.Children.Add(_selectAllButton);
		// 自启动全选：一键勾选/取消所有卡片的「自启动」（放在全选与批量按钮之间的空处）
		_autoAllCheck = new CheckBox
		{
			Content = "自启动全选",
			FontSize = 13,
			VerticalAlignment = VerticalAlignment.Center
		};
		ToolTipService.SetToolTip(_autoAllCheck, "一键勾选/取消当前所有卡片的「自启动」");
		_autoAllCheck.Click += (_, _) => SetAutoStartForAllVisible(_autoAllCheck?.IsChecked == true);
		checks.Children.Add(_autoAllCheck);
		// 「自启动机器人」放到「全显日志」左边（与总开关语义保持一致）
		checks.Children.Add(_autoStartCheck);
		// 全显日志：不满时全显，已全显时全部收起（三态观感，但只有一个勾选框）
		_logAllCheck = new CheckBox
		{
			Content = "全显日志",
			FontSize = 13,
			VerticalAlignment = VerticalAlignment.Center
		};
		ToolTipService.SetToolTip(_logAllCheck, "展开所有卡片的日志区；再点一次全部收起");
		_logAllCheck.Click += (_, _) => ToggleAllLogs(_logAllCheck?.IsChecked == true);
		checks.Children.Add(_logAllCheck);
		// 高级信息：监听数 / 上下行包 / 各服务器进服数，默认关闭
		_advancedCheck = new CheckBox
		{
			Content = "高级信息",
			FontSize = 13,
			IsChecked = SettingsManager.Instance.ShowAdvancedSessionInfo,
			VerticalAlignment = VerticalAlignment.Center
		};
		ToolTipService.SetToolTip(_advancedCheck, "显示监听数量、上下行流量包与各服务器已进服数量");
		_advancedCheck.Click += (_, _) =>
		{
			SettingsManager.Instance.ShowAdvancedSessionInfo = _advancedCheck?.IsChecked == true;
			SaveSettingsQuiet();
			ApplyAdvancedVisibility();
			Refresh();
		};
		checks.Children.Add(_advancedCheck);
		batchBar.Children.Add(SmallButton("批量启动", () => BatchStartStop(true, false)));
		batchBar.Children.Add(SmallButton("批量停止", () => BatchStartStop(false, false)));
		batchBar.Children.Add(SmallButton("批量重启", () => BatchStartStop(false, true)));
		// 机器人只能挂在带 MythosNexus 的挂号版客户端里；普通版没有该模组，写账号也不会出现机器人。
		if (NexusEndpointService.ResolveHostVersionDir("").Length > 0)
		{
			batchBar.Children.Add(SmallButton("启动挂号版", () => _ = LaunchHostClientAsync()));
		}
		actions.Children.Add(new TextBlock
		{
			Text = "行情角色",
			FontSize = 12.5,
			Opacity = 0.75,
			VerticalAlignment = VerticalAlignment.Center
		});
		_marketCombo = new ComboBox
		{
			FontSize = 12,
			MinWidth = 150,
			VerticalAlignment = VerticalAlignment.Center,
			PlaceholderText = "不指定"
		};
		_marketCombo.DropDownOpened += (_, _) => _marketKeyOnOpen = (_marketCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
		_marketCombo.DropDownClosed += (_, _) =>
		{
			// 重填下拉项导致的被动关闭不保存，否则会把行情角色误写成「不指定」。
			if (_fillingRoleCombo)
			{
				return;
			}
			string now = (_marketCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
			if (!string.Equals(now, _marketKeyOnOpen, StringComparison.OrdinalIgnoreCase))
			{
				OnMarketComboChanged();
			}
		};
		ApplyOpaqueDropDown(_marketCombo);
		actions.Children.Add(_marketCombo);
		actions.Children.Add(new TextBlock
		{
			Text = "显示组",
			FontSize = 12.5,
			Opacity = 0.75,
			VerticalAlignment = VerticalAlignment.Center
		});
		_groupCombo = new ComboBox
		{
			FontSize = 12,
			MinWidth = 130,
			VerticalAlignment = VerticalAlignment.Center
		};
		_groupCombo.SelectionChanged += (_, _) => OnDisplayGroupChanged();
		ApplyOpaqueDropDown(_groupCombo);
		actions.Children.Add(_groupCombo);
		RefreshGroupCombo();
		Grid.SetColumn(actions, 1);
		topRow.Children.Add(actions);
		Grid.SetColumn(batchBar, 0);
		topRow.Children.Add(batchBar);
		Grid.SetRow(topRow, 0);
		header.Children.Add(topRow);

		// 第二行右侧：刷新角色卡片 / 模组复制到桌面
		var toolbar = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center
		};
		toolbar.Children.Add(Button("刷新角色卡片", () =>
		{
			RefreshGroupCombo();
			_roleComboSignature = "";
			_signature = "";
			Refresh();
			MainWindow.Instance?.ShowToast("已刷新角色卡片");
		}));
		toolbar.Children.Add(Button("模组复制到桌面", () =>
		{
			string message = ModExporter.CopyToDesktop(overwrite: true);
			MainWindow.Instance?.ShowToast(message, ok: message.StartsWith("已复制", StringComparison.Ordinal));
		}));
		// 第二行：左侧勾选类控件，右侧刷新/复制（同一行，互不挤压）
		var secondRow = new Grid();
		secondRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		secondRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		Grid.SetColumn(checks, 0);
		secondRow.Children.Add(checks);
		Grid.SetColumn(toolbar, 1);
		secondRow.Children.Add(toolbar);
		Grid.SetRow(secondRow, 1);
		header.Children.Add(secondRow);

		Grid.SetRow(header, 0);
		root.Children.Add(header);

		// 摘要区：默认只显示「已进服数/总数」；详细统计（监听数、上下行包、各服务器
		// 进服数）由第二行的「高级信息」勾选后才展开。
		_summary = new TextBlock
		{
			Text = "正在读取角色端口…",
			FontSize = 13,
			Opacity = 0.9,
			TextWrapping = TextWrapping.Wrap
		};
		_summaryDetail = new TextBlock
		{
			FontSize = 12.5,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap,
			Visibility = Visibility.Collapsed
		};
		_serverSummary = new TextBlock
		{
			FontSize = 12.5,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap,
			Visibility = Visibility.Collapsed
		};
		var summaryPanel = new StackPanel { Spacing = 2 };
		summaryPanel.Children.Add(_summary);
		summaryPanel.Children.Add(_summaryDetail);
		summaryPanel.Children.Add(_serverSummary);
		Grid.SetRow(summaryPanel, 1);
		root.Children.Add(summaryPanel);

		_tiles = new StackPanel { Spacing = 10, Margin = new Thickness(0, 2, 0, 0) };
		// 角色多时卡片需要完整高度（含底部操作按钮），这里改成可竖向滚动、每组自适应高度。
		_contentScroll = new ScrollViewer
		{
			Content = _tiles,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollMode = ScrollMode.Enabled,
			HorizontalScrollMode = ScrollMode.Disabled,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		};
		Grid.SetRow(_contentScroll, 2);
		root.Children.Add(_contentScroll);

		_lockPanel = BuildLockPanel();
		Grid.SetRow(_lockPanel, 2);
		root.Children.Add(_lockPanel);
		ApplyLockState(SettingsManager.Instance.NexusRiskAccepted);
		return root;
	}

	/// <summary>刷新「显示组」下拉（DisplayGroupName 与快速启动共用）。</summary>
	private void RefreshGroupCombo()
	{
		if (_groupCombo == null)
		{
			return;
		}
		try
		{
			List<ProxyGroup> groups = (SettingsManager.Instance.ProxyGroups ?? new List<ProxyGroup>())
				.Where(g => !string.IsNullOrWhiteSpace(g.Name))
				.ToList();
			string current = SettingsManager.Instance.DisplayGroupName ?? "";
			string signature = "全部组\u0001" + string.Join("\u0001", groups.Select(g => g.Name));
			_groupComboSyncing = true;
			try
			{
				FillGroupCombo(_groupCombo, groups, current, ref _groupItemsSignature);
			}
			finally
			{
				_groupComboSyncing = false;
			}
		}
		catch (Exception ex)
		{
			_groupComboSyncing = false;
			LogHelper.Log("[实例多开] 刷新显示组下拉失败：" + ex.Message);
		}
	}

	private void OnDisplayGroupChanged()
	{
		if (_groupComboSyncing || _groupApplyQueued)
		{
			return;
		}
		// 不能在 ComboBox.SelectionChanged 里清空/重建 Items（WinUI 会闪退），
		// 统一挪到下一个 UI 队列里执行。
		_groupApplyQueued = true;
		if (!_queue.TryEnqueue(() =>
		{
			_groupApplyQueued = false;
			ApplyDisplayGroup();
		}))
		{
			_groupApplyQueued = false;
		}
	}

	private void ApplyDisplayGroup()
	{
		try
		{
			string name = ((_groupCombo?.SelectedItem as ComboBoxItem)?.Tag as string) ?? "";
			AppSettings cfg = SettingsManager.Instance;
			if (!string.Equals(cfg.DisplayGroupName ?? "", name, StringComparison.OrdinalIgnoreCase))
			{
				cfg.DisplayGroupName = name;
				SettingsManager.Save(cfg);
			}
			_signature = "";
			Refresh();
			// 换组 = 换挂机名单：组内的机器人账号补写进客户端，组外的立刻撤下，
			// 否则上一组挂过的机器人会一直留在游戏里（「莫名冒出来的角色」就是这条）。
			NexusEndpointService.SyncClientAccountsToDisplayGroup();
			MainWindow.Instance?.ShowToast(string.IsNullOrWhiteSpace(name) ? "实例多开：显示全部组" : "实例多开：显示组 " + name);
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 切换显示组失败：" + ex.Message);
			MainWindow.Instance?.ShowToast("切换显示组失败：" + ex.Message, ok: false);
		}
	}

	/// <summary>把快速启动组展开成角色键（account@host:port），与 NexusEndpoints 的键一致。</summary>
	private static HashSet<string> GroupRoleKeys(ProxyGroup group)
	{
		return new HashSet<string>(GroupRoleKeysInOrder(group), StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>按组内角色顺序返回角色键，行情角色默认取这个顺序的第一个。</summary>
	private static List<string> GroupRoleKeysInOrder(ProxyGroup group)
	{
		var keys = new List<string>();
		int index = 0;
		foreach (ProxyGroupRole role in group.Roles ?? new List<ProxyGroupRole>())
		{
			int port = role.ListenPort > 0 ? role.ListenPort : (group.BasePort > 0 ? group.BasePort + index : 20000 + index);
			string server = string.IsNullOrWhiteSpace(role.ServerName) ? group.ServerName : role.ServerName;
			string host = server ?? "";
			int serverPort = 25565;
			int colon = host.LastIndexOf(':');
			if (colon > 0 && int.TryParse(host.Substring(colon + 1), out int parsed))
			{
				serverPort = parsed;
				host = host.Substring(0, colon);
			}
			keys.Add(role.Account + "@" + host + ":" + serverPort);
			index++;
		}
		return keys;
	}

	private void Refresh()
	{
		try
		{
			bool accepted = SettingsManager.Instance.NexusRiskAccepted;
			ApplyLockState(accepted);
			if (!accepted)
			{
				return;
			}
			string gameDir = NexusEndpointService.ResolveGameDir();
			_gameDir = gameDir;
			if (_autoStartCheck != null)
			{
				// 必须读机器人总开关：上次改成 AutoStartBotOnStartup 后这里漏改，
				// Refresh 每 2 秒用服务器代理开关的值把勾选框弹回去，表现就是「点不动」。
				bool auto = SettingsManager.Instance.AutoStartBotOnStartup;
				if (_autoStartCheck.IsChecked != auto)
				{
					_autoStartCheck.IsChecked = auto;
				}
			}
			string groupName = SettingsManager.Instance.DisplayGroupName ?? "";
			string groupListSignature = string.Join(",", (SettingsManager.Instance.ProxyGroups ?? new List<ProxyGroup>()).Select(g => g.Name));
			if (string.Equals(_groupSignature, groupName, StringComparison.OrdinalIgnoreCase) == false
				|| !string.Equals(_groupListSignature, groupListSignature, StringComparison.Ordinal)
				|| _groupCombo?.Items.Count == 0)
			{
				_groupSignature = groupName;
				_groupListSignature = groupListSignature;
				RefreshGroupCombo();
			}
			List<NexusEndpointService.Endpoint> endpoints = string.IsNullOrWhiteSpace(gameDir)
				? new List<NexusEndpointService.Endpoint>()
				: NexusEndpointService.Load(gameDir);
			List<NexusEndpointService.Endpoint> all = endpoints
				.Where(e => e.ListenPort > 0)
				.OrderBy(e => e.ListenPort)
				.ToList();
			if (all.Count == 0)
			{
				all = endpoints.OrderBy(e => e.ListenPort).ToList();
			}
			// 行情角色跟随显示组：换组时自动切到该组记住的角色（没记录就用组内第一个）。
			EnsureMarketRoleForGroup(groupName, endpoints);
			SyncRoleCombos(groupName, endpoints);

			IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> active = ProxyCaptureService.ActiveBindingsSnapshot();
			var listeningPorts = new HashSet<int>(active.Select(b => b.ListenPort));
			// 排序口径：按服务器分组，角色最多的服务器排最前；组内按监听端口升序。
			List<NexusEndpointService.Endpoint> visible = OrderForDisplay(FilterEndpointsForGroup(all, groupName));
			Dictionary<int, ProxyCaptureService.TrafficInfo> traffic = ProxyCaptureService.TrafficSnapshot()
				.GroupBy(t => t.ListenPort)
				.ToDictionary(g => g.Key, g => g.First());
			// 摘要必须跟当前「显示组」走：旧实现用 all（全部组/全部历史端口），
			// 选了一个只有 1 个角色的组仍显示 0/19，看起来完全不合理。
			// 保留原来的数字格式「0 元素之诗 | 0/1 已进服」，只把统计范围修正确。
			List<NexusEndpointService.Endpoint> visibleDistinct = visible
				.GroupBy(e => e.ListenPort)
				.Select(g => g.First())
				.ToList();
			int listening = visibleDistinct.Count(e => listeningPorts.Contains(e.ListenPort));
			int connected = visibleDistinct.Count(e => IsConnected(traffic, e.ListenPort));
			long upBytes = traffic.Values.Sum(t => t.UpBytes);
			long downBytes = traffic.Values.Sum(t => t.DownBytes);
			long upPackets = traffic.Values.Sum(t => t.UpPackets);
			long downPackets = traffic.Values.Sum(t => t.DownPackets);
			string groupHint = string.IsNullOrWhiteSpace(groupName) ? "" : "（显示组：" + groupName + "）";
			// 主摘要：各服务器已进服数 + 总进服数（如「0 失落世界 | 2 元素之诗 | 2/5 已进服」）。
			_summary.Text = visibleDistinct.Count == 0
				? groupHint + "未启动机器人：没有匹配的角色端口，请到「快速启动」给角色分配 127.0.0.1 端口并勾选启用。"
				: BuildSummaryLine(visibleDistinct, traffic, connected);
			int disabledCount = visibleDistinct.Count(e => !e.Enabled);
			_summaryDetail.Text = "监听 " + listening + "/" + visibleDistinct.Count + "｜上行 " + FormatBytes(upBytes)
				+ " / " + upPackets + " 包｜下行 " + FormatBytes(downBytes) + " / " + downPackets + " 包"
				+ (disabledCount > 0 ? "｜已停用 " + disabledCount + " 张" : "") + groupHint;
			_serverSummary.Text = "";
			ApplyAdvancedVisibility();
			string signature = groupName + "|" + SettingsManager.Instance.ShowAdvancedSessionInfo
				+ "|" + string.Join("|", visible.Select(e => e.Key + "#" + e.ListenPort + "#" + e.Enabled + "#" + listeningPorts.Contains(e.ListenPort)));
			SyncAutoAllCheckState();
			SyncLogAllCheckState();
			if (signature == _signature && _tiles.Children.Count > 0)
			{
				UpdateLive(traffic, listeningPorts);
				return;
			}
			_signature = signature;

			RebuildTiles(visible, active);
			UpdateLive(traffic, listeningPorts);
			SyncAutoAllCheckState();
			SyncLogAllCheckState();
		}
		catch (Exception ex)
		{
			_summary.Text = "读取角色端口失败：" + ex.Message;
		}
	}

	/// <summary>
	/// 刷新「行情角色」下拉框。选项只来自当前显示组内的角色 + 本地端口，
	/// 换组时强制重建，这样行情角色不会一直停在第一个组。
	/// 只在花名册/分组变化时重建，避免 2 秒刷新把用户正在展开的下拉框关掉。
	/// </summary>
	private void SyncRoleCombos(string groupName, List<NexusEndpointService.Endpoint> endpoints)
	{
		// 用户正在展开下拉框时绝不重建，否则列表被清空、弹层被关掉，表现就是「选不中」。
		if (_marketCombo?.IsDropDownOpen == true)
		{
			return;
		}
		List<NexusEndpointService.Endpoint> pool = FilterEndpointsForGroup(endpoints, groupName);
		// 按 Key 排序后再算签名：花名册每次加载顺序不完全稳定，不排序会导致 2 秒一次的重建。
		string signature = groupName + "|" + string.Join("|", pool
			.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
			.Select(e => e.Key + "#" + e.ListenPort + "#" + e.RoleName));
		if (signature == _roleComboSignature)
		{
			return;
		}
		_roleComboSignature = signature;
		FillRoleCombo(_marketCombo, pool, SettingsManager.Instance.MarketAccountKey);
	}

	/// <summary>按「显示组」筛出组内角色；空组名 = 全部角色。</summary>
	private static List<NexusEndpointService.Endpoint> FilterEndpointsForGroup(List<NexusEndpointService.Endpoint> endpoints, string groupName)
	{
		if (string.IsNullOrWhiteSpace(groupName))
		{
			return endpoints;
		}
		ProxyGroup? group = (SettingsManager.Instance.ProxyGroups ?? new List<ProxyGroup>())
			.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
		if (group == null)
		{
			return new List<NexusEndpointService.Endpoint>();
		}
		HashSet<string> keys = GroupRoleKeys(group);
		return endpoints.Where(e => keys.Contains(e.Key)).ToList();
	}

	/// <summary>
	/// 行情角色随「显示组」变化：每组单独记住一个指定角色，换组时自动恢复；
	/// 该组从没指定过则默认用组内第一个角色。只在换组后的第一次刷新执行，避免死循环保存。
	/// </summary>
	private void EnsureMarketRoleForGroup(string groupName, List<NexusEndpointService.Endpoint> endpoints)
	{
		if (string.Equals(_marketGroupSignature, groupName, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		// 页面刚打开时保留原有指定（只要它属于当前组），避免一进页面就把用户原来的行情角色换掉；
		// 之后用户主动换组才按「该组记住的 / 组内第一个」切换。
		bool firstSync = _marketGroupSignature == "\u0002";
		_marketGroupSignature = groupName;
		AppSettings cfg = SettingsManager.Instance;
		cfg.MarketAccountKeyByGroup ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		List<NexusEndpointService.Endpoint> pool = FilterEndpointsForGroup(endpoints, groupName);
		string key;
		bool changed = false;
		if (!string.IsNullOrWhiteSpace(groupName))
		{
			if (cfg.MarketAccountKeyByGroup.TryGetValue(groupName, out string? remembered))
			{
				// 记住过「不指定」就保持不指定；记住的角色已不存在才回退到组内第一个角色。
				key = string.IsNullOrWhiteSpace(remembered)
					? ""
					: (pool.Any(e => string.Equals(e.Key, remembered, StringComparison.OrdinalIgnoreCase)) ? remembered : PickGroupMarketRole(pool, groupName));
			}
			else if (firstSync && pool.Any(e => string.Equals(e.Key, cfg.MarketAccountKey ?? "", StringComparison.OrdinalIgnoreCase)))
			{
				key = cfg.MarketAccountKey ?? "";
			}
			else
			{
				// 这个组还从没指定过行情角色：默认用组内第一个角色，不再沿用上一组的选择。
				key = PickGroupMarketRole(pool, groupName);
			}
			// 组里还没有角色时不写入记忆，等这个组有角色后仍能自动选第一个。
			if (pool.Count > 0
				&& !string.Equals(cfg.MarketAccountKeyByGroup.TryGetValue(groupName, out string? stored) ? stored : "\u0002", key, StringComparison.OrdinalIgnoreCase))
			{
				cfg.MarketAccountKeyByGroup[groupName] = key;
				changed = true;
			}
		}
		else
		{
			// 全部组：保留当前指定（有效时），否则退回不指定。
			key = pool.Any(e => string.Equals(e.Key, cfg.MarketAccountKey ?? "", StringComparison.OrdinalIgnoreCase))
				? (cfg.MarketAccountKey ?? "")
				: "";
		}
		if (!string.Equals(cfg.MarketAccountKey ?? "", key, StringComparison.OrdinalIgnoreCase))
		{
			cfg.MarketAccountKey = key;
			changed = true;
		}
		if (changed)
		{
			SettingsManager.Save(cfg);
			LogHelper.Log("[实例多开] 行情角色跟随显示组 → "
				+ (string.IsNullOrWhiteSpace(groupName) ? "全部组" : groupName)
				+ "：" + (string.IsNullOrWhiteSpace(key) ? "不指定" : key));
			// 换组后立刻把新的行情角色写给客户端，避免行情重连还指向上一组的端口。
			try
			{
				string dir = string.IsNullOrWhiteSpace(_gameDir) ? NexusEndpointService.ResolveGameDir() : _gameDir;
				if (!string.IsNullOrWhiteSpace(dir))
				{
					NexusEndpointService.ApplyRoleFiles(
						NexusEndpointService.Load(dir),
						ProxyCaptureService.ActiveBindingsSnapshot().Select(b => b.ListenPort).ToList());
				}
			}
			catch (Exception ex)
			{
				LogHelper.Log("[实例多开] 应用行情角色失败：" + ex.Message);
			}
		}
	}

	private static string PickGroupMarketRole(List<NexusEndpointService.Endpoint> pool, string groupName)
	{
		// 优先按「快速启动组」里的角色顺序取第一个，和用户看到的组内顺序一致。
		ProxyGroup? group = (SettingsManager.Instance.ProxyGroups ?? new List<ProxyGroup>())
			.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
		if (group != null)
		{
			foreach (string key in GroupRoleKeysInOrder(group))
			{
				NexusEndpointService.Endpoint? match = pool.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
				if (match != null && match.Enabled && match.ListenPort > 0)
				{
					return match.Key;
				}
			}
		}
		NexusEndpointService.Endpoint? pick = pool.FirstOrDefault(e => e.Enabled && e.ListenPort > 0) ?? pool.FirstOrDefault();
		return pick?.Key ?? "";
	}

	private void FillRoleCombo(ComboBox? combo, List<NexusEndpointService.Endpoint> endpoints, string selectedKey)
	{
		if (combo == null)
		{
			return;
		}
		var desired = new List<(string Content, string Tag)> { ("（不指定）", "") };
		foreach (NexusEndpointService.Endpoint e in endpoints)
		{
			string name = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
			desired.Add((name + " · " + e.ListenPort, e.Key));
		}
		bool same = combo.Items.Count == desired.Count;
		if (same)
		{
			for (int i = 0; i < desired.Count; i++)
			{
				if (combo.Items[i] is not ComboBoxItem item
					|| !string.Equals(item.Tag as string ?? "", desired[i].Tag, StringComparison.OrdinalIgnoreCase)
					|| !string.Equals(item.Content?.ToString() ?? "", desired[i].Content, StringComparison.Ordinal))
				{
					same = false;
					break;
				}
			}
		}
		if (!same)
		{
			// Items.Clear() 会强制关闭下拉框并触发 DropDownClosed，这里标记为程序化重填。
			_fillingRoleCombo = true;
			try
			{
				combo.Items.Clear();
				foreach ((string Content, string Tag) d in desired)
				{
					combo.Items.Add(new ComboBoxItem { Content = d.Content, Tag = d.Tag });
				}
			}
			finally
			{
				_fillingRoleCombo = false;
			}
		}
		int selected = 0;
		for (int i = 1; i < desired.Count; i++)
		{
			if (!string.IsNullOrWhiteSpace(selectedKey) && string.Equals(desired[i].Tag, selectedKey, StringComparison.OrdinalIgnoreCase))
			{
				selected = i;
				break;
			}
		}
		if (combo.SelectedIndex != selected)
		{
			combo.SelectedIndex = selected;
		}
	}

	private void OnMarketComboChanged()
	{
		string key = (_marketCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
		AppSettings cfg = SettingsManager.Instance;
		cfg.MarketAccountKey = key;
		// 按当前显示组记住这次选择，切组再切回来能恢复。
		string groupName = cfg.DisplayGroupName ?? "";
		if (!string.IsNullOrWhiteSpace(groupName))
		{
			cfg.MarketAccountKeyByGroup ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			cfg.MarketAccountKeyByGroup[groupName] = key;
		}
		SettingsManager.Save(cfg);
		LogHelper.Log("[实例多开] 行情角色"
			+ (string.IsNullOrWhiteSpace(groupName) ? "" : "（组 " + groupName + "）")
			+ " = " + (string.IsNullOrWhiteSpace(key) ? "不指定" : key));
		try
		{
			string dir = string.IsNullOrWhiteSpace(_gameDir) ? NexusEndpointService.ResolveGameDir() : _gameDir;
			if (!string.IsNullOrWhiteSpace(dir))
			{
				NexusEndpointService.ApplyRoleFiles(
					NexusEndpointService.Load(dir),
					ProxyCaptureService.ActiveBindingsSnapshot().Select(b => b.ListenPort).ToList());
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 应用角色设置失败：" + ex.Message);
		}
	}

	private void RebuildTiles(
		List<NexusEndpointService.Endpoint> endpoints,
		IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> active)
	{
		_tiles.Children.Clear();
		_live.Clear();
		_headChecks.Clear();
		_lastVisible = endpoints.ToList();
		// 卡片已经不存在的端口不必再记展开状态 / 勾选状态
		_logExpanded.RemoveWhere(port => endpoints.All(x => x.ListenPort != port));
		_checkedPorts.RemoveWhere(port => endpoints.All(x => x.ListenPort != port));
		FillPane(_tiles, endpoints, active);
		SyncSelectAllCheckState();
	}

	/// <summary>「全选角色」按钮文案跟随子卡片：全部勾上时显示「取消全选」。</summary>
	private void SyncSelectAllCheckState()
	{
		if (_selectAllButton == null)
		{
			return;
		}
		bool all = _lastVisible.Count > 0 && _lastVisible.All(e => _checkedPorts.Contains(e.ListenPort));
		string text = all ? "取消全选" : "全选角色";
		if (!string.Equals(_selectAllButton.Content as string, text, StringComparison.Ordinal))
		{
			_selectAllButton.Content = text;
		}
	}

	/// <summary>把一组卡片按服务器分组堆进卡片区。</summary>
	private void FillPane(
		StackPanel panel,
		List<NexusEndpointService.Endpoint> endpoints,
		IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> active)
	{
		if (endpoints.Count == 0)
		{
			var empty = new Border
			{
				CornerRadius = new CornerRadius(10),
				BorderThickness = new Thickness(1),
				BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
				Background = CardBrush(),
				MinHeight = 96,
				Child = new TextBlock
				{
					Text = "暂无角色卡片\n\n在「快速启动」里给角色分配本地端口并勾选启用后，这里会按服务器分组生成机器人代理卡片。",
					FontSize = 13,
					Opacity = 0.65,
					TextAlignment = TextAlignment.Center,
					TextWrapping = TextWrapping.Wrap,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				}
			};
			panel.Children.Add(empty);
			return;
		}
		// 按服务器分组堆叠：角色最多的服务器排最前，同组卡片紧挨在一起。
		foreach (IGrouping<string, NexusEndpointService.Endpoint> section in endpoints.GroupBy(ServerGroupName))
		{
			panel.Children.Add(BuildServerSection(section.Key, section.ToList(), active));
		}
	}

	/// <summary>卡片所属服务器名：优先服务器预设名，没有就用地址（去掉端口）。</summary>
	private static string ServerGroupName(NexusEndpointService.Endpoint e)
	{
		string name = (e.ServerHint ?? "").Trim();
		if (name.Length == 0)
		{
			name = StripPort(e.ServerKey);
		}
		return name.Length == 0 ? "未知服务器" : name;
	}

	/// <summary>
	/// 一个服务器分组：顶部一条可点击的收缩横栏 + 组内卡片网格。
	/// 点横栏收起/展开整组，带高度与透明度动画。
	/// </summary>
	private FrameworkElement BuildServerSection(
		string server,
		List<NexusEndpointService.Endpoint> list,
		IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> active)
	{
		var root = new StackPanel { Spacing = 6 };
		bool collapsed = _collapsedGroups.Contains(server);

		int columns = list.Count <= 1 ? 1 : (list.Count <= 4 ? 2 : 3);
		int rows = (int)Math.Ceiling(list.Count / (double)columns);
		var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
		for (int i = 0; i < columns; i++)
		{
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		}
		for (int i = 0; i < rows; i++)
		{
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		}
		for (int i = 0; i < list.Count; i++)
		{
			NexusEndpointService.Endpoint e = list[i];
			FrameworkElement tile = BuildTile(e, active.Any(b => b.ListenPort == e.ListenPort));
			tile.Tag = e.ListenPort;
			Grid.SetRow(tile, i / columns);
			Grid.SetColumn(tile, i % columns);
			grid.Children.Add(tile);
		}
		grid.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

		var chevron = new TextBlock
		{
			Text = collapsed ? "\u25B8" : "\u25BE",
			FontSize = 12,
			Opacity = 0.85,
			VerticalAlignment = VerticalAlignment.Center,
			Width = 14
		};
		var nameText = new TextBlock
		{
			Text = server,
			FontSize = 13,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		var countText = new TextBlock
		{
			Text = list.Count + " 个角色",
			FontSize = 11.5,
			Opacity = 0.62,
			VerticalAlignment = VerticalAlignment.Center
		};
		var headGrid = new Grid();
		headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		Grid.SetColumn(chevron, 0);
		headGrid.Children.Add(chevron);
		Grid.SetColumn(nameText, 1);
		nameText.Margin = new Thickness(6, 0, 0, 0);
		headGrid.Children.Add(nameText);
		Grid.SetColumn(countText, 2);
		headGrid.Children.Add(countText);

		var header = new Border
		{
			CornerRadius = new CornerRadius(8),
			BorderThickness = new Thickness(1),
			BorderBrush = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)),
			Background = SectionHeaderBrush(),
			Padding = new Thickness(10, 5, 10, 5),
			MinHeight = 30,
			Child = headGrid
		};
		ToolTipService.SetToolTip(header, collapsed ? "点一下展开「" + server + "」的角色卡片" : "点一下收起「" + server + "」的角色卡片");
		header.Tapped += (_, _) => ToggleSection(server, grid, chevron, header);

		root.Children.Add(header);
		root.Children.Add(grid);
		if (!collapsed)
		{
			AnimateCardsIn(grid);
		}
		return root;
	}

	/// <summary>收起 / 展开一个服务器分组：高度 + 透明度动画。</summary>
	private void ToggleSection(string server, FrameworkElement content, TextBlock chevron, Border header)
	{
		if (_sectionAnimating.Contains(server))
		{
			return;
		}
		_sectionAnimating.Add(server);
		bool expand = content.Visibility != Visibility.Visible;
		var story = new Storyboard();
		if (expand)
		{
			_collapsedGroups.Remove(server);
			content.Visibility = Visibility.Visible;
			content.Opacity = 0;
			content.MaxHeight = double.PositiveInfinity;
			double width = _tiles.ActualWidth > 40 ? _tiles.ActualWidth - 8 : 640;
			content.Measure(new Size(width, double.PositiveInfinity));
			double target = content.DesiredSize.Height;
			if (double.IsNaN(target) || double.IsInfinity(target) || target <= 0)
			{
				target = 220;
			}
			content.MaxHeight = 0;
			AddSectionAnimation(story, content, "MaxHeight", 0, target, 240, EasingMode.EaseOut);
			AddSectionAnimation(story, content, "Opacity", 0, 1, 200, EasingMode.EaseOut);
			story.Completed += (_, _) =>
			{
				content.MaxHeight = double.PositiveInfinity;
				content.Opacity = 1;
			};
			chevron.Text = "\u25BE";
		}
		else
		{
			_collapsedGroups.Add(server);
			double from = content.ActualHeight > 0 ? content.ActualHeight : 0;
			content.MaxHeight = from;
			AddSectionAnimation(story, content, "MaxHeight", from, 0, 200, EasingMode.EaseIn);
			AddSectionAnimation(story, content, "Opacity", 1, 0, 160, EasingMode.EaseIn);
			story.Completed += (_, _) =>
			{
				content.Visibility = Visibility.Collapsed;
				content.MaxHeight = double.PositiveInfinity;
				content.Opacity = 1;
			};
			chevron.Text = "\u25B8";
		}
		ToolTipService.SetToolTip(header, expand ? "点一下收起「" + server + "」的角色卡片" : "点一下展开「" + server + "」的角色卡片");
		story.Begin();
		// 动画结束后解除防抖（280ms 略长于最长动画 240ms）。
		_ = Task.Delay(280).ContinueWith(_ => _queue.TryEnqueue(() => _sectionAnimating.Remove(server)));
	}

	private static void AddSectionAnimation(Storyboard story, DependencyObject target, string property, double from, double to, int ms, EasingMode mode)
	{
		var anim = new DoubleAnimation
		{
			From = from,
			To = to,
			Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
			EasingFunction = new CubicEase { EasingMode = mode }
		};
		Storyboard.SetTarget(anim, target);
		Storyboard.SetTargetProperty(anim, property);
		story.Children.Add(anim);
	}

	/// <summary>新出现的卡片淡入并上移 10px（只在重建时跑一次，不影响 2 秒刷新）。</summary>
	private static void AnimateCardsIn(Panel grid)
	{
		foreach (UIElement child in grid.Children)
		{
			if (child is not FrameworkElement fe)
			{
				continue;
			}
			var transform = new TranslateTransform { Y = 10 };
			fe.RenderTransform = transform;
			fe.Opacity = 0;
			var story = new Storyboard();
			AddTileAnimation(story, transform, "Y", 10, 200);
			var fade = new DoubleAnimation
			{
				From = 0,
				To = 1,
				Duration = new Duration(TimeSpan.FromMilliseconds(180)),
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			Storyboard.SetTarget(fade, fe);
			Storyboard.SetTargetProperty(fade, "Opacity");
			story.Children.Add(fade);
			story.Begin();
		}
	}

	/// <summary>按服务器分组排序：角色最多的服务器排最前，组内按监听端口升序。</summary>
	private static List<NexusEndpointService.Endpoint> OrderForDisplay(List<NexusEndpointService.Endpoint> visible)
	{
		return visible
			.GroupBy(ServerGroupName)
			.OrderByDescending(g => g.Count())
			.ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
			.SelectMany(g => g.OrderBy(e => e.ListenPort))
			.ToList();
	}

	private static void AddTileAnimation(Storyboard story, DependencyObject target, string property, double from, int ms)
	{
		var anim = new DoubleAnimation
		{
			From = from,
			To = 0,
			Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
		};
		Storyboard.SetTarget(anim, target);
		Storyboard.SetTargetProperty(anim, property);
		story.Children.Add(anim);
	}

	private FrameworkElement BuildTile(NexusEndpointService.Endpoint e, bool listening)
	{
			var border = new Border
			{
				CornerRadius = new CornerRadius(10),
				BorderThickness = new Thickness(1),
				BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
				Background = CardBrush(),
				Padding = new Thickness(12),
				MinHeight = SettingsManager.Instance.ShowAdvancedSessionInfo ? 168 : 132
			};

			var grid = new Grid { RowSpacing = 6 };
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		var head = new Grid();
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		// 批量操作勾选框（与快速启动页的分组勾选同一交互）
		var pick = new CheckBox
		{
			// 勾选状态放在 _checkedPorts 里：卡片重建（每 2 秒可能的整批刷新）不再清空用户的勾选。
			IsChecked = _checkedPorts.Contains(e.ListenPort),
			MinWidth = 0,
			Padding = new Thickness(0),
			Margin = new Thickness(0, 0, 6, 0),
			VerticalAlignment = VerticalAlignment.Center
		};
		ToolTipService.SetToolTip(pick, "勾选后可批量启动 / 停止 / 重启 / 自启动");
		pick.Click += (_, _) =>
		{
			if (pick.IsChecked == true)
			{
				_checkedPorts.Add(e.ListenPort);
			}
			else
			{
				_checkedPorts.Remove(e.ListenPort);
			}
			SyncSelectAllCheckState();
		};
		if (!_headChecks.TryGetValue(e.ListenPort, out List<CheckBox>? pickList))
		{
			pickList = new List<CheckBox>();
			_headChecks[e.ListenPort] = pickList;
		}
		pickList.Add(pick);
		head.Children.Add(pick);
		var titleText = new TextBlock
		{
			// 卡片标题：角色名 · 服务器名（服务器名从第二行挪到标题右侧）
			Text = CardTitle(e),
			FontSize = 15,
			FontWeight = FontWeights.SemiBold,
			TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(titleText, 1);
		head.Children.Add(titleText);
		var badgeText = new TextBlock
		{
			Text = listening ? "监听中" : "",
			FontSize = 11,
			VerticalAlignment = VerticalAlignment.Center
		};
		// 右上角只允许出现「监听中 / 已进服」，其余状态（已连接、已断开、踢出原因等）一律不显示。
		var state = new Border
		{
			CornerRadius = new CornerRadius(10),
			Padding = new Thickness(9, 3, 9, 3),
			Background = new SolidColorBrush(Color.FromArgb(60, 40, 170, 90)),
			Child = badgeText,
			// 不拉伸：否则圆角底会跟着网格行高变高，文字贴顶、底部露出一块「沉边」的背景
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Right,
			Visibility = listening ? Visibility.Visible : Visibility.Collapsed
		};
		Grid.SetColumn(state, 3);
		head.Children.Add(state);
		grid.Children.Add(head);

		// 第一行：机器人账号实际挂在哪个客户端实例（服务器名已经在标题里，这里不再重复）
		var line1 = new TextBlock
		{
			Text = "实例 " + ClientDisplayName(e),
			FontSize = 12,
			Opacity = 0.7,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		ToolTipService.SetToolTip(line1, "机器人账号写在 " + (NexusEndpointService.ClientDirOf(e) is string d && d.Length > 0 ? d : "未识别客户端"));
		Grid.SetRow(line1, 1);
		grid.Children.Add(line1);

		// 第二行：此机器人代理卡片的上下行包数（每 2 秒刷新）
		var stats = new TextBlock
		{
			Text = "↑ 发送 0 B / 0 包　↓ 接收 0 B / 0 包",
			FontSize = 11.5,
			Opacity = 0.72,
			TextTrimming = TextTrimming.CharacterEllipsis,
			FontFamily = new FontFamily("Consolas")
		};
		// 统计行只在「高级信息」打开时出现（默认隐藏）。
		stats.Visibility = SettingsManager.Instance.ShowAdvancedSessionInfo ? Visibility.Visible : Visibility.Collapsed;
		Grid.SetRow(stats, 2);
		grid.Children.Add(stats);

		// 第三行：最近一次连接失败 / 被踢原因
		var errorText = new TextBlock
		{
			Text = "",
			FontSize = 11.5,
			Opacity = 0.85,
			Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 90, 80)),
			TextWrapping = TextWrapping.Wrap,
			MaxLines = 2,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Visibility = Visibility.Collapsed
		};
		Grid.SetRow(errorText, 3);
		grid.Children.Add(errorText);

		var chatText = new TextBlock
		{
			Text = "等待聊天栏内容…",
			FontSize = 11,
			FontFamily = new FontFamily("Consolas"),
			Opacity = 0.78,
			TextWrapping = TextWrapping.Wrap,
			MaxLines = 6,
			TextTrimming = TextTrimming.CharacterEllipsis,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			// 默认收起：卡片多时聊天栏最占高度，点标题上的按钮再展开
			Visibility = Visibility.Collapsed
		};
		var eventText = new TextBlock
		{
			Text = "最近事件：等待代理数据…",
			FontSize = 11.5,
			Opacity = 0.6,
			TextWrapping = TextWrapping.Wrap,
			MaxLines = 2,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		var preview = new Border
		{
			CornerRadius = new CornerRadius(6),
			Margin = new Thickness(0, 4, 0, 0),
			Padding = new Thickness(10, 8, 10, 8),
			Background = PreviewBrush(),
			// 默认整块收起；用户展开过的端口在重建后按 _logExpanded 恢复。
			Visibility = _logExpanded.Contains(e.ListenPort) ? Visibility.Visible : Visibility.Collapsed,
			Child = new StackPanel
			{
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Center,
				Spacing = 6,
				Children =
				{
					new TextBlock
					{
						Text = "实例视角 · 端口 " + e.ListenPort + " · 聊天栏",
						FontSize = 14,
						FontWeight = FontWeights.SemiBold,
						TextAlignment = TextAlignment.Center
					},
					chatText,
					eventText
				}
			}
		};
		Grid.SetRow(preview, 4);
		grid.Children.Add(preview);
		// 「日志」开关放在标题行右侧：一次收起整块预览（端口 / 聊天栏 / 最近事件）
		bool logOpen = _logExpanded.Contains(e.ListenPort);
		var logToggle = new Button
		{
			Content = "日志 " + (logOpen ? "▴" : "▾"),
			FontSize = 11,
			MinHeight = 0,
			Padding = new Thickness(8, 2, 8, 2)
		};
		logToggle.Click += (_, _) =>
		{
			bool show = preview.Visibility != Visibility.Visible;
			preview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
			if (show)
			{
				_logExpanded.Add(e.ListenPort);
			}
			else
			{
				_logExpanded.Remove(e.ListenPort);
			}
			logToggle.Content = "日志 " + (show ? "▴" : "▾");
			SyncLogAllCheckState();
		};
		logToggle.FontSize = 11;
		logToggle.MinHeight = 0;
		logToggle.Padding = new Thickness(8, 2, 8, 2);
		logToggle.Margin = new Thickness(0, 0, 8, 0);
		logToggle.HorizontalAlignment = HorizontalAlignment.Right;
		logToggle.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(logToggle, 2);
		head.Children.Add(logToggle);

		// 卡片操作：启动/停止合并为一个按钮；重启常驻，未启动时不可点；
		// 删除在左、自启动勾选在右（按用户定版布局）。
		var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
		bool listeningNow = listening;
		actions.Children.Add(SmallButton(listeningNow ? "停止" : "启动", () =>
		{
			if (listeningNow)
			{
				StopProxy(e);
			}
			else
			{
				StartProxy(e);
			}
		}));
		Button restartButton = SmallButton("重启", () => StartProxy(e));
		restartButton.IsEnabled = listeningNow;
		ToolTipService.SetToolTip(restartButton, listeningNow ? "重启该机器人代理" : "机器人未启动，无法重启");
		actions.Children.Add(restartButton);
		actions.Children.Add(SmallButton("删除", () => DeleteCard(e)));
		CheckBox autoStartCheck = new CheckBox
		{
			Content = "自启动",
			FontSize = 12,
			IsChecked = e.AutoStart,
			VerticalAlignment = VerticalAlignment.Center
		};
		ToolTipService.SetToolTip(autoStartCheck,
			"勾选后，程序启动且「自动启动机器人」总开关打开时，会自动为此角色恢复机器人代理");
		autoStartCheck.Click += (_, _) =>
		{
			bool on = autoStartCheck.IsChecked == true;
			// 只改标记，不重建卡片：这里若触发 Saved→Refresh 会整页闪一下。
			_muteSavedRefresh = true;
			try
			{
				NexusEndpointService.SetCardAutoStart(e, on);
			}
			finally
			{
				_muteSavedRefresh = false;
			}
			e.AutoStart = on;
			SyncAutoAllCheckState();
			MainWindow.Instance?.ShowToast((on ? "已勾选自启动：" : "已取消自启动：")
				+ (string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName)
				+ (on ? "（下次启动程序时自动挂此机器人）" : "（下次启动程序不再自动挂此机器人）"));
		};
		actions.Children.Add(autoStartCheck);
		Grid.SetRow(actions, 5);
		grid.Children.Add(actions);

		if (!_live.TryGetValue(e.ListenPort, out List<LiveRefs>? refsList))
		{
			refsList = new List<LiveRefs>();
			_live[e.ListenPort] = refsList;
		}
		refsList.Add(new LiveRefs
		{
			Stats = stats,
			Event = eventText,
			Error = errorText,
			Status = badgeText,
			Chat = chatText,
			StatusBadge = state,
			Listening = listening,
			Advanced = SettingsManager.Instance.ShowAdvancedSessionInfo
		});

		border.Child = grid;
		return border;
	}

	/// <summary>启动宿主挂号版客户端（页面顶部「启动挂号版」按钮）。</summary>
	private async Task LaunchHostClientAsync()
	{
		try
		{
			string dir = NexusEndpointService.ResolveHostVersionDirForDisplay();
			if (dir.Length == 0)
			{
				MainWindow.Instance?.ShowToast("未找到挂号版客户端目录", ok: false);
				return;
			}
			if (NexusEndpointService.IsHostClientRunning(dir))
			{
				MainWindow.Instance?.ShowToast("挂号版已在运行");
				return;
			}
			OfficialClientLaunchResult r = await Task.Run(() => NexusEndpointService.LaunchHostClient(""));
			MainWindow.Instance?.ShowToast(r.Ok ? "已启动挂号版，机器人会自动进服" : ("启动挂号版失败：" + r.Message), r.Ok);
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 启动挂号版失败：" + ex.Message);
			MainWindow.Instance?.ShowToast("启动挂号版失败：" + ex.Message, ok: false);
		}
	}

	// ---- 卡片操作：启动 / 停止机器人代理、删除卡片 ----

	private void StartProxy(NexusEndpointService.Endpoint e, bool batch = false)
	{
		string name = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
		try
		{
			if (string.IsNullOrWhiteSpace(_gameDir))
			{
				_gameDir = NexusEndpointService.ResolveGameDir();
			}
			if (string.IsNullOrWhiteSpace(_gameDir))
			{
				LogHelper.Log("[实例多开] 启动机器人失败：未识别到客户端目录（" + e.Key + "）");
				if (!batch)
				{
					_summary.Text = "未识别到客户端目录，无法启动机器人。请先在 PCL 区绑定启动器路径。";
					MainWindow.Instance?.ShowToast("启动机器人失败：未识别到客户端目录", ok: false);
				}
				return;
			}
			bool wasListening = ProxyCaptureService.ActiveBindingsSnapshot().Any(b => b.ListenPort == e.ListenPort);
			if (wasListening)
			{
				// 重启用：只停止 nexus 侧监听，绝不动脱盒原服务器代理。
				ProxyCaptureService.StopNexusBinding(e.ListenPort);
			}
			if (!batch)
			{
				_summary.Text = "正在启动机器人：" + name + " → 127.0.0.1:" + e.ListenPort + "…";
			}
			AppSettings cfg = SettingsManager.Instance;
			// 按卡片键（账号@服务器:端口 + 角色名）精确匹配：同一账号多个角色互不干扰。
			string cardKey = e.CardKey;
			NexusEndpointSetting? item = cfg.NexusEndpoints
				.FirstOrDefault(x => string.Equals(NexusEndpointService.CardKeyOf(x), cardKey, StringComparison.OrdinalIgnoreCase));
			bool changed = false;
			if (item == null)
			{
				cfg.NexusEndpoints.Add(new NexusEndpointSetting
				{
					Key = e.Key,
					RoleName = e.RoleName ?? "",
					ListenPort = e.ListenPort,
					Enabled = true
				});
				changed = true;
			}
			else if (!item.Enabled || item.ListenPort != e.ListenPort)
			{
				item.Enabled = true;
				item.ListenPort = e.ListenPort;
				changed = true;
			}
			if (cfg.NexusHiddenCards != null && cfg.NexusHiddenCards.RemoveAll(x =>
				string.Equals(x, cardKey, StringComparison.OrdinalIgnoreCase)
				|| (string.IsNullOrWhiteSpace(e.RoleName) && string.Equals(x, e.Key, StringComparison.OrdinalIgnoreCase))) > 0)
			{
				changed = true;
			}
			if (changed)
			{
				SettingsManager.Save(cfg);
			}
			bool isBot = string.Equals(e.Group, "机器人代理", StringComparison.OrdinalIgnoreCase);
			string hostHint = "";
			if (isBot)
			{
				string botUser = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
				// 显式启动 = 用户改主意了：先解掉删除墓碑，否则这次启动写不进账号。
				NexusEndpointService.ClearCardTombstone(e);
				NexusEndpointService.EnsureBotAccount(e.ServerHint, botUser, e.ListenPort, "机器人代理", e.CardKey, NexusEndpointService.ClientDirOf(e));
				string hostDir = NexusEndpointService.ResolveHostVersionDir(e.ServerHint);
				if (hostDir.Length > 0 && !NexusEndpointService.IsHostClientRunning(hostDir))
				{
					hostHint = "；宿主挂号版未运行，启动后自动进服";
				}
				// 单客户端 10 个会话到顶时只提醒、不拦（过渡逻辑在 Load 里统一处理）。
				int sessions = NexusEndpointService.CountBotSessions(
					NexusEndpointService.Load(_gameDir), NexusEndpointService.ClientDirOf(e));
				NexusEndpointService.WarnSessionLimitIfNeeded(NexusEndpointService.ClientDirOf(e), sessions);
			}
			int started = NexusEndpointService.StartMembers(_gameDir, new[] { e.CardKey });
			bool listening = ProxyCaptureService.ActiveBindingsSnapshot().Any(b => b.ListenPort == e.ListenPort);
			LogHelper.Log("[实例多开] 卡片启动机器人 " + e.Key + " → 127.0.0.1:" + e.ListenPort
				+ "：已提交 " + started + " 条，监听=" + listening + "，当前监听 " + ProxyCaptureService.ActiveBindingsSnapshot().Count + " 个端口"
				+ (isBot ? "，机器人账号已写入客户端" : ""));
			if (listening)
			{
				LogHelper.Log("[实例多开] 已启动：" + name + " · 127.0.0.1:" + e.ListenPort);
				if (!batch)
				{
					_summary.Text = "已启动机器人：" + name + " · 127.0.0.1:" + e.ListenPort + hostHint;
					MainWindow.Instance?.ShowToast(hostHint.Length == 0
						? ("已启动机器人：" + name + " · 127.0.0.1:" + e.ListenPort)
						: "机器人账号已写入；宿主挂号版未运行，启动挂号版后会自动进服", hostHint.Length == 0);
				}
			}
			else
			{
				string error = "启动失败：127.0.0.1:" + e.ListenPort + " 未建立监听（端口可能被占用，或该角色不在元素之诗/失落世界的机器人花名册内）";
				LogHelper.Log("[实例多开] " + error + "，key=" + e.Key);
				if (!batch)
				{
					_summary.Text = error;
					MainWindow.Instance?.ShowToast(error, ok: false);
				}
			}
			if (!batch)
			{
				_signature = "";
				Refresh();
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 卡片启动机器人异常：" + ex.Message);
			if (!batch)
			{
				_summary.Text = "启动机器人失败：" + ex.Message;
				MainWindow.Instance?.ShowToast("启动机器人失败：" + ex.Message, ok: false);
			}
		}
	}

	/// <summary>
	/// 停止单个卡片。
	/// releaseForRestart=true（批量/单卡「重启」用）时，额外把**同一端口上占位的
	/// 脱盒代理**也停掉，让重启能把端口原样抢回来。
	/// 背景：日志里 26 次 `已被另一类代理占用` → 端口被顶到 20226+，再触发
	/// `客户端账号端口已更新`（还会写进 .bak 备份目录），每次批量重启都会漂移一段。
	/// </summary>
	private void StopProxy(NexusEndpointService.Endpoint e, bool batch = false, bool releaseForRestart = false)
	{
		string name = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
		try
		{
			string botUser = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
			bool isBot = string.Equals(e.Group, "机器人代理", StringComparison.OrdinalIgnoreCase);
			// 机器人卡：只停 nexus- 侧监听，端口归脱盒代理时只挂断该机器人会话；
			// 脱盒代理卡：用户点的就是这条代理，直接按端口停（旧实现只调 StopNexusBinding，
			// 服务器侧监听 tag 不是 nexus-，会被拒绝 → 「停止」看起来没反应）。
			bool stopped = isBot
				? ProxyCaptureService.StopNexusBinding(e.ListenPort)
				: ProxyCaptureService.StopPort(e.ListenPort);
			if (isBot)
			{
				if (!stopped)
				{
					// 端口归脱盒代理所有：只挂断该机器人的连接，保留脱盒监听。
					stopped = ProxyCaptureService.DropNexusSessions(e.ListenPort, botUser) > 0;
				}
				if (!stopped && releaseForRestart)
				{
					// 重启路径：连脱盒侧监听一起释放，否则 StartBatch 只能改端口。
					if (ProxyCaptureService.StopBindingForRole(e.ListenPort, botUser))
					{
						stopped = true;
						LogHelper.Log("[实例多开] 重启前释放端口 " + e.ListenPort + " 上的占位代理（角色 " + botUser + "）");
					}
				}
				NexusEndpointService.RemoveBotAccountFromAllClients(botUser, e.ListenPort);
				if (!releaseForRestart)
				{
					// 停用落盘：否则下次游戏启动 / 程序启动又会按「已启用」把账号写回来，
					// 表现就是「关掉了机器人却又自己挂上了」。
					NexusEndpointService.SetCardEnabled(e, false);
				}
			}
			LogHelper.Log("[实例多开] 卡片停止机器人 " + e.Key + " → 127.0.0.1:" + e.ListenPort + "：stopped=" + stopped);
			if (!batch)
			{
				_summary.Text = stopped
					? "已停止机器人：" + name + " · 127.0.0.1:" + e.ListenPort
					: "没有可停止的机器人监听：" + name + " · 127.0.0.1:" + e.ListenPort;
				MainWindow.Instance?.ShowToast(stopped ? "已停止机器人：" + name : "该端口没有机器人在监听（脱盒代理不受影响）");
				_signature = "";
				Refresh();
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 卡片停止机器人异常：" + ex.Message);
			if (!batch)
			{
				MainWindow.Instance?.ShowToast("停止机器人失败：" + ex.Message, ok: false);
			}
		}
	}

	private void DeleteCard(NexusEndpointService.Endpoint e)
	{
		try
		{
			string cardKey = e.CardKey;
			string roleName = e.RoleName ?? "";
			if (string.Equals(e.Group, "机器人代理", StringComparison.OrdinalIgnoreCase))
			{
				ProxyCaptureService.StopNexusBinding(e.ListenPort);
			}
			else
			{
				// 脱盒代理卡：端口上是非 nexus- 监听，必须按端口停，否则删了卡片代理还在跑。
				ProxyCaptureService.StopPort(e.ListenPort);
			}
			AppSettings cfg = SettingsManager.Instance;
			// 只删这一张卡：按卡片键（Key + 角色名）精确匹配，同账号其它角色的配置不受影响。
			// 没有角色名的卡片以 Key 唯一，按 Key + 端口兜底清理旧数据。
			cfg.NexusEndpoints.RemoveAll(x =>
				string.Equals(NexusEndpointService.CardKeyOf(x), cardKey, StringComparison.OrdinalIgnoreCase)
				|| (string.IsNullOrWhiteSpace(roleName)
					&& string.Equals(x.Key, e.Key, StringComparison.OrdinalIgnoreCase)
					&& (x.ListenPort == e.ListenPort || x.ListenPort <= 0)));
			if (cfg.NexusHiddenCards == null)
			{
				cfg.NexusHiddenCards = new List<string>();
			}
			cfg.NexusHiddenCards.RemoveAll(x => string.IsNullOrWhiteSpace(roleName)
				&& string.Equals(x, e.Key, StringComparison.OrdinalIgnoreCase));
			if (!cfg.NexusHiddenCards.Any(x => string.Equals(x, cardKey, StringComparison.OrdinalIgnoreCase)))
			{
				cfg.NexusHiddenCards.Add(cardKey);
			}
			// 打墓碑：这张卡被删了，任何自动补写（游戏启动重放 / 启动补齐 / 端口自愈）都不许再写回来。
			NexusEndpointService.AddCardTombstone(e);
			// 快速启动分组里对应的角色一起删，避免「卡片删了、组里还在」的悬空角色。
			int rolesRemoved = NexusEndpointService.RemoveQuickStartRole(e);
			// 机器人代理卡：客户端里写入的机器人账号一并移除，避免游戏一直重连没人监听的端口。
			if (string.Equals(e.Group, "机器人代理", StringComparison.OrdinalIgnoreCase))
			{
				string botUser = string.IsNullOrWhiteSpace(roleName) ? e.User : roleName;
				NexusEndpointService.RemoveBotAccountFromAllClients(botUser, e.ListenPort);
			}
			SettingsManager.Save(cfg);
			LogHelper.Log("[实例多开] 已删除卡片：" + cardKey + "（联动删除快速启动角色 " + rolesRemoved + " 个）");
			MainWindow.Instance?.ShowToast("已删除卡片：" + (string.IsNullOrWhiteSpace(roleName) ? e.User : roleName)
				+ (rolesRemoved > 0 ? "，快速启动角色已同步删除" : ""));
			_signature = "";
			Refresh();
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 删除卡片异常：" + ex.Message);
		}
	}

	private void UpdateLive(Dictionary<int, ProxyCaptureService.TrafficInfo> traffic, HashSet<int> listeningPorts)
	{
		foreach (KeyValuePair<int, List<LiveRefs>> pair in _live)
		{
			bool listening = listeningPorts.Contains(pair.Key);
			traffic.TryGetValue(pair.Key, out ProxyCaptureService.TrafficInfo? t);
			bool connected = IsConnected(traffic, pair.Key);
			foreach (LiveRefs refs in pair.Value)
			{
				refs.Listening = listening;
				if (t != null)
				{
					refs.Stats.Text = "↑ 发送 " + FormatBytes(t.UpBytes) + " / " + t.UpPackets
						+ " 包　↓ 接收 " + FormatBytes(t.DownBytes) + " / " + t.DownPackets + " 包"
						+ "　画面 " + t.ScreenPackets + " 包";
					refs.Event.Text = string.IsNullOrWhiteSpace(t.LastEvent) ? "最近事件：等待代理数据…" : ("最近事件：" + t.LastEvent);
					if (t.ChatLines != null && t.ChatLines.Length > 0)
					{
						int start = Math.Max(0, t.ChatLines.Length - 6);
						refs.Chat.Text = string.Join("\n", t.ChatLines.Skip(start));
						refs.SawChat = true;
					}
					else
					{
						refs.Chat.Text = "等待聊天栏内容…";
					}
					refs.Stats.Opacity = t.Sessions > 0 ? 0.9 : 0.72;
					if (refs.Advanced && !string.IsNullOrWhiteSpace(t.LastError))
					{
						refs.Error.Text = "最近错误：" + t.LastError;
						refs.Error.Visibility = Visibility.Visible;
					}
					else
					{
						refs.Error.Visibility = Visibility.Collapsed;
					}
				}
				else
				{
					refs.Stats.Text = "↑ 发送 0 B / 0 包　↓ 接收 0 B / 0 包";
					refs.Event.Text = "最近事件：等待代理数据…";
					refs.Chat.Text = "等待聊天栏内容…";
					refs.Stats.Opacity = 0.72;
					refs.Error.Visibility = Visibility.Collapsed;
				}
				UpdateStatusBadge(refs, t?.Status, connected);
			}
		}
	}

	/// <summary>「已进服」的有效期：最近一次聊天栏内容必须在 10 秒内。</summary>
	private const int ChatFreshSeconds = 10;

	/// <summary>
	/// 该端口是否算「已进服」：会话在、且最近 10 秒内还收到过聊天栏内容。
	/// 停止机器人或长时间收不到聊天（掉线/被踢/退回大厅）都不再显示已进服。
	/// </summary>
	private static bool IsConnected(Dictionary<int, ProxyCaptureService.TrafficInfo> traffic, int port)
	{
		if (!traffic.TryGetValue(port, out ProxyCaptureService.TrafficInfo? t) || t.Sessions <= 0)
		{
			return false;
		}
		if (t.LastChatUtc == default)
		{
			return false;
		}
		return (DateTime.UtcNow - t.LastChatUtc).TotalSeconds <= ChatFreshSeconds;
	}

	/// <summary>
	/// 卡片右上角只显示三种状态：已进服 / 进入子服 / 监听中。
	/// 其余状态（已连接 / 等待连接 / 已断开及原因 / 未启动代理）一律隐藏。
	/// </summary>
	private static void UpdateStatusBadge(LiveRefs refs, string? status, bool connected)
	{
		if (!refs.Listening)
		{
			// 停止 / 没有监听：连「监听中」都不显示，更不会残留「已进服」。
			refs.StatusBadge.Visibility = Visibility.Collapsed;
			return;
		}
		if (connected)
		{
			refs.Status.Text = "已进服";
			refs.StatusBadge.Background = new SolidColorBrush(Color.FromArgb(70, 40, 130, 200));
			refs.StatusBadge.Visibility = Visibility.Visible;
			return;
		}
		if (!string.IsNullOrEmpty(status) && status.StartsWith("进入子服", StringComparison.Ordinal))
		{
			refs.Status.Text = "进入子服";
			refs.StatusBadge.Background = new SolidColorBrush(Color.FromArgb(70, 40, 130, 200));
			refs.StatusBadge.Visibility = Visibility.Visible;
			return;
		}
		refs.Status.Text = "监听中";
		refs.StatusBadge.Background = new SolidColorBrush(Color.FromArgb(70, 40, 170, 90));
		refs.StatusBadge.Visibility = Visibility.Visible;
	}

	/// <summary>卡片上显示的客户端实例名：优先用版本目录名，去掉「挂号版」后缀太啰嗦就不动它。</summary>
	private static string ClientDisplayName(NexusEndpointService.Endpoint e)
	{
		string dir = NexusEndpointService.ClientDirOf(e);
		if (string.IsNullOrWhiteSpace(dir))
		{
			return "未识别客户端";
		}
		string name = System.IO.Path.GetFileName(dir) ?? "";
		return name.Length == 0 ? dir : name;
	}

	/// <summary>卡片标题：角色名 · 服务器名（如 Notel · 失落世界）。</summary>
	private static string CardTitle(NexusEndpointService.Endpoint e)
	{
		string role = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
		string server = StripPort(e.ServerKey);
		if (string.IsNullOrWhiteSpace(server) || string.Equals(server, role, StringComparison.OrdinalIgnoreCase))
		{
			return role;
		}
		return role + " · " + server;
	}

	/// <summary>当前被勾选的卡片（批量操作对象）。</summary>
	private List<NexusEndpointService.Endpoint> SelectedEndpoints()
	{
		return _lastVisible
			.Where(e => _checkedPorts.Contains(e.ListenPort))
			.ToList();
	}

	/// <summary>批量操作进行中：防止重复点击，并让单卡方法跳过界面刷新。</summary>
	private bool _batchRunning;

	/// <summary>
	/// 批量启动 / 停止 / 重启。
	///
	/// 旧实现是在 UI 线程里 for 循环，每张卡都 Stop→Start 并且各自 Refresh 一次：
	/// 15 张卡 = 30 次全量重建卡片（每次 15 张卡的 XAML 树 + FLIP 动画 + UpdateLayout），
	/// 中间还夹着端口自适配 → 客户端账号文件改写 → 触发登录流程，于是界面直接
	/// 卡成「未响应」，XAML 垃圾与登录任务同时堆积，内存一路涨。
	///
	/// 现在：整段搬到后台线程串行执行（停止阶段全部完成并等端口释放后再统一启动），
	/// 期间不碰任何 UI，结束后在主线程刷新一次。
	/// </summary>
	private async void BatchStartStop(bool start, bool restart)
	{
		if (_batchRunning)
		{
			MainWindow.Instance?.ShowToast("批量操作正在进行，请稍候", false);
			return;
		}
		List<NexusEndpointService.Endpoint> picked = SelectedEndpoints();
		if (picked.Count == 0)
		{
			MainWindow.Instance?.ShowToast("请先勾选要操作的卡片", false);
			return;
		}
		_batchRunning = true;
		// 批量期间不要为第 6/7/8 个机器人各弹一次模态对话框（那会直接卡住输入）。
		NexusEndpointService.BatchMode = true;
		string verb = restart ? "重启" : (start ? "启动" : "停止");
		try
		{
			_summary.Text = "正在" + verb + " " + picked.Count + " 个机器人…";
			List<int> ports = picked.Select(x => x.ListenPort).ToList();
			string result = await Task.Run(() =>
			{
				int ok = 0;
				if (restart || !start)
				{
					foreach (NexusEndpointService.Endpoint e in picked)
					{
						StopProxy(e, batch: true, releaseForRestart: restart);
					}
					// 端口释放是同步的（TcpListener.Stop），但仍给系统一点回收时间，
					// 否则紧接着的 bind 会拿到 SocketException 而被顶到别的端口。
					WaitPortsReleased(ports, 1500);
				}
				if (restart || start)
				{
					foreach (NexusEndpointService.Endpoint e in picked)
					{
						StartProxy(e, batch: true);
						if (ProxyCaptureService.ActiveBindingsSnapshot().Any(b => b.ListenPort == e.ListenPort))
						{
							ok++;
						}
					}
				}
				else
				{
					ok = picked.Count;
				}
				// 批量期间 StartMembers 跳过了逐卡写回，这里统一写一次。
				NexusEndpointService.ApplyRoleFilesNow(_gameDir);
				return ok + "|" + picked.Count;
			});
			string[] parts = result.Split('|');
			int done = int.TryParse(parts[0], out int d) ? d : 0;
			int total = int.TryParse(parts[1], out int t) ? t : picked.Count;
			_signature = "";
			Refresh();
			bool allOk = done == total;
			_summary.Text = "已" + verb + " " + done + "/" + total + " 个机器人"
				+ (allOk ? "" : "（未建立监听的卡片见日志/错误提示）");
			MainWindow.Instance?.ShowToast("已" + verb + " " + done + "/" + total + " 个机器人", allOk);
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 批量" + verb + "失败：" + ex.Message);
			MainWindow.Instance?.ShowToast("批量" + verb + "失败：" + ex.Message, ok: false);
		}
		finally
		{
			NexusEndpointService.BatchMode = false;
			_batchRunning = false;
			// 批量期间攒下的「元素之诗超限」提醒，合并成一次弹窗（主线程）。
			NexusEndpointService.FlushPendingElementPoetryWarn();
		}
	}

	/// <summary>等一组端口不再被本进程监听（最多等 timeoutMs），给重启留出干净的绑定窗口。</summary>
	private static void WaitPortsReleased(IReadOnlyList<int> ports, int timeoutMs)
	{
		long deadline = Environment.TickCount64 + timeoutMs;
		while (Environment.TickCount64 < deadline)
		{
			bool any = ProxyCaptureService.ActiveBindingsSnapshot()
				.Any(b => ports.Contains(b.ListenPort));
			if (!any)
			{
				return;
			}
			System.Threading.Thread.Sleep(50);
		}
	}

	/// <summary>「自启动全选」：对当前显示的全部卡片生效（不需要先勾选）。</summary>
	private void SetAutoStartForAllVisible(bool on)
	{
		List<NexusEndpointService.Endpoint> list = _lastVisible;
		if (list.Count == 0)
		{
			MainWindow.Instance?.ShowToast("当前没有可操作的卡片", false);
			return;
		}
		ApplyAutoStart(list, on);
		MainWindow.Instance?.ShowToast((on ? "已为全部 " : "已取消全部 ") + list.Count + " 个机器人设置自启动", true);
	}

	/// <summary>批量写自启动：每张卡都 Save 会触发多次整页重建，这里静默保存后只刷新一次。</summary>
	private void ApplyAutoStart(List<NexusEndpointService.Endpoint> targets, bool on)
	{
		_muteSavedRefresh = true;
		try
		{
			foreach (NexusEndpointService.Endpoint e in targets)
			{
				NexusEndpointService.SetCardAutoStart(e, on);
				e.AutoStart = on;
			}
			SettingsManager.Save(SettingsManager.Instance);
		}
		finally
		{
			_muteSavedRefresh = false;
		}
		_signature = "";
		Refresh();
	}

	/// <summary>「全显日志」：不满就全部展开；已经全展开则全部收起。</summary>
	private void ToggleAllLogs(bool on)
	{
		List<NexusEndpointService.Endpoint> list = _lastVisible;
		if (list.Count == 0)
		{
			return;
		}
		bool expand = on;
		if (expand && list.All(e => _logExpanded.Contains(e.ListenPort)))
		{
			// 已经全展开了，再点一次就是全部收起
			expand = false;
		}
		if (expand)
		{
			foreach (NexusEndpointService.Endpoint e in list)
			{
				_logExpanded.Add(e.ListenPort);
			}
		}
		else
		{
			foreach (NexusEndpointService.Endpoint e in list)
			{
				_logExpanded.Remove(e.ListenPort);
			}
		}
		_signature = "";
		Refresh();
		SyncLogAllCheckState();
	}

	private void SyncLogAllCheckState()
	{
		if (_logAllCheck == null)
		{
			return;
		}
		bool all = _lastVisible.Count > 0 && _lastVisible.All(e => _logExpanded.Contains(e.ListenPort));
		if (_logAllCheck.IsChecked != all)
		{
			_logAllCheck.IsChecked = all;
		}
	}

	/// <summary>页头「自启动全选」跟随卡片状态：全部勾上才显示勾选。</summary>
	private void SyncAutoAllCheckState()
	{
		if (_autoAllCheck == null)
		{
			return;
		}
		bool all = _lastVisible.Count > 0 && _lastVisible.All(e => e.AutoStart);
		if (_autoAllCheck.IsChecked != all)
		{
			_autoAllCheck.IsChecked = all;
		}
	}

	/// <summary>服务器行只显示服务器名/地址，去掉末尾的 :端口（如 :25565）。</summary>
	private static string StripPort(string server)
	{
		if (string.IsNullOrWhiteSpace(server))
		{
			return "";
		}
		string value = server.Trim();
		int colon = value.LastIndexOf(':');
		if (colon > 0 && colon < value.Length - 1 && int.TryParse(value.Substring(colon + 1), out _))
		{
			value = value.Substring(0, colon);
		}
		return value;
	}

	private static string FormatBytes(long bytes)
	{
		if (bytes < 1024)
		{
			return bytes + " B";
		}
		double kb = bytes / 1024.0;
		if (kb < 1024)
		{
			return kb.ToString("0.0") + " KB";
		}
		double mb = kb / 1024.0;
		if (mb < 1024)
		{
			return mb.ToString("0.00") + " MB";
		}
		return (mb / 1024.0).ToString("0.00") + " GB";
	}

	/// <summary>修复 ComboBox 下拉弹层背景透明：强制使用不透明主题色。</summary>
	private static void ApplyOpaqueDropDown(ComboBox combo)
	{
		Color bg = ThemeHelper.IsBlack
			? Color.FromArgb(255, 24, 24, 24)
			: (Application.Current.RequestedTheme == ApplicationTheme.Dark
				? Color.FromArgb(255, 44, 44, 44)
				: Color.FromArgb(255, 249, 249, 249));
		Color border = ThemeHelper.IsBlack
			? Color.FromArgb(255, 56, 56, 56)
			: (Application.Current.RequestedTheme == ApplicationTheme.Dark
				? Color.FromArgb(255, 80, 80, 80)
				: Color.FromArgb(255, 214, 214, 214));
		combo.Resources["ComboBoxDropDownBackground"] = new SolidColorBrush(bg);
		combo.Resources["ComboBoxDropDownBorderBrush"] = new SolidColorBrush(border);
	}

	private static Brush CardBrush()
	{
		Color c = ThemeHelper.IsBlack
			? Color.FromArgb(255, 12, 12, 12)
			: (Application.Current.RequestedTheme == ApplicationTheme.Dark
				? Color.FromArgb(255, 32, 32, 32)
				: Color.FromArgb(255, 250, 250, 250));
		return new SolidColorBrush(c);
	}

	/// <summary>服务器分组横栏的底色：比卡片稍亮一点，作为「一组」的视觉起点。</summary>
	private static Brush SectionHeaderBrush()
	{
		Color c = ThemeHelper.IsBlack
			? Color.FromArgb(255, 26, 26, 26)
			: (Application.Current.RequestedTheme == ApplicationTheme.Dark
				? Color.FromArgb(255, 46, 46, 46)
				: Color.FromArgb(255, 240, 240, 240));
		return new SolidColorBrush(c);
	}

	private static Brush PreviewBrush()
	{
		var brush = new LinearGradientBrush
		{
			StartPoint = new Point(0, 0),
			EndPoint = new Point(1, 1)
		};
		bool dark = ThemeHelper.IsBlack || Application.Current.RequestedTheme == ApplicationTheme.Dark;
		brush.GradientStops.Add(new GradientStop
		{
			Color = dark ? Color.FromArgb(255, 16, 22, 28) : Color.FromArgb(255, 226, 233, 240),
			Offset = 0
		});
		brush.GradientStops.Add(new GradientStop
		{
			Color = dark ? Color.FromArgb(255, 26, 38, 48) : Color.FromArgb(255, 208, 219, 230),
			Offset = 1
		});
		return brush;
	}

	private static Button Button(string text, Action onClick)
	{
		var button = new Button
		{
			Content = text,
			FontSize = 13,
			Padding = new Thickness(12, 6, 12, 6)
		};
		button.Click += (_, _) =>
		{
			try
			{
				onClick();
			}
			catch
			{
			}
		};
		return button;
	}

	private static Button SmallButton(string text, Action onClick)
	{
		var button = new Button
		{
			Content = text,
			FontSize = 12,
			Padding = new Thickness(10, 4, 10, 4)
		};
		button.Click += (_, _) =>
		{
			try
			{
				onClick();
			}
			catch
			{
			}
		};
		return button;
	}
}

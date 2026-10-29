using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MhyunBox.Services;
using MhyunBox.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace MhyunBox.Pages;

public sealed class GroupsQuickPage : Page
{
	private sealed class UndoRepo
	{
		public required string Kind;

		public required ProxyGroup Group;

		public ProxyGroupRole? Role;
	}

	private readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;

	private readonly Dictionary<string, bool> _groupRoleChecked = new Dictionary<string, bool>();

	private bool _uiRefreshQueued;

	private bool _committingGroupPort;

	private bool _startGroupFlowBusy;

	private readonly Stack<UndoRepo> _undo = new Stack<UndoRepo>();

	private readonly List<Action> _runningStateRefreshers = new List<Action>();

	private StackPanel? _groupsSection;

	private TextBlock? _groupsHint;

	private Border? _groupsPanel;

	private StackPanel? _groupsListPanel;

	private Button? _serverIpPortButton;

	private Button? _startGroupButton;

	private Button? _newGroupButton;

	private readonly List<RadioButton> _presetRadios = new List<RadioButton>();

	private StackPanel? _presetRadioPanel;

	private TextBox ListenPortBox;

	private ToggleSwitch? _autoStartProxySwitch;

	private TextBlock? _lastLaunchText;

	private bool _syncingAutoStartSwitch;

	public StackPanel GroupsSection => _groupsSection ?? (_groupsSection = CreateGroupsSection());

	public TextBlock GroupsHint
	{
		get
		{
			TextBlock textBlock = _groupsHint;
			if ((object)textBlock == null)
			{
				textBlock = (_groupsHint = new TextBlock
				{
					FontSize = 12.0,
					Opacity = 0.7,
					VerticalAlignment = VerticalAlignment.Center
				});
			}
			return textBlock;
		}
	}

	public Border GroupsPanel
	{
		get
		{
			Border border = _groupsPanel;
			if ((object)border == null)
			{
				border = (_groupsPanel = new Border
				{
					Visibility = Visibility.Collapsed,
					Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
					BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
					BorderThickness = new Thickness(1.0),
					CornerRadius = new CornerRadius(12.0),
					Padding = new Thickness(12.0, 10.0, 12.0, 10.0),
					Child = GroupsListPanel
				});
			}
			return border;
		}
	}

	public StackPanel GroupsListPanel
	{
		get
		{
			StackPanel stackPanel = _groupsListPanel;
			if ((object)stackPanel == null)
			{
				stackPanel = (_groupsListPanel = new StackPanel
				{
					Spacing = 8.0
				});
			}
			return stackPanel;
		}
	}

	public Button ServerIpPortButton
	{
		get
		{
			Button button = _serverIpPortButton;
			if ((object)button == null)
			{
				button = (_serverIpPortButton = new Button
				{
					Content = "查看端口",
					Width = 108.0,
					MinHeight = 32.0,
					FontSize = 13.0,
					VerticalAlignment = VerticalAlignment.Center,
					IsEnabled = ProxyCaptureService.IsRunning
				});
			}
			return button;
		}
	}

	public Button StartGroupButton
	{
		get
		{
			Button button = _startGroupButton;
			if ((object)button == null)
			{
				button = (_startGroupButton = new Button
				{
					Content = "启动分组",
					Width = 108.0,
					MinHeight = 32.0,
					FontSize = 13.0,
					VerticalAlignment = VerticalAlignment.Center
				});
			}
			return button;
		}
	}

	private ToggleSwitch AutoStartProxySwitch
	{
		get
		{
			if (_autoStartProxySwitch == null)
			{
				_autoStartProxySwitch = new ToggleSwitch
				{
					Header = "自启动代理",
					OnContent = "开",
					OffContent = "关",
					FontSize = 13.0,
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(0.0, 0.0, 12.0, 0.0)
				};
				_autoStartProxySwitch.Toggled += AutoStartProxySwitch_Toggled;
			}
			return _autoStartProxySwitch;
		}
	}

	public Button NewGroupButton
	{
		get
		{
			Button button = _newGroupButton;
			if ((object)button == null)
			{
				button = (_newGroupButton = new Button
				{
					Content = "新建分组",
					Width = 108.0,
					MinHeight = 32.0,
					FontSize = 13.0,
					VerticalAlignment = VerticalAlignment.Center
				});
			}
			return button;
		}
	}

	public GroupsQuickPage()
	{
		_queue = base.DispatcherQueue;
		base.Content = BuildPageUi();
		base.Unloaded += OnUnloaded;
		base.Loaded += OnLoaded;
		KeyboardAccelerator keyboardAccelerator = new KeyboardAccelerator
		{
			Key = VirtualKey.Z,
			Modifiers = VirtualKeyModifiers.Control
		};
		keyboardAccelerator.Invoked += delegate(KeyboardAccelerator _, KeyboardAcceleratorInvokedEventArgs args)
		{
			RequestUndoLastDelete();
			args.Handled = true;
		};
		base.KeyboardAccelerators.Add(keyboardAccelerator);
	}

	private FrameworkElement BuildPageUi()
	{
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 10.0,
			Padding = new Thickness(16.0, 12.0, 16.0, 12.0),
			MaxWidth = 1180.0,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top
		};
		BuildControlCard();
		stackPanel.Children.Add(GroupsSection);
		return new ScrollViewer
		{
			Content = stackPanel,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollMode = ScrollMode.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			HorizontalScrollMode = ScrollMode.Disabled
		};
	}

	private FrameworkElement BuildControlCard()
	{
		BuildPresetRadios();
		ListenPortBox = new TextBox
		{
			Width = 120.0,
			PlaceholderText = "监听端口",
			FontFamily = new FontFamily("Consolas"),
			VerticalContentAlignment = VerticalAlignment.Center
		};
		ListenPortBox.LostFocus += delegate
		{
			CommitListeningPort();
		};
		ListenPortBox.KeyDown += delegate(object _, KeyRoutedEventArgs e)
		{
			if (e.Key == VirtualKey.Enter)
			{
				CommitListeningPort();
				e.Handled = true;
			}
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		_presetRadioPanel = stackPanel;
		foreach (RadioButton presetRadio in _presetRadios)
		{
			stackPanel.Children.Add(presetRadio);
		}
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		stackPanel2.Children.Add(new TextBlock
		{
			Text = "监听端口",
			FontSize = 13.0,
			VerticalAlignment = VerticalAlignment.Center
		});
		stackPanel2.Children.Add(ListenPortBox);
		StackPanel stackPanel3 = new StackPanel
		{
			Spacing = 6.0
		};
		stackPanel3.Children.Add(new TextBlock
		{
			Text = "代理快速启动",
			FontSize = 15.0,
			FontWeight = FontWeights.SemiBold
		});
		stackPanel3.Children.Add(stackPanel);
		stackPanel3.Children.Add(stackPanel2);
		return new Border
		{
			Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
			BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(12.0),
			Padding = new Thickness(14.0, 10.0, 14.0, 10.0),
			Child = stackPanel3
		};
	}

	private void BuildPresetRadios()
	{
		List<ServerPreset> list = (from p in ServerPresets.All
			where !p.IsPending && !string.IsNullOrWhiteSpace(p.Host)
			orderby (!string.Equals(p.Name, "元素之诗", StringComparison.Ordinal)) ? 1 : 0
			select p).ToList();
		if (list.Count == 0)
		{
			list = ServerPresets.All.ToList();
		}
		foreach (ServerPreset item in list)
		{
			RadioButton radioButton = new RadioButton
			{
				Content = item.Name,
				Tag = item.Name,
				GroupName = "gqp_preset",
				FontSize = 13.0
			};
			radioButton.Checked += PresetRadio_Checked;
			_presetRadios.Add(radioButton);
		}
		RestorePresetSelection();
	}

	/// <summary>重建「代理快速启动」的预设单选组（删除预设后调用）。</summary>
	private void RebuildQuickPresetRadios()
	{
		if (_presetRadioPanel == null)
		{
			return;
		}
		_presetRadios.Clear();
		_presetRadioPanel.Children.Clear();
		BuildPresetRadios();
		foreach (RadioButton item in _presetRadios)
		{
			_presetRadioPanel.Children.Add(item);
		}
	}

	/// <summary>沿可视树向上查找所在 ContentDialog（用于在按钮回调中关闭宿主对话框）。</summary>
	private static ContentDialog? FindAncestorDialog(DependencyObject? start)
	{
		DependencyObject? cur = start;
		while (cur != null)
		{
			if (cur is ContentDialog dialog)
			{
				return dialog;
			}
			cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur);
		}
		return null;
	}

	private void RestorePresetSelection()
	{
		string saved = SettingsManager.Instance.ProxyPresetName?.Trim() ?? "";
		RadioButton radioButton = _presetRadios.FirstOrDefault((RadioButton r) => string.Equals(r.Tag as string, saved, StringComparison.Ordinal));
		if ((object)radioButton == null)
		{
			radioButton = _presetRadios.FirstOrDefault();
		}
		if (radioButton != null)
		{
			radioButton.IsChecked = true;
		}
	}

	private void PresetRadio_Checked(object sender, RoutedEventArgs e)
	{
		if (sender is RadioButton { Tag: string tag } && !string.IsNullOrWhiteSpace(tag))
		{
			AppSettings instance = SettingsManager.Instance;
			instance.ProxyPresetName = tag;
			SettingsManager.Save(instance);
		}
		QueueUiRefresh();
	}

	private void CommitListeningPort()
	{
		string text = ListenPortBox.Text.Trim();
		if (!string.IsNullOrWhiteSpace(text) && int.TryParse(text, out var result) && result >= 1 && result <= 65535)
		{
			AppSettings instance = SettingsManager.Instance;
			if (instance.ProxyListenPort != result)
			{
				instance.ProxyListenPort = result;
				SettingsManager.Save(instance);
			}
		}
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

	private void SyncAutoStartProxySwitch()
	{
		if (_autoStartProxySwitch == null)
		{
			return;
		}
		try
		{
			_syncingAutoStartSwitch = true;
			_autoStartProxySwitch.IsOn = SettingsManager.Instance.AutoStartProxyOnStartup;
		}
		finally
		{
			_syncingAutoStartSwitch = false;
		}
	}

	private void AutoStartProxySwitch_Toggled(object sender, RoutedEventArgs e)
	{
		if (_syncingAutoStartSwitch || _autoStartProxySwitch == null)
		{
			return;
		}
		try
		{
			AppSettings instance = SettingsManager.Instance;
			instance.AutoStartProxyOnStartup = _autoStartProxySwitch.IsOn;
			SettingsManager.Save(instance);
			MainWindow.Instance?.ShowToast(instance.AutoStartProxyOnStartup ? "已开启：启动程序后按上次启动方式自动启动代理" : "已关闭：启动程序后自动启动代理");
			UpdateLastLaunchText();
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[快速启动页] 保存自启动代理开关失败");
		}
	}

	private void UpdateLastLaunchText()
	{
		if (_lastLaunchText == null)
		{
			return;
		}
		try
		{
			AppSettings instance = SettingsManager.Instance;
			int num = instance.LastLaunchBindings?.Count ?? 0;
			string text = instance.LastLaunchSource ?? "";
			string text2 = instance.LastLaunchSummary ?? "";
			if (instance.AutoStartProxyOnStartup && num > 0)
			{
				// 自启动代理已开启且有启动记录：不显示「已启动的角色端口」提示
				_lastLaunchText.Visibility = Visibility.Collapsed;
				return;
			}
			_lastLaunchText.Visibility = Visibility.Visible;
			if (num <= 0)
			{
				_lastLaunchText.Text = (instance.AutoStartProxyOnStartup ? "尚无启动记录：请先在这里启动一次代理，开机自启才会按同样方式恢复。" : "自启动代理已关闭。");
				return;
			}
			_lastLaunchText.Text = "已记住上次启动方式：" + (string.IsNullOrWhiteSpace(text) ? "未知来源" : text) + " " + text2 + "（" + num + " 个角色端口）。" + (instance.AutoStartProxyOnStartup ? "下次启动程序会按此恢复。" : "自启动代理已关闭，不会自动恢复。");
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[快速启动页] 刷新上次启动方式提示失败");
		}
	}

	private StackPanel CreateGroupsSection()
	{
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10.0
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "代理分组",
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center
		});
		stackPanel.Children.Add(GroupsHint);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		ServerIpPortButton.Click += ServerIpPort_Click;
		StartGroupButton.Click += StartGroup_Click;
		NewGroupButton.Click += NewGroup_Click;
		stackPanel2.Children.Add(ServerIpPortButton);
		stackPanel2.Children.Add(StartGroupButton);
		stackPanel2.Children.Add(NewGroupButton);
		_lastLaunchText = new TextBlock
		{
			FontSize = 12.0,
			Opacity = 0.75,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		StackPanel stackPanel3 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		stackPanel3.Children.Add(AutoStartProxySwitch);
		Button button = new Button
		{
			Content = "清除启动记忆",
			FontSize = 12.0,
			MinHeight = 30.0,
			Padding = new Thickness(12.0, 0.0, 12.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		button.Click += ClearLaunchMemory_Click;
		stackPanel3.Children.Add(button);
		StackPanel result = new StackPanel
		{
			Spacing = 4.0,
			Children = 
			{
				(UIElement)stackPanel,
				(UIElement)stackPanel2,
				(UIElement)stackPanel3,
				(UIElement)_lastLaunchText,
				(UIElement)GroupsPanel
			}
		};
		SyncAutoStartProxySwitch();
		UpdateLastLaunchText();
		return result;
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		try
		{
			AccountStore.Changed += OnAccountsChanged;
			ProxyCaptureService.StateChanged += OnStateChanged;
			SettingsManager.Saved += OnSettingsSaved;
			if (SettingsManager.Instance.ProxyListenPort > 0)
			{
				ListenPortBox.Text = SettingsManager.Instance.ProxyListenPort.ToString();
			}
			SyncAutoStartProxySwitch();
			UpdateLastLaunchText();
			UpdateStatus();
			RefreshGroups();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "快速启动页加载失败");
		}
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		AccountStore.Changed -= OnAccountsChanged;
		ProxyCaptureService.StateChanged -= OnStateChanged;
		SettingsManager.Saved -= OnSettingsSaved;
	}

	private void OnSettingsSaved(AppSettings settings)
	{
		try
		{
			_queue.TryEnqueue(delegate
			{
				SyncAutoStartProxySwitch();
				UpdateLastLaunchText();
			});
		}
		catch
		{
		}
	}

	private void ClearLaunchMemory_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			AppSettings instance = SettingsManager.Instance;
			instance.LastLaunchBindings = new List<SavedProxyBinding>();
			instance.LastLaunchSource = "";
			instance.LastLaunchSummary = "";
			SettingsManager.Save(instance);
			UpdateLastLaunchText();
			MainWindow.Instance?.ShowToast("已清除启动记忆，下次启动程序不会自动恢复代理");
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[快速启动页] 清除启动记忆失败");
		}
	}

	private void OnStateChanged(bool running)
	{
		_queue.TryEnqueue(delegate
		{
			SyncUiState();
			// 代理状态变化（含从「代理启动」页启动/停止）：重建分组列表，
			// 让对应角色的运行状态点（●）与「启动/停止」按钮即时联动。
			try
			{
				RefreshGroups();
			}
			catch
			{
			}
		});
	}

	private void SyncUiState()
	{
		ServerIpPortButton.IsEnabled = ProxyCaptureService.IsRunning;
		StartGroupButton.IsEnabled = true;
		NewGroupButton.IsEnabled = true;
		SyncAutoStartProxySwitch();
		UpdateLastLaunchText();
		UpdateStatus();
		foreach (Action runningStateRefresher in _runningStateRefreshers)
		{
			try
			{
				runningStateRefresher();
			}
			catch
			{
			}
		}
	}

	// 角色代理运行判定：角色名 + 账号（channel/account）匹配活动绑定，
	// 兼容从「代理启动」页以不同端口启动的同角色代理；账号信息缺失时回退按端口匹配。
	private static bool IsRoleRunning(ProxyGroupRole? role)
	{
		if (role == null)
		{
			return false;
		}
		List<AccountStore.LocalAccount> accounts = AccountStore.All.ToList();
		return ProxyCaptureService.ActiveBindingsSnapshot().Any((ProxyCaptureService.ActiveBindingInfo b) => BindingMatchesRole(b, role, accounts));
	}

	/// <summary>活动绑定与分组角色是否同一实例：角色名必须一致；账号能对上即命中
	/// （无论端口是否一致——代理启动页可能用记忆端口启动）；账号对不上或缺失时回退按端口匹配。</summary>
	private static bool BindingMatchesRole(ProxyCaptureService.ActiveBindingInfo? b, ProxyGroupRole? role, List<AccountStore.LocalAccount> accounts)
	{
		if (b == null || role == null || string.IsNullOrWhiteSpace(role.RoleName))
		{
			return false;
		}
		if (!string.Equals(b.LoginName, role.RoleName, StringComparison.Ordinal))
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(role.Account) && b.AuthAccountIndex >= 0 && accounts != null && b.AuthAccountIndex < accounts.Count)
		{
			AccountStore.LocalAccount acc = accounts[b.AuthAccountIndex];
			return string.Equals(acc?.Channel ?? "", role.Channel ?? "", StringComparison.Ordinal)
				&& string.Equals(acc?.Account ?? "", role.Account ?? "", StringComparison.OrdinalIgnoreCase);
		}
		return role.ListenPort > 0 && b.ListenPort == role.ListenPort;
	}

	/// <summary>查角色当前实际运行端口（代理启动页可能用不同端口启动同一角色）。</summary>
	private static int RunningPortForRole(ProxyGroupRole? role)
	{
		if (role == null)
		{
			return 0;
		}
		List<AccountStore.LocalAccount> accounts = AccountStore.All.ToList();
		ProxyCaptureService.ActiveBindingInfo b = ProxyCaptureService.ActiveBindingsSnapshot().FirstOrDefault((ProxyCaptureService.ActiveBindingInfo x) => BindingMatchesRole(x, role, accounts));
		if (b != null && b.ListenPort > 0)
		{
			return b.ListenPort;
		}
		return (role.ListenPort > 0 && ProxyCaptureService.ActivePorts.Contains(role.ListenPort)) ? role.ListenPort : 0;
	}

	private void UpdateStatus()
	{
		if (!ProxyCaptureService.IsRunning)
		{
			return;
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
		List<string> runningPresets = RunningPresetNamesInOrder();
		if (runningPresets.Count != 0)
		{
			text = text + "   |  服务器: " + string.Join("、", runningPresets);
		}
		LogHelper.Log("[快速启动] 运行中：" + text);
	}

	/// <summary>从代理服务的活动绑定推导运行中的服务器预设（服务端真值，避免页内记忆与实际状态不符）。</summary>
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
		StackPanel root = new StackPanel
		{
			Spacing = 10.0,
			MinWidth = 420.0
		};
		StackPanel rowsRoot = new StackPanel
		{
			Spacing = 10.0
		};
		foreach (IGrouping<string, ProxyCaptureService.ActiveBindingInfo> item in from b in list
				group b by ((!string.IsNullOrWhiteSpace(b.TargetHost)) ? (ServerPresets.All.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, b.TargetHost, StringComparison.OrdinalIgnoreCase))?.Name ?? b.TargetHost) : "未知服务器") into g
				select g)
		{
			ServerPreset serverPreset = ServerPresets.Find(item.Key);
			rowsRoot.Children.Add(new TextBlock
			{
				Text = "◆ " + item.Key + ((serverPreset == null) ? "" : $"（{serverPreset.Host}:{serverPreset.Port}）"),
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
		ScrollViewer content = new ScrollViewer
		{
			Content = rowsRoot,
			MaxHeight = 440.0,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Padding = new Thickness(0.0, 4.0, 4.0, 4.0)
		};
		root.Children.Add(content);
		ContentDialog contentDialog = new ContentDialog
		{
			Title = "查看端口",
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default),
			XamlRoot = base.XamlRoot
		};
		Button closeBtn = new Button
		{
			Content = "关闭",
			MinHeight = 36.0,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		closeBtn.Click += delegate
		{
			contentDialog.Hide();
		};
		root.Children.Add(closeBtn);
		contentDialog.Content = root;
		DialogGate.ShowAsync(contentDialog);
	}

	private static FrameworkElement BuildPortRow(string label, string address)
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
		return AccountStore.Mask(localAccount.Account);
	}

	// 主题笔刷统一走 ThemeHelper（App.xaml 未定义 ThemeDictionaries，本地查表在深色主题下会拿到浅色画笔）
	private static Brush? ThemeBrush(string key)
	{
		return ThemeHelper.Resolve(key);
	}

	private void AppendTail(string text)
	{
		LogHelper.Log("[快速启动] " + (text ?? "").TrimEnd());
	}

	private string CurrentConnectHost()
	{
		if (SettingsManager.Instance.AllowLanListen)
		{
			List<string> lanIps = GetLanIps();
			if (lanIps.Count > 0)
			{
				return lanIps[0];
			}
		}
		return "127.0.0.1";
	}

	private List<ProxyGroup> CurrentProxyGroups(ServerPreset? preset)
	{
		return new List<ProxyGroup>(SettingsManager.Instance.ProxyGroups ?? new List<ProxyGroup>());
	}

	private async void RoleCopy_Click(object sender, RoutedEventArgs e)
	{
		Button button = sender as Button;
		string tag2 = default(string);
		int num;
		if ((object)button != null)
		{
			object tag = button.Tag;
			tag2 = tag as string;
			if (tag2 != null)
			{
				num = ((!string.IsNullOrWhiteSpace(tag2)) ? 1 : 0);
				goto IL_005c;
			}
		}
		num = 0;
		goto IL_005c;
		IL_005c:
		if (num != 0)
		{
			DataPackage dataPackage = new DataPackage();
			dataPackage.SetText(tag2);
			Clipboard.SetContent(dataPackage);
			button.Content = "已复制";
			await Task.Delay(1500);
			button.Content = "复制";
		}
	}

	private async void NewGroup_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (await ShowGroupDialogAsync(null))
			{
				RefreshGroups();
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			try
			{
				LogHelper.Log("[新建分组] 异常: " + ex2);
			}
			catch
			{
			}
			MainWindow.Instance?.ShowToast("新建分组窗口打开失败，请稍后重试", false);
		}
	}

	private async void StartGroup_Click(object sender, RoutedEventArgs e)
	{
		if (_startGroupFlowBusy)
		{
			return;
		}
		_startGroupFlowBusy = true;
		try
		{
			List<ProxyGroup> list;
			while (true)
			{
				list = CurrentProxyGroups(null);
				if (list.Count != 0)
				{
					break;
				}
				if (await ShowGroupDialogAsync(null))
				{
					RefreshGroups();
					if (CurrentProxyGroups(null).Count == 0)
					{
						// 弹窗关闭后仍无可启动分组：直接退出，避免反复弹窗造成卡死
						return;
					}
					continue;
				}
				return;
			}
			await ShowGroupStartDialogAsync(list);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			LogHelper.Log("[启动分组] 异常: " + ex2);
			MainWindow.Instance?.ShowToast("启动分组流程异常：" + ex2.Message, false);
		}
		finally
		{
			_startGroupFlowBusy = false;
		}
	}

	private async Task<bool> ShowGroupStartDialogAsync(List<ProxyGroup> groups)
	{
		AppSettings settings = SettingsManager.Instance;
		HashSet<string> hashSet = new HashSet<string>(settings.LastAutoStartGroupNames ?? new List<string>(), StringComparer.Ordinal);
		bool flag = hashSet.Count == 0;
		ScrollViewer scrollViewer = new ScrollViewer
		{
			MaxHeight = 360.0
		};
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 4.0
		};
		List<CheckBox> checks = new List<CheckBox>();
		foreach (ProxyGroup group in groups)
		{
			ProxyGroup tag = group;
			int num = ((group.Roles != null) ? group.Roles.Count : 0);
			CheckBox item = new CheckBox
			{
				Content = group.Name + "（" + num + " 个角色会话）",
				Tag = tag,
				IsChecked = (flag || hashSet.Contains(group.Name ?? "")),
				FontSize = 13.0
			};
			checks.Add(item);
			stackPanel.Children.Add(item);
		}
		scrollViewer.Content = stackPanel;
		StackPanel stackPanel2 = new StackPanel
		{
			Spacing = 10.0
		};
		StackPanel stackPanel3 = new StackPanel
		{
			Spacing = 6.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
		};
		stackPanel3.Children.Add(DialogGate.CreateDragBar());
		stackPanel3.Children.Add(new TextBlock
		{
			Text = "勾选要启动的分组，程序会记住本次实际启动的角色与端口：下次程序启动时按同样方式恢复。确定后可在此页查看各组运行状态。",
			FontSize = 12.0,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap
		});
		stackPanel2.Children.Add(stackPanel3);
		Button toggleAllBtn = new Button
		{
			Content = "全选",
			FontSize = 13.0,
			MinHeight = 34.0,
			Padding = new Thickness(16.0, 0.0, 16.0, 0.0),
			HorizontalAlignment = HorizontalAlignment.Left
		};
		toggleAllBtn.Click += delegate
		{
			bool flag2 = checks.Count > 0 && checks.All((CheckBox c) => c.IsChecked == true);
			foreach (CheckBox item2 in checks)
			{
				item2.IsChecked = !flag2;
			}
			toggleAllBtn.Content = ((checks.Count > 0 && checks.All((CheckBox c) => c.IsChecked == true)) ? "取消全选" : "全选");
		};
		StackPanel stackPanel4 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		stackPanel4.Children.Add(toggleAllBtn);
		stackPanel4.Children.Add(new TextBlock
		{
			Text = "勾选后「确定启动」",
			FontSize = 12.0,
			VerticalAlignment = VerticalAlignment.Center,
			Opacity = 0.7
		});
		stackPanel2.Children.Add(scrollViewer);
		stackPanel2.Children.Add(stackPanel4);
		ToggleSwitch autoStartSwitch = new ToggleSwitch
		{
			Header = "自启动代理（与设置页同一个开关）",
			OnContent = "开",
			OffContent = "关",
			IsOn = SettingsManager.Instance.AutoStartProxyOnStartup,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		stackPanel2.Children.Add(autoStartSwitch);
		stackPanel2.Children.Add(new TextBlock
		{
			Text = "开启后，程序下次启动会按你这次实际启动的角色与端口恢复代理；关闭则不自动启动。",
			FontSize = 12.0,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap
		});
		if (await DialogGate.ShowAsync(new ContentDialog
		{
			Title = "启动分组",
			Content = stackPanel2,
			PrimaryButtonText = "确定启动",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default),
			XamlRoot = base.XamlRoot
		}) != ContentDialogResult.Primary)
		{
			return false;
		}
		try
		{
			AppSettings instance = SettingsManager.Instance;
			if (instance.AutoStartProxyOnStartup != autoStartSwitch.IsOn)
			{
				instance.AutoStartProxyOnStartup = autoStartSwitch.IsOn;
				SettingsManager.Save(instance);
			}
			SyncAutoStartProxySwitch();
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动分组] 同步自启动代理开关失败");
		}
		List<ProxyGroup> list = (from c in checks
			where c.IsChecked == true && c.Tag is ProxyGroup
			select (ProxyGroup)c.Tag).ToList();
		settings.LastAutoStartGroupNames = list.Select((ProxyGroup g) => g.Name ?? "").Distinct().ToList();
		SettingsManager.Save(settings);
		if (list.Count == 0)
		{
			MainWindow.Instance?.ShowToast("未勾选任何分组，本次不启动", false);
			return true;
		}
		try
		{
			await StartGroupsAsync(list);
			UpdateLastLaunchText();
		}
		catch (Exception ex2)
		{
			LogHelper.Log("[启动分组] 批量启动异常: " + ex2);
			MainWindow.Instance?.ShowToast("批量启动出现异常：" + ex2.Message, false);
		}
		return true;
	}

	private async Task StartGroupsAsync(List<ProxyGroup> groups)
	{
		if (groups == null || groups.Count == 0)
		{
			MainWindow.Instance?.ShowToast("暂无分组可启动", false);
			return;
		}
		int started = 0;
		foreach (ProxyGroup group in groups)
		{
			if (await StartGroupCoreAsync(group, silent: true))
			{
				started++;
			}
		}
		UpdateStatus();
		RefreshGroups();
		if (started > 0)
		{
			MainWindow.Instance?.ShowToast("已启动 " + started + " 个分组（" + groups.Count + " 个）", true);
		}
		else
		{
			MainWindow.Instance?.ShowToast("分组启动失败：没有可用的角色（账号可能已删除或未登录）", false);
		}
	}

	private FrameworkElement BuildQuickStartCreateButton(bool centered)
	{
		// 统一走同一个视觉：强调色实心按钮（虚线卡片在深色/浅色主题下对比度不稳，
		// 之前那版点上去没反馈，容易被当成「按了没反应」）。按钮点击立刻改文案给出反馈。
		Button CreateButton(double fontSize, Thickness padding, double minHeight)
		{
			Button b = new Button
			{
				Content = "＋ 创建分组",
				FontSize = fontSize,
				FontWeight = FontWeights.SemiBold,
				Padding = padding,
				MinHeight = minHeight,
				MinWidth = 0.0,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				HorizontalContentAlignment = HorizontalAlignment.Center,
				Style = AccentButtonStyle()
			};
			ToolTipService.SetToolTip(b, "选择服务器与角色编成组，之后可一键全部启动、固定端口");
			b.Click += QuickCreate_Click;
			return b;
		}

		if (!centered)
		{
			Button compact = CreateButton(14.0, new Thickness(16.0, 10.0, 16.0, 10.0), 40.0);
			compact.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
			return compact;
		}

		Border border = new Border
		{
			Margin = new Thickness(0.0, 24.0, 0.0, 0.0),
			Padding = new Thickness(20.0, 26.0, 20.0, 26.0),
			CornerRadius = new CornerRadius(14.0),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			Background = ThemeHelper.Resolve("CardBackgroundFillColorSecondaryBrush") ?? new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)),
			BorderBrush = ThemeHelper.Resolve("CardStrokeColorDefaultBrush") ?? new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
			BorderThickness = new Thickness(1.0)
		};
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 12.0,
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "还没有分组",
			FontSize = 17.0,
			FontWeight = FontWeights.SemiBold,
			HorizontalAlignment = HorizontalAlignment.Center
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = "创建分组：选择服务器与角色编成组，之后可一键全部启动、固定端口。",
			FontSize = 12.5,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap,
			TextAlignment = TextAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Stretch
		});
		stackPanel.Children.Add(CreateButton(15.0, new Thickness(18.0, 12.0, 18.0, 12.0), 44.0));
		border.Child = stackPanel;
		return border;
	}

	/// <summary>取强调色按钮样式；取不到就回退默认样式，绝不因样式缺失导致按钮不可用。</summary>
	private static Style? AccentButtonStyle()
	{
		try
		{
			if (Application.Current?.Resources != null
				&& Application.Current.Resources.TryGetValue("AccentButtonStyle", out object value)
				&& value is Style style)
			{
				return style;
			}
		}
		catch
		{
		}
		return null;
	}

	private async void QuickCreate_Click(object sender, RoutedEventArgs e)
	{
		// 点击立刻给出可见反馈：之前点了没有任何变化，用户会以为按钮失灵
		Button? btn = sender as Button;
		string? original = btn?.Content as string;
		if (btn != null)
		{
			btn.Content = "正在打开…";
			btn.IsEnabled = false;
		}
		try
		{
			await QuickCreateCoreAsync();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[创建分组] 打开失败");
			MainWindow.Instance?.ShowToast("创建分组窗口打开失败：" + ex.Message, false);
		}
		finally
		{
			if (btn != null)
			{
				btn.Content = original ?? "＋ 创建分组";
				btn.IsEnabled = true;
			}
		}
	}

	private async Task QuickCreateCoreAsync()
	{
		await ShowGroupDialogAsync(null);
	}

	private async Task<bool> StartGroupCoreAsync(ProxyGroup group, bool silent)
	{
		GroupStartResult groupStartResult = ProxyGroupStarter.StartGroup(group, silent ? null : new Action<string>(AppendTail));
		if (groupStartResult.ActivatedMissing)
		{
			if (!silent)
			{
				EnsureActivated();
			}
			SyncUiState();
			return false;
		}
		if (groupStartResult.PresetUnavailable)
		{
			if (!silent)
			{
				AppendTail("状态: 分组对应的服务器预设不可用\n");
			}
			return false;
		}
		if (!groupStartResult.Started)
		{
			if (!silent)
			{
				AppendTail("状态: 分组代理启动失败：端口可能被占用或没有可用角色\n");
			}
			SyncUiState();
			return false;
		}
		SyncUiState();
		return true;
	}

	private void RefreshGroups()
	{
		ServerPreset preset = SelectedPreset();
		List<ProxyGroup> list = CurrentProxyGroups(preset);
		_runningStateRefreshers.Clear();
		GroupsListPanel.Children.Clear();
		if (list.Count == 0)
		{
			GroupsPanel.Visibility = Visibility.Visible;
			GroupsHint.Text = "还没有分组：创建一个分组，把角色端口编成组，即可一键启动、固定端口。";
			GroupsListPanel.Children.Add(BuildQuickStartCreateButton(centered: true));
			StartGroupButton.IsEnabled = true;
			NewGroupButton.IsEnabled = true;
			return;
		}
		GroupsPanel.Visibility = Visibility.Visible;
		GroupsHint.Text = "共 " + list.Count + " 个分组（可混合两个服务器的角色）";
		string connectHost = CurrentConnectHost();
		foreach (ProxyGroup item in list)
		{
			GroupsListPanel.Children.Add(BuildGroupRow(item, connectHost));
		}
		GroupsListPanel.Children.Add(BuildQuickStartCreateButton(centered: false));
		StartGroupButton.IsEnabled = true;
		NewGroupButton.IsEnabled = true;
	}

	private async Task<bool> StartGroupRoleAsync(ProxyGroup g, ProxyGroupRole role, bool silent, bool refreshUi = true)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		if (g == null || role == null || string.IsNullOrWhiteSpace(role.RoleName) || string.IsNullOrWhiteSpace(role.Account))
		{
			return false;
		}
		if (!ActivationService.IsActivated)
		{
			if (!silent)
			{
				EnsureActivated();
			}
			return false;
		}
		ServerPreset server = ServerPresets.Find(role.ServerName) ?? ServerPresets.Find(g.ServerName);
		if (server == null || server.IsPending || string.IsNullOrWhiteSpace(server.Host))
		{
			if (!silent)
			{
				AppendTail("状态: 分组角色对应的服务器预设不可用\n");
			}
			return false;
		}
		List<AccountStore.LocalAccount> list = AccountStore.All.ToList();
		int index = ProxyStarter.ResolveAccountIndex(role, list, out var remapped);
		if (index < 0)
		{
			if (!silent)
			{
				AppendTail("状态: 「" + role.RoleName + "」账号已失效（请到账号管理重新登录覆盖登录态）\n");
			}
			return false;
		}
		if (!list[index].HasCookie)
		{
			if (!silent)
			{
				AppendTail("状态: 「" + role.RoleName + "」未保存登录凭证（请到账号管理重新登录）\n");
			}
			return false;
		}
		if (remapped)
		{
			role.Channel = list[index].Channel;
			role.Account = list[index].Account;
			SettingsManager.Save(SettingsManager.Instance);
			if (!silent)
			{
				AppendTail("状态: 「" + role.RoleName + "」已自动覆盖为当前有效登录态（" + list[index].Channel + ":" + list[index].Account + "）\n");
			}
		}
		if (role.ListenPort <= 0)
		{
			if (!silent)
			{
				AppendTail("状态: 「" + role.RoleName + "」未设置端口\n");
			}
			return false;
		}
		ProxyCaptureService.ProxyBinding proxyBinding = new ProxyCaptureService.ProxyBinding
		{
			ListenPort = role.ListenPort,
			LoginName = role.RoleName,
			TargetHost = server.Host,
			TargetPort = server.Port,
			AuthAccountIndex = index
		};
		List<ProxyCaptureService.ActiveBindingInfo> list2 = (from x in ProxyCaptureService.ActiveBindingsSnapshot()
			where x.AuthAccountIndex == index && string.Equals(x.LoginName, role.RoleName, StringComparison.Ordinal)
			select x).ToList();
		if (list2.Any((ProxyCaptureService.ActiveBindingInfo x) => x.ListenPort == role.ListenPort))
		{
			MainWindow.Instance?.ShowToast("该角色代理已在运行");
			return false;
		}
		if (list2.Count > 0)
		{
			if (!silent)
			{
				AppendTail($"状态: {role.RoleName} 已用端口 {list2[0].ListenPort} 在连，本组端口 {role.ListenPort} 去重跳过（重复连接会持续连接中止）\n");
			}
			return false;
		}
		ProxyCaptureService.ActiveBindingInfo activeBindingInfo = ProxyCaptureService.ActiveBindingsSnapshot().FirstOrDefault((ProxyCaptureService.ActiveBindingInfo x) => x.ListenPort == role.ListenPort && x.AuthAccountIndex != index);
		if (activeBindingInfo != null)
		{
			if (!silent)
			{
				AppendTail($"状态: 端口 {role.ListenPort} 已被其他代理（{activeBindingInfo.LoginName}）使用，跳过（不覆盖已有代理）\n");
			}
			MainWindow.Instance?.ShowToast("端口 " + role.ListenPort + " 已被其他代理占用，未覆盖已有代理", false);
			return false;
		}
		ProxyCaptureService.TargetFromHandshake = true;
		ProxyCaptureService.StartBatch(new ProxyCaptureService.ProxyBinding[1] { proxyBinding }, server.Host, server.Port);
		long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
		if (!ProxyCaptureService.IsRunning)
		{
			if (!silent)
			{
				AppendTail("状态: 代理启动失败：端口可能被占用\n");
			}
			return false;
		}
		ProxyStarter.RememberStarted(bindings: (from b in ProxyCaptureService.ActiveBindingsSnapshot()
			where string.Equals(b.TargetHost, server.Host, StringComparison.OrdinalIgnoreCase)
			select new ProxyCaptureService.ProxyBinding
			{
				ListenPort = b.ListenPort,
				LoginName = b.LoginName,
				TargetHost = b.TargetHost,
				TargetPort = b.TargetPort,
				AuthAccountIndex = b.AuthAccountIndex
			}).ToList(), preset: server);
		long value = stopwatch.ElapsedMilliseconds - elapsedMilliseconds;
		ProxyStarter.RememberLastLaunch("快速启动", "分组「" + g.Name + "」角色「" + role.RoleName + "」端口 " + role.ListenPort);
		AppendTail($"[{DateTime.Now:HH:mm:ss.fff}] 已开启分组「{g.Name}」角色「{role.RoleName}」代理 127.0.0.1:{role.ListenPort}（{server.Name}）\n");
		LogHelper.Log($"[性能] 分组角色「{role.RoleName}」启动总计 {stopwatch.ElapsedMilliseconds} ms（StartBatch={elapsedMilliseconds}，落盘={value}）");
		if (refreshUi)
		{
			QueueUiRefresh();
		}
		return true;
	}

	private async Task StopGroupRoleAsync(ProxyGroup g, ProxyGroupRole role, bool refreshUi = true)
	{
		if (g != null && role != null)
		{
			// 只停该角色自己的代理：优先按角色名+账号匹配实际运行端口
			//（兼容代理启动页用不同端口启动的同一角色），匹配不到再按分组记录端口。
			int port = RunningPortForRole(role);
			if (port <= 0)
			{
				port = role.ListenPort;
			}
			if (port > 0)
			{
				ProxyCaptureService.StopPort(port);
				AppendTail($"[{DateTime.Now:HH:mm:ss.fff}] 已停止分组「{g.Name}」角色「{role.RoleName}」代理（端口 {port}）\n");
			}
			if (refreshUi)
			{
				QueueUiRefresh();
			}
			await Task.CompletedTask;
		}
	}

	private UIElement BuildGroupRow(ProxyGroup g, string connectHost)
	{
		int count = ((g.Roles != null) ? g.Roles.Count : 0);
		Border border = new Border
		{
			Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
			BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(8.0),
			Padding = new Thickness(10.0, 8.0, 10.0, 8.0)
		};
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 6.0
		};
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10.0
		};
		TextBlock item = new TextBlock
		{
			Text = g.Name,
			FontSize = 13.0,
			FontWeight = FontWeights.Bold,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxLines = 1
		};
		TextBlock item2 = new TextBlock
		{
			Text = count + " 个角色会话",
			FontSize = 11.0,
			Opacity = 0.75,
			VerticalAlignment = VerticalAlignment.Center
		};
		ProxyGroup captured = g;
		List<CheckBox> roleChecks = new List<CheckBox>();
		List<string> roleKeys = new List<string>();
		Button selectAllBtn = new Button
		{
			Content = "全选",
			FontSize = 13.0,
			MinHeight = 32.0,
			Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		TextBlock item3 = new TextBlock
		{
			Text = "分组端口",
			FontSize = 11.0,
			Opacity = 0.7,
			VerticalAlignment = VerticalAlignment.Center
		};
		TextBox groupPortBox = new TextBox
		{
			Text = ((captured.BasePort > 0) ? captured.BasePort : SettingsManager.Instance.GroupPortBase).ToString(),
			Width = 92.0,
			FontSize = 13.0,
			FontFamily = new FontFamily("Consolas"),
			MaxLength = 5,
			VerticalContentAlignment = VerticalAlignment.Center,
			PlaceholderText = "起始端口"
		};
		Button actionBtn = new Button
		{
			Content = "启动",
			FontSize = 13.0,
			MinHeight = 32.0,
			Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			IsEnabled = (count > 0)
		};
		Button button = new Button
		{
			Content = "加入角色",
			FontSize = 13.0,
			MinHeight = 32.0,
			Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		Button button2 = new Button
		{
			Content = "删除",
			FontSize = 13.0,
			MinHeight = 32.0,
			Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		Action updateSelectAll = delegate
		{
			bool flag2 = roleChecks.Count == 0 || roleChecks.All((CheckBox c) => c.IsChecked == true);
			selectAllBtn.Content = (flag2 ? "取消" : "全选");
			bool flag3 = (g.Roles ?? new List<ProxyGroupRole>()).Any(IsRoleRunning);
			bool flag4 = roleChecks.Any((CheckBox c) => c.IsChecked == true);
			if (flag3)
			{
				actionBtn.Content = "停止";
			}
			else
			{
				actionBtn.Content = (flag4 ? "批量启动" : "启动");
			}
			actionBtn.IsEnabled = count > 0;
		};
		_runningStateRefreshers.Add(updateSelectAll);
		Action commitGroupPort = delegate
		{
			if (!_committingGroupPort && !(groupPortBox == null) && captured != null)
			{
				string text2 = groupPortBox.Text.Trim();
				if (text2.Length != 0 && int.TryParse(text2, out var result) && result >= 1 && result <= 65535)
				{
					captured.BasePort = result;
					ReallocateGroupRolePorts(captured, result);
					SettingsManager.Save(SettingsManager.Instance);
					MainWindow.Instance?.ShowToast("分组「" + captured.Name + "」端口已设为 " + result + "，组内角色端口已按低到高重新分配", true);
					_committingGroupPort = true;
					try
					{
						RefreshGroups();
						return;
					}
					finally
					{
						_committingGroupPort = false;
					}
				}
				groupPortBox.Text = ((captured.BasePort > 0) ? captured.BasePort : SettingsManager.Instance.GroupPortBase).ToString();
			}
		};
		groupPortBox.LostFocus += delegate
		{
			commitGroupPort();
		};
		groupPortBox.KeyDown += delegate(object _, KeyRoutedEventArgs keyArgs)
		{
			if (keyArgs.Key == VirtualKey.Enter)
			{
				commitGroupPort();
				keyArgs.Handled = true;
			}
		};
		selectAllBtn.Click += delegate
		{
			bool flag2 = roleChecks.Count > 0 && roleChecks.All((CheckBox c) => c.IsChecked == true);
			for (int num = 0; num < roleChecks.Count; num++)
			{
				roleChecks[num].IsChecked = !flag2;
				if (num < roleKeys.Count)
				{
					_groupRoleChecked[roleKeys[num]] = !flag2;
				}
			}
			PersistGroupRoleChecked();
			updateSelectAll();
		};
		actionBtn.Click += async delegate
		{
			if (EnsureActivated())
			{
				List<AccountStore.LocalAccount> accountsForStop = AccountStore.All.ToList();
				List<ProxyGroupRole> all = (g.Roles ?? new List<ProxyGroupRole>()).Where((ProxyGroupRole proxyGroupRole) => !string.IsNullOrWhiteSpace(proxyGroupRole.RoleName) && !string.IsNullOrWhiteSpace(proxyGroupRole.Account)).ToList();
				List<ProxyCaptureService.ActiveBindingInfo> binds = ProxyCaptureService.ActiveBindingsSnapshot().ToList();
				if (all.Any((ProxyGroupRole proxyGroupRole) => binds.Any((ProxyCaptureService.ActiveBindingInfo b) => BindingMatchesRole(b, proxyGroupRole, accountsForStop))))
				{
					int stopped = 0;
					foreach (ProxyGroupRole r2 in all)
					{
						if (binds.Any((ProxyCaptureService.ActiveBindingInfo b) => BindingMatchesRole(b, r2, accountsForStop)))
						{
							await StopGroupRoleAsync(captured, r2, refreshUi: false);
							stopped++;
						}
					}
					QueueUiRefresh();
					MainWindow.Instance?.ShowToast((stopped > 0) ? ("已停止 " + stopped + " 个角色代理") : "该组没有运行中的角色代理", stopped > 0);
				}
				else
				{
					List<ProxyGroupRole> selected = (from c in roleChecks
						where c.IsChecked == true && c.Tag is ProxyGroupRole
						select (ProxyGroupRole)c.Tag).ToList();
					if (selected.Count > 0)
					{
						int stopped2 = 0;
						int invalid = 0;
						foreach (ProxyGroupRole item5 in selected)
						{
							if (GroupCredentialWarning(item5) != null)
							{
								invalid++;
							}
							if (await StartGroupRoleAsync(captured, item5, silent: true, refreshUi: false))
							{
								stopped2++;
							}
						}
						QueueUiRefresh();
						if (invalid > 0)
						{
							MainWindow.Instance?.ShowToast("其中 " + invalid + " 个角色登录凭证失效（可能启动失败），请到账号管理重新验证绑定", false);
						}
					}
					else if (all.Count == 0)
					{
						MainWindow.Instance?.ShowToast("该组没有可用的角色会话", false);
					}
					else
					{
						int startedAll = 0;
						int invalidAll = 0;
						foreach (ProxyGroupRole item6 in all)
						{
							if (GroupCredentialWarning(item6) != null)
							{
								invalidAll++;
							}
							if (await StartGroupRoleAsync(captured, item6, silent: true, refreshUi: false))
							{
								startedAll++;
							}
						}
						QueueUiRefresh();
						if (invalidAll > 0)
						{
							MainWindow.Instance?.ShowToast("其中 " + invalidAll + " 个角色登录凭证失效（可能启动失败），请到账号管理重新验证绑定", false);
						}
					}
				}
			}
		};
		button.Click += async delegate
		{
			await ShowGroupDialogAsync(captured);
		};
		button2.Click += async delegate
		{
			if (await DialogGate.ShowAsync(new ContentDialog
			{
				Title = "删除分组",
				Content = "确定删除分组「" + g.Name + "」（" + count + " 个角色会话）？",
				PrimaryButtonText = "删除",
				CloseButtonText = "取消",
				DefaultButton = ContentDialogButton.Close,
				RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default),
				XamlRoot = base.XamlRoot
			}) == ContentDialogResult.Primary)
			{
				AppSettings instance = SettingsManager.Instance;
				_undo.Push(new UndoRepo
				{
					Kind = "group",
					Group = captured
				});
				instance.ProxyGroups.RemoveAll((ProxyGroup x) => x == captured);
				SettingsManager.Save(instance);
				string deadPrefix = captured.Name + "\0";
				foreach (string item7 in _groupRoleChecked.Keys.Where((string k) => k.StartsWith(deadPrefix, StringComparison.Ordinal)).ToList())
				{
					_groupRoleChecked.Remove(item7);
				}
				RefreshGroups();
				MainWindow.Instance?.ShowToast("已删除分组「" + captured.Name + "」，按 Ctrl+Z 可撤销", true);
			}
		};
		stackPanel2.Children.Add(item);
		stackPanel2.Children.Add(item2);
		stackPanel2.Children.Add(item3);
		stackPanel2.Children.Add(groupPortBox);
		stackPanel2.Children.Add(selectAllBtn);
		stackPanel2.Children.Add(actionBtn);
		stackPanel2.Children.Add(button);
		stackPanel2.Children.Add(button2);
		stackPanel.Children.Add(stackPanel2);
		if (g.Roles != null)
		{
			IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> source = ProxyCaptureService.ActiveBindingsSnapshot();
			Brush foreground = ThemeBrush("AccentFillColorDefaultBrush") ?? new SolidColorBrush(Color.FromArgb(byte.MaxValue, 64, 158, byte.MaxValue));
			foreach (ProxyGroupRole r in (from proxyGroupRole in g.Roles
				where proxyGroupRole != null && !string.IsNullOrWhiteSpace(proxyGroupRole.RoleName)
				orderby (proxyGroupRole.ListenPort <= 0) ? int.MaxValue : proxyGroupRole.ListenPort
				select proxyGroupRole).ThenBy<ProxyGroupRole, string>((ProxyGroupRole proxyGroupRole) => proxyGroupRole.RoleName ?? "", StringComparer.Ordinal))
			{
				ServerPreset serverPreset = ServerPresets.Find(r.ServerName) ?? ServerPresets.Find(g.ServerName);
				string serverLabel = serverPreset?.Name ?? g.ServerName ?? "";
			// 运行状态：按角色名+账号匹配活动绑定（兼容代理启动页用不同端口启动的同角色代理）
			List<AccountStore.LocalAccount> accountsForRow = AccountStore.All.ToList();
				bool flag = source.Any((ProxyCaptureService.ActiveBindingInfo b) => BindingMatchesRole(b, r, accountsForRow));
				int runningPort = (flag ? RunningPortForRole(r) : 0);
				string credWarn = GroupCredentialWarning(r);
				string text = serverPreset?.Name ?? g.ServerName ?? "";
				string rkey = captured.Name + "\0" + text + "\0" + r.Channel + "\0" + r.Account + "\0" + r.RoleName;
				roleKeys.Add(rkey);
				bool value = (_groupRoleChecked.TryGetValue(rkey, out var value2) ? value2 : string.Equals(text, "元素之诗", StringComparison.Ordinal));
				CheckBox pick = new CheckBox
				{
					IsChecked = value,
					Tag = r,
					VerticalAlignment = VerticalAlignment.Center
				};
				roleChecks.Add(pick);
				pick.Click += delegate
				{
					_groupRoleChecked[rkey] = pick.IsChecked == true;
					PersistGroupRoleChecked();
					updateSelectAll();
				};
				StackPanel stackPanel3 = new StackPanel
				{
					Spacing = 1.0
				};
				stackPanel3.Children.Add(new TextBlock
				{
					Text = r.RoleName,
					FontSize = 14.0,
					FontWeight = FontWeights.SemiBold,
					TextTrimming = TextTrimming.CharacterEllipsis,
					MaxLines = 1,
					VerticalAlignment = VerticalAlignment.Center
				});
				stackPanel3.Children.Add(new TextBlock
				{
					Text = serverLabel + " · " + r.Channel + ":" + r.Account,
					FontSize = 11.0,
					Opacity = 0.6,
					TextTrimming = TextTrimming.CharacterEllipsis,
					MaxLines = 1
				});
				if (credWarn != null)
				{
					stackPanel3.Children.Add(new TextBlock
					{
						Text = "⚠ " + credWarn,
						FontSize = 11.0,
						FontWeight = FontWeights.SemiBold,
						Foreground = (ThemeBrush("SystemFillColorCautionBrush") ?? new SolidColorBrush(Color.FromArgb(byte.MaxValue, 230, 160, 0))),
						TextTrimming = TextTrimming.CharacterEllipsis,
						MaxLines = 1
					});
				}
				TextBlock item4 = new TextBlock
				{
					Text = "●",
					FontSize = 12.0,
					FontWeight = FontWeights.Bold,
					Foreground = foreground,
					VerticalAlignment = VerticalAlignment.Center,
					Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible)
				};
				if (flag)
				{
					ToolTipService.SetToolTip(item4, "代理运行中" + ((runningPort > 0) ? ("：127.0.0.1:" + runningPort) : ""));
				}
				StackPanel stackPanel4 = new StackPanel
				{
					Orientation = Orientation.Horizontal,
					Spacing = 5.0
				};
				stackPanel4.Children.Add(item4);
				ProxyGroupRole capturedRolePort = r;
				TextBox portInput = new TextBox
				{
					Text = ((r.ListenPort > 0) ? r.ListenPort.ToString() : ""),
					Width = 78.0,
					FontSize = 13.0,
					FontFamily = new FontFamily("Consolas"),
					Padding = new Thickness(6.0, 2.0, 6.0, 2.0),
					VerticalAlignment = VerticalAlignment.Center,
					PlaceholderText = "端口",
					MinWidth = 0.0
				};
				portInput.LostFocus += delegate
				{
					CommitRolePortValue(portInput, capturedRolePort);
				};
				portInput.KeyDown += delegate(object _, KeyRoutedEventArgs keyArgs)
				{
					if (keyArgs.Key == VirtualKey.Enter)
					{
						CommitRolePortValue(portInput, capturedRolePort);
						keyArgs.Handled = true;
					}
				};
				stackPanel4.Children.Add(portInput);
				Button startRole = new Button
				{
					Content = (flag ? "停止" : "启动"),
					FontSize = 13.0,
					MinHeight = 32.0,
					Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
					VerticalAlignment = VerticalAlignment.Center
				};
				ProxyGroup capturedG = captured;
				ProxyGroupRole capturedRole = r;
				startRole.Click += async delegate
				{
					if (IsRoleRunning(capturedRole))
					{
						await StopGroupRoleAsync(capturedG, capturedRole);
					}
					else
					{
						CommitRolePortValue(portInput, capturedRolePort);
						if (EnsureActivated())
						{
							if (credWarn != null)
							{
								MainWindow.Instance?.ShowToast("「" + r.RoleName + "」" + credWarn + "，可能无法启动代理，请重新验证绑定", false);
							}
							await StartGroupRoleAsync(capturedG, capturedRole, silent: false);
						}
					}
				};
				// 复制按钮优先给实际运行端口（代理启动页可能用不同端口启动了该角色）
				Button button3 = new Button
				{
					Content = "复制",
					FontSize = 13.0,
					MinHeight = 32.0,
					Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
					VerticalAlignment = VerticalAlignment.Center,
					Tag = (((runningPort > 0) ? runningPort : ((r.ListenPort > 0) ? r.ListenPort : 0)) > 0 ? (connectHost + ":" + ((runningPort > 0) ? runningPort : r.ListenPort)) : "")
				};
				button3.Click += RoleCopy_Click;
				Button button4 = new Button
				{
					Content = "删除",
					FontSize = 13.0,
					MinHeight = 32.0,
					Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
					VerticalAlignment = VerticalAlignment.Center
				};
				ProxyGroupRole capturedRoleDel = r;
				button4.Click += delegate
				{
					if (capturedRoleDel.ListenPort > 0)
					{
						ProxyCaptureService.StopPort(capturedRoleDel.ListenPort);
					}
					_undo.Push(new UndoRepo
					{
						Kind = "role",
						Group = captured,
						Role = capturedRoleDel
					});
					captured.Roles.Remove(capturedRoleDel);
					string key = captured.Name + "\0" + serverLabel + "\0" + capturedRoleDel.Channel + "\0" + capturedRoleDel.Account + "\0" + capturedRoleDel.RoleName;
					_groupRoleChecked.Remove(key);
					SettingsManager.Save(SettingsManager.Instance);
					RefreshGroups();
					MainWindow.Instance?.ShowToast("已删除角色「" + capturedRoleDel.RoleName + "」（分组「" + captured.Name + "」），按 Ctrl+Z 可撤销", true);
				};
				Grid grid = new Grid
				{
					ColumnSpacing = 10.0,
					Padding = new Thickness(8.0, 6.0, 8.0, 6.0)
				};
				grid.ColumnDefinitions.Add(new ColumnDefinition
				{
					Width = GridLength.Auto
				});
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
				grid.ColumnDefinitions.Add(new ColumnDefinition
				{
					Width = GridLength.Auto
				});
				Grid.SetColumn(pick, 0);
				Grid.SetColumn(stackPanel3, 1);
				Grid.SetColumn(stackPanel4, 2);
				Grid.SetColumn(startRole, 3);
				Grid.SetColumn(button3, 4);
				Grid.SetColumn(button4, 5);
				grid.Children.Add(pick);
				grid.Children.Add(stackPanel3);
				grid.Children.Add(stackPanel4);
				grid.Children.Add(startRole);
				grid.Children.Add(button3);
				grid.Children.Add(button4);
				Border sessionBorder = new Border
				{
					Background = (ThemeBrush(flag ? "SubtleFillColorSecondaryBrush" : "ControlFillColorSecondaryBrush") ?? ThemeBrush("CardBackgroundFillColorDefaultBrush")),
					BorderBrush = (flag ? (ThemeBrush("AccentFillColorDefaultBrush") ?? ThemeBrush("ControlStrokeColorDefaultBrush")) : (ThemeBrush("ControlStrokeColorDefaultBrush") ?? ThemeBrush("CardStrokeColorDefaultBrush"))),
					BorderThickness = new Thickness(1.0),
					CornerRadius = new CornerRadius(8.0),
					Margin = new Thickness(0.0, 2.0, 0.0, 2.0),
					Child = grid
				};
				stackPanel.Children.Add(sessionBorder);
				ProxyGroupRole syncRole = r;
				_runningStateRefreshers.Add(delegate
				{
					bool flag2 = IsRoleRunning(syncRole);
					startRole.Content = (flag2 ? "停止" : "启动");
					sessionBorder.Background = ThemeBrush(flag2 ? "SubtleFillColorSecondaryBrush" : "ControlFillColorSecondaryBrush") ?? ThemeBrush("CardBackgroundFillColorDefaultBrush");
					sessionBorder.BorderBrush = (flag2 ? (ThemeBrush("AccentFillColorDefaultBrush") ?? ThemeBrush("ControlStrokeColorDefaultBrush")) : (ThemeBrush("ControlStrokeColorDefaultBrush") ?? ThemeBrush("CardStrokeColorDefaultBrush")));
				});
			}
			updateSelectAll();
		}
		border.Child = stackPanel;
		return border;
	}

	private bool EnsureActivated()
	{
		if (ActivationService.IsActivated)
		{
			return true;
		}
		MainWindow.Instance?.ShowToast("未激活，无法使用代理功能，请先在首页右侧「代理入口 / 激活」输入卡密激活", false);
		return false;
	}

	private void CommitRolePortValue(TextBox box, ProxyGroupRole role)
	{
		if (box == null || role == null)
		{
			return;
		}
		string text = box.Text.Trim();
		int result;
		if (string.IsNullOrWhiteSpace(text))
		{
			box.Text = ((role.ListenPort > 0) ? role.ListenPort.ToString() : "");
		}
		else if (!int.TryParse(text, out result) || result < 1 || result > 65535)
		{
			box.Text = ((role.ListenPort > 0) ? role.ListenPort.ToString() : "");
			MainWindow.Instance?.ShowToast("端口无效（1-65535）", false);
		}
		else if (result != role.ListenPort)
		{
			HashSet<int> hashSet = CollectAllUsedPorts();
			hashSet.Remove(role.ListenPort);
			if (hashSet.Contains(result))
			{
				box.Text = ((role.ListenPort > 0) ? role.ListenPort.ToString() : "");
				MainWindow.Instance?.ShowToast("端口 " + result + " 已被其他账号/角色使用", false);
			}
			else
			{
				role.ListenPort = result;
				SettingsManager.Save(SettingsManager.Instance);
				MainWindow.Instance?.ShowToast("已记住端口 " + result, true);
			}
		}
	}

	private string? GroupCredentialWarning(ProxyGroupRole role)
	{
		List<AccountStore.LocalAccount> list = AccountStore.All.ToList();
		int num = ProxyStarter.ResolveAccountIndex(role, list, out var remapped);
		if (num < 0)
		{
			return "账号已失效";
		}
		if (remapped)
		{
			return null;
		}
		AccountStore.LocalAccount localAccount = list[num];
		if (!localAccount.HasCookie)
		{
			return "未保存登录凭证";
		}
		return null;
	}

	private HashSet<int> CollectAllUsedPorts()
	{
		HashSet<int> hashSet = new HashSet<int>();
		foreach (ProxyCaptureService.ActiveBindingInfo item in ProxyCaptureService.ActiveBindingsSnapshot())
		{
			if (item.ListenPort > 0)
			{
				hashSet.Add(item.ListenPort);
			}
		}
		AppSettings instance = SettingsManager.Instance;
		if (instance.ProxyGroups != null)
		{
			foreach (ProxyGroup proxyGroup in instance.ProxyGroups)
			{
				if (proxyGroup.Roles == null)
				{
					continue;
				}
				foreach (ProxyGroupRole role in proxyGroup.Roles)
				{
					if (role.ListenPort > 0)
					{
						hashSet.Add(role.ListenPort);
					}
				}
			}
		}
		return hashSet;
	}

	private static bool IsPortReallyUsed(int port, HashSet<int> activePorts, HashSet<int> groupPorts)
	{
		if (port <= 0)
		{
			return false;
		}
		if (!activePorts.Contains(port))
		{
			return groupPorts.Contains(port);
		}
		return true;
	}

	private int DefaultGroupPort(ServerPreset preset, int accountIndex, AccountStore.LocalAccount acc, string roleName, HashSet<int> used, out string source, int baseOrigin = -1)
	{
		ProxyCaptureService.ActiveBindingInfo activeBindingInfo = ProxyCaptureService.ActiveBindingsSnapshot().FirstOrDefault((ProxyCaptureService.ActiveBindingInfo b) => b.AuthAccountIndex == accountIndex && string.Equals(b.LoginName, roleName, StringComparison.Ordinal));
		if (activeBindingInfo != null && activeBindingInfo.ListenPort > 0)
		{
			source = "运行中";
			return activeBindingInfo.ListenPort;
		}
		AppSettings instance = SettingsManager.Instance;
		HashSet<int> activePorts = new HashSet<int>(from b in ProxyCaptureService.ActiveBindingsSnapshot()
			select b.ListenPort into p
			where p > 0
			select p);
		HashSet<int> hashSet = new HashSet<int>();
		if (instance.ProxyGroups != null)
		{
			foreach (ProxyGroup proxyGroup in instance.ProxyGroups)
			{
				if (proxyGroup.Roles == null)
				{
					continue;
				}
				foreach (ProxyGroupRole role in proxyGroup.Roles)
				{
					if (role.ListenPort > 0)
					{
						hashSet.Add(role.ListenPort);
					}
				}
			}
		}
		List<SavedProxyBinding> list = new List<SavedProxyBinding>();
		if (instance.LastProxyBindings != null)
		{
			list.AddRange(instance.LastProxyBindings);
		}
		if (instance.RemovedAccountPortMemory != null)
		{
			list.AddRange(instance.RemovedAccountPortMemory);
		}
		int candidate;
		int result = (candidate = ((baseOrigin >= 1 && baseOrigin <= 65535) ? baseOrigin : CurrentGroupBasePort()));
		for (int num = 0; num < 1024; num++)
		{
			if (candidate < 1)
			{
				break;
			}
			if (candidate > 65535)
			{
				break;
			}
			if (!used.Contains(candidate) && !IsPortReallyUsed(candidate, activePorts, hashSet))
			{
				ProxyGroupRole proxyGroupRole = FindGroupRole(instance, preset, acc, roleName, out string _);
				if (proxyGroupRole != null && proxyGroupRole.ListenPort > 0)
				{
					used.Remove(proxyGroupRole.ListenPort);
					source = "分组";
					return proxyGroupRole.ListenPort;
				}
				SavedProxyBinding savedProxyBinding = list.FirstOrDefault((SavedProxyBinding m) => m.ListenPort == candidate);
				if (savedProxyBinding != null && !string.Equals(savedProxyBinding.RoleName, roleName, StringComparison.Ordinal))
				{
					SavedProxyBinding savedProxyBinding2 = list.FirstOrDefault((SavedProxyBinding m) => string.Equals(m.RoleName, roleName, StringComparison.Ordinal) && string.Equals(m.ServerName, preset.Name, StringComparison.Ordinal) && m.ListenPort > 0 && m.ListenPort != candidate);
					if (savedProxyBinding2 != null && !used.Contains(savedProxyBinding2.ListenPort))
					{
						used.Remove(savedProxyBinding.ListenPort);
						used.Add(savedProxyBinding2.ListenPort);
						source = "分组";
						return savedProxyBinding2.ListenPort;
					}
				}
				used.Add(candidate);
				source = "分组";
				return candidate;
			}
			candidate++;
		}
		source = "自动";
		return result;
	}

	private int CurrentGroupBasePort()
	{
		if (SettingsManager.Instance.GroupPortBase < 1 || SettingsManager.Instance.GroupPortBase > 65535)
		{
			return 20000;
		}
		return SettingsManager.Instance.GroupPortBase;
	}

	private void ReallocateGroupRolePorts(ProxyGroup g, int basePort)
	{
		if (g?.Roles == null || g.Roles.Count == 0)
		{
			return;
		}
		List<ProxyCaptureService.ActiveBindingInfo> source = ProxyCaptureService.ActiveBindingsSnapshot().ToList();
		HashSet<int> hashSet = new HashSet<int>(CollectAllUsedPorts());
		foreach (ProxyGroupRole role in g.Roles)
		{
			if (role.ListenPort > 0)
			{
				hashSet.Remove(role.ListenPort);
			}
		}
		int num = basePort;
		for (int i = 0; i < g.Roles.Count; i++)
		{
			ProxyGroupRole r = g.Roles[i];
			if (r.ListenPort > 0 && source.Any((ProxyCaptureService.ActiveBindingInfo b) => b.ListenPort == r.ListenPort && string.Equals(b.LoginName, r.RoleName, StringComparison.Ordinal)))
			{
				if (r.ListenPort > 0)
				{
					hashSet.Add(r.ListenPort);
				}
				continue;
			}
			for (; num <= 65535 && hashSet.Contains(num); num++)
			{
			}
			if (num <= 65535)
			{
				r.ListenPort = num;
				hashSet.Add(num);
				num++;
				continue;
			}
			break;
		}
	}

	public async Task<bool> ShowGroupDialogAsync(ProxyGroup? existing, (string Channel, string Account)? accountFilter = null, string? presetOverride = null, XamlRoot? hostRoot = null)
	{
		List<ServerPreset> list = (from p in ServerPresets.All
			where !p.IsPending && !string.IsNullOrWhiteSpace(p.Host)
			orderby (!string.Equals(p.Name, "元素之诗", StringComparison.Ordinal)) ? 1 : 0
			select p).ToList();
		if (list.Count == 0)
		{
			MainWindow.Instance?.ShowToast("暂无可用服务器预设", false);
			return false;
		}
		AppSettings settings = SettingsManager.Instance;
		ServerPreset fallbackPreset = ((presetOverride != null) ? ServerPresets.Find(presetOverride) : null);
		if (fallbackPreset == null && existing != null && !string.IsNullOrWhiteSpace(existing.ServerName))
		{
			fallbackPreset = ServerPresets.Find(existing.ServerName);
		}
		if (fallbackPreset == null)
		{
			fallbackPreset = SelectedPreset();
		}
		if (fallbackPreset == null || fallbackPreset.IsPending || !list.Contains(fallbackPreset))
		{
			fallbackPreset = list[0];
		}
		HashSet<int> externalUsed = CollectAllUsedPorts();
		HashSet<int> hashSet = new HashSet<int>(externalUsed);
		List<AccountStore.LocalAccount> list2 = AccountStore.All.ToList();
		Dictionary<int, List<string>> externalOwners = new Dictionary<int, List<string>>();
		foreach (ProxyCaptureService.ActiveBindingInfo b in ProxyCaptureService.ActiveBindingsSnapshot())
		{
			string serverName = "";
			if (!string.IsNullOrWhiteSpace(b.TargetHost))
			{
				ServerPreset serverPreset = list.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, b.TargetHost, StringComparison.OrdinalIgnoreCase));
				if (serverPreset != null)
				{
					serverName = serverPreset.Name;
				}
			}
			AddOwner(text2: (b.AuthAccountIndex >= 0 && b.AuthAccountIndex < list2.Count) ? (list2[b.AuthAccountIndex].Channel ?? "") : "", text3: (b.AuthAccountIndex >= 0 && b.AuthAccountIndex < list2.Count) ? (list2[b.AuthAccountIndex].Account ?? "") : "", port: b.ListenPort, text: serverName, role: b.LoginName ?? "");
		}
		foreach (SavedProxyBinding item8 in settings.LastProxyBindings ?? new List<SavedProxyBinding>())
		{
			AddOwner(item8.ListenPort, item8.ServerName ?? "", item8.Channel ?? "", item8.Account ?? "", item8.RoleName ?? "");
		}
		foreach (SavedProxyBinding item9 in settings.RemovedAccountPortMemory ?? new List<SavedProxyBinding>())
		{
			AddOwner(item9.ListenPort, item9.ServerName ?? "", item9.Channel ?? "", item9.Account ?? "", item9.RoleName ?? "");
		}
		foreach (ProxyGroup item10 in settings.ProxyGroups ?? new List<ProxyGroup>())
		{
			foreach (ProxyGroupRole item11 in item10.Roles ?? new List<ProxyGroupRole>())
			{
				AddOwner(item11.ListenPort, (!string.IsNullOrWhiteSpace(item11.ServerName)) ? item11.ServerName : item10.ServerName, item11.Channel ?? "", item11.Account ?? "", item11.RoleName ?? "");
			}
		}
		List<AccountStore.LocalAccount> list3 = list2.Where((AccountStore.LocalAccount a) => a.IsLoggedIn).ToList();
		if (accountFilter.HasValue)
		{
			list3 = list3.Where((AccountStore.LocalAccount a) => string.Equals(a.Channel, accountFilter.Value.Channel, StringComparison.Ordinal) && string.Equals(a.Account, accountFilter.Value.Account, StringComparison.OrdinalIgnoreCase)).ToList();
		}
		TextBlock nameWarning = new TextBlock
		{
			Text = "⚠ 分组名称不能为空，请填写名称或再次保存将自动命名",
			FontSize = 12.0,
			Foreground = new SolidColorBrush(Color.FromArgb(byte.MaxValue, byte.MaxValue, 69, 0)),
			Visibility = Visibility.Collapsed,
			TextWrapping = TextWrapping.Wrap
		};
		TextBox nameBox = new TextBox
		{
			PlaceholderText = "分组名称，如：五开A",
			Text = (existing?.Name ?? ""),
			MinWidth = 320.0
		};
		TextBlock portWarning = new TextBlock
		{
			FontSize = 12.0,
			Foreground = new SolidColorBrush(Color.FromArgb(byte.MaxValue, byte.MaxValue, 69, 0)),
			Visibility = Visibility.Collapsed,
			TextWrapping = TextWrapping.Wrap
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 12.0
		};
		List<(ServerPreset Server, StackPanel Panel)> serverViews = new List<(ServerPreset, StackPanel)>();
		List<(ServerPreset Server, int AccIdx, AccountStore.LocalAccount Acc, string Role, CheckBox Check, TextBox Port, int DefPort)> rows = new List<(ServerPreset, int, AccountStore.LocalAccount, string, CheckBox, TextBox, int)>();
		bool updatingChecks = false;
		Dictionary<(ServerPreset Server, AccountStore.LocalAccount Acc), CheckBox> accountHeads = new Dictionary<(ServerPreset, AccountStore.LocalAccount), CheckBox>();
		int baseOrigin = ((existing != null && existing.BasePort >= 1 && existing.BasePort <= 65535) ? existing.BasePort : CurrentGroupBasePort());
		foreach (ServerPreset sp in list)
		{
			StackPanel stackPanel2 = new StackPanel
			{
				Spacing = 6.0
			};
			List<CheckBox> serverHeads = new List<CheckBox>();
			StackPanel stackPanel3 = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Spacing = 6.0,
				Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
			};
			ServerPreset serverForBar = sp;
			Button button = new Button
			{
				Content = "全选",
				FontSize = 13.0,
				MinHeight = 34.0,
				Padding = new Thickness(16.0, 0.0, 16.0, 0.0)
			};
			Button button2 = new Button
			{
				Content = "清空",
				FontSize = 13.0,
				MinHeight = 34.0,
				Padding = new Thickness(16.0, 0.0, 16.0, 0.0)
			};
			button.Click += delegate
			{
				foreach (var item28 in rows)
				{
					if (item28.Server == serverForBar)
					{
						item28.Check.IsChecked = true;
					}
				}
				foreach (CheckBox item29 in serverHeads)
				{
					item29.IsChecked = true;
				}
				UpdatePortWarnings();
				SyncAllAccountHeads();
			};
			button2.Click += delegate
			{
				foreach (var item30 in rows)
				{
					if (item30.Server == serverForBar)
					{
						item30.Check.IsChecked = false;
					}
				}
				foreach (CheckBox item31 in serverHeads)
				{
					item31.IsChecked = false;
				}
				UpdatePortWarnings();
				SyncAllAccountHeads();
			};
			stackPanel3.Children.Add(button);
			stackPanel3.Children.Add(button2);
			stackPanel2.Children.Add(stackPanel3);
			stackPanel2.Children.Add(new TextBlock
			{
				Text = $"◆ {sp.Name}（{sp.Host}:{sp.Port}）",
				FontSize = 12.0,
				FontWeight = FontWeights.SemiBold,
				Opacity = 0.85,
				Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
			});
			bool flag = false;
			foreach (AccountStore.LocalAccount item16 in list3)
			{
				List<string> list5 = new List<string>();
				if (item16.ServerRoles != null && !sp.IsPending && item16.ServerRoles.TryGetValue(sp.Name, out var value) && value != null)
				{
					list5.AddRange(value);
				}
				if (item16.ServerRoles != null && item16.ServerRoles.TryGetValue("未分组", out var value2) && value2 != null)
				{
					list5.AddRange(value2);
				}
				list5 = list5.Distinct().OrderBy<string, string>((string result) => result, StringComparer.Ordinal).ToList();
				if (list5.Count == 0)
				{
					continue;
				}
				flag = true;
				CheckBox accHead = new CheckBox
				{
					IsChecked = string.Equals(sp.Name, "元素之诗", StringComparison.Ordinal),
					IsThreeState = true,
					FontWeight = FontWeights.SemiBold,
					VerticalAlignment = VerticalAlignment.Center,
					Tag = item16
				};
				TextBlock content = new TextBlock
				{
					Text = item16.Channel + " · " + (string.IsNullOrWhiteSpace(item16.Nickname) ? AccountStore.Mask(item16.Account ?? "") : item16.Nickname),
					FontSize = 13.0,
					TextTrimming = TextTrimming.CharacterEllipsis,
					MaxLines = 1,
					MaxWidth = 320.0,
					VerticalAlignment = VerticalAlignment.Center
				};
				accHead.Content = content;
				serverHeads.Add(accHead);
				accountHeads[(sp, item16)] = accHead;
				StackPanel stackPanel4 = new StackPanel
				{
					Spacing = 3.0,
					Margin = new Thickness(28.0, 0.0, 0.0, 6.0)
				};
				foreach (string item17 in list5)
				{
					int num = Math.Max(0, list2.IndexOf(item16));
					ProxyGroupRole proxyGroupRole = FindGroupRole(settings, sp, item16, item17, out string sourceGroup);
					int num2;
					string source;
					if (proxyGroupRole != null && proxyGroupRole.ListenPort > 0)
					{
						num2 = proxyGroupRole.ListenPort;
						source = "固定";
					}
					else
					{
						num2 = DefaultGroupPort(sp, num, item16, item17, hashSet, out source, baseOrigin);
					}
					if (num2 >= 1 && num2 <= 65535)
					{
						hashSet.Add(num2);
					}
					CheckBox checkBox = new CheckBox
					{
						IsChecked = string.Equals(sp.Name, "元素之诗", StringComparison.Ordinal),
						VerticalAlignment = VerticalAlignment.Center
					};
					string placeholderText = source;
					if (proxyGroupRole != null && proxyGroupRole.ListenPort > 0)
					{
						placeholderText = ((!string.IsNullOrWhiteSpace(sourceGroup) && !string.Equals(sourceGroup, existing?.Name ?? "", StringComparison.Ordinal)) ? ("复用「" + sourceGroup + "」端口") : "固定");
					}
					TextBox textBox = new TextBox
					{
						Text = ((num2 > 0) ? num2.ToString() : ""),
						PlaceholderText = placeholderText,
						Width = 96.0,
						MinHeight = 28.0,
						VerticalAlignment = VerticalAlignment.Center,
						FontSize = 12.0
					};
					TextBlock textBlock = new TextBlock
					{
						Text = item17,
						FontSize = 12.0,
						VerticalAlignment = VerticalAlignment.Center,
						TextWrapping = TextWrapping.Wrap
					};
					Grid grid = new Grid
					{
						ColumnSpacing = 8.0,
						Margin = new Thickness(0.0, 1.0, 0.0, 1.0)
					};
					grid.ColumnDefinitions.Add(new ColumnDefinition
					{
						Width = GridLength.Auto
					});
					grid.ColumnDefinitions.Add(new ColumnDefinition
					{
						Width = new GridLength(1.0, GridUnitType.Star)
					});
					grid.ColumnDefinitions.Add(new ColumnDefinition
					{
						Width = GridLength.Auto
					});
					Grid.SetColumn(checkBox, 0);
					Grid.SetColumn(textBlock, 1);
					Grid.SetColumn(textBox, 2);
					grid.Children.Add(checkBox);
					grid.Children.Add(textBlock);
					grid.Children.Add(textBox);
					stackPanel4.Children.Add(grid);
					rows.Add((sp, num, item16, item17, checkBox, textBox, num2));
					sourceGroup = null;
					source = null;
				}
				AccountStore.LocalAccount capturedAcc = item16;
				accHead.Click += delegate
				{
					if (!updatingChecks)
					{
						bool? isChecked = accHead.IsChecked;
						foreach (var item32 in rows)
						{
							if (item32.Server == sp && item32.Acc == capturedAcc)
							{
								item32.Check.IsChecked = isChecked;
							}
						}
						UpdatePortWarnings();
						SyncAccountHead(sp, capturedAcc);
					}
				};
				StackPanel stackPanel5 = new StackPanel
				{
					Spacing = 2.0
				};
				stackPanel5.Children.Add(accHead);
				stackPanel5.Children.Add(stackPanel4);
				stackPanel2.Children.Add(stackPanel5);
				value = null;
				value2 = null;
			}
			if (!flag)
			{
				stackPanel2.Children.Add(new TextBlock
				{
					Text = "该服务器下没有可添加的角色",
					FontSize = 12.0,
					Opacity = 0.7
				});
			}
			stackPanel2.Visibility = ((sp != fallbackPreset) ? Visibility.Collapsed : Visibility.Visible);
			serverViews.Add((sp, stackPanel2));
		}
		// 服务器预设选择改为下拉列表（与「更新客户端」页一致）：内置预设 + 服务器列表页新添加的预设都在选项里，
		// 额外预设带删除按钮，避免原竖排单选又长又难找。
		ComboBox presetCombo = new ComboBox
		{
			MinWidth = 260.0,
			FontSize = 13.0,
			HorizontalAlignment = HorizontalAlignment.Left
		};
		ThemeHelper.SolidComboPopup(presetCombo);
		Button delPresetBtn = new Button
		{
			Content = "🗑 删除该预设",
			FontSize = 12.0,
			MinHeight = 32.0
		};
		ToolTipService.SetToolTip(delPresetBtn, "从服务器预设中移除当前选中的额外预设（内置预设不可删，不影响已创建的分组）");
		StackPanel presetRow2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		presetRow2.Children.Add(presetCombo);
		presetRow2.Children.Add(delPresetBtn);
		foreach (ServerPreset sp2 in list)
		{
			ComboBoxItem comboItem = new ComboBoxItem
			{
				Content = sp2.Name + "（" + sp2.Host + ":" + sp2.Port + "）" + (ServerPresets.IsBuiltin(sp2) ? "" : "　· 额外预设"),
				Tag = sp2.Name
			};
			presetCombo.Items.Add(comboItem);
			if (sp2 == fallbackPreset)
			{
				presetCombo.SelectedItem = comboItem;
			}
		}
		if (presetCombo.SelectedItem == null && presetCombo.Items.Count > 0)
		{
			presetCombo.SelectedIndex = 0;
		}
		presetCombo.SelectionChanged += delegate
		{
			string selName = (presetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
			foreach (var viewItem in serverViews)
			{
				viewItem.Panel.Visibility = ((!string.Equals(viewItem.Server.Name, selName, StringComparison.Ordinal)) ? Visibility.Collapsed : Visibility.Visible);
			}
			ServerPreset? selPreset = ServerPresets.Find(selName);
			delPresetBtn.IsEnabled = selPreset != null && !ServerPresets.IsBuiltin(selPreset);
		};
		delPresetBtn.Click += async delegate
		{
			string delName = (presetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
			ServerPreset? delTarget = ServerPresets.Find(delName);
			if (delTarget == null || ServerPresets.IsBuiltin(delTarget))
			{
				return;
			}
			ContentDialog confirm = new ContentDialog
			{
				Title = "删除服务器预设",
				Content = "确定删除「" + delTarget.Name + "」（" + delTarget.Host + ":" + delTarget.Port + "）的预设吗？",
				PrimaryButtonText = "删除",
				CloseButtonText = "取消",
				DefaultButton = ContentDialogButton.Close,
				RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default)
			};
			if (await DialogGate.ShowAsync(confirm) != ContentDialogResult.Primary)
			{
				return;
			}
			if (!ServerPresets.Remove(delTarget))
			{
				MainWindow.Instance?.ShowToast("删除失败：该预设不可移除", false);
				return;
			}
			MainWindow.Instance?.ShowToast("已删除预设「" + delTarget.Name + "」", true);
			FindAncestorDialog(delPresetBtn)?.Hide();
			RebuildQuickPresetRadios();
			_ = ShowGroupDialogAsync(existing, accountFilter, presetOverride);
		};
		delPresetBtn.IsEnabled = !ServerPresets.IsBuiltin(fallbackPreset);
		stackPanel.Children.Add(presetRow2);
		StackPanel stackPanel6 = new StackPanel
		{
			Spacing = 4.0
		};
		foreach (var item19 in serverViews)
		{
			stackPanel6.Children.Add(item19.Panel);
		}
		ScrollViewer scroller = new ScrollViewer
		{
			Content = stackPanel6,
			Height = 300.0,
			Padding = new Thickness(0.0, 0.0, 0.0, 0.0),
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		foreach (var r in rows)
		{
			r.Check.Click += delegate
			{
				UpdatePortWarnings();
				SyncAccountHead(r.Server, r.Acc);
			};
			r.Port.TextChanged += delegate
			{
				UpdatePortWarnings();
			};
		}
		ApplyPreselect();
		StackPanel stackPanel7 = new StackPanel
		{
			Spacing = 4.0
		};
		stackPanel7.Children.Add(nameBox);
		stackPanel7.Children.Add(nameWarning);
		stackPanel7.Children.Add(stackPanel);
		stackPanel7.Children.Add(new TextBlock
		{
			Text = "选择服务器：",
			FontSize = 11.0,
			Opacity = 0.7
		});
		StackPanel stackPanel8 = new StackPanel
		{
			Spacing = 10.0
		};
		stackPanel8.Children.Add(DialogGate.CreateDragBar("按住此条拖动窗口"));
		stackPanel8.Children.Add(stackPanel7);
		stackPanel8.Children.Add(scroller);
		stackPanel8.Children.Add(portWarning);
		stackPanel8.Children.Add(new TextBlock
		{
			Text = "勾选角色并设置端口，保存后加入分组。默认端口按基础端口叠加分配，重复或已占用端口会被阻止保存；\n分组可混合加入两个服务器的角色。",
			FontSize = 11.0,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap
		});
		ContentDialog contentDialog = new ContentDialog
		{
			Title = ((existing == null) ? "新建分组" : "编辑分组"),
			Content = stackPanel8,
			PrimaryButtonText = "保存分组",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default),
			XamlRoot = (hostRoot ?? base.XamlRoot)
		};
		bool nameInvalid = false;
		bool didSave = false;
		contentDialog.PrimaryButtonClick += async delegate(ContentDialog _, ContentDialogButtonClickEventArgs args)
		{
			string text = nameBox.Text.Trim();
			if (string.IsNullOrWhiteSpace(text))
			{
				string text2 = ((settings.ProxyGroups != null && settings.ProxyGroups.Count > 0) ? "3.1415926" : "114514");
				if (!nameInvalid)
				{
					nameInvalid = true;
					nameWarning.Visibility = Visibility.Visible;
					AnimateShake(nameBox);
					MainWindow.Instance?.ShowToast("分组名称不能为空，请填写名称（或再点一次保存自动命名 " + text2 + "）", false);
					args.Cancel = true;
					return;
				}
				nameBox.Text = text2;
				text = text2;
			}
			else if (nameInvalid)
			{
				nameInvalid = false;
				nameWarning.Visibility = Visibility.Collapsed;
			}
			ProxyGroup proxyGroup;
			bool flag2;
			if (existing != null)
			{
				proxyGroup = existing;
				flag2 = false;
			}
			else
			{
				proxyGroup = new ProxyGroup();
				flag2 = true;
			}
			List<ProxyGroupRole> list6 = new List<ProxyGroupRole>();
			HashSet<int> hashSet2 = new HashSet<int>();
			List<string> list7 = new List<string>();
			List<ProxyGroupRole> list8 = new List<ProxyGroupRole>();
			if (!flag2 && existing.Roles != null)
			{
				HashSet<string> hashSet3 = new HashSet<string>();
				foreach (var item21 in rows)
				{
					hashSet3.Add(RoleKey(item21.Server.Name, item21.Acc, item21.Role));
				}
				foreach (ProxyGroupRole role in existing.Roles)
				{
					string text3 = ((!string.IsNullOrWhiteSpace(role.ServerName)) ? role.ServerName : (existing.ServerName ?? ""));
					string item22 = text3 + "\0" + role.Channel + "\0" + role.Account + "\0" + role.RoleName;
					if (!hashSet3.Contains(item22))
					{
						list8.Add(role);
						if (role.ListenPort > 0 && role.ListenPort <= 65535)
						{
							hashSet2.Add(role.ListenPort);
						}
					}
				}
			}
			for (int i = 0; i < rows.Count; i++)
			{
				if (rows[i].Check.IsChecked == true)
				{
					(ServerPreset Server, int AccIdx, AccountStore.LocalAccount Acc, string Role, CheckBox Check, TextBox Port, int DefPort) tuple = rows[i];
					ServerPreset item23 = tuple.Server;
					AccountStore.LocalAccount item24 = tuple.Acc;
					string item25 = tuple.Role;
					TextBox item26 = tuple.Port;
					int item27 = tuple.DefPort;
					string text4 = item26.Text.Trim();
					int result;
					if (string.IsNullOrWhiteSpace(text4))
					{
						result = ((item27 >= 1 && item27 <= 65535) ? item27 : 0);
					}
					else if (!int.TryParse(text4, out result) || result < 1 || result > 65535)
					{
						result = 0;
					}
					if (result < 1 || result > 65535)
					{
						list7.Add("「" + item25 + "」端口无效：" + (string.IsNullOrWhiteSpace(text4) ? "留空" : text4));
					}
					else if (!hashSet2.Add(result))
					{
						list7.Add("端口 " + result + " 在分组内重复：" + item25);
					}
					else
					{
						list6.Add(new ProxyGroupRole
						{
							Channel = (item24.Channel ?? ""),
							Account = (item24.Account ?? ""),
							RoleName = item25,
							ServerName = item23.Name,
							ListenPort = result
						});
					}
				}
			}
			if (list7.Count > 0)
			{
				MainWindow.Instance?.ShowToast("分组未保存：" + string.Join("；", list7.Take(3)), false);
				args.Cancel = true;
			}
			else if (list6.Count == 0 && list8.Count == 0)
			{
				MainWindow.Instance?.ShowToast("请至少勾选一个角色", false);
				args.Cancel = true;
			}
			else
			{
				proxyGroup.Name = text;
				proxyGroup.ServerName = (string.IsNullOrWhiteSpace(proxyGroup.ServerName) ? fallbackPreset.Name : proxyGroup.ServerName);
				List<ProxyGroupRole> list9 = new List<ProxyGroupRole>();
				list9.AddRange(list8);
				list9.AddRange(list6);
				proxyGroup.Roles = list9;
				if (flag2)
				{
					AppSettings appSettings = settings;
					if (appSettings.ProxyGroups == null)
					{
						appSettings.ProxyGroups = new List<ProxyGroup>();
					}
					settings.ProxyGroups.Add(proxyGroup);
				}
				SettingsManager.Save(settings);
				didSave = true;
				string message = (flag2 ? "已创建分组「" : "已更新分组「") + text + "」，共 " + list9.Count + " 个角色端口";
				MainWindow.Instance?.ShowToast(message, true);
			}
		};
		try
		{
			try
			{
				LogHelper.Log("[分组对话框] 已打开等待操作");
			}
			catch
			{
			}
			await DialogGate.ShowAsync(contentDialog);
		}
		catch (Exception ex)
		{
			try
			{
				LogHelper.Log("[分组对话框] 展示失败\n" + ex);
			}
			catch
			{
			}
			MainWindow.Instance?.ShowToast("分组窗口打开失败，请稍后重试", false);
			return didSave;
		}
		if (didSave)
		{
			RefreshGroups();
		}
		return didSave;
		void AddOwner(int port, string text, string text2, string text3, string role)
		{
			if (port >= 1 && port <= 65535)
			{
				string item20 = text + "\0" + text2 + "\0" + text3 + "\0" + role;
				if (!externalOwners.TryGetValue(port, out var value3))
				{
					value3 = new List<string>();
					externalOwners[port] = value3;
				}
				if (!value3.Contains(item20))
				{
					value3.Add(item20);
				}
			}
		}
		void ApplyPreselect()
		{
			if (existing != null)
			{
				nameBox.Text = existing.Name;
				updatingChecks = true;
				try
				{
					HashSet<string> hashSet2 = new HashSet<string>();
					if (existing.Roles != null)
					{
						foreach (ProxyGroupRole role2 in existing.Roles)
						{
							string b2 = ((!string.IsNullOrWhiteSpace(role2.ServerName)) ? role2.ServerName : existing.ServerName);
							foreach (var item34 in rows)
							{
								if (string.Equals(item34.Server.Name, b2, StringComparison.Ordinal) && string.Equals(item34.Acc.Channel ?? "", role2.Channel, StringComparison.Ordinal) && string.Equals(item34.Acc.Account ?? "", role2.Account ?? "", StringComparison.OrdinalIgnoreCase) && string.Equals(item34.Role, role2.RoleName, StringComparison.Ordinal))
								{
									hashSet2.Add(RoleKey(item34.Server.Name, item34.Acc, item34.Role));
									if (role2.ListenPort > 0)
									{
										item34.Port.Text = role2.ListenPort.ToString();
									}
								}
							}
						}
					}
					foreach (var item35 in rows)
					{
						item35.Check.IsChecked = hashSet2.Contains(RoleKey(item35.Server.Name, item35.Acc, item35.Role));
					}
				}
				catch
				{
				}
				finally
				{
					updatingChecks = false;
				}
				UpdatePortWarnings();
				SyncAllAccountHeads();
			}
		}
		static string RoleKey(string text, AccountStore.LocalAccount acc, string role)
		{
			return text + "\0" + acc.Channel + "\0" + acc.Account + "\0" + role;
		}
		string RowKeyOf(int i)
		{
			return rows[i].Server.Name + "\0" + rows[i].Acc.Channel + "\0" + rows[i].Acc.Account + "\0" + rows[i].Role;
		}
		void SyncAccountHead(ServerPreset serverPreset2, AccountStore.LocalAccount acc)
		{
			if (accountHeads.TryGetValue((serverPreset2, acc), out var value3) && !(value3 == null))
			{
				List<(ServerPreset, int, AccountStore.LocalAccount, string, CheckBox, TextBox, int)> list6 = rows.Where(((ServerPreset Server, int AccIdx, AccountStore.LocalAccount Acc, string Role, CheckBox Check, TextBox Port, int DefPort) tuple) => tuple.Server == serverPreset2 && tuple.Acc == acc).ToList();
				if (list6.Count == 0)
				{
					value3.IsChecked = false;
				}
				else
				{
					int num3 = list6.Count<(ServerPreset, int, AccountStore.LocalAccount, string, CheckBox, TextBox, int)>(((ServerPreset Server, int AccIdx, AccountStore.LocalAccount Acc, string Role, CheckBox Check, TextBox Port, int DefPort) tuple) => tuple.Check.IsChecked == true);
					CheckBox checkBox2 = value3;
					bool? isChecked = ((num3 == 0) ? new bool?(false) : ((num3 == list6.Count) ? new bool?(true) : ((bool?)null)));
					checkBox2.IsChecked = isChecked;
				}
			}
		}
		void SyncAllAccountHeads()
		{
			foreach (KeyValuePair<(ServerPreset, AccountStore.LocalAccount), CheckBox> item36 in accountHeads)
			{
				SyncAccountHead(item36.Key.Item1, item36.Key.Item2);
			}
		}
		void UpdatePortWarnings()
		{
			if (rows.Count != 0)
			{
				Dictionary<int, List<int>> dictionary = new Dictionary<int, List<int>>();
				for (int i = 0; i < rows.Count; i++)
				{
					if (int.TryParse(rows[i].Port.Text.Trim(), out var result) && result >= 1 && result <= 65535)
					{
						if (!dictionary.TryGetValue(result, out var value3))
						{
							List<int> list6 = (dictionary[result] = new List<int>());
							value3 = list6;
						}
						value3.Add(i);
					}
				}
				List<string> list8 = new List<string>();
				Brush brush = ThemeBrush("ControlStrokeColorDefaultBrush");
				Brush brush2 = new SolidColorBrush(Color.FromArgb(byte.MaxValue, byte.MaxValue, 69, 0));
				int num3 = 0;
				int j;
				for (j = 0; j < rows.Count; j++)
				{
					if (rows[j].Check.IsChecked == true)
					{
						string text = rows[j].Port.Text.Trim();
						bool flag2 = false;
						string text2 = "";
						int num4 = -1;
						if (!string.IsNullOrWhiteSpace(text))
						{
							List<int> value4;
							List<string> value5;
							if (!int.TryParse(text, out var result2) || result2 < 1 || result2 > 65535)
							{
								flag2 = true;
								text2 = "端口无效";
							}
							else if (dictionary.TryGetValue(result2, out value4) && value4.Count > 1)
							{
								flag2 = true;
								num4 = result2;
								text2 = "端口在分组内重复";
							}
							else if (externalUsed.Contains(result2) && rows[j].DefPort != result2 && externalOwners.TryGetValue(result2, out value5) && value5.Any((string k) => k != RowKeyOf(j)))
							{
								flag2 = true;
								num4 = result2;
								text2 = "端口已被其他代理占用";
							}
						}
						if (flag2)
						{
							num3++;
						}
						rows[j].Port.BorderBrush = (flag2 ? brush2 : brush);
						rows[j].Port.BorderThickness = (flag2 ? new Thickness(1.5) : new Thickness(1.0));
						if (flag2 && !list8.Contains(text2 + " " + ((num4 > 0) ? num4.ToString() : text)))
						{
							list8.Add(text2 + "：" + ((num4 > 0) ? num4.ToString() : text) + "（" + rows[j].Role + "）");
						}
					}
				}
				if (list8.Count > 0)
				{
					portWarning.Text = "⚠ " + string.Join("；", list8.Take(4));
					portWarning.Visibility = Visibility.Visible;
				}
				else
				{
					portWarning.Text = "";
					portWarning.Visibility = Visibility.Collapsed;
				}
				List<string> list9 = new List<string>();
				if (num3 > 0)
				{
					list9.Add(num3 + " 个角色端口有问题，无法保存");
				}
				if (list9.Count > 0)
				{
					portWarning.Visibility = Visibility.Visible;
					portWarning.Text = "⚠ " + string.Join("；", list9);
				}
			}
		}
	}

	private static ProxyGroupRole? FindGroupRole(AppSettings settings, ServerPreset server, AccountStore.LocalAccount acc, string role, out string? sourceGroup)
	{
		sourceGroup = null;
		if (settings.ProxyGroups == null)
		{
			return null;
		}
		foreach (ProxyGroup proxyGroup in settings.ProxyGroups)
		{
			if (proxyGroup.Roles == null)
			{
				continue;
			}
			foreach (ProxyGroupRole role2 in proxyGroup.Roles)
			{
				if (role2.ListenPort > 0 && string.Equals(role2.ServerName ?? "", server.Name, StringComparison.Ordinal) && string.Equals(role2.Channel ?? "", acc.Channel, StringComparison.Ordinal) && string.Equals(role2.Account ?? "", acc.Account ?? "", StringComparison.OrdinalIgnoreCase) && string.Equals(role2.RoleName, role, StringComparison.Ordinal))
				{
					sourceGroup = proxyGroup.Name;
					return role2;
				}
			}
		}
		return null;
	}

	private static void AnimateShake(UIElement target)
	{
		try
		{
			Transform transform = (target.RenderTransform = new TranslateTransform());
			TranslateTransform target2 = (TranslateTransform)transform;
			DoubleAnimation doubleAnimation = new DoubleAnimation
			{
				From = 0.0,
				To = -6.0,
				Duration = new Duration(TimeSpan.FromMilliseconds(60.0)),
				AutoReverse = true,
				RepeatBehavior = new RepeatBehavior
				{
					Count = 4.0
				}
			};
			Storyboard storyboard = new Storyboard();
			Storyboard.SetTarget(doubleAnimation, target2);
			Storyboard.SetTargetProperty(doubleAnimation, "X");
			storyboard.Children.Add(doubleAnimation);
			storyboard.Begin();
		}
		catch
		{
		}
	}

	private static string AccountUidLabel(AccountStore.LocalAccount acc)
	{
		string channel = acc.Channel;
		if (1 == 0)
		{
		}
		string text = channel switch
		{
			"netease" => "网易:", 
			"netease-mobile" => "网易手机:", 
			"4399" => "4399:", 
			"4399-mobile" => "4399手机:", 
			_ => "", 
		};
		if (1 == 0)
		{
		}
		string text2 = text + (acc.Account ?? "(空账号)");
		if (!string.IsNullOrWhiteSpace(acc.Nickname) && !string.Equals(acc.Nickname, acc.Account, StringComparison.Ordinal))
		{
			text2 = text2 + "（" + acc.Nickname + "）";
		}
		if (!string.IsNullOrWhiteSpace(acc.EntityId))
		{
			text2 = text2 + "  uid:" + acc.EntityId;
		}
		return text2;
	}

	private async void QuickStartBtn_Click(object sender, RoutedEventArgs e)
	{
		object tag = default(object);
		int num;
		if (sender is Button button)
		{
			tag = button.Tag;
			num = 1;
		}
		else
		{
			num = 0;
		}
		if (num == 0)
		{
			return;
		}
		string accountStr = tag as string;
		if (accountStr != null && !string.IsNullOrWhiteSpace(accountStr))
		{
			AccountStore.LocalAccount localAccount = AccountStore.All.FirstOrDefault((AccountStore.LocalAccount a) => string.Equals(a.Account, accountStr, StringComparison.OrdinalIgnoreCase) && a.IsLoggedIn);
			if (localAccount != null)
			{
				await StartProxyForAccountAsync(localAccount);
			}
		}
	}

	private async Task StartProxyForAccountAsync(AccountStore.LocalAccount acc)
	{
		ServerPreset serverPreset = SelectedPreset();
		if (serverPreset?.IsPending ?? true)
		{
			AppendTail("状态: 未选择可用的服务器预设\n");
			return;
		}
		int basePort = ((!int.TryParse(ListenPortBox.Text.Trim(), out var result) || result < 1 || result > 65535) ? ((SettingsManager.Instance.ProxyListenPort > 0) ? SettingsManager.Instance.ProxyListenPort : 25565) : result);
		List<AccountStore.LocalAccount> list = AccountStore.All.ToList();
		int index = list.FindIndex((AccountStore.LocalAccount a) => string.Equals(a.Channel, acc.Channel, StringComparison.Ordinal) && string.Equals(a.Account, acc.Account, StringComparison.OrdinalIgnoreCase));
		if (index < 0)
		{
			AppendTail("状态: 账号状态异常，无法启动代理\n");
			return;
		}
		List<ProxyCaptureService.ProxyBinding> list2 = (from b in ProxyStarter.BuildRoleBindings(serverPreset, basePort)
			where b.AuthAccountIndex == index
			select b).ToList();
		if (list2.Count == 0)
		{
			MainWindow.Instance?.ShowToast("该账号没有可用角色，代理未启动", false);
			return;
		}
		ProxyCaptureService.TargetFromHandshake = true;
		ProxyCaptureService.StartBatch(list2, serverPreset.Host, serverPreset.Port);
		if (!ProxyCaptureService.IsRunning)
		{
			AppendTail("状态: 代理启动失败：端口可能被占用\n");
			return;
		}
		AppendTail($"[{DateTime.Now:HH:mm:ss.fff}] 已开启「{serverPreset.Name}」服务器（{AccountLabel(index)}）的代理（端口自动向上递增）\n");
		SyncUiState();
	}

	private void ServerIpPort_Click(object sender, RoutedEventArgs e)
	{
		ShowPortsDialog();
	}

	private void PersistGroupRoleChecked()
	{
		AppSettings instance = SettingsManager.Instance;
		instance.GroupRoleChecked = new Dictionary<string, bool>(_groupRoleChecked);
		SettingsManager.Save(instance);
	}

	internal void RequestUndoLastDelete()
	{
		UndoLastDelete();
	}

	private void UndoLastDelete()
	{
		if (_undo.Count == 0)
		{
			MainWindow.Instance?.ShowToast("没有可撤销的删除操作", false);
			return;
		}
		UndoRepo item = _undo.Pop();
		AppSettings instance = SettingsManager.Instance;
		if (item.Kind == "group" && item.Group != null)
		{
			if (!instance.ProxyGroups.Any((ProxyGroup x) => x == item.Group))
			{
				instance.ProxyGroups.Add(item.Group);
			}
			SettingsManager.Save(instance);
			RefreshGroups();
			MainWindow.Instance?.ShowToast("已恢复分组「" + item.Group.Name + "」", true);
		}
		else
		{
			if (!(item.Kind == "role") || item.Role == null)
			{
				return;
			}
			ProxyGroup proxyGroup = instance.ProxyGroups.FirstOrDefault((ProxyGroup x) => x == item.Group) ?? instance.ProxyGroups.FirstOrDefault((ProxyGroup x) => string.Equals(x.Name ?? "", item.Group?.Name ?? "", StringComparison.Ordinal));
			if (proxyGroup == null)
			{
				_undo.Push(item);
				MainWindow.Instance?.ShowToast("找不到原分组，无法撤销（可点「加入角色」重新添加）", false);
				return;
			}
			ProxyGroup proxyGroup2 = proxyGroup;
			if (proxyGroup2.Roles == null)
			{
				List<ProxyGroupRole> list = (proxyGroup2.Roles = new List<ProxyGroupRole>());
				List<ProxyGroupRole> list3 = list;
			}
			if (!proxyGroup.Roles.Any((ProxyGroupRole x) => x == item.Role))
			{
				proxyGroup.Roles.Add(item.Role);
			}
			SettingsManager.Save(instance);
			RefreshGroups();
			MainWindow.Instance?.ShowToast("已恢复删除的角色「" + item.Role.RoleName + "」（分组「" + proxyGroup.Name + "」）", true);
		}
	}

	private void QueueUiRefresh()
	{
		if (_uiRefreshQueued)
		{
			return;
		}
		_uiRefreshQueued = true;
		_queue.TryEnqueue(delegate
		{
			_uiRefreshQueued = false;
			Stopwatch stopwatch = Stopwatch.StartNew();
			SyncUiState();
			if (stopwatch.ElapsedMilliseconds >= 16)
			{
				LogHelper.Log($"[性能] QueueUiRefresh(SyncUiState) 耗时 {stopwatch.ElapsedMilliseconds} ms");
			}
		});
	}

	private void OnAccountsChanged()
	{
		if (_groupsListPanel == null)
		{
			return;
		}
		_queue?.TryEnqueue(delegate
		{
			try
			{
				RefreshGroups();
			}
			catch
			{
			}
		});
	}
}

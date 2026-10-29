using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MhyunBox.Services;
using MhyunBox.Settings;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace MhyunBox.Pages;

public sealed class ProxyStartDialog : ContentDialog
{
	private sealed class AccountPickItem
	{
		public CheckBox Use;

		public TextBox Port;

		public AccountStore.LocalAccount Account;

		public string? RoleName;

		public string? TargetHost;

		public int TargetPort;

		public int AccountIndex = -1;

		public bool AlreadyActive;
	}

	private List<AccountStore.LocalAccount> _accounts = new List<AccountStore.LocalAccount>();

	private string? _preselectAccount;

	private string? _preselectRole;

	private List<AccountPickItem> _picks = new List<AccountPickItem>();

	private List<CheckBox> _accChecks = new List<CheckBox>();

	private StackPanel _configRoot = new StackPanel();

	private ScrollViewer _scroll;

	private StackPanel _configRowsPanel = new StackPanel();

	private StackPanel _runRoot = new StackPanel();

	private CheckBox _allUseCheck = new CheckBox();

	private CheckBox _autoPclCheck = new CheckBox();

	private TextBlock _statusText = new TextBlock();

	private int _basePort;

	private string _targetHost = "";

	private int _targetPort;

	private string _defaultTargetHost = "";

	private int _defaultTargetPort;

	private TextBox? _targetBox;

	private string? _presetName;

	private bool _stopOnStart;

	private bool _enableAutoPcl;

	private bool _started;

	private bool _syncing;

	private bool _refreshingRoles;

	private readonly Dictionary<string, bool> _pickCheckedMemory = new Dictionary<string, bool>(StringComparer.Ordinal);

	private readonly Dictionary<string, string> _pickPortMemory = new Dictionary<string, string>(StringComparer.Ordinal);

	private DispatcherQueueTimer? _changedDebounce;

	private string _rowsSignature = "";

	public bool AutoLaunchPcl
	{
		get
		{
			if (_enableAutoPcl && _autoPclCheck != null)
			{
				return _autoPclCheck.IsChecked == true;
			}
			return false;
		}
	}

	public event Action? ProxyStarted;

	public ProxyStartDialog(int basePort, string targetHost, int targetPort, string? presetName, bool stopOnStart = false, bool enableAutoPcl = false, string? preselectAccount = null, string? preselectRole = null)
	{
		_basePort = basePort;
		_targetHost = targetHost;
		_targetPort = targetPort;
		_presetName = presetName;
		_stopOnStart = stopOnStart;
		_enableAutoPcl = enableAutoPcl;
		_preselectAccount = preselectAccount;
		_preselectRole = preselectRole;
		ServerPreset serverPreset = ServerPresets.Find("元素之诗");
		_defaultTargetHost = ((!string.IsNullOrWhiteSpace(_targetHost)) ? _targetHost : (serverPreset?.Host ?? "netease1.yszscraft.cn"));
		_defaultTargetPort = ((_targetPort > 0) ? _targetPort : (serverPreset?.Port ?? 25019));
		_accounts = AccountStore.All.ToList();
		base.Closed += delegate
		{
			if (_enableAutoPcl)
			{
				AppSettings instance = SettingsManager.Instance;
				instance.PclAutoLaunchGame = _autoPclCheck?.IsChecked == true;
				SettingsManager.Save(instance);
			}
		};
		base.Title = "启动代理（选择登录账号 / 角色）";
		base.PrimaryButtonText = "启动";
		base.CloseButtonText = "取消";
		base.DefaultButton = ContentDialogButton.Primary;
		base.PrimaryButtonClick += OnPrimaryClick;
		_configRoot = BuildConfigPanel();
		_runRoot = BuildRunPanel();
		_runRoot.Visibility = Visibility.Collapsed;
		_statusText.Opacity = 0.9;
		_statusText.TextWrapping = TextWrapping.Wrap;
		_statusText.FontSize = 13.0;
		_statusText.FontWeight = FontWeights.SemiBold;
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 12.0,
			MinWidth = 480.0
		};
		stackPanel.Children.Add(_configRoot);
		stackPanel.Children.Add(_runRoot);
		StackPanel stackPanel2 = new StackPanel
		{
			Spacing = 10.0
		};
		_scroll = new ScrollViewer
		{
			Content = stackPanel,
			MaxHeight = 460.0,
			Padding = new Thickness(0.0, 12.0, 0.0, 12.0),
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		base.Loaded += delegate
		{
			ApplyScrollMaxHeight();
		};
		stackPanel2.Children.Add(_scroll);
		stackPanel2.Children.Add(_statusText);
		base.RequestedTheme = MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default;
		base.Content = stackPanel2;
		AccountStore.Changed += OnAccountsChanged;
		_changedDebounce = base.DispatcherQueue.CreateTimer();
		_changedDebounce.Interval = TimeSpan.FromMilliseconds(400.0);
		_changedDebounce.IsRepeating = false;
		_changedDebounce.Tick += delegate
		{
			try
			{
				_changedDebounce?.Stop();
			}
			catch
			{
			}
			if (_started || _refreshingRoles)
			{
				return;
			}
			try
			{
				if (!(BuildRowsSignature() == _rowsSignature))
				{
					_accounts = AccountStore.All.ToList();
					RebuildRows();
				}
			}
			catch
			{
			}
		};
		base.Closed += delegate
		{
			AccountStore.Changed -= OnAccountsChanged;
			try
			{
				_changedDebounce?.Stop();
				_changedDebounce = null;
			}
			catch
			{
			}
		};
		TryRefreshRolesAsync();
	}

	private void OnAccountsChanged()
	{
		if (_started || _refreshingRoles || _changedDebounce == null)
		{
			return;
		}
		base.DispatcherQueue.TryEnqueue(delegate
		{
			try
			{
				_changedDebounce.Stop();
				_changedDebounce.Start();
			}
			catch
			{
			}
		});
	}

	private async Task TryRefreshRolesAsync()
	{
		if (_refreshingRoles || _started)
		{
			return;
		}
		_refreshingRoles = true;
		try
		{
			ServerPreset preset = (string.IsNullOrWhiteSpace(_presetName) ? null : ServerPresets.Find(_presetName));
			List<AccountStore.LocalAccount> list = _accounts.Where((AccountStore.LocalAccount a) => !string.IsNullOrWhiteSpace(a.CookieJson) && (a.ServerRoles == null || a.ServerRoles.Count == 0 || (preset != null && !preset.IsPending && !a.ServerRoles.ContainsKey(preset.Name)))).ToList();
			if (list.Count == 0)
			{
				return;
			}
			SetStatus("正在刷新角色…", ok: false);
			List<string> serverNames = ServerPresets.All.Select((ServerPreset p) => p.Name).ToList();
			bool any = false;
			foreach (AccountStore.LocalAccount acc in list)
			{
				try
				{
					NeteaseLoginResult refreshed;
					using (NeteaseLogin login = new NeteaseLogin())
					{
						refreshed = await login.LoginWithCookieAsync(acc.CookieJson, acc.Channel);
					}
					if (!refreshed.Success || string.IsNullOrWhiteSpace(refreshed.EntityId) || string.IsNullOrWhiteSpace(refreshed.Token))
					{
						LogHelper.Log("[代理角色刷新] " + acc.Account + " => 会话刷新失败: " + refreshed.Message);
						if (NeteaseAuthBridge.IsSessionInvalidMessage(refreshed.Message ?? ""))
						{
							LogHelper.Log("[代理角色刷新] " + acc.Account + " => 角色列表刷新提示会话可能失效，但不代表游戏内会话失效，不标记账号（仅在游戏内重复进入会话失败时才判定）");
						}
						continue;
					}
					refreshed.CookieJson = acc.CookieJson;
					if (string.IsNullOrWhiteSpace(refreshed.Nickname))
					{
						refreshed.Nickname = acc.Nickname;
					}
					(bool, Dictionary<string, List<string>>, string) tuple = await NeteaseRoleService.CaptureRolesAsync(refreshed.EntityId, refreshed.Token, serverNames);
					if (tuple.Item1)
					{
						AccountStore.RefreshRolesVerified(acc.Channel, acc.Account, refreshed, tuple.Item2);
						any = true;
					}
					LogHelper.Log("[代理角色刷新] " + acc.Account + " => " + tuple.Item3);
					refreshed = null;
					refreshed = null;
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "[代理角色刷新] " + acc.Account);
				}
			}
			if (!any)
			{
				return;
			}
			_accounts = AccountStore.All.ToList();
			base.DispatcherQueue.TryEnqueue(delegate
			{
				try
				{
					RebuildRows();
					SetStatus("角色已刷新，请选择要启动的账号 / 角色。", ok: true);
				}
				catch
				{
				}
			});
		}
		catch
		{
		}
		finally
		{
			_refreshingRoles = false;
		}
	}

	private void ApplyScrollMaxHeight()
	{
		try
		{
			if (!(base.XamlRoot == null))
			{
				double val = base.XamlRoot.Size.Height - 175.0;
				_scroll.MaxHeight = Math.Max(300.0, Math.Min(520.0, val));
			}
		}
		catch
		{
		}
	}

	private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
	{
		if (!_started)
		{
			if (!TryStartProxy())
			{
				args.Cancel = true;
				return;
			}
			SwitchToRunPhase();
			args.Cancel = true;
		}
	}

	private bool TryStartProxy()
	{
		string text = _targetBox?.Text?.Trim() ?? "";
		bool flag = text.Length > 0;
		string host = _targetHost;
		int port = _targetPort;
		if (flag && !TryParseTarget(text, out host, out port))
		{
			SetStatus("目标服务器地址格式无效，应为 host:port，例如 " + _defaultTargetHost + ":" + _defaultTargetPort, ok: false);
			return false;
		}
		if (port <= 0)
		{
			port = _defaultTargetPort;
		}
		if (string.IsNullOrWhiteSpace(host))
		{
			host = _defaultTargetHost;
		}
		List<ProxyCaptureService.ProxyBinding> list = new List<ProxyCaptureService.ProxyBinding>();
		foreach (AccountPickItem pick in _picks)
		{
			if (pick.Use.IsChecked == true)
			{
				if (!int.TryParse(pick.Port.Text.Trim(), out var result) || result < 1 || result > 65535)
				{
					SetStatus("端口无效（" + pick.Port.Text.Trim() + "），未启动", ok: false);
					return false;
				}
				list.Add(new ProxyCaptureService.ProxyBinding
				{
					ListenPort = result,
					LoginName = (string.IsNullOrWhiteSpace(pick.RoleName) ? null : pick.RoleName),
					TargetHost = (flag ? host : pick.TargetHost),
					TargetPort = (flag ? port : pick.TargetPort),
					AuthAccountIndex = pick.AccountIndex
				});
			}
		}
		if (list.Count == 0)
		{
			if (_accounts.Count > 0)
			{
				bool allActive = _picks.Count > 0 && _picks.All((AccountPickItem p) => p.AlreadyActive);
				SetStatus(allActive ? "所选角色均已运行，无需重复启动。" : "未勾选任何需要启动的角色（暂无角色的账号不支持直连与启动代理，请先刷新角色）", allActive);
				return false;
			}
			list.Add(new ProxyCaptureService.ProxyBinding
			{
				ListenPort = _basePort
			});
		}
		if (flag)
		{
			SettingsManager.Instance.ProxyTargetOverride = text;
			SettingsManager.Save(SettingsManager.Instance);
		}
		if (_stopOnStart && !string.IsNullOrWhiteSpace(host))
		{
			ProxyCaptureService.StopTarget(host);
		}
		ProxyCaptureService.TargetFromHandshake = true;
		ProxyCaptureService.StartBatch(list, host, port);
		if (!ProxyCaptureService.IsRunning)
		{
			SetStatus("代理启动失败：没有可用的监听绑定（端口可能被占用）", ok: false);
			return false;
		}
		ServerPreset serverPreset = (string.IsNullOrWhiteSpace(_presetName) ? null : ServerPresets.Find(_presetName));
		if (!ProxyCaptureService.HasEarliestRecord())
		{
			ProxyStarter.RememberStarted(serverPreset, list);
		}
		ProxyStarter.RememberLastLaunch("代理启动", ((serverPreset != null) ? serverPreset.Name : "") + " 共 " + list.Count + " 个角色端口");
		return true;
	}

	private void SwitchToRunPhase()
	{
		_started = true;
		_configRoot.Visibility = Visibility.Collapsed;
		_runRoot.Visibility = Visibility.Visible;
		BuildRunPanel();
		base.PrimaryButtonText = "";
		base.CloseButtonText = "";
		base.DefaultButton = ContentDialogButton.None;
		SetStatus("代理已启动。", ok: true);
		ProxyStarted?.Invoke();
		AutoCloseAsync();
	}

	private async Task AutoCloseAsync()
	{
		await Task.Delay(10000);
		base.DispatcherQueue.TryEnqueue(delegate
		{
			try
			{
				Hide();
			}
			catch
			{
			}
		});
	}

	private static bool TryParseTarget(string text, out string host, out int port)
	{
		host = "";
		port = 0;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		text = text.Trim();
		string text2 = text;
		string text3 = "";
		if (text.StartsWith("[", StringComparison.Ordinal) && text.Contains("]"))
		{
			int num = text.IndexOf(']');
			text2 = text.Substring(1, num - 1);
			string text4 = text.Substring(num + 1);
			if (text4.StartsWith(":", StringComparison.Ordinal))
			{
				text3 = text4.Substring(1);
			}
		}
		else
		{
			if (text.IndexOf(':') != text.LastIndexOf(':'))
			{
				return false;
			}
			int num2 = text.IndexOf(':');
			if (num2 >= 0)
			{
				text2 = text.Substring(0, num2);
				text3 = text.Substring(num2 + 1);
			}
		}
		text2 = text2.Trim();
		if (text2.Length == 0 || text2.Contains(" "))
		{
			return false;
		}
		host = text2;
		if (text3.Length == 0)
		{
			return true;
		}
		if (!int.TryParse(text3, out port) || port < 1 || port > 65535)
		{
			return false;
		}
		return true;
	}

	private StackPanel BuildConfigPanel()
	{
		_configRoot.Children.Clear();
		_configRoot.Spacing = 10.0;
		_configRoot.MinWidth = 480.0;
		_configRoot.Children.Add(new TextBlock
		{
			Text = "◆ 游戏内添加服务器：127.0.0.1:对应端口（每个账号 / 角色对应一行一个端口）",
			FontWeight = FontWeights.SemiBold,
			FontSize = 13.0,
			TextWrapping = TextWrapping.Wrap
		});
		if (_enableAutoPcl)
		{
			_autoPclCheck = new CheckBox
			{
				Content = "我的世界自加入指定端口（启动后自动打开 PCL 并填入服务器地址）",
				IsChecked = SettingsManager.Instance.PclAutoLaunchGame,
				FontWeight = FontWeights.Bold
			};
			_configRoot.Children.Add(_autoPclCheck);
		}
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 4.0
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "目标服务器地址（格式 host:port，留空使用默认）",
			FontSize = 13.0,
			FontWeight = FontWeights.SemiBold
		});
		_targetBox = new TextBox
		{
			PlaceholderText = _defaultTargetHost + ":" + _defaultTargetPort,
			MinWidth = 420.0,
			Text = (SettingsManager.Instance.ProxyTargetOverride ?? "")
		};
		stackPanel.Children.Add(_targetBox);
		_configRoot.Children.Add(stackPanel);
		_allUseCheck = new CheckBox
		{
			Content = "全部账号（含角色）",
			IsChecked = true,
			FontWeight = FontWeights.Bold
		};
		_allUseCheck.Click += delegate
		{
			if (!_syncing)
			{
				bool valueOrDefault = _allUseCheck.IsChecked == true;
				foreach (CheckBox accCheck in _accChecks)
				{
					if (accCheck.IsEnabled)
					{
						object tag = accCheck.Tag;
						AccountStore.LocalAccount accItem = tag as AccountStore.LocalAccount;
						if (accItem != null)
						{
							List<AccountPickItem> list = _picks.Where((AccountPickItem p) => p.Account == accItem).ToList();
							if (list.Count > 0 && list.All((AccountPickItem p) => p.AlreadyActive))
							{
								continue;
							}
						}
						accCheck.IsChecked = valueOrDefault;
					}
				}
				foreach (AccountPickItem pick in _picks)
				{
					if (!pick.AlreadyActive)
					{
						pick.Use.IsChecked = valueOrDefault;
					}
				}
				SyncSelectionState();
			}
		};
		_configRoot.Children.Add(_allUseCheck);
		_configRowsPanel = new StackPanel
		{
			Spacing = 10.0
		};
		_configRoot.Children.Add(_configRowsPanel);
		RebuildRows();
		return _configRoot;
	}

	private void RebuildRows()
	{
		foreach (AccountPickItem pick in _picks)
		{
			if (pick?.Account != null)
			{
				string key = PickKey(pick.Account, pick.RoleName, pick.TargetHost, pick.TargetPort);
				_pickCheckedMemory[key] = pick.Use.IsChecked == true;
				try
				{
					_pickPortMemory[key] = pick.Port.Text ?? "";
				}
				catch
				{
				}
			}
		}
		_configRowsPanel.Children.Clear();
		_picks = new List<AccountPickItem>();
		_accChecks = new List<CheckBox>();
		int cursor = _basePort;
		ServerPreset serverPreset = (string.IsNullOrWhiteSpace(_presetName) ? null : ServerPresets.Find(_presetName));
		IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> activeSnapshot = ProxyCaptureService.ActiveBindingsSnapshot();
		foreach (AccountStore.LocalAccount acc in _accounts)
		{
			CheckBox accUse = new CheckBox
			{
				Content = acc.Channel + " · " + DisplayName(acc),
				IsChecked = true,
				FontWeight = FontWeights.SemiBold,
				VerticalAlignment = VerticalAlignment.Center,
				Tag = acc
			};
			accUse.Click += delegate
			{
				if (!_syncing)
				{
					bool? isChecked = accUse.IsChecked;
					foreach (AccountPickItem pick2 in _picks)
					{
						if (pick2.Account == acc && !pick2.AlreadyActive)
						{
							pick2.Use.IsChecked = isChecked;
						}
					}
					SyncSelectionState();
				}
			};
			_accChecks.Add(accUse);
			StackPanel stackPanel = new StackPanel
			{
				Spacing = 4.0,
				Margin = new Thickness(28.0, 0.0, 0.0, 6.0)
			};
			bool flag = false;
			if (acc.ServerRoles != null && serverPreset != null && !serverPreset.IsPending && acc.ServerRoles.TryGetValue(serverPreset.Name, out var value) && value != null && value.Count > 0)
			{
				flag = true;
				stackPanel.Children.Add(new TextBlock
				{
					Text = $"◆ {serverPreset.Name}（{serverPreset.Host}:{serverPreset.Port}）",
					FontSize = 13.0,
					FontWeight = FontWeights.SemiBold,
					Opacity = 0.9
				});
				foreach (string item in value.OrderBy<string, string>((string r) => r, StringComparer.Ordinal))
				{
					AddPickRow(stackPanel, ref cursor, acc, item, item, serverPreset.Host, serverPreset.Port, activeSnapshot);
				}
			}
			if (acc.ServerRoles != null && acc.ServerRoles.TryGetValue("未分组", out var value2) && value2 != null && value2.Count > 0)
			{
				flag = true;
				stackPanel.Children.Add(new TextBlock
				{
					Text = "◆ 未分组（旧记录，走页面预设服务器）",
					FontSize = 13.0,
					FontWeight = FontWeights.SemiBold,
					Opacity = 0.9
				});
				foreach (string item2 in value2.OrderBy<string, string>((string r) => r, StringComparer.Ordinal))
				{
					AddPickRow(stackPanel, ref cursor, acc, item2, item2, null, 0, activeSnapshot);
				}
			}
			if (!flag)
			{
				accUse.IsChecked = false;
				accUse.IsEnabled = false;
				accUse.Opacity = 0.75;
				stackPanel.Children.Add(new TextBlock
				{
					Text = "暂无角色，不支持直连与启动代理，请先在「账号绑定」页刷新角色",
					FontSize = 12.0,
					Opacity = 0.7,
					TextWrapping = TextWrapping.Wrap
				});
			}
			List<AccountPickItem> list = _picks.Where((AccountPickItem p) => p.Account == acc).ToList();
			if (list.Count > 0 && list.All((AccountPickItem p) => p.AlreadyActive))
			{
				accUse.IsChecked = false;
			}
			StackPanel stackPanel2 = new StackPanel
			{
				Spacing = 2.0
			};
			stackPanel2.Children.Add(accUse);
			stackPanel2.Children.Add(stackPanel);
			_configRowsPanel.Children.Add(stackPanel2);
		}
		SyncSelectionState();
		_rowsSignature = BuildRowsSignature();
	}

	private static string BuildRowsSignature()
	{
		StringBuilder stringBuilder = new StringBuilder();
		try
		{
			IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> readOnlyList = ProxyCaptureService.ActiveBindingsSnapshot();
			List<string> list = new List<string>();
			foreach (ProxyCaptureService.ActiveBindingInfo item in readOnlyList)
			{
				list.Add(item.AuthAccountIndex + ":" + item.ListenPort + ":" + item.LoginName + ":" + item.TargetHost + ":" + item.TargetPort);
			}
			list.Sort(StringComparer.Ordinal);
			stringBuilder.Append(string.Join(";", list)).Append('|');
			foreach (AccountStore.LocalAccount item2 in AccountStore.All)
			{
				stringBuilder.Append(item2.Channel).Append('/').Append(item2.Account)
					.Append('/')
					.Append(item2.Nickname ?? "")
					.Append('=');
				if (item2.ServerRoles != null)
				{
					List<string> list2 = new List<string>();
					foreach (KeyValuePair<string, List<string>> serverRole in item2.ServerRoles)
					{
						list2.Add(serverRole.Key + "[" + string.Join(",", serverRole.Value ?? new List<string>()) + "]");
					}
					list2.Sort(StringComparer.Ordinal);
					stringBuilder.Append(string.Join("|", list2));
				}
				stringBuilder.Append(';');
			}
		}
		catch
		{
		}
		return stringBuilder.ToString();
	}

	private static string PickKey(AccountStore.LocalAccount acc, string? roleName, string? effHost, int effPort)
	{
		return acc.Channel + "\u0001" + acc.Account + "\u0001" + roleName + "\u0001" + effHost + ":" + effPort;
	}

	private void AddPickRow(StackPanel panel, ref int cursor, AccountStore.LocalAccount acc, string label, string? roleName, string? targetHost, int targetPort, IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> activeSnapshot)
	{
		int accountIndex = Math.Max(0, _accounts.IndexOf(acc));
		string effHost = (string.IsNullOrWhiteSpace(targetHost) ? _targetHost : targetHost);
		int effPort = ((targetPort > 0) ? targetPort : _targetPort);
		bool flag = activeSnapshot.Any((ProxyCaptureService.ActiveBindingInfo b) => b.AuthAccountIndex == accountIndex && string.Equals(b.LoginName, roleName, StringComparison.Ordinal) && string.Equals(b.TargetHost ?? "", effHost ?? "", StringComparison.OrdinalIgnoreCase) && b.TargetPort == effPort);
		string key = PickKey(acc, roleName, targetHost, targetPort);
		bool flag2 = _pickCheckedMemory.TryGetValue(key, out var value);
		bool preselectMatch = (!string.IsNullOrWhiteSpace(_preselectAccount) && string.Equals(acc.Account, _preselectAccount, StringComparison.Ordinal) && (string.IsNullOrWhiteSpace(_preselectRole) ? (roleName == null) : string.Equals(roleName, _preselectRole, StringComparison.Ordinal)));
		CheckBox checkBox = new CheckBox
		{
			Content = (flag ? (label + "（已运行）") : label),
			IsChecked = (preselectMatch || (!flag && (!flag2 | value))),
			VerticalAlignment = VerticalAlignment.Center,
			FontSize = 13.0
		};
		checkBox.Click += delegate
		{
			if (!_syncing)
			{
				SyncSelectionState();
			}
		};
		int num = ProxyCaptureService.NextFreePort(cursor);
		if (num <= 0)
		{
			num = cursor;
		}
		cursor = num + 1;
		TextBox textBox = new TextBox
		{
			Text = ((_pickPortMemory.TryGetValue(key, out var value2) && !string.IsNullOrWhiteSpace(value2)) ? value2 : num.ToString()),
			Width = 90.0,
			HorizontalAlignment = HorizontalAlignment.Right
		};
		Grid grid = new Grid
		{
			ColumnSpacing = 8.0
		};
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		Grid.SetColumn(checkBox, 0);
		Grid.SetColumn(textBox, 1);
		grid.Children.Add(checkBox);
		grid.Children.Add(textBox);
		panel.Children.Add(grid);
		_picks.Add(new AccountPickItem
		{
			Use = checkBox,
			Port = textBox,
			Account = acc,
			RoleName = roleName,
			TargetHost = targetHost,
			TargetPort = targetPort,
			AccountIndex = accountIndex,
			AlreadyActive = flag
		});
	}

	private StackPanel BuildRunPanel()
	{
		_runRoot.Children.Clear();
		_runRoot.Spacing = 10.0;
		_runRoot.MinWidth = 480.0;
		_runRoot.Children.Add(new TextBlock
		{
			Text = "代理已启动，以下为各入口的端口信息（只读）：\n在游戏内添加服务器，地址填 127.0.0.1，端口对应下方每一行。",
			FontWeight = FontWeights.SemiBold,
			FontSize = 13.0,
			TextWrapping = TextWrapping.Wrap
		});
		IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> source = ProxyCaptureService.ActiveBindingsSnapshot();
		List<(string Label, string Address)> rows = new List<(string, string)>();
		foreach (AccountPickItem pick in _picks)
		{
			if (pick.Use.IsChecked != true)
			{
				continue;
			}
			int num = source.FirstOrDefault((ProxyCaptureService.ActiveBindingInfo b) => b.AuthAccountIndex == pick.AccountIndex && string.Equals(b.LoginName, pick.RoleName, StringComparison.Ordinal))?.ListenPort ?? (int.TryParse(pick.Port.Text.Trim(), out var result) ? result : 0);
			if (num > 0)
			{
				string text = DisplayName(pick.Account);
				if (!string.IsNullOrWhiteSpace(pick.RoleName))
				{
					text = text + " · " + pick.RoleName;
				}
				rows.Add((text, "127.0.0.1:" + num));
			}
		}
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 8.0
		};
		foreach (var item in rows)
		{
			stackPanel.Children.Add(BuildRunRow(item.Label, item.Address));
		}
		// 底部占满宽的关闭键（运行状态下不再用对话框右上角按钮）
		Button closeBtn = new Button
		{
			Content = "关闭",
			MinHeight = 36.0,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		};
		closeBtn.Click += delegate
		{
			Hide();
		};
		stackPanel.Children.Add(closeBtn);
		_runRoot.Children.Add(stackPanel);
		return _runRoot;
	}

	private FrameworkElement BuildRunRow(string label, string address)
	{
		Grid grid = new Grid
		{
			ColumnSpacing = 8.0,
			ColumnDefinitions = 
			{
				new ColumnDefinition
				{
					Width = new GridLength(1.0, GridUnitType.Star)
				},
				new ColumnDefinition
				{
					Width = GridLength.Auto
				},
				new ColumnDefinition
				{
					Width = GridLength.Auto
				}
			}
		};
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
			MinWidth = 64.0
		};
		button.Click += delegate
		{
			CopyToClipboard(address);
			SetStatus("已复制：" + address, ok: true);
		};
		Grid.SetColumn(textBlock, 0);
		Grid.SetColumn(border, 1);
		Grid.SetColumn(button, 2);
		grid.Children.Add(textBlock);
		grid.Children.Add(border);
		grid.Children.Add(button);
		return grid;
	}

	private void SetStatus(string message, bool ok)
	{
		_statusText.Text = message;
		_statusText.Foreground = new SolidColorBrush(ok ? Colors.Green : Colors.OrangeRed);
	}

	private static void CopyToClipboard(string text)
	{
		try
		{
			DataPackage dataPackage = new DataPackage();
			dataPackage.SetText(text);
			Clipboard.SetContent(dataPackage);
			Clipboard.Flush();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "复制到剪贴板失败");
		}
	}

	// 主题笔刷统一走 ThemeHelper（App.xaml 未定义 ThemeDictionaries，本地查表在深色主题下会拿到浅色画笔）
	private static Brush? ThemeBrush(string key)
	{
		return ThemeHelper.Resolve(key);
	}

	private static string DisplayName(AccountStore.LocalAccount acc)
	{
		if (!string.IsNullOrWhiteSpace(acc.Nickname))
		{
			return acc.Nickname;
		}
		return AccountStore.Mask(acc.Account);
	}

	private void SyncSelectionState()
	{
		_syncing = true;
		try
		{
			foreach (CheckBox accCheck in _accChecks)
			{
				if (!accCheck.IsEnabled)
				{
					continue;
				}
				object tag = accCheck.Tag;
				AccountStore.LocalAccount acc = tag as AccountStore.LocalAccount;
				if (acc == null)
				{
					continue;
				}
				List<AccountPickItem> list = _picks.Where((AccountPickItem p) => p.Account == acc && !p.AlreadyActive).ToList();
				if (list.Count > 0)
				{
					accCheck.IsChecked = list.All((AccountPickItem p) => p.Use.IsChecked == true);
				}
			}
			List<CheckBox> list2 = _accChecks.Where((CheckBox c) => c.IsEnabled).ToList();
			_allUseCheck.IsChecked = list2.Count > 0 && list2.All((CheckBox c) => c.IsChecked == true);
		}
		finally
		{
			_syncing = false;
		}
	}
}

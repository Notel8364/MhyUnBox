using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MhyunBox.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace MhyunBox.Pages;

public sealed partial class LoginPage : Page
{
	private static readonly Regex PhoneRegex = new Regex("^1[3-9]\\d{9}$", RegexOptions.Compiled);

	private readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;

	private bool _smsCodeSent;

	private bool _busy;

	private Border? RiskBannerBorder;

	private TextBlock? RiskBannerText;

	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _riskTimer;

	private StackPanel LoginRoot;

	private InfoBar StatusBar;

	private InfoBar ActivationGateBar;

	private TextBlock ActivationGateText;

	private Button GoActivateButton;

	private ComboBox ChannelSelect;

	private Button RegisterButton;

	private TextBlock AccountLabel;

	private TextBox AccountBox;

	private ComboBox EmailDomainSelect;

	private TextBlock PasswordLabel;

	private Grid PasswordRow;

	private PasswordBox PasswordBox;

	private Button SendCodeButton;

	private Border CookieConvertPanel;

	private TextBox CookieEmailBox;

	private PasswordBox CookiePasswordBox;

	private Button CookieConvertButton;

	private Button CopyCookieButton;

	private TextBlock CookieConvertHint;

	private TextBlock ChannelHint;

	private Button VerifyButton;

	private ProgressRing BusyRing;

	private TextBlock AutoLoginHint;

	private Button AutoLoginAllButton;

	private Button DelAllButton;

	private StackPanel SessionListPanel;

	private string SelectedChannel => ((ChannelSelect.SelectedItem as ComboBoxItem)?.Tag as string) ?? "netease";

	private static AccountStore.LocalAccount? Current => AccountStore.Current;

	public LoginPage()
	{
		_queue = base.DispatcherQueue;
		base.Content = BuildUi();
		base.SizeChanged += Page_SizeChanged;
		ChannelSelect.SelectedIndex = 0;
		base.Unloaded += OnUnloaded;
		base.Loaded += OnLoaded;
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		AccountStore.Changed += OnStoreChanged;
		ElsewhereLoginNotice.Changed += OnElsewhereChanged;
		ActivationService.StateChanged += OnActivationChanged;
		base.KeyDown += OnPageKeyDown;
		RefreshAccountList();
		UpdateActivationGate();
		RelayoutLogin(base.ActualWidth);
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		AccountStore.Changed -= OnStoreChanged;
		ElsewhereLoginNotice.Changed -= OnElsewhereChanged;
		ActivationService.StateChanged -= OnActivationChanged;
		base.KeyDown -= OnPageKeyDown;
	}

	private void OnElsewhereChanged()
	{
		_queue.TryEnqueue(delegate
		{
			RefreshAccountList();
			ShowInfo("检测到账号疑似在别处登录，已在账号列表中标记提醒（仅提醒，不影响使用；重新登录成功后自动清除）。", ok: false, "别处登录提醒");
		});
	}

	private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		RelayoutLogin(e.NewSize.Width);
	}

	private void RelayoutLogin(double width)
	{
		bool flag = width >= 1200.0;
		LoginRoot.HorizontalAlignment = (flag ? HorizontalAlignment.Center : HorizontalAlignment.Left);
		LoginRoot.MaxWidth = (flag ? 1000.0 : 680.0);
	}

	private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
	{
		object focusedElement = FocusManager.GetFocusedElement(base.XamlRoot);
		// 用 ReferenceEquals 明确表达「判断焦点是不是在这两个控件上」，
		// 直接 == 会被编译器当成可疑的引用比较（CS0252）
		bool flag = ReferenceEquals(focusedElement, AccountBox);
		bool flag2 = ReferenceEquals(focusedElement, PasswordBox);
		if (e.Key == VirtualKey.Enter && VerifyButton.IsEnabled && (flag | flag2) && !AccountBox.AcceptsReturn)
		{
			e.Handled = true;
			Verify_Click(sender, new RoutedEventArgs());
		}
	}

	private void OnActivationChanged()
	{
		_queue.TryEnqueue(UpdateActivationGate);
	}

	private void OnStoreChanged()
	{
		_queue.TryEnqueue(RefreshAccountList);
	}

	private void UpdateActivationGate()
	{
		bool isActivated = ActivationService.IsActivated;
		ActivationGateBar.IsOpen = !isActivated;
		ActivationGateText.Text = (isActivated ? "已激活，可绑定账号。" : "请先在首页右侧「代理入口 / 激活」输入卡密完成激活后再绑定账号。");
		ChannelSelect.IsEnabled = isActivated;
		AccountBox.IsEnabled = isActivated;
		PasswordBox.IsEnabled = isActivated;
		CookieEmailBox.IsEnabled = isActivated;
		CookiePasswordBox.IsEnabled = isActivated;
		CookieConvertButton.IsEnabled = isActivated;
		VerifyButton.IsEnabled = isActivated && !_busy;
		AutoLoginAllButton.IsEnabled = isActivated && !_busy;
	}

	private void ShowInfo(string message, bool ok, string? title = null)
	{
		// 绑定成功/失败改用顶部 Toast 提示（绿=成功、红=失败），不再用底部 InfoBar
		_ = title;
		_queue.TryEnqueue(delegate
		{
			MainWindow.Instance?.ShowToast(message, ok);
		});
	}

	private void SetBusy(bool busy)
	{
		_busy = busy;
		VerifyButton.IsEnabled = !busy && ActivationService.IsActivated;
		BusyRing.IsActive = busy;
		BusyRing.Visibility = ((!busy) ? Visibility.Collapsed : Visibility.Visible);
	}

	// 主题笔刷统一走 ThemeHelper：App.xaml 未定义 ThemeDictionaries，
	// 代码查找只能拿到应用级（启动时）主题的浅色画笔，深色主题下必须按当前主题解析
	private static Brush? ThemeBrush(string key)
	{
		return ThemeHelper.Resolve(key);
	}

	private static Brush CardBrush(string key)
	{
		return ThemeHelper.Resolve(key);
	}

	private static Border Card(double radius, double padH, double padV, string? fillKey = null)
	{
		return new Border
		{
			Background = CardBrush(fillKey ?? "CardBackgroundFillColorDefaultBrush"),
			BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(radius),
			Padding = new Thickness(padH, padV, padH, padV)
		};
	}

	private static ComboBoxItem ChannelItem(string content, string tag)
	{
		return new ComboBoxItem
		{
			Content = content,
			Tag = tag
		};
	}

	private UIElement BuildUi()
	{
		LoginRoot = new StackPanel
		{
			Padding = new Thickness(40.0, 28.0, 40.0, 28.0),
			Spacing = 12.0,
			MaxWidth = 680.0,
			HorizontalAlignment = HorizontalAlignment.Left
		};
		LoginRoot.Children.Add(new TextBlock
		{
			Text = "账号绑定",
			FontSize = 28.0,
			FontWeight = FontWeights.Bold,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		StatusBar = new InfoBar
		{
			IsOpen = false,
			IsClosable = true
		};
		LoginRoot.Children.Add(StatusBar);
		ActivationGateBar = new InfoBar
		{
			IsOpen = false,
			IsClosable = false
		};
		ActivationGateText = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			VerticalAlignment = VerticalAlignment.Center
		};
		GoActivateButton = new Button
		{
			Content = "去激活",
			VerticalAlignment = VerticalAlignment.Center
		};
		GoActivateButton.Click += GoActivate_Click;
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 12.0
		};
		stackPanel.Children.Add(ActivationGateText);
		stackPanel.Children.Add(GoActivateButton);
		ActivationGateBar.Content = stackPanel;
		LoginRoot.Children.Add(ActivationGateBar);
		LoginRoot.Children.Add(BuildVerifyPanel());
		LoginRoot.Children.Add(BuildSessionCard());
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Content = LoginRoot
		};
	}

	private Border BuildVerifyPanel()
	{
		Border border = Card(12.0, 16.0, 12.0);
		border.Margin = new Thickness(0.0, 8.0, 0.0, 0.0);
		border.Name = "VerifyPanel";
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 8.0
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "登录渠道"
		});
		ChannelSelect = new ComboBox
		{
			MinWidth = 220.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		ThemeHelper.SolidComboPopup(ChannelSelect);
		ChannelSelect.Items.Add(ChannelItem("网易账号（邮箱/手机号）", "netease"));
		ChannelSelect.Items.Add(ChannelItem("4399 账号（用户名/手机号）", "4399"));
		ChannelSelect.Items.Add(ChannelItem("Cookie 加载账号", "cookie"));
		ChannelSelect.SelectionChanged += ChannelSelect_Changed;
		RegisterButton = new Button
		{
			Content = "注册账号",
			VerticalAlignment = VerticalAlignment.Center
		};
		RegisterButton.Click += Register_Click;
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		stackPanel2.Children.Add(ChannelSelect);
		stackPanel2.Children.Add(RegisterButton);
		stackPanel.Children.Add(stackPanel2);
		AccountLabel = new TextBlock
		{
			Text = "网易账号"
		};
		stackPanel.Children.Add(AccountLabel);
		AccountBox = new TextBox();
		AccountBox.KeyDown += AccountBox_KeyDown;
		AccountBox.TextChanged += AccountBox_TextChanged;
		EmailDomainSelect = new ComboBox
		{
			MinWidth = 132.0,
			VerticalAlignment = VerticalAlignment.Center,
			SelectedIndex = 0,
			Visibility = Visibility.Collapsed
		};
		ThemeHelper.SolidComboPopup(EmailDomainSelect);
		EmailDomainSelect.Items.Add(ChannelItem("@163.com", "163.com"));
		EmailDomainSelect.Items.Add(ChannelItem("@qq.com", "qq.com"));
		EmailDomainSelect.Items.Add(ChannelItem("@126.com", "126.com"));
		EmailDomainSelect.Items.Add(ChannelItem("@yeah.net", "yeah.net"));
		EmailDomainSelect.Items.Add(ChannelItem("@vip.163.com", "vip.163.com"));
		EmailDomainSelect.Items.Add(ChannelItem("@188.com", "188.com"));
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
		Grid.SetColumn(AccountBox, 0);
		grid.Children.Add(AccountBox);
		Grid.SetColumn(EmailDomainSelect, 1);
		grid.Children.Add(EmailDomainSelect);
		stackPanel.Children.Add(grid);
		PasswordLabel = new TextBlock
		{
			Text = "登录密码"
		};
		stackPanel.Children.Add(PasswordLabel);
		PasswordBox = new PasswordBox();
		SendCodeButton = new Button
		{
			Content = "发送验证码",
			MinWidth = 112.0,
			Visibility = Visibility.Collapsed
		};
		SendCodeButton.Click += SendCode_Click;
		PasswordRow = new Grid
		{
			ColumnSpacing = 8.0
		};
		PasswordRow.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		PasswordRow.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		Grid.SetColumn(PasswordBox, 0);
		PasswordRow.Children.Add(PasswordBox);
		Grid.SetColumn(SendCodeButton, 1);
		PasswordRow.Children.Add(SendCodeButton);
		stackPanel.Children.Add(PasswordRow);
		CookieConvertPanel = Card(8.0, 12.0, 10.0, "CardBackgroundFillColorSecondaryBrush");
		CookieConvertPanel.Visibility = Visibility.Collapsed;
		StackPanel stackPanel3 = new StackPanel
		{
			Spacing = 6.0
		};
		stackPanel3.Children.Add(new TextBlock
		{
			Text = "网易账密转 Cookie",
			FontWeight = FontWeights.SemiBold
		});
		stackPanel3.Children.Add(new TextBlock
		{
			Text = "输入网易邮箱与密码生成 x19 Cookie 填入上方再「验证并绑定」（替代桌面版「网易163账号转Cookies」工具）。生成的 Cookie 会保存到本机，密码不保存。",
			Opacity = 0.7,
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap
		});
		CookieEmailBox = new TextBox
		{
			PlaceholderText = "网易邮箱账号（如 name@163.com，可只填 name）"
		};
		stackPanel3.Children.Add(CookieEmailBox);
		CookiePasswordBox = new PasswordBox
		{
			PlaceholderText = "登录密码"
		};
		stackPanel3.Children.Add(CookiePasswordBox);
		CookieConvertButton = new Button
		{
			Content = "账密转Cookie"
		};
		CookieConvertButton.Click += CookieConvert_Click;
		CopyCookieButton = new Button
		{
			Content = "一键复制 Cookie",
			Visibility = Visibility.Collapsed
		};
		CopyCookieButton.Click += CopyCookie_Click;
		CookieConvertHint = new TextBlock
		{
			VerticalAlignment = VerticalAlignment.Center,
			FontSize = 12.0,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap
		};
		StackPanel stackPanel4 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		stackPanel4.Children.Add(CookieConvertButton);
		stackPanel4.Children.Add(CopyCookieButton);
		stackPanel4.Children.Add(CookieConvertHint);
		stackPanel3.Children.Add(stackPanel4);
		CookieConvertPanel.Child = stackPanel3;
		stackPanel.Children.Add(CookieConvertPanel);
		ChannelHint = new TextBlock
		{
			FontSize = 12.0,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap,
			Visibility = Visibility.Collapsed
		};
		stackPanel.Children.Add(ChannelHint);
		VerifyButton = new Button
		{
			Content = "验证并绑定",
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		VerifyButton.Click += Verify_Click;
		BusyRing = new ProgressRing
		{
			Width = 16.0,
			Height = 16.0,
			IsActive = false,
			Visibility = Visibility.Collapsed,
			VerticalAlignment = VerticalAlignment.Center
		};
		StackPanel stackPanel5 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10.0
		};
		stackPanel5.Children.Add(VerifyButton);
		stackPanel5.Children.Add(BusyRing);
		stackPanel5.Children.Add(new TextBlock
		{
			Text = "Enter 可下一行与确认",
			VerticalAlignment = VerticalAlignment.Center,
			FontSize = 12.0,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap
		});
		stackPanel.Children.Add(stackPanel5);
		border.Child = stackPanel;
		return border;
	}

	private Border BuildSessionCard()
	{
		Border border = Card(12.0, 16.0, 12.0);
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 10.0
		};
		AutoLoginHint = new TextBlock
		{
			FontSize = 12.0,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap
		};
		AutoLoginAllButton = new Button
		{
			Content = "自动登录",
			MinWidth = 104.0,
			Margin = new Thickness(8.0, 2.0, 0.0, 2.0)
		};
		AutoLoginAllButton.Click += AutoLoginAll_Click;
		DelAllButton = new Button
		{
			Content = "全部删除",
			MinWidth = 96.0,
			Margin = new Thickness(0.0, 2.0, 4.0, 2.0)
		};
		DelAllButton.Click += DeleteAllSessions_Click;
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10.0,
			HorizontalAlignment = HorizontalAlignment.Right
		};
		stackPanel2.Children.Add(AutoLoginAllButton);
		stackPanel2.Children.Add(DelAllButton);
		Grid grid = new Grid
		{
			ColumnSpacing = 12.0,
			RowSpacing = 4.0
		};
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Auto)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		TextBlock textBlock = new TextBlock
		{
			Text = "账号会话",
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(textBlock, 0);
		Grid.SetRow(textBlock, 0);
		grid.Children.Add(textBlock);
		stackPanel2.VerticalAlignment = VerticalAlignment.Center;
		stackPanel2.HorizontalAlignment = HorizontalAlignment.Right;
		Grid.SetColumn(stackPanel2, 1);
		Grid.SetRow(stackPanel2, 0);
		grid.Children.Add(stackPanel2);
		AutoLoginHint.HorizontalAlignment = HorizontalAlignment.Left;
		AutoLoginHint.VerticalAlignment = VerticalAlignment.Top;
		Grid.SetColumn(AutoLoginHint, 0);
		Grid.SetRow(AutoLoginHint, 1);
		Grid.SetColumnSpan(AutoLoginHint, 2);
		grid.Children.Add(AutoLoginHint);
		stackPanel.Children.Add(grid);
		// 网易 IP 风控标记倒计时横幅（纯信息展示，不设请求门槛；触发时出现，登录成功自动消失）
		RiskBannerBorder = new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(230, 120, 53, 15)),
			CornerRadius = new CornerRadius(6.0),
			Padding = new Thickness(10.0, 6.0, 10.0, 6.0),
			Visibility = Visibility.Collapsed
		};
		RiskBannerText = new TextBlock
		{
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			Foreground = new SolidColorBrush(Colors.White)
		};
		RiskBannerBorder.Child = RiskBannerText;
		stackPanel.Children.Add(RiskBannerBorder);
		RiskNotice.Changed += OnRiskNoticeChanged;
		_riskTimer = _queue.CreateTimer();
		_riskTimer.Interval = TimeSpan.FromSeconds(1.0);
		_riskTimer.Tick += delegate
		{
			UpdateRiskBanner();
		};
		_riskTimer.Start();
		SessionListPanel = new StackPanel
		{
			Spacing = 8.0
		};
		stackPanel.Children.Add(new ScrollViewer
		{
			MaxHeight = 400.0,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Content = SessionListPanel
		});
		border.Child = stackPanel;
		return border;
	}

	private void ChannelSelect_Changed(object sender, SelectionChangedEventArgs e)
	{
		_smsCodeSent = false;
		bool flag = SelectedChannel == "cookie";
		bool flag2 = SelectedChannel == "4399";
		AccountBox.AcceptsReturn = flag;
		AccountBox.MinHeight = (flag ? 84.0 : 0.0);
		AccountBox.TextWrapping = ((!flag) ? TextWrapping.NoWrap : TextWrapping.Wrap);
		PasswordRow.Visibility = (flag ? Visibility.Collapsed : Visibility.Visible);
		PasswordLabel.Visibility = (flag ? Visibility.Collapsed : Visibility.Visible);
		CookieConvertPanel.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		CopyCookieButton.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		RegisterButton.Visibility = (flag ? Visibility.Collapsed : Visibility.Visible);
		if (flag2)
		{
			ChannelHint.Text = "提示：推荐直接在程序内用「4399 账号密码登录」绑定；手机号可点「发送验证码」改用短信验证码登录。";
			ChannelHint.Visibility = Visibility.Visible;
		}
		else
		{
			ChannelHint.Visibility = Visibility.Collapsed;
		}
		if (!flag)
		{
			CookieConvertHint.Text = "";
		}
		UpdateChannelMarkers();
	}

	private void AccountBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		_smsCodeSent = false;
		UpdateChannelMarkers();
	}

	private static bool IsPhone(string account)
	{
		if (!string.IsNullOrWhiteSpace(account))
		{
			return PhoneRegex.IsMatch(account.Trim());
		}
		return false;
	}

	private void UpdateChannelMarkers()
	{
		string selectedChannel = SelectedChannel;
		if (selectedChannel == "cookie")
		{
			AccountLabel.Text = "x19 Cookie";
			AccountBox.PlaceholderText = "粘贴由网易账密转换出的 x19 Cookie JSON";
			SendCodeButton.Visibility = Visibility.Collapsed;
			EmailDomainSelect.Visibility = Visibility.Collapsed;
			return;
		}
		bool flag = selectedChannel == "4399";
		if (IsPhone(AccountBox.Text))
		{
			AccountLabel.Text = (flag ? "4399 手机号" : "网易手机号");
			AccountBox.PlaceholderText = "11 位手机号";
			SendCodeButton.Visibility = Visibility.Visible;
			if (_smsCodeSent)
			{
				PasswordLabel.Text = "验证码";
				PasswordBox.PlaceholderText = "输入接收到的短信验证码";
			}
			else
			{
				PasswordLabel.Text = "登录密码";
				PasswordBox.PlaceholderText = "登录密码（也可点「发送验证码」改用验证码登录）";
			}
			EmailDomainSelect.Visibility = Visibility.Collapsed;
		}
		else
		{
			SendCodeButton.Visibility = Visibility.Collapsed;
			AccountLabel.Text = (flag ? "4399 账号" : "网易账号");
			AccountBox.PlaceholderText = (flag ? "4399 用户名或手机号" : "网易账号（邮箱或手机号）");
			PasswordLabel.Text = "登录密码";
			PasswordBox.PlaceholderText = "登录密码（仅本次验证使用，不保存）";
			EmailDomainSelect.Visibility = (flag ? Visibility.Collapsed : Visibility.Visible);
		}
	}

	private void AccountBox_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Enter)
		{
			e.Handled = true;
			if (string.IsNullOrWhiteSpace(AccountBox.Text))
			{
				ShowInfo("请先输入账号", ok: false);
				AccountBox.Focus(FocusState.Programmatic);
			}
			else if (PasswordRow.Visibility == Visibility.Visible && string.IsNullOrEmpty(PasswordBox.Password))
			{
				PasswordBox.Focus(FocusState.Programmatic);
			}
			else
			{
				VerifyButton.Focus(FocusState.Programmatic);
			}
		}
	}

	private string MergeEmailDomain(string input)
	{
		string text = input?.Trim() ?? "";
		if (string.IsNullOrWhiteSpace(text) || text.Contains('@'))
		{
			return text;
		}
		string text2 = (EmailDomainSelect.SelectedItem as ComboBoxItem)?.Tag as string;
		if (string.IsNullOrWhiteSpace(text2))
		{
			text2 = "163.com";
		}
		return text + "@" + text2;
	}

	private async void SendCode_Click(object sender, RoutedEventArgs e)
	{
		string phone = AccountBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(phone))
		{
			ShowInfo("请先填写手机号", ok: false);
			return;
		}
		if (!IsPhone(phone))
		{
			ShowInfo("请填写 11 位手机号后再获取验证码", ok: false);
			return;
		}
		SendCodeButton.IsEnabled = false;
		StatusBar.IsOpen = false;
		string channel = SelectedChannel;
		try
		{
			var (flag, message) = await Task.Run(async delegate
			{
				if (channel == "4399")
				{
					using (Login4399Service svc = new Login4399Service())
					{
						return ((bool Ok, string Message))(await svc.SendSmsCodeAsync(phone));
					}
				}
				using NeteaseLogin login = new NeteaseLogin();
				return ((bool Ok, string Message))(await login.SendSmsCodeAsync(phone));
			});
			if (flag)
			{
				_smsCodeSent = true;
			}
			ShowInfo(message, flag, flag ? "验证码已发送" : null);
			// 验证码发送达到上限：引导改用网页验证（短信上行等），完成后可回来重试
			if (!flag && !string.IsNullOrEmpty(message) && (message.Contains("上限") || message.Contains("频繁") || message.Contains("稍后再试") || message.Contains("too many") || message.Contains("Too Many") || message.Contains("frequency")))
			{
				ShowSmsLimitDialog(phone, channel);
			}
			UpdateChannelMarkers();
		}
		finally
		{
			SendCodeButton.IsEnabled = true;
		}
	}

	private async void CookieConvert_Click(object sender, RoutedEventArgs e)
	{
		if (SelectedChannel != "cookie")
		{
			return;
		}
		string email = MergeEmailDomain(CookieEmailBox.Text);
		string password = CookiePasswordBox.Password;
		if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
		{
			ShowInfo("请填写网易邮箱和登录密码后再转换", ok: false, "转换失败");
			return;
		}
		CookieConvertButton.IsEnabled = false;
		CookieConvertHint.Text = "正在转换…";
		StatusBar.IsOpen = false;
		try
		{
			NeteaseLogin login = new NeteaseLogin();
			NeteaseLoginResult neteaseLoginResult;
			try
			{
				neteaseLoginResult = await Task.Run(() => login.LoginAsync(email, password, createSession: false));
			}
			finally
			{
				if (login != null)
				{
					((IDisposable)login).Dispose();
				}
			}
			if (neteaseLoginResult.Success && !string.IsNullOrWhiteSpace(neteaseLoginResult.CookieJson))
			{
				AccountBox.Text = neteaseLoginResult.CookieJson;
				CookieConvertHint.Text = "已生成 Cookie，请点「验证并绑定」完成绑定。";
				ShowInfo(string.IsNullOrWhiteSpace(neteaseLoginResult.Nickname) ? "Cookie 转换成功" : ("Cookie 转换成功：" + neteaseLoginResult.Nickname), ok: true, "转换成功");
				return;
			}
			CookieConvertHint.Text = "";
			ShowInfo(neteaseLoginResult.Message, ok: false, "转换失败");
			if (!string.IsNullOrWhiteSpace(neteaseLoginResult.VerifyUrl))
			{
				OpenVerifyUrl(neteaseLoginResult.VerifyUrl);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			CookieConvertHint.Text = "";
			LogHelper.LogError(ex2, "账密转 Cookie 异常");
			ShowInfo("转换异常：" + ex2.Message, ok: false, "转换失败");
		}
		finally
		{
			CookieConvertButton.IsEnabled = true;
		}
	}

	private void CopyCookie_Click(object sender, RoutedEventArgs e)
	{
		string text = AccountBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			ShowInfo("当前没有可复制的 Cookie 内容", ok: false);
			return;
		}
		DataPackage dataPackage = new DataPackage();
		dataPackage.SetText(text);
		Clipboard.SetContent(dataPackage);
		ShowInfo("Cookie 已复制到剪贴板", ok: true);
	}

	private async void Verify_Click(object sender, RoutedEventArgs e)
	{
		string selectedChannel = SelectedChannel;
		string account = AccountBox.Text.Trim();
		string password = PasswordBox.Password;
		bool flag = selectedChannel == "cookie";
		if (string.IsNullOrWhiteSpace(account) || (!flag && string.IsNullOrWhiteSpace(password)))
		{
			if (flag)
			{
				ShowInfo("请先粘贴 x19 Cookie 后再加载账号", ok: false);
			}
			else if (IsPhone(account) && _smsCodeSent)
			{
				ShowInfo("请填写手机号和短信验证码后再登录", ok: false);
			}
			else
			{
				ShowInfo((selectedChannel == "netease") ? "请填写网易账号（邮箱或手机号）和登录密码后再验证" : "请填写 4399 账号和密码后再验证", ok: false);
			}
			return;
		}
		SetBusy(busy: true);
		StatusBar.IsOpen = false;
		try
		{
			bool flag2 = IsPhone(account);
			bool smsLogin = flag2 && _smsCodeSent;
			string storeChannel = selectedChannel;
			NeteaseLoginResult neteaseLoginResult;
			if (selectedChannel == "netease")
			{
				if (smsLogin)
				{
					storeChannel = "netease-mobile";
					NeteaseLogin smsLoginService = new NeteaseLogin();
					try
					{
						neteaseLoginResult = await Task.Run(() => smsLoginService.LoginWithSmsCodeAsync(account, password));
					}
					finally
					{
						if (smsLoginService != null)
						{
							((IDisposable)smsLoginService).Dispose();
						}
					}
				}
				else
				{
					string merged = (flag2 ? account : MergeEmailDomain(account));
					NeteaseLogin login = new NeteaseLogin();
					try
					{
						neteaseLoginResult = await Task.Run(() => login.LoginAsync(merged, password));
					}
					finally
					{
						if (login != null)
						{
							((IDisposable)login).Dispose();
						}
					}
				}
			}
			else if (flag)
			{
				NeteaseLogin cookieLogin = new NeteaseLogin();
				try
				{
					neteaseLoginResult = await Task.Run(() => cookieLogin.LoginWithCookieAsync(account, "netease"));
					if (neteaseLoginResult.Success)
					{
						neteaseLoginResult.CookieJson = account;
						account = CookieAccount(account);
						storeChannel = "netease";
					}
				}
				finally
				{
					if (cookieLogin != null)
					{
						((IDisposable)cookieLogin).Dispose();
					}
				}
			}
			else
			{
				storeChannel = (smsLogin ? "4399-mobile" : "4399");
				neteaseLoginResult = await Task.Run(async delegate
				{
					string cookie;
					using (Login4399Service svc = new Login4399Service())
					{
						Login4399Result login4399Result = (smsLogin ? (await svc.LoginWithSmsCodeAsync(account, password)) : (await svc.LoginAsync(account, password)));
						Login4399Result login4399Result2 = login4399Result;
						Login4399Result login4399Result3 = login4399Result2;
						if (!login4399Result3.Success)
						{
							return new NeteaseLoginResult
							{
								Success = false,
								Message = login4399Result3.Message
							};
						}
						cookie = login4399Result3.CookieJson;
					}
					using NeteaseLogin netease = new NeteaseLogin();
					NeteaseLoginResult neteaseLoginResult2 = await netease.LoginWithCookieAsync(cookie, "4399pc");
					if (neteaseLoginResult2.Success && string.IsNullOrWhiteSpace(neteaseLoginResult2.CookieJson))
					{
						neteaseLoginResult2.CookieJson = cookie;
					}
					return neteaseLoginResult2;
				});
			}
			if (neteaseLoginResult.Success)
			{
				if (string.IsNullOrWhiteSpace(neteaseLoginResult.CookieJson))
				{
					neteaseLoginResult.CookieJson = Current?.CookieJson ?? "";
				}
				string item = AccountStore.BindVerified(storeChannel, account, neteaseLoginResult).Message;
				// 手动登录成功：解除该账号的自动登录暂停（手动不受限，成功即说明会话已恢复）
				AccountAutoLoginService.ClearAutoLoginSuspension(storeChannel, account);
				// 账密登录（4399 账密 / 网易账密直登，均非短信验证码）：明文保存账密，供自动重登持续换新会话（每 30 分钟账密重拉 + 失效兜底）
				if ((storeChannel == "4399" || storeChannel == "netease") && !smsLogin && !string.IsNullOrWhiteSpace(password))
				{
					// 网易账密登录实际用的是补全域名后的账号，保存完整登录账号以便自动重登时直接使用
					string credUser = (storeChannel != "netease" || IsPhone(account)) ? account : MergeEmailDomain(account);
					AccountStore.LocalAccount saved = AccountStore.All.FirstOrDefault((AccountStore.LocalAccount a) => string.Equals(a.Channel, storeChannel, StringComparison.Ordinal) && string.Equals(a.Account, account, StringComparison.OrdinalIgnoreCase));
					if (saved != null && (saved.LoginUserPlain != credUser || saved.LoginPassPlain != password))
					{
						saved.SetCredentials(credUser, password);
						AccountStore.Save(saved);
						LogHelper.Log("[账号存储] 已保存 " + storeChannel + " 账密（本地加密，用于自动账密重登）: " + AccountStore.Mask(account));
					}
				}
				ShowInfo(item, ok: true);
				RefreshAccountList();
				PasswordBox.Password = "";
				AccountBox.Text = "";
				ElsewhereLoginNotice.Clear(storeChannel, account);
				_ = ProbeElsewhereLoginAsync(storeChannel, account, neteaseLoginResult.EntityId, neteaseLoginResult.Token);
				await CaptureRolesAsync(account, neteaseLoginResult, storeChannel);
				AccountBox.Focus(FocusState.Programmatic);
			}
			else
			{
				ShowInfo(neteaseLoginResult.Message, ok: false);
				if (!string.IsNullOrWhiteSpace(neteaseLoginResult.VerifyUrl))
				{
					OpenVerifyUrl(neteaseLoginResult.VerifyUrl);
				}
			}
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	private static string CookieAccount(string cookieJson)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(cookieJson);
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.ValueKind == JsonValueKind.Object && rootElement.TryGetProperty("sdkuid", out var value) && value.ValueKind == JsonValueKind.String)
			{
				string text = value.GetString();
				if (!string.IsNullOrWhiteSpace(text))
				{
					return text;
				}
			}
			if (rootElement.ValueKind == JsonValueKind.Object && rootElement.TryGetProperty("sessionid", out var value2) && value2.ValueKind == JsonValueKind.String)
			{
				string text2 = value2.GetString();
				if (!string.IsNullOrWhiteSpace(text2))
				{
					return text2;
				}
			}
		}
		catch
		{
		}
		return "Cookie账号";
	}

	private static async Task CaptureRolesAsync(string account, NeteaseLoginResult result, string channel)
	{
		if (!string.IsNullOrWhiteSpace(result.EntityId) && !string.IsNullOrWhiteSpace(result.Token))
		{
			var (flag, dictionary, text) = await NeteaseRoleService.CaptureRolesAsync(serverNames: ServerPresets.All.Select((ServerPreset p) => p.Name).ToList(), userId: result.EntityId, userToken: result.Token);
			if (flag)
			{
				AccountStore.UpdateRoles(channel, account, dictionary);
			}
			List<string> values = dictionary.SelectMany((KeyValuePair<string, List<string>> kv) => kv.Value).ToList();
			LogHelper.Log("[角色抓取] " + account + " " + channel + " => " + (flag ? string.Join(" / ", values) : text));
		}
	}

	/// <summary>登录后顺手探测一次商城接口：若提示疑似别处登录则标记提醒（仅提醒，不影响使用）。</summary>
	private static async Task ProbeElsewhereLoginAsync(string channel, string account, string entityId, string token)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(entityId) || string.IsNullOrWhiteSpace(token))
			{
				return;
			}
			await NeteaseShopService.GetCurrencyAsync(entityId, token);
		}
		catch (Exception ex) when (ElsewhereLoginNotice.IsElsewhereMessage(ex.Message))
		{
			ElsewhereLoginNotice.Mark(channel, account, "登录后探测商城：疑似在别处登录。" + ex.Message);
		}
		catch
		{
		}
	}

	private void Register_Click(object sender, RoutedEventArgs e)
	{
		string selectedChannel = SelectedChannel;
		string text = ((!(selectedChannel == "netease")) ? ((!(selectedChannel == "4399")) ? "" : "https://pt.4399.com/reg/") : "https://zc.reg.163.com/regInitialized?onlyNetease=1&fromH5=1&tp=4#/");
		string text2 = text;
		if (string.IsNullOrEmpty(text2))
		{
			ShowInfo("当前渠道暂不支持注册（Cookie 加载账号无需注册）", ok: false);
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo(text2)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "打开注册页面失败");
			ShowInfo("打开注册页面失败", ok: false);
		}
	}

	private void GoActivate_Click(object sender, RoutedEventArgs e)
	{
		MainWindow.Instance?.NavigateTo("HomePage");
	}

	private static void OpenVerifyUrl(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo(url)
			{
				UseShellExecute = true
			});
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

	private void RefreshAccountList()
	{
		if (SessionListPanel == null)
		{
			return;
		}
		SessionListPanel.Children.Clear();
		List<AccountStore.LocalAccount> list = AccountStore.All.ToList();
		if (list.Count == 0)
		{
			AutoLoginAllButton.Visibility = Visibility.Collapsed;
			AutoLoginHint.Text = "";
			SessionListPanel.Children.Add(new TextBlock
			{
				Text = "暂无保存的账号会话，请先在上方登录绑定账号。",
				Opacity = 0.6,
				TextWrapping = TextWrapping.Wrap
			});
			return;
		}
		int num = list.Count((AccountStore.LocalAccount a) => a.IsLoggedIn);
		bool flag = list.Any((AccountStore.LocalAccount a) => a.AutoLogin);
		AutoLoginAllButton.Visibility = Visibility.Visible;
		AutoLoginAllButton.Content = (flag ? "关闭自动登录" : "自动登录");
		AutoLoginHint.Text = (flag ? ("自动登录已开启（已开启 " + list.Count((AccountStore.LocalAccount a) => a.AutoLogin) + " 个）：每 5 分钟自动保活换新 token，4399 账号会话失效时自动用保存的账密重登换新 Cookie（持续挂机防掉号）；进入服务器时也会自动换取新 token。当前已登录 " + num + " 个。\n⚠ 自承风控标记风险：自动登录会周期性请求网易，可能触发网易 IP 级登录环境风控（login abnormally），风险自担。") : "");
		// 平铺展示全部账号（用户定版：不再按渠道插分组头，账号列表直接排列）
		foreach (AccountStore.LocalAccount item in list)
		{
			SessionListPanel.Children.Add(BuildAccountRow(item));
		}
	}

	private void OnRiskNoticeChanged()
	{
		_queue.TryEnqueue(delegate
		{
			UpdateRiskBanner();
		});
	}

	private void UpdateRiskBanner()
	{
		if (RiskBannerBorder == null || RiskBannerText == null)
		{
			return;
		}
		string? text = RiskNotice.BannerText();
		if (text == null)
		{
			RiskBannerBorder.Visibility = Visibility.Collapsed;
			return;
		}
		RiskBannerText.Text = text;
		RiskBannerBorder.Visibility = Visibility.Visible;
	}

	/// <summary>
	/// 验证码发送达到上限时的引导弹窗：改用网页验证（短信上行等），完成后回来重试发送。
	/// </summary>
	private async void ShowSmsLimitDialog(string phone, string channel)
	{
		try
		{
			ContentDialog contentDialog = new ContentDialog
			{
				Title = "验证码发送已达上限",
				Content = new TextBlock
				{
					Text = "该手机号今日免费验证码发送数量已达上限，改用网页验证：\n\n1. 点「打开网易登录页」，在网页完成登录/安全验证（如页面要求向指定号码发送验证短信，请按页面提示用手机发送）\n2. 完成后回到这里点「我已完成」，再点「发送验证码」重试\n\n网页验证通过后本机网络环境的风险等级会降低，验证码通常即可恢复发送。",
					TextWrapping = TextWrapping.Wrap
				},
				PrimaryButtonText = "打开网易登录页",
				SecondaryButtonText = "我已完成",
				CloseButtonText = "取消",
				DefaultButton = ContentDialogButton.Primary,
				XamlRoot = XamlRoot
			};
			ContentDialogResult contentDialogResult = await contentDialog.ShowAsync();
			if (contentDialogResult == ContentDialogResult.Primary)
			{
				Process.Start(new ProcessStartInfo("https://pay.163.com/")
				{
					UseShellExecute = true
				});
			}
			else if (contentDialogResult == ContentDialogResult.Secondary)
			{
				SendCodeButton.IsEnabled = false;
				StatusBar.IsOpen = false;
				try
				{
					var (flag2, message2) = await Task.Run(async delegate
					{
						if (channel == "4399")
						{
							using (Login4399Service login4399Service = new Login4399Service())
							{
								return ((bool Ok, string Message))(await login4399Service.SendSmsCodeAsync(phone));
							}
						}
						using NeteaseLogin login = new NeteaseLogin();
						return ((bool Ok, string Message))(await login.SendSmsCodeAsync(phone));
					});
					if (flag2)
					{
						_smsCodeSent = true;
					}
					ShowInfo(message2, flag2, flag2 ? "验证码已发送" : null);
				}
				finally
				{
					SendCodeButton.IsEnabled = true;
				}
			}
		}
		catch
		{
		}
	}

	private UIElement BuildAccountRow(AccountStore.LocalAccount acc)
	{
		Grid grid = new Grid
		{
			ColumnSpacing = 8.0
		};
		for (int i = 0; i < 6; i++)
		{
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = ((i == 0) ? new GridLength(1.0, GridUnitType.Star) : GridLength.Auto)
			});
		}
		TextBlock textBlock = new TextBlock
		{
			Text = AccountUidLabel(acc),
			TextAlignment = TextAlignment.Left,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			FontWeight = FontWeights.SemiBold,
			IsTextScaleFactorEnabled = false,
			IsTextSelectionEnabled = true
		};
		StackPanel labelPanel = new StackPanel
		{
			Spacing = 2.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		labelPanel.Children.Add(textBlock);
		if (AppLock.IsPreferredNetAccount(acc.Account))
		{
			labelPanel.Children.Add(new TextBlock
			{
				Text = "✓ 验证账号（网易/4399 均可）",
				FontSize = 11.0,
				Foreground = new SolidColorBrush(Colors.LightGreen)
			});
		}
		ElsewhereLoginNotice.Notice elsewhere = ElsewhereLoginNotice.Get(acc.Channel, acc.Account);
		if (elsewhere != null)
		{
			labelPanel.Children.Add(new TextBlock
			{
				Text = "⚠ 疑似别处登录（" + elsewhere.Time.ToString("HH:mm") + "）：" + elsewhere.Reason,
				FontSize = 11.0,
				TextWrapping = TextWrapping.Wrap,
				Foreground = new SolidColorBrush(Colors.Orange)
			});
		}
		Grid.SetColumn(labelPanel, 0);
		grid.Children.Add(labelPanel);
		TextBox remark = new TextBox
		{
			Text = (acc.Remark ?? ""),
			PlaceholderText = "备注",
			FontSize = 12.0,
			MinWidth = 88.0,
			MaxLength = 10,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalContentAlignment = HorizontalAlignment.Left
		};
		remark.Width = RemarkWidth(acc.Remark ?? "");
		remark.TextChanged += delegate
		{
			remark.Width = RemarkWidth(remark.Text ?? "");
		};
		remark.LostFocus += delegate
		{
			AccountStore.LocalAccount localAccount = AccountStore.All.FirstOrDefault((AccountStore.LocalAccount a) => a == acc);
			if (localAccount != null)
			{
				localAccount.Remark = remark.Text?.Trim() ?? "";
				AccountStore.Save(localAccount);
			}
		};
		Grid.SetColumn(remark, 1);
		grid.Children.Add(remark);
		Button button = new Button
		{
			Content = (acc.IsLoggedIn ? "取消登录" : "登录账号"),
			MinWidth = 96.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		button.Click += async delegate
		{
			AccountStore.LocalAccount cur = AccountStore.All.FirstOrDefault((AccountStore.LocalAccount a) => a == acc);
			if (cur == null)
			{
				RefreshAccountList();
			}
			else
			{
				if (cur.IsLoggedIn)
				{
					AccountStore.SetLoginState(cur.Channel, cur.Account, loggedIn: false);
					ProxyCaptureService.RestartIfRunningForPreset();
					ShowInfo("已取消登录，该账号不再加入代理分配池。", ok: true, "取消登录");
				}
				else
				{
					if (string.IsNullOrWhiteSpace(cur.CookieJson))
					{
						ShowInfo("该账号没有已保存会话，请在上方重新验证绑定。", ok: false, "请先验证");
						return;
					}
					SetBusy(busy: true);
					try
					{
						if (await TryReloginAsync(cur))
						{
							AccountStore.SetLoginState(cur.Channel, cur.Account, loggedIn: true);
							// 手动登录成功：解除该账号的自动登录暂停（手动不受限，成功即说明会话已恢复）
							AccountAutoLoginService.ClearAutoLoginSuspension(cur.Channel, cur.Account);
							ElsewhereLoginNotice.Clear(cur.Channel, cur.Account);
							ProxyCaptureService.RestartIfRunningForPreset();
							ShowInfo("已登录该账号，加入代理分配池。", ok: true, "登录成功");
							_ = ProbeElsewhereLoginAsync(cur.Channel, cur.Account, cur.EntityId, cur.Token);
						}
						else
						{
							ShowInfo("自动登录失败，凭证可能已过期，请在上方重新验证绑定。", ok: false, "登录失败");
						}
					}
					finally
					{
						SetBusy(busy: false);
					}
				}
				RefreshAccountList();
			}
		};
		Grid.SetColumn(button, 2);
		grid.Children.Add(button);
		Button button2 = new Button
		{
			Content = "删除",
			MinWidth = 88.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		button2.Click += delegate
		{
			AccountStore.Remove(acc.Channel, acc.Account);
			ProxyCaptureService.RestartIfRunningForPreset();
			ShowInfo("已删除该账号及其保存的会话，验证账号记录已同步移除。分组角色与端口记忆已保留，重新绑定同账号可继续使用；手动启动代理时会自动跳过已删账号。", ok: true, "删除账号");
			RefreshAccountList();
		};
		Grid.SetColumn(button2, 3);
		grid.Children.Add(button2);
		// 设为/取消 网易验证账号（本机锁解锁与改密验证使用该账号账密）
		// 不可用情形直接禁用+淡色（而不是可点但点了只弹提示）：
		// 1) 「设为验证」在程序锁定中禁用（取消验证锁定态允许）；2) 已满 3 个再「设为验证」；
		// 3) 只剩 1 个验证账号的「取消验证」（至少保留一个）
		bool preferred = AppLock.IsPreferredNetAccount(acc.Account);
		int verifyCount = AppLock.PreferredNetAccountCount;
		string? verifyDisabledReason = null;
		if (AppLock.IsUnlocked)
		{
			if (!preferred && verifyCount >= 3)
			{
				verifyDisabledReason = "验证账号已满 3 个，请先取消一个再设置";
			}
			else if (preferred && verifyCount <= 1)
			{
				verifyDisabledReason = "至少保留一个验证账号，不能取消";
			}
		}
		else if (!preferred)
		{
			verifyDisabledReason = "程序锁定中，「设为验证」需先解锁（取消验证不受限）";
		}
		else if (verifyCount <= 1)
		{
			verifyDisabledReason = "至少保留一个验证账号，不能取消";
		}
		Button verifyBtn = new Button
		{
			Content = (preferred ? "取消验证" : "设为验证"),
			MinWidth = 92.0,
			VerticalAlignment = VerticalAlignment.Center,
			IsEnabled = (verifyDisabledReason == null)
		};
		if (verifyDisabledReason != null)
		{
			ToolTipService.SetToolTip(verifyBtn, verifyDisabledReason);
			verifyBtn.Opacity = 0.4;
		}
		verifyBtn.Click += delegate
		{
			(bool Ok, string Message) = AppLock.SetPreferredNetAccount(acc.Account, !preferred);
			MainWindow.Instance?.ShowToast(Message, Ok);
			RefreshAccountList();
		};
		Grid.SetColumn(verifyBtn, 4);
		grid.Children.Add(verifyBtn);
		return grid;
	}

	private static double RemarkWidth(string text)
	{
		return Math.Max(88.0, Math.Min(170.0, 40.0 + (double)text.Length * 14.0));
	}

	private void AutoLoginAll_Click(object sender, RoutedEventArgs e)
	{
		List<AccountStore.LocalAccount> list = AccountStore.All.ToList();
		if (list.Count == 0)
		{
			return;
		}
		if (list.Any((AccountStore.LocalAccount a) => a.AutoLogin))
		{
			int num = list.Count((AccountStore.LocalAccount a) => a.AutoLogin);
			AccountAutoLoginService.Cancel();
			AccountStore.SetAutoLoginAll(autoLogin: false);
			LogHelper.Log("[自动登录] 用户关闭自动登录（原开启 " + num + " 个）");
			ShowInfo("已关闭自动登录，账号记录保留。", ok: true, "自动登录");
			RefreshAccountList();
			return;
		}
		AccountStore.SetAutoLoginAll(autoLogin: true);
		LogHelper.Log("[自动登录] 用户开启自动登录（" + list.Count + " 个账号），周期保活自动进行");
		// 不再全量逐个拉取：开关只改状态，会话由 5 分钟保活 / 30 分钟账密保活 / 进服失败兜底自动维护
		ShowInfo("已开启自动登录：每 5 分钟保活会话，保存了账密的账号每 30 分钟自动换新会话，无需手动拉取。", ok: true, "自动登录");
		RefreshAccountList();
	}

	private async void DeleteAllSessions_Click(object sender, RoutedEventArgs e)
	{
		List<AccountStore.LocalAccount> accounts = AccountStore.All.Where((AccountStore.LocalAccount a) => !string.IsNullOrWhiteSpace(a.Channel) && !string.IsNullOrWhiteSpace(a.Account)).ToList();
		if (accounts.Count == 0)
		{
			ShowInfo("当前没有已保存的账号会话。", ok: false, "全部删除");
		}
		else
		{
			if (await DialogGate.ShowAsync(new ContentDialog
			{
				Title = "全部删除确认",
				Content = "确定要删除全部 " + accounts.Count + " 个已保存的账号会话吗？将同时删除各账号的登录凭证与验证账号记录，且不可恢复。分组角色与端口记忆会保留（重新绑定后可继续使用，启动代理时自动跳过已删账号）。",
				PrimaryButtonText = "全部删除",
				CloseButtonText = "取消",
				DefaultButton = ContentDialogButton.Close,
				XamlRoot = base.XamlRoot,
				RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default)
			}) != ContentDialogResult.Primary)
			{
				return;
			}
			try
			{
				foreach (AccountStore.LocalAccount item in accounts)
				{
					AccountStore.Remove(item.Channel, item.Account);
				}
				ProxyCaptureService.RestartIfRunningForPreset();
				LogHelper.Log("[账号] 全部删除账号会话: " + accounts.Count + " 个");
				ShowInfo("已删除全部 " + accounts.Count + " 个账号会话及其登录凭证，验证账号记录已同步移除。分组角色与端口记忆保留。", ok: true, "全部删除");
				RefreshAccountList();
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "全部删除账号会话失败");
				ShowInfo("删除失败：" + ex.Message, ok: false, "全部删除");
			}
		}
	}

	private static async Task<bool> TryReloginAsync(AccountStore.LocalAccount acc)
	{
		NeteaseLogin login = new NeteaseLogin();
		NeteaseLoginResult neteaseLoginResult;
		try
		{
			neteaseLoginResult = await Task.Run(() => login.LoginWithCookieAsync(acc.CookieJson, acc.Channel));
		}
		finally
		{
			if (login != null)
			{
				((IDisposable)login).Dispose();
			}
		}
		if (!neteaseLoginResult.Success || string.IsNullOrWhiteSpace(neteaseLoginResult.EntityId) || string.IsNullOrWhiteSpace(neteaseLoginResult.Token))
		{
			LogHelper.Log("[账号] 自动登录失败: " + acc.Channel + " " + acc.Account + " => " + neteaseLoginResult.Message);
			return false;
		}
		neteaseLoginResult.CookieJson = acc.CookieJson;
		if (string.IsNullOrWhiteSpace(neteaseLoginResult.Nickname))
		{
			neteaseLoginResult.Nickname = acc.Nickname;
		}
		AccountStore.BindVerified(acc.Channel, acc.Account, neteaseLoginResult);
		await CaptureRolesAsync(acc.Account, neteaseLoginResult, acc.Channel);
		return true;
	}
}

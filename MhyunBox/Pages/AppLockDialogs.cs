using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MhyunBox.Services;
using Windows.System;

namespace MhyunBox.Pages;

// 本机锁弹窗集合：启动创建账号 / 登录解锁（含网易账号验证解锁）/ 更改密码 / 退出程序。
// 输入体验与「账号绑定」页一致：账号框输入后按 Enter 跳到密码框，密码输完按 Enter 直接验证/提交。
// 解锁与首启创建弹窗都是「弹窗内跳转页」：可在 程序账号页 ↔ 网易账号页 之间切换，
// 首次打开也可以不创建程序账号，直接用可登录的网易账号登录验证（并保存到账号绑定）。
public static class AppLockDialogs
{
	/// <summary>启动流程：无账号 → 创建；有账号未解锁 → 登录解锁（已开启自动登录时不会走到这里）。</summary>
	public static async System.Threading.Tasks.Task ShowStartupFlowAsync()
	{
		try
		{
			if (!AppLock.HasAccount)
			{
				await ShowCreateDialogAsync();
			}
			else if (!AppLock.IsUnlocked)
			{
				await ShowUnlockDialogAsync();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[程序锁] 启动弹窗流程异常");
		}
	}

	/// <summary>首次弹窗：创建本机程序账号，或直接用可登录的网易账号登录验证并保存到账号绑定。</summary>
	public static async System.Threading.Tasks.Task ShowCreateDialogAsync()
	{
		XamlRoot? root = RootXaml();
		if (root == null)
		{
			return;
		}
		// —— 页 1：创建程序账号 ——
		TextBox userBox = new TextBox
		{
			PlaceholderText = "仅保存在本机，如 Notel",
			Header = "账号名"
		};
		PasswordBox passBox = new PasswordBox
		{
			PlaceholderText = "至少 4 位",
			Header = "密码"
		};
		PasswordBox confirmBox = new PasswordBox
		{
			PlaceholderText = "再输入一次",
			Header = "确认密码"
		};
		TextBlock err1 = ErrText();
		StackPanel page1 = new StackPanel
		{
			Spacing = 10.0,
			MinWidth = 340.0
		};
		page1.Children.Add(new TextBlock
		{
			Text = "欢迎！首次使用请创建一个本机程序账号。它只保存在这台电脑上（密码仅存 MD5 单向哈希），用于解锁程序里的账密数据。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13.0,
			Opacity = 0.8
		});
		page1.Children.Add(userBox);
		page1.Children.Add(passBox);
		page1.Children.Add(confirmBox);
		page1.Children.Add(err1);
		page1.Children.Add(PageLink("不想创建程序账号？可直接登录网易账号（登录验证并保存到账号绑定）"));
		// —— 页 2：网易账号登录验证（首启免建程序账号）——
		ComboBox channelBox = new ComboBox
		{
			Header = "登录渠道",
			SelectedIndex = 0,
			MinWidth = 240.0
		};
		ThemeHelper.SolidComboPopup(channelBox);
		channelBox.Items.Add(MakeItem("网易账号（邮箱/手机号）", "netease"));
		channelBox.Items.Add(MakeItem("4399 账号（用户名/手机号）", "4399"));
		ComboBox domainBox = new ComboBox
		{
			Header = "邮箱后缀（账号没填 @ 后缀时自动补全合并）",
			SelectedIndex = 0,
			MinWidth = 240.0
		};
		ThemeHelper.SolidComboPopup(domainBox);
		domainBox.Items.Add(MakeItem("@163.com", "163.com"));
		domainBox.Items.Add(MakeItem("@qq.com", "qq.com"));
		domainBox.Items.Add(MakeItem("@126.com", "126.com"));
		domainBox.Items.Add(MakeItem("@yeah.net", "yeah.net"));
		domainBox.Items.Add(MakeItem("@vip.163.com", "vip.163.com"));
		domainBox.Items.Add(MakeItem("@188.com", "188.com"));
		TextBox netAcc = new TextBox
		{
			Header = "网易账号"
		};
		PasswordBox netPass = new PasswordBox
		{
			Header = "该网易账号的密码"
		};
		TextBlock err2 = ErrText();
		StackPanel page2 = new StackPanel
		{
			Spacing = 10.0,
			MinWidth = 340.0
		};
		page2.Children.Add(new TextBlock
		{
			Text = "不创建程序账号也可以：直接登录一个可用的网易/4399 账号完成验证，登录成功后会自动保存到「账号绑定」，\n该账号密码同时作为本机程序密码（只存 MD5 单向哈希）。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13.0,
			Opacity = 0.8
		});
		page2.Children.Add(channelBox);
		page2.Children.Add(domainBox);
		page2.Children.Add(netAcc);
		page2.Children.Add(netPass);
		page2.Children.Add(err2);
		page2.Children.Add(PageLink("返回创建程序账号"));
		StackPanel host = new StackPanel
		{
			MinWidth = 340.0
		};
		host.Children.Add(page1);
		host.Children.Add(page2);
		ContentDialog dialog = new ContentDialog
		{
			Title = "欢迎使用",
			Content = host,
			PrimaryButtonText = "创建并解锁",
			CloseButtonText = "稍后",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = root
		};
		void ShowPage(int index)
		{
			page1.Visibility = (index == 1 ? Visibility.Visible : Visibility.Collapsed);
			page2.Visibility = (index == 2 ? Visibility.Visible : Visibility.Collapsed);
			dialog.Title = (index == 1 ? "创建本机程序账号" : "使用网易账号登录");
			dialog.PrimaryButtonText = (index == 1 ? "创建并解锁" : "登录并验证");
			ShowErr(err1, "");
			ShowErr(err2, "");
		}
		bool success = false;
		void Run1()
		{
			(bool Ok, string Message) = AppLock.CreateAccount(userBox.Text, passBox.Password, confirmBox.Password);
			if (Ok)
			{
				success = true;
				dialog.Hide();
			}
			else
			{
				ShowErr(err1, Message);
			}
		}
		async void Run2()
		{
			try
			{
				string account = netAcc.Text.Trim();
				string pass = netPass.Password;
				if (account.Length == 0 || pass.Length == 0)
				{
					ShowErr(err2, "请输入账号和密码");
					return;
				}
				string channel = ((channelBox.SelectedItem as ComboBoxItem)?.Tag as string) ?? "netease";
				// 网易邮箱账号没填 @ 后缀时按所选后缀自动补全合并（与账号绑定页一致）
				if (channel == "netease")
				{
					account = MergeEmailDomain(account, domainBox);
				}
				NeteaseLoginResult result;
				if (channel == "4399")
				{
					using Login4399Service login = new Login4399Service();
					Login4399Result r = await login.LoginAsync(account, pass);
					result = new NeteaseLoginResult
					{
						Success = r.Success,
						Message = r.Message,
						Channel = "4399",
						CookieJson = r.CookieJson,
						Nickname = account
					};
				}
				else
				{
					using NeteaseLogin login = new NeteaseLogin();
					result = await login.LoginAsync(account, pass);
					if (string.IsNullOrEmpty(result.Channel))
					{
						result.Channel = "netease";
					}
				}
				if (!result.Success)
				{
					ShowErr(err2, (string.IsNullOrEmpty(result.VerifyUrl) ? "登录失败：" + result.Message : "登录需要验证，请到「账号绑定」页完成滑块/验证后再试") );
					return;
				}
				// 用该网易账密创建程序账号（程序密码=该密码）并指定为验证账号
				(bool Ok, string Message) created = AppLock.CreateAccountFromNetease(account, pass);
				if (!created.Ok)
				{
					ShowErr(err2, created.Message);
					return;
				}
				// 登录成功 → 保存到账号绑定（含 Cookie），并补存账密供之后自动重登/验证通道使用
				(bool Ok2, string Message2) = AccountStore.BindVerified(result.Channel, account, result);
				if (Ok2)
				{
					foreach (AccountStore.LocalAccount a in AccountStore.All)
					{
						if (AppLock.SameAccountName(a.Account, account) && a.Channel == result.Channel)
						{
							a.LoginUser = CredentialProtector.Encrypt(account);
							a.LoginPass = CredentialProtector.Encrypt(pass);
							a.AutoLogin = true;
							AccountStore.Save(a);
							break;
						}
					}
				}
				success = true;
				dialog.Hide();
				MainWindow.Instance?.ShowToast("已用网易账号验证并登录，账号已保存到「账号绑定」", true);
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[程序锁] 首启网易登录创建失败");
				ShowErr(err2, "登录异常：" + ex.Message);
			}
		}
		// Enter 跳转：页1 账号→密码→确认→创建；页2 账号→密码→登录
		userBox.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				passBox.Focus(FocusState.Programmatic);
			}
		};
		passBox.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				confirmBox.Focus(FocusState.Programmatic);
			}
		};
		confirmBox.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				Run1();
			}
		};
		netAcc.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				netPass.Focus(FocusState.Programmatic);
			}
		};
		netPass.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				Run2();
			}
		};
		dialog.PrimaryButtonClick += (d, args) =>
		{
			args.Cancel = true;
			if (page1.Visibility == Visibility.Visible)
			{
				Run1();
			}
			else
			{
				Run2();
			}
		};
		// 渠道切换：4399 不需要邮箱后缀
		channelBox.SelectionChanged += (s, e) =>
		{
			domainBox.IsEnabled = ((channelBox.SelectedItem as ComboBoxItem)?.Tag as string) != "4399";
		};
		// 页面跳转链接
		((HyperlinkButton)page1.Children[page1.Children.Count - 1]).Click += (s, e) =>
		{
			ShowPage(2);
		};
		((HyperlinkButton)page2.Children[page2.Children.Count - 1]).Click += (s, e) =>
		{
			ShowPage(1);
		};
		ShowPage(1);
		await DialogGate.ShowAsync(dialog);
		if (success && page1.Visibility == Visibility.Visible)
		{
			MainWindow.Instance?.ShowToast("程序账号已创建，账密功能已解锁", true);
		}
	}

	/// <summary>解锁弹窗（弹窗内跳转页）：程序账号密码登录 ↔ 网易账号验证解锁。</summary>
	public static System.Threading.Tasks.Task ShowUnlockDialogAsync()
	{
		return ShowUnlockDialogCore(startOnNetease: false);
	}

	/// <summary>网易账号验证解锁（设置页入口）：直接打开解锁弹窗的网易验证页。</summary>
	public static System.Threading.Tasks.Task ShowNeteaseUnlockDialogAsync()
	{
		return ShowUnlockDialogCore(startOnNetease: true);
	}

	private static async System.Threading.Tasks.Task ShowUnlockDialogCore(bool startOnNetease)
	{
		XamlRoot? root = RootXaml();
		if (root == null)
		{
			return;
		}
		if (startOnNetease && AppLock.NetAccountName == null)
		{
			MainWindow.Instance?.ShowToast("本机还没有网易验证通道：请先用程序密码解锁一次（绑定网易账密后自动建立）", false);
			return;
		}
		// —— 页 1：程序账号解锁 ——
		TextBox userBox = new TextBox
		{
			Header = "程序账号（可不填）",
			PlaceholderText = "账号已匿名打码：" + AppLock.AccountNameMasked + "，可不填或只输主体名"
		};
		PasswordBox passBox = new PasswordBox
		{
			Header = "密码"
		};
		TextBlock err1 = ErrText();
		StackPanel page1 = new StackPanel
		{
			Spacing = 10.0,
			MinWidth = 340.0
		};
		page1.Children.Add(new TextBlock
		{
			Text = "程序处于锁定状态，登录本机程序账号后才能使用账密 / Cookie 功能。（程序账号：" + AppLock.AccountNameMasked + "，只露前几位）",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13.0,
			Opacity = 0.8
		});
		page1.Children.Add(userBox);
		page1.Children.Add(passBox);
		page1.Children.Add(err1);
		page1.Children.Add(PageLink("改用网易账号验证解锁（不需要程序密码）"));
		// —— 页 2：网易账号验证解锁 ——
		TextBox accBox = new TextBox
		{
			Header = "已绑定的网易账号",
			PlaceholderText = "账号内容（可不带后缀，有后缀也可带上）"
		};
		PasswordBox netPass = new PasswordBox
		{
			Header = "该网易账号的密码"
		};
		TextBlock err2 = ErrText();
		StackPanel page2 = new StackPanel
		{
			Spacing = 10.0,
			MinWidth = 340.0
		};
		page2.Children.Add(new TextBlock
		{
			Text = "本机验证账号：" + AppLock.NetAccountHint + "（网易/4399 账号均可，完整账号不会显示）\n输入该账号的账密，验证通过即可解锁程序（等效程序密码解锁，不需要网络）。账号名可不带后缀。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13.0,
			Opacity = 0.8
		});
		page2.Children.Add(accBox);
		page2.Children.Add(netPass);
		page2.Children.Add(err2);
		page2.Children.Add(PageLink("返回程序账号登录"));
		StackPanel host = new StackPanel
		{
			MinWidth = 340.0
		};
		host.Children.Add(page1);
		host.Children.Add(page2);
		ContentDialog dialog = new ContentDialog
		{
			Title = "解锁程序",
			Content = host,
			PrimaryButtonText = "解锁",
			CloseButtonText = "稍后",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = root
		};
		void ShowPage(int index)
		{
			page1.Visibility = (index == 1 ? Visibility.Visible : Visibility.Collapsed);
			page2.Visibility = (index == 2 ? Visibility.Visible : Visibility.Collapsed);
			dialog.Title = (index == 1 ? "解锁程序" : "网易账号验证解锁");
			dialog.PrimaryButtonText = (index == 1 ? "解锁" : "验证并解锁");
			ShowErr(err1, "");
			ShowErr(err2, "");
		}
		bool success = false;
		void Run1()
		{
			(bool Ok, string Message) = AppLock.Unlock(userBox.Text, passBox.Password);
			if (Ok)
			{
				success = true;
				dialog.Hide();
			}
			else
			{
				ShowErr(err1, Message);
			}
		}
		void Run2()
		{
			(bool Ok, string Message) = AppLock.UnlockByNetease(accBox.Text, netPass.Password);
			if (Ok)
			{
				success = true;
				dialog.Hide();
			}
			else
			{
				ShowErr(err2, Message);
				netPass.Password = "";
			}
		}
		// Enter 跳转
		userBox.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				passBox.Focus(FocusState.Programmatic);
			}
		};
		passBox.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				Run1();
			}
		};
		accBox.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				netPass.Focus(FocusState.Programmatic);
			}
		};
		netPass.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				Run2();
			}
		};
		dialog.PrimaryButtonClick += (d, args) =>
		{
			args.Cancel = true;
			if (page1.Visibility == Visibility.Visible)
			{
				Run1();
			}
			else
			{
				Run2();
			}
		};
		((HyperlinkButton)page1.Children[page1.Children.Count - 1]).Click += (s, e) =>
		{
			if (AppLock.NetAccountName == null)
			{
				ShowErr(err1, "本机还没有网易验证通道（解锁一次并绑定网易账密后自动建立）");
				return;
			}
			ShowPage(2);
		};
		((HyperlinkButton)page2.Children[page2.Children.Count - 1]).Click += (s, e) =>
		{
			ShowPage(1);
		};
		ShowPage(startOnNetease ? 2 : 1);
		await DialogGate.ShowAsync(dialog);
		if (success)
		{
			MainWindow.Instance?.ShowToast(page1.Visibility == Visibility.Visible ? "已解锁，账密数据可用" : "网易账号验证通过，已解锁", true);
		}
	}

	/// <summary>更改程序账号密码：先用「当前程序密码」或「网易账号账密」验证身份，再设置新密码。
	/// 输入框 Enter 逐级跳转，最后一级 Enter 直接提交。</summary>
	public static async System.Threading.Tasks.Task ShowChangePasswordDialogAsync()
	{
		XamlRoot? root = RootXaml();
		if (root == null)
		{
			return;
		}
		if (!AppLock.IsUnlocked)
		{
			MainWindow.Instance?.ShowToast("请先解锁程序", false);
			return;
		}
		RadioButton byPass = new RadioButton
		{
			Content = "用当前程序密码验证",
			IsChecked = true,
			GroupName = "verify"
		};
		PasswordBox curPass = new PasswordBox
		{
			Header = "当前程序密码",
			Margin = new Thickness(20.0, 0.0, 0.0, 0.0)
		};
		RadioButton byNet = new RadioButton
		{
			Content = "用已绑定网易/4399 账号验证",
			GroupName = "verify"
		};
		bool hasNet = AppLock.NetAccountName != null;
		if (!hasNet)
		{
			byNet.IsEnabled = false;
			byNet.Content += "（未建立网易验证通道）";
		}
		else
		{
			byNet.Content += "（本机验证账号：" + AppLock.NetAccountHint + "）";
		}
		TextBox netAcc = new TextBox
		{
			Header = "网易账号",
			PlaceholderText = "账号内容（可不带后缀，有后缀也可带上）",
			Margin = new Thickness(20.0, 0.0, 0.0, 0.0),
			IsEnabled = hasNet
		};
		PasswordBox netPass = new PasswordBox
		{
			Header = "该网易账号的密码",
			Margin = new Thickness(20.0, 0.0, 0.0, 0.0)
		};
		PasswordBox newPass = new PasswordBox
		{
			Header = "新密码（至少 4 位）"
		};
		PasswordBox newPass2 = new PasswordBox
		{
			Header = "确认新密码"
		};
		TextBlock err = ErrText();
		StackPanel body = new StackPanel
		{
			Spacing = 10.0,
			MinWidth = 360.0
		};
		body.Children.Add(byPass);
		body.Children.Add(curPass);
		body.Children.Add(byNet);
		body.Children.Add(netAcc);
		body.Children.Add(netPass);
		body.Children.Add(new TextBlock
		{
			Text = "—— 新密码 ——",
			FontSize = 12.0,
			Opacity = 0.6,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		});
		body.Children.Add(newPass);
		body.Children.Add(newPass2);
		body.Children.Add(err);
		ContentDialog dialog = new ContentDialog
		{
			Title = "更改程序账号密码",
			Content = body,
			PrimaryButtonText = "确认更改",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = root
		};
		bool success = false;
		void Run()
		{
			bool verified;
			if (byNet.IsChecked == true && hasNet)
			{
				verified = AppLock.VerifyByNetease(netAcc.Text, netPass.Password);
				if (!verified)
				{
					ShowErr(err, "网易账号账密验证未通过");
					return;
				}
			}
			else
			{
				verified = AppLock.VerifyProgramPassword(curPass.Password);
				if (!verified)
				{
					ShowErr(err, "当前程序密码不正确");
					return;
				}
			}
			(bool Ok, string Message) = AppLock.ChangePassword(newPass.Password, newPass2.Password);
			if (Ok)
			{
				success = true;
				dialog.Hide();
			}
			else
			{
				ShowErr(err, Message);
			}
		}
		// Enter 逐级跳转，最后一级 Enter 提交
		curPass.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				newPass.Focus(FocusState.Programmatic);
			}
		};
		netAcc.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				netPass.Focus(FocusState.Programmatic);
			}
		};
		netPass.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				newPass.Focus(FocusState.Programmatic);
			}
		};
		newPass.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				newPass2.Focus(FocusState.Programmatic);
			}
		};
		newPass2.KeyDown += (s, e) =>
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				Run();
			}
		};
		dialog.PrimaryButtonClick += (d, args) =>
		{
			args.Cancel = true;
			Run();
		};
		await DialogGate.ShowAsync(dialog);
		if (success)
		{
			MainWindow.Instance?.ShowToast("程序账号密码已更改", true);
		}
	}

	/// <summary>退出程序：锁定程序账号并关闭程序（首页 / 导航栏底部的退出入口共用）。</summary>
	public static async System.Threading.Tasks.Task ShowExitProgramDialogAsync()
	{
		XamlRoot? root = RootXaml();
		if (root == null)
		{
			return;
		}
		ContentDialog dialog = new ContentDialog
		{
			Title = "退出程序",
			Content = "将锁定本程序的账密 / Cookie 数据（退出程序账号），然后关闭程序。确定退出？",
			PrimaryButtonText = "退出并关闭",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = root
		};
		if (await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary)
		{
			AppLock.Lock();
			LogHelper.Log("[程序锁] 已退出程序账号并关闭程序");
			MainWindow.Instance?.Close();
		}
	}

	private static HyperlinkButton PageLink(string text)
	{
		return new HyperlinkButton
		{
			Content = text,
			FontSize = 13.0,
			Padding = new Thickness(0.0),
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
		};
	}

	private static ComboBoxItem MakeItem(string content, string tag)
	{
		return new ComboBoxItem
		{
			Content = content,
			Tag = tag
		};
	}

	/// <summary>邮箱后缀合并：账号没填 @ 后缀时按所选域名补全（与账号绑定页 MergeEmailDomain 一致）。</summary>
	private static string MergeEmailDomain(string input, ComboBox domainBox)
	{
		string text = input?.Trim() ?? "";
		if (string.IsNullOrWhiteSpace(text) || text.Contains('@'))
		{
			return text;
		}
		string domain = (domainBox.SelectedItem as ComboBoxItem)?.Tag as string;
		if (string.IsNullOrWhiteSpace(domain))
		{
			domain = "163.com";
		}
		return text + "@" + domain;
	}

	private static TextBlock ErrText()
	{
		return new TextBlock
		{
			Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
			TextWrapping = TextWrapping.Wrap,
			Visibility = Visibility.Collapsed
		};
	}

	private static void ShowErr(TextBlock err, string msg)
	{
		if (string.IsNullOrEmpty(msg))
		{
			err.Visibility = Visibility.Collapsed;
			return;
		}
		err.Text = msg;
		err.Visibility = Visibility.Visible;
	}

	private static XamlRoot? RootXaml()
	{
		try
		{
			if (MainWindow.Instance?.Content is FrameworkElement fe && fe.XamlRoot != null)
			{
				return fe.XamlRoot;
			}
		}
		catch
		{
		}
		return null;
	}
}

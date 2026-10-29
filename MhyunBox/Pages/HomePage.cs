using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Threading.Tasks;
using MhyunBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using WinRT;

namespace MhyunBox.Pages;

public sealed partial class HomePage : Page
{
	private readonly DispatcherQueue _queue;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel HomeRoot;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Grid HomeCardGrid;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Border ActivationCard;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Border NoticeCard;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Border DisclaimerCard;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock ActivationStatusText;


	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button GoProxyButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBox CardKeyBox;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button ActivateButton;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Button DeactivateButton;


	public HomePage()
	{
		InitializeComponent();
		TrimNoticeCard();
		ApplyOpenSourceTweaks();
		// GitHub 仓库联动卡片：置于首页顶部开源提示声明之下、盐值获取之上
		try
		{
			HomeRoot.Children.Add(BuildGitHubCard());
		}
		catch (Exception ex3)
		{
			LogHelper.LogError(ex3, "首页 GitHub 联动卡片加载失败");
		}
		// 盐值与版本卡片（代码后置构建：Alane 首位地址 / 404 三层 Fernet / 官方 DLL 索取 / 0x11 自动切换）
		try
		{
			HomeRoot.Children.Add(new SaltSourcePanel());
		}
		catch (Exception ex4)
		{
			LogHelper.LogError(ex4, "首页盐值面板加载失败");
		}
		// 彩蛋：会躲鼠标的「不要点击」按钮（机制参考 PCL 愚人节 AprilScaleTrans）
		try
		{
			PrankButton prank = new PrankButton();
			prank.Surrendered += delegate
			{
				LogHelper.Log("[彩蛋] 用户逮到了「不要点击」按钮");
			};
			HomeRoot.Children.Add(prank);
		}
		catch (Exception ex5)
		{
			LogHelper.LogError(ex5, "彩蛋按钮加载失败");
		}
		_queue = base.DispatcherQueue;
		base.Unloaded += OnUnloaded;
		base.Loaded += delegate
		{
			ActivationService.StateChanged += OnActivationChanged;
			RefreshActivation();
			RelayoutHomeCards(base.ActualWidth);
		};
	}

	// ---------- GitHub 联动卡片（更新检查 / 合并请求） ----------

	private FrameworkElement BuildGitHubCard()
	{
		StackPanel card = new StackPanel
		{
			Spacing = 10.0
		};
		card.Children.Add(new TextBlock
		{
			Text = "仓库与更新",
			FontSize = 16.0,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
		});
		card.Children.Add(new TextBlock
		{
			Text = "开源仓库：" + GitHubService.RepoUrl + "　当前版本 " + AppVersion.Display
				+ (GitHubService.HasToken ? "　（已配置 Token，合并请求可在程序内直接提交）" : "　（未配置 Token，合并请求将打开网页预填页）"),
			FontSize = 12.0,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap,
			IsTextSelectionEnabled = true
		});

		StackPanel row = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10.0
		};

		Button checkBtn = new Button
		{
			Content = "🔄 检查更新",
			MinWidth = 120.0
		};
		checkBtn.Click += async delegate
		{
			checkBtn.IsEnabled = false;
			checkBtn.Content = "检查中…";
			try
			{
				await ShowUpdateResultAsync();
			}
			finally
			{
				checkBtn.Content = "🔄 检查更新";
				checkBtn.IsEnabled = true;
			}
		};
		row.Children.Add(checkBtn);

		Button prBtn = new Button
		{
			Content = "🔀 提交合并请求",
			MinWidth = 140.0
		};
		prBtn.Click += async delegate
		{
			await ShowMergeRequestDialogAsync();
		};
		row.Children.Add(prBtn);

		Button repoBtn = new Button
		{
			Content = "打开仓库"
		};
		repoBtn.Click += delegate
		{
			if (!GitHubService.OpenUrl(GitHubService.RepoUrl))
			{
				MainWindow.Instance?.ShowToast("打开浏览器失败，请手动访问 " + GitHubService.RepoUrl, false);
			}
		};
		row.Children.Add(repoBtn);

		Button tokenBtn = new Button
		{
			Content = GitHubService.HasToken ? "管理 Token" : "设置 Token"
		};
		tokenBtn.Click += async delegate
		{
			if (await ShowTokenDialogAsync() && tokenBtn != null)
			{
				tokenBtn.Content = GitHubService.HasToken ? "管理 Token" : "设置 Token";
			}
		};
		row.Children.Add(tokenBtn);

		card.Children.Add(row);

		return new Border
		{
			Background = ThemeHelper.Resolve("CardBackgroundFillColorSecondaryBrush") ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(28, 128, 128, 128)),
			BorderBrush = ThemeHelper.Resolve("CardStrokeColorDefaultBrush") ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new Microsoft.UI.Xaml.CornerRadius(12.0),
			Padding = new Thickness(16.0, 14.0, 16.0, 14.0),
			Margin = new Thickness(0.0, 10.0, 0.0, 4.0),
			Child = card
		};
	}

	private async Task ShowUpdateResultAsync()
	{
		GitHubService.UpdateInfo info = await GitHubService.CheckUpdateAsync();
		StackPanel body = new StackPanel
		{
			Spacing = 8.0,
			MinWidth = 380.0
		};
		if (!info.Ok)
		{
			body.Children.Add(new TextBlock
			{
				Text = "无法获取上游版本信息",
				FontSize = 15.0,
				FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
			});
			body.Children.Add(new TextBlock
			{
				Text = info.Message,
				FontSize = 12.5,
				Opacity = 0.8,
				TextWrapping = TextWrapping.Wrap
			});
			body.Children.Add(new TextBlock
			{
				Text = "可直接打开仓库 Release 页手动查看。",
				FontSize = 12.0,
				Opacity = 0.7,
				TextWrapping = TextWrapping.Wrap
			});
			ContentDialog failDialog = new ContentDialog
			{
				Title = "检查更新",
				Content = body,
				PrimaryButtonText = "打开 Release 页",
				CloseButtonText = "关闭",
				XamlRoot = base.XamlRoot,
				RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default)
			};
			if (await DialogGate.ShowAsync(failDialog) == ContentDialogResult.Primary)
			{
				GitHubService.OpenUrl(GitHubService.ReleasesUrl);
			}
			return;
		}
		body.Children.Add(new TextBlock
		{
			Text = info.HasUpdate ? ("发现新版本：" + (string.IsNullOrWhiteSpace(info.LatestName) ? info.LatestTag : info.LatestName)) : "当前已是最新版本",
			FontSize = 16.0,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			TextWrapping = TextWrapping.Wrap
		});
		body.Children.Add(new TextBlock
		{
			Text = "本地：" + info.CurrentVersion + "　上游：" + info.LatestTag + (string.IsNullOrWhiteSpace(info.PublishedAt) ? "" : ("　发布：" + info.PublishedAt)),
			FontSize = 12.5,
			FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
			Opacity = 0.85,
			TextWrapping = TextWrapping.Wrap
		});
		if (!string.IsNullOrWhiteSpace(info.Notes))
		{
			body.Children.Add(new Microsoft.UI.Xaml.Controls.ScrollViewer
			{
				MaxHeight = 220.0,
				Content = new TextBlock
				{
					Text = info.Notes,
					FontSize = 12.0,
					Opacity = 0.8,
					TextWrapping = TextWrapping.Wrap
				}
			});
		}
		ContentDialog dialog = new ContentDialog
		{
			Title = "检查更新",
			Content = body,
			CloseButtonText = "关闭",
			XamlRoot = base.XamlRoot,
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default)
		};
		if (info.HasUpdate)
		{
			dialog.PrimaryButtonText = string.IsNullOrWhiteSpace(info.DownloadUrl) ? "前往 Release 页" : "下载更新";
		}
		ContentDialogResult result = await DialogGate.ShowAsync(dialog);
		if (result == ContentDialogResult.Primary)
		{
			GitHubService.OpenUrl(string.IsNullOrWhiteSpace(info.DownloadUrl) ? (string.IsNullOrWhiteSpace(info.HtmlUrl) ? GitHubService.ReleasesUrl : info.HtmlUrl) : info.DownloadUrl);
		}
	}

	private async Task ShowMergeRequestDialogAsync()
	{
		TextBox titleBox = new TextBox
		{
			PlaceholderText = "一句话说明，如：修复 Auth fail 后自动重登不生效",
			MaxLength = 120
		};
		TextBox bodyBox = new TextBox
		{
			PlaceholderText = "详细说明：期望行为 / 实际行为 / 复现步骤（可选，会自动附带版本与环境信息）",
			AcceptsReturn = true,
			TextWrapping = TextWrapping.Wrap,
			Height = 130.0,
			MaxLength = 4000
		};
		TextBlock hint = new TextBlock
		{
			FontSize = 12.0,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap,
			Text = GitHubService.HasToken
				? "已配置 Token：将自动在仓库创建分支、提交说明文件并开出 Pull Request。"
				: "未配置 Token：将打开 GitHub 预填好的合并请求页面，点一下「Create pull request」即可提交。"
		};
		StackPanel body = new StackPanel
		{
			Spacing = 10.0,
			MinWidth = 420.0
		};
		body.Children.Add(new TextBlock
		{
			Text = "提交合并请求到 " + GitHubService.RepoOwner + "/" + GitHubService.RepoName,
			FontSize = 12.5,
			Opacity = 0.8,
			TextWrapping = TextWrapping.Wrap
		});
		body.Children.Add(titleBox);
		body.Children.Add(bodyBox);
		body.Children.Add(hint);

		ContentDialog dialog = new ContentDialog
		{
			Title = "提交合并请求",
			Content = body,
			PrimaryButtonText = "提交",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = base.XamlRoot,
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default)
		};
		if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
		{
			return;
		}
		string title = (titleBox.Text ?? "").Trim();
		if (string.IsNullOrWhiteSpace(title))
		{
			MainWindow.Instance?.ShowToast("请先填写一句话标题", false);
			return;
		}
		GitHubService.MergeRequestResult r = await GitHubService.SubmitMergeRequestAsync(title, bodyBox.Text ?? "");
		MainWindow.Instance?.ShowToast(r.Message, r.Ok || r.OpenedInBrowser);
		if (r.Ok && !string.IsNullOrWhiteSpace(r.PrUrl))
		{
			LogHelper.Log("[GitHub] 合并请求已提交: " + r.PrUrl);
		}
	}

	private async Task<bool> ShowTokenDialogAsync()
	{
		PasswordBox tokenBox = new PasswordBox
		{
			PlaceholderText = "ghp_...（需要 repo 写权限；留空并保存表示清除）",
			Width = 320.0
		};
		if (GitHubService.HasToken)
		{
			// 已配置时不再回显明文，避免肩窥；只提示已保存
			tokenBox.PlaceholderText = "已保存 Token（重新输入将覆盖，留空保存则清除）";
		}
		StackPanel body = new StackPanel
		{
			Spacing = 8.0
		};
		body.Children.Add(new TextBlock
		{
			Text = "Token 仅用于把合并请求直接提交到仓库，用 DPAPI 按当前 Windows 用户加密保存在本地，不会写入设置文件。",
			FontSize = 12.0,
			Opacity = 0.75,
			TextWrapping = TextWrapping.Wrap,
			MaxWidth = 380.0
		});
		body.Children.Add(tokenBox);
		ContentDialog dialog = new ContentDialog
		{
			Title = "GitHub Token（可选）",
			Content = body,
			PrimaryButtonText = "保存",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = base.XamlRoot,
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default)
		};
		if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
		{
			return false;
		}
		string token = tokenBox.Password ?? "";
		GitHubService.SaveToken(token);
		MainWindow.Instance?.ShowToast(string.IsNullOrWhiteSpace(token) ? "已清除 GitHub Token（之后合并请求走网页预填页）" : "已保存 GitHub Token", true);
		return true;
	}

	private void ApplyOpenSourceTweaks()
	{
		try
		{
			// 开源版：移除免责声明卡片；「使用须知」整卡去掉
			HomeCardGrid.Children.Remove(DisclaimerCard);
			HomeCardGrid.Children.Remove(NoticeCard);
			// 激活卡片改为「开源版 · 全功能开放」
			if (ActivationCard?.Child is StackPanel panel && panel.Children.Count > 0 && panel.Children[0] is TextBlock title)
			{
				title.Text = "开源版 · 全功能开放";
			}
			CardKeyBox.Visibility = Visibility.Collapsed;
			ActivateButton.Visibility = Visibility.Collapsed;
			DeactivateButton.Visibility = Visibility.Collapsed;
			GoProxyButton.IsEnabled = true;
			// 使用须知中与卡密/激活相关的条目一并移除
			if (NoticeCard?.Child is StackPanel notice)
			{
				for (int i = notice.Children.Count - 1; i >= 0; i--)
				{
					if (notice.Children[i] is TextBlock tb && (tb.Text?.Contains("卡密") == true || tb.Text?.Contains("未激活") == true || tb.Text?.Contains("激活") == true))
					{
						notice.Children.RemoveAt(i);
					}
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "首页开源化调整失败");
		}
	}

	private void TrimNoticeCard()
	{
		try
		{
			if (NoticeCard?.Child is StackPanel panel)
			{
				for (int i = panel.Children.Count - 1; i >= 0; i--)
				{
					if (panel.Children[i] is TextBlock textBlock && textBlock.Text is string text && text.Length > 1 && text[1] == '.' && char.IsDigit(text[0]) && text[0] != '1')
					{
						panel.Children.RemoveAt(i);
					}
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "首页使用须知裁剪失败");
		}
	}

	private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		RelayoutHomeCards(e.NewSize.Width);
	}

	private void RelayoutHomeCards(double width)
	{
		HomeCardGrid.RowDefinitions.Clear();
		HomeCardGrid.ColumnDefinitions.Clear();
		bool flag = HomeRoot.ActualWidth >= 860.0;
		bool flag2 = width >= 1200.0;
		HomeRoot.HorizontalAlignment = (flag2 ? HorizontalAlignment.Center : HorizontalAlignment.Left);
		for (int i = 0; i < ((!flag) ? 1 : 2); i++)
		{
			HomeCardGrid.ColumnDefinitions.Add(new ColumnDefinition());
		}
		int num = ((!flag2) ? (flag ? 2 : 3) : 2);
		for (int j = 0; j < num; j++)
		{
			HomeCardGrid.RowDefinitions.Add(new RowDefinition());
		}
		if (flag2)
		{
			Grid.SetRow(ActivationCard, 0);
			Grid.SetColumn(ActivationCard, 0);
			Grid.SetColumnSpan(ActivationCard, 1);
			Grid.SetRow(NoticeCard, 0);
			Grid.SetColumn(NoticeCard, 1);
			Grid.SetColumnSpan(NoticeCard, 1);
			Grid.SetRow(DisclaimerCard, 1);
			Grid.SetColumn(DisclaimerCard, 0);
			Grid.SetColumnSpan(DisclaimerCard, 2);
		}
		else
		{
			Grid.SetRow(ActivationCard, 0);
			Grid.SetColumn(ActivationCard, 0);
			Grid.SetColumnSpan(ActivationCard, (!flag) ? 1 : 2);
			Grid.SetRow(NoticeCard, flag ? 1 : 1);
			Grid.SetColumn(NoticeCard, 0);
			Grid.SetColumnSpan(NoticeCard, 1);
			Grid.SetRow(DisclaimerCard, flag ? 1 : 2);
			Grid.SetColumn(DisclaimerCard, flag ? 1 : 0);
			Grid.SetColumnSpan(DisclaimerCard, 1);
		}
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		ActivationService.StateChanged -= OnActivationChanged;
	}

	private void OnActivationChanged()
	{
		_queue.TryEnqueue(RefreshActivation);
	}

	private void RefreshActivation()
	{
		ActivationStatusText.Text = "开源版本无需激活，所有功能已开放。";
		GoProxyButton.IsEnabled = true;
	}

	private void Activate_Click(object sender, RoutedEventArgs e)
	{
	}

	private void Deactivate_Click(object sender, RoutedEventArgs e)
	{
	}

	private void GoLogin_Click(object sender, RoutedEventArgs e)
	{
		MainWindow.Instance?.NavigateTo("LoginPage");
	}

	private void GoProxy_Click(object sender, RoutedEventArgs e)
	{
		MainWindow.Instance?.NavigateTo("ProxyCapturePage");
	}

	private void GoAbout_Click(object sender, RoutedEventArgs e)
	{
		MainWindow.Instance?.NavigateTo("AboutPage");
	}

	
	
	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	[DebuggerNonUserCode]
	public IComponentConnector GetBindingConnector(int connectionId, object target)
	{
		return null;
	}
}

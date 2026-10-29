using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MhyunBox.Pages;

public sealed class ShopPage : Page
{
	private sealed class CategoryRow
	{
		public string Display { get; set; } = "";

		public ShopCategory Source { get; set; } = new ShopCategory();
	}

	private sealed class ItemRow
	{
		public string Name { get; set; } = "";

		public string Summary { get; set; } = "";

		public string Price { get; set; } = "";

		public ShopItem Source { get; set; } = new ShopItem();
	}

	private sealed class ServerOption
	{
		public string Id { get; set; } = "";

		public string Name { get; set; } = "";
	}

	private sealed class AccountOption
	{
		public string Label { get; set; } = "";

		public AccountStore.LocalAccount Account { get; set; }
	}

	private readonly DispatcherQueue _queue;

	private ListView _accountList;

	private ComboBox _serverBox;

	private TextBlock _currencyText;

	private TextBlock _statusText;

	private Button _refreshButton;

	private ListView _categoryList;

	private ListView _itemList;

	private bool _loading;

	private CancellationTokenSource _cts;

	private const string RechargeUrl = "https://pay.ds.163.com/activity/x19?channel=yydc_cps10.czzx&guideAlipay=1&hitCrowd=1&setId=68c008ae51590a7a056065cb&wvFullScreen=true&wvStatusBarStyle=white";

	private const string RechargeNotice = "1. 选择充值将打开网易官方充值页面，充值、登录和支付都在网页中完成。\n2. 充值必须选择 Android 或者 PC，iOS 和安卓 PC 不互通。\n3. 安卓每日前三次充值有 9 折优惠，PC 平台没有优惠；如需 9 折充值，需先用手机下载 Android 我的世界客户端登录一次账号。\n4. 安卓端充值的钻石 PC 端也可以用，赠送的绑定钻石只有安卓可以用。\n5. 充值前请确认登录账号、平台、商品金额和到账角色，避免充错账号。\n6. 本程序只是打开充值页面，不对充值过程中的任何环节负责，如有问题请联系网易客服。";

	private static readonly DataTemplate CategoryTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding Display}\" TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\"/></DataTemplate>");

	private static readonly DataTemplate ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><StackPanel Orientation=\"Vertical\" Spacing=\"2\" Padding=\"2\"><TextBlock Text=\"{Binding Name}\" FontWeight=\"Bold\" TextTrimming=\"CharacterEllipsis\"/><TextBlock Text=\"{Binding Summary}\" Opacity=\"0.8\" FontSize=\"12\" TextTrimming=\"CharacterEllipsis\"/><TextBlock Text=\"{Binding Price}\" FontSize=\"12\" FontWeight=\"SemiBold\" Opacity=\"0.95\"/></StackPanel></DataTemplate>");

	private static readonly DataTemplate AccountTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding Label}\" TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\"/></DataTemplate>");

	private static Style LoadLibraryItemStyle()
	{
		try
		{
			return (Style)XamlReader.Load("<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"ListViewItem\"><Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\"/><Setter Property=\"VerticalContentAlignment\" Value=\"Center\"/><Setter Property=\"Padding\" Value=\"8,4\"/><Setter Property=\"Template\"><Setter.Value><ControlTemplate TargetType=\"ListViewItem\"><Grid Background=\"#00FFFFFF\"><VisualStateManager.VisualStateGroups><VisualStateGroup x:Name=\"CommonStates\"><VisualState x:Name=\"Normal\"><VisualState.Storyboard><Storyboard><DoubleAnimation TargetName=\"SelectionVisual\" TargetProperty=\"Opacity\" To=\"0\" Duration=\"0:0:0.16\"/></Storyboard></VisualState.Storyboard></VisualState><VisualState x:Name=\"PointerOver\"><VisualState.Storyboard><Storyboard><DoubleAnimation TargetName=\"SelectionVisual\" TargetProperty=\"Opacity\" To=\"0.4\" Duration=\"0:0:0.16\"/></Storyboard></VisualState.Storyboard></VisualState><VisualState x:Name=\"Pressed\"><VisualState.Storyboard><Storyboard><DoubleAnimation TargetName=\"SelectionVisual\" TargetProperty=\"Opacity\" To=\"1\" Duration=\"0:0:0.1\"/></Storyboard></VisualState.Storyboard></VisualState></VisualStateGroup><VisualStateGroup x:Name=\"SelectionStates\"><VisualState x:Name=\"Unselected\"><VisualState.Storyboard><Storyboard><DoubleAnimation TargetName=\"SelectionVisual\" TargetProperty=\"Opacity\" To=\"0\" Duration=\"0:0:0.16\"/></Storyboard></VisualState.Storyboard></VisualState><VisualState x:Name=\"SelectedUnfocused\"><VisualState.Storyboard><Storyboard><DoubleAnimation TargetName=\"SelectionVisual\" TargetProperty=\"Opacity\" To=\"1\" Duration=\"0:0:0.16\"/></Storyboard></VisualState.Storyboard></VisualState><VisualState x:Name=\"SelectedPointerOver\"><VisualState.Storyboard><Storyboard><DoubleAnimation TargetName=\"SelectionVisual\" TargetProperty=\"Opacity\" To=\"1\" Duration=\"0:0:0.16\"/></Storyboard></VisualState.Storyboard></VisualState><VisualState x:Name=\"Selected\"><VisualState.Storyboard><Storyboard><DoubleAnimation TargetName=\"SelectionVisual\" TargetProperty=\"Opacity\" To=\"1\" Duration=\"0:0:0.16\"/></Storyboard></VisualState.Storyboard></VisualState></VisualStateGroup></VisualStateManager.VisualStateGroups><Border x:Name=\"SelectionVisual\" Background=\"#33FFFFFF\" CornerRadius=\"0\" IsHitTestVisible=\"False\" Opacity=\"0\"/><ContentPresenter Margin=\"{TemplateBinding Padding}\" HorizontalAlignment=\"{TemplateBinding HorizontalContentAlignment}\" VerticalAlignment=\"{TemplateBinding VerticalContentAlignment}\"/></Grid></ControlTemplate></Setter.Value></Setter></Style>");
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 商城列表底色：透明深色下不用 ListSurface 的不透明纯深灰（在浅色壁纸上是一块死黑，
	/// 与半透明内容卡片风格脱节），改用半透明深色让壁纸微微透出；
	/// 黑底深色给实黑；浅色主题维持不透明白。
	/// </summary>
	private static Brush ListBackground()
	{
		if (ThemeHelper.IsDark)
		{
			return new SolidColorBrush(ThemeHelper.IsBlack ? Color.FromArgb(255, 10, 10, 10) : Color.FromArgb(140, 22, 22, 22));
		}
		return ThemeHelper.ListSurface();
	}

	public ShopPage()
	{
		_queue = DispatcherQueue;
		Content = BuildUi();
		Unloaded += OnUnloaded;
		Loaded += OnLoaded;
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		// 主题切换后页面重建前 Loaded 仍可能先走到：按当前主题重刷列表底色，避免旧快照
		Brush listBg = ListBackground();
		if (_accountList != null)
		{
			_accountList.Background = listBg;
		}
		if (_categoryList != null)
		{
			_categoryList.Background = listBg;
		}
		if (_itemList != null)
		{
			_itemList.Background = listBg;
		}
		PopulateAccounts();
		PopulateServerPresets();
		_loading = true;
		if (_serverBox.Items.Count > 0)
		{
			_serverBox.SelectedIndex = 0;
		}
		_loading = false;
		if (SelectedAccount() == null)
		{
			SetStatus("请先在「账号绑定」页添加并登录账号", isError: false);
			return;
		}
		if (_serverBox.Items.Count == 0)
		{
			SetStatus("没有可用的服务器预设（缺少 GameId），商城商品列表无法加载", isError: true);
			return;
		}
		RefreshCategoriesAsync();
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		CancelPending();
	}

	private void CancelPending()
	{
		try
		{
			_cts?.Cancel();
		}
		catch
		{
		}
	}

	private void PopulateAccounts()
	{
		_loading = true;
		List<AccountOption> list = new List<AccountOption>();
		foreach (AccountStore.LocalAccount account in AccountStore.All)
		{
			if (string.IsNullOrWhiteSpace(account.EntityId) || string.IsNullOrWhiteSpace(account.Token))
			{
				continue;
			}
			list.Add(new AccountOption
			{
				Label = makeAccountLabel(account),
				Account = account
			});
		}
		_accountList.ItemsSource = list;
		_loading = false;
		if (list.Count > 0 && _accountList.SelectedIndex < 0)
		{
			AccountStore.LocalAccount current = AccountStore.Current;
			int index = 0;
			if (current != null)
			{
				for (int i = 0; i < list.Count; i++)
				{
					if (ReferenceEquals(list[i].Account, current))
					{
						index = i;
						break;
					}
				}
			}
			_loading = true;
			_accountList.SelectedIndex = index;
			_loading = false;
		}
	}

	private static string makeAccountLabel(AccountStore.LocalAccount account)
	{
		string text = string.IsNullOrWhiteSpace(account.Nickname) ? AccountStore.Mask(account.Account) : account.Nickname;
		return text + "（" + account.Channel + "）";
	}

	private void PopulateServerPresets()
	{
		_loading = true;
		_serverBox.Items.Clear();
		foreach (ServerPreset preset in ServerPresets.All)
		{
			if (preset.IsPending || preset.GameId <= 0)
			{
				continue;
			}
			_serverBox.Items.Add(new ComboBoxItem
			{
				Content = preset.Name,
				Tag = new ServerOption
				{
					Id = preset.GameId.ToString(),
					Name = preset.Name
				}
			});
		}
		_loading = false;
	}

	private AccountStore.LocalAccount SelectedAccount()
	{
		if (_accountList.SelectedItem is AccountOption option)
		{
			return option.Account;
		}
		return null;
	}

	private ServerOption SelectedServer()
	{
		if (_serverBox.SelectedItem is ComboBoxItem { Tag: ServerOption option })
		{
			return option;
		}
		return null;
	}

	private void AccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loading)
		{
			return;
		}
		if (SelectedAccount() != null)
		{
			RefreshCategoriesAsync();
		}
	}

	private void ServerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_loading)
		{
			RefreshCategoriesAsync();
		}
	}

	private void RefreshButton_Click(object sender, RoutedEventArgs e)
	{
		RefreshCategoriesAsync();
	}

	private async void RefreshCategoriesAsync()
	{
		AccountStore.LocalAccount account = SelectedAccount();
		ServerOption server = SelectedServer();
		_categoryList.ItemsSource = null;
		_itemList.ItemsSource = null;
		if (account == null || server == null)
		{
			SetStatus("请先选择账号与服务器", isError: false);
			return;
		}
		CancelPending();
		_cts = new CancellationTokenSource();
		CancellationToken token = _cts.Token;
		_refreshButton.IsEnabled = false;
		SetStatus("正在加载商店…", isError: false);
		try
		{
			ShopCategory[] categories = await LoadCategoriesWithReloginAsync(account, server.Id, token);
			if (token.IsCancellationRequested)
			{
				return;
			}
			if (categories == null)
			{
				SetStatus("登录状态已失效且自动重登失败，请到「账号绑定」页重新登录该账号。", isError: true);
				return;
			}
			await RefreshCurrencyAsync(account, token);
			if (token.IsCancellationRequested)
			{
				return;
			}
			List<CategoryRow> rows = new List<CategoryRow>();
			foreach (ShopCategory category in categories)
			{
				rows.Add(new CategoryRow
				{
					Display = string.IsNullOrWhiteSpace(category.Name) ? "未命名分类" : category.Name,
					Source = category
				});
			}
			_categoryList.ItemsSource = rows;
			if (rows.Count > 0)
			{
				_categoryList.SelectedIndex = 0;
			}
			else
			{
				SetStatus("该服务器暂无可用商品分类", isError: false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			if (ElsewhereLoginNotice.IsElsewhereMessage(ex.Message))
			{
				SetStatus("账号疑似在别处登录，商城拒绝返回数据——请到「账号绑定」页重新登录该账号后再试", isError: true);
			}
			else
			{
				SetStatus("加载商店失败：" + ex.Message, isError: true);
			}
			LogHelper.DebugError(ex, "[商城] 加载商店失败（刷新链路，仅调试模式写日志）");
		}
		finally
		{
			_refreshButton.IsEnabled = true;
		}
	}

	private async Task<ShopCategory[]> LoadCategoriesWithReloginAsync(AccountStore.LocalAccount account, string serverId, CancellationToken token)
	{
		Exception firstError = null;
		try
		{
			ShopCategory[] categories = await NeteaseShopService.GetCategoriesAsync(account.EntityId, account.Token, serverId, token);
			if (categories.Length > 0)
			{
				return categories;
			}
			LogHelper.Log("[商城] 分类为空，尝试跟随自动登录机制明文重取 token");
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			firstError = ex;
			LogHelper.DebugError(ex, "[商城] 加载分类失败，尝试跟随自动登录机制明文重取 token（刷新链路，仅调试模式写日志）");
		}
		if (token.IsCancellationRequested)
		{
			return Array.Empty<ShopCategory>();
		}
		if (!await TryReloginAsync(account, token))
		{
			if (firstError != null)
			{
				throw firstError;
			}
			return null;
		}
		return await NeteaseShopService.GetCategoriesAsync(account.EntityId, account.Token, serverId, token);
	}

	private async Task<bool> TryReloginAsync(AccountStore.LocalAccount account, CancellationToken token)
	{
		SetStatus("登录状态可能已失效，正在自动明文重登并刷新 token…", isError: false);
		bool ok = await AccountAutoLoginService.ReloginAsync(account, token);
		if (!ok)
		{
			LogHelper.Log("[商城] 自动重新登录失败（可能未保存密码）：" + account.Channel + " " + AccountStore.Mask(account.Account));
			return false;
		}
		SyncAccountToken(account);
		LogHelper.Log("[商城] 自动重新登录成功，已刷新账号 token：" + account.Channel + " " + AccountStore.Mask(account.Account));
		return true;
	}

	private static void SyncAccountToken(AccountStore.LocalAccount account)
	{
		foreach (AccountStore.LocalAccount latest in AccountStore.All)
		{
			if (string.Equals(latest.Channel, account.Channel, StringComparison.Ordinal) && string.Equals(latest.Account, account.Account, StringComparison.OrdinalIgnoreCase))
			{
				account.EntityId = latest.EntityId;
				account.Token = latest.Token;
				account.CookieJson = latest.CookieJson;
				if (!string.IsNullOrWhiteSpace(latest.Nickname))
				{
					account.Nickname = latest.Nickname;
				}
				return;
			}
		}
	}

	private async Task RefreshCurrencyAsync(AccountStore.LocalAccount account, CancellationToken token)
	{
		try
		{
			ShopCurrency currency = await NeteaseShopService.GetCurrencyAsync(account.EntityId, account.Token, token);
			if (!token.IsCancellationRequested)
			{
				_currencyText.Text = $"钻石余额：{currency.TotalDiamonds}（付费 {currency.PaidDiamonds} / 免费 {currency.FreeDiamonds}）";
			}
		}
		catch (Exception ex)
		{
			if (!token.IsCancellationRequested)
			{
				_currencyText.Text = "货币读取失败";
			}
			LogHelper.DebugError(ex, "[商城] 读取货币失败（刷新链路，仅调试模式写日志）");
		}
	}

	private async void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loading)
		{
			return;
		}
		AccountStore.LocalAccount account = SelectedAccount();
		ServerOption server = SelectedServer();
		if (account == null || server == null || !(_categoryList.SelectedItem is CategoryRow row))
		{
			return;
		}
		_itemList.ItemsSource = null;
		CancellationToken token = (_cts != null) ? _cts.Token : CancellationToken.None;
		SetStatus("正在加载商品…", isError: false);
		try
		{
			ShopPageResult page = await LoadItemsWithReloginAsync(account, server.Id, row.Source.Id, token);
			if (token.IsCancellationRequested)
			{
				return;
			}
			if (page == null)
			{
				SetStatus("登录状态已失效且自动重登失败，请到「账号绑定」页重新登录该账号。", isError: true);
				return;
			}
			List<ItemRow> rows = new List<ItemRow>();
			foreach (ShopItem item in page.Items)
			{
				rows.Add(new ItemRow
				{
					Name = string.IsNullOrWhiteSpace(item.Name) ? "未命名商品" : item.Name,
					Summary = item.Summary,
					Price = FormatPrice(item.FinalDiamonds, item.FinalPoints),
					Source = item
				});
			}
			_itemList.ItemsSource = rows;
			SetStatus(((page.Total > 0) ? $"共 {page.Total} 个商品" : "该分类下暂无商品（服务器未返回商品，响应已记入日志）"), isError: false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			if (ElsewhereLoginNotice.IsElsewhereMessage(ex.Message))
			{
				SetStatus("账号疑似在别处登录，商城拒绝返回商品——请到「账号绑定」页重新登录该账号后再试", isError: true);
			}
			else
			{
				SetStatus("加载商品失败：" + ex.Message, isError: true);
			}
			LogHelper.DebugError(ex, "[商城] 加载商品失败（刷新链路，仅调试模式写日志）");
		}
	}

	private async Task<ShopPageResult> LoadItemsWithReloginAsync(AccountStore.LocalAccount account, string serverId, string categoryId, CancellationToken token)
	{
		Exception firstError = null;
		try
		{
			ShopPageResult page = await NeteaseShopService.GetItemsAsync(account.EntityId, account.Token, serverId, categoryId, 0, 100, token);
			if (page.Items.Length > 0)
			{
				return page;
			}
			LogHelper.Log("[商城] 商品为空，尝试跟随自动登录机制明文重取 token");
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			firstError = ex;
			LogHelper.DebugError(ex, "[商城] 加载商品失败，尝试跟随自动登录机制明文重取 token（刷新链路，仅调试模式写日志）");
		}
		if (token.IsCancellationRequested)
		{
			return new ShopPageResult();
		}
		if (!await TryReloginAsync(account, token))
		{
			if (firstError != null)
			{
				throw firstError;
			}
			return null;
		}
		return await NeteaseShopService.GetItemsAsync(account.EntityId, account.Token, serverId, categoryId, 0, 100, token);
	}

	private static string FormatPrice(int diamonds, int points)
	{
		if (diamonds > 0)
		{
			return $"钻石 {diamonds}" + ((points > 0) ? $" / 点券 {points}" : "");
		}
		if (points > 0)
		{
			return $"点券 {points}";
		}
		return "免费 / 未标价";
	}

	private void ItemList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		DependencyObject element = e.OriginalSource as DependencyObject;
		while (element != null && !(element is ListViewItem))
		{
			element = VisualTreeHelper.GetParent(element);
		}
		if (element is ListViewItem listViewItem && listViewItem.Content is ItemRow row)
		{
			_itemList.SelectedItem = row;
			_ = BuyItemAsync(row);
		}
	}

	private async Task BuyItemAsync(ItemRow row)
	{
		AccountStore.LocalAccount account = SelectedAccount();
		if (account == null || row == null || row.Source == null)
		{
			return;
		}
		ShopItem item = row.Source;
		string priceText = FormatPrice(item.FinalDiamonds, item.FinalPoints);
		ContentDialogResult confirm = await DialogGate.ShowAsync(new ContentDialog
		{
			Title = "确认购买",
			Content = "商品：" + row.Name + "\n价格：" + priceText + "\n\n购买后道具将发放到当前账号，是否继续？",
			PrimaryButtonText = "确认购买",
			CloseButtonText = "取消",
			XamlRoot = XamlRoot
		});
		if (confirm != ContentDialogResult.Primary)
		{
			return;
		}
		SetStatus("正在提交购买…", isError: false);
		try
		{
			ShopPurchaseResult result = await NeteaseShopService.PurchaseAsync(account.EntityId, account.Token, item.Id, item.FinalDiamonds, item.FinalPoints);
			SetStatus(result.Message, isError: !result.Success);
			MainWindow.Instance?.ShowToast(result.Message, result.Success);
			await RefreshCurrencyAsync(account, CancellationToken.None);
		}
		catch (Exception ex)
		{
			SetStatus("购买失败：" + ex.Message, isError: true);
			MainWindow.Instance?.ShowToast("购买失败：" + ex.Message, false);
			LogHelper.LogError(ex, "[商城] 购买失败");
			if (IsInsufficient(ex.Message))
			{
				await PromptRechargeAsync("钻石不足，无法完成购买。是否打开网易官方充值页充值后再试？");
			}
		}
	}

	private static bool IsInsufficient(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return false;
		}
		return message.Contains("钻石不足") || message.Contains("余额不足") || message.Contains("不足");
	}

	private async void Recharge_Click(object sender, RoutedEventArgs e)
	{
		await PromptRechargeAsync("将打开系统浏览器进入网易官方充值页，请在网页中登录账号并选择 Android 或 PC 平台进行充值。");
	}

	private async void RechargeInfo_Click(object sender, RoutedEventArgs e)
	{
		await DialogGate.ShowAsync(new ContentDialog
		{
			Title = "网易钻石充值说明",
			Content = RechargeNotice,
			CloseButtonText = "知道了",
			XamlRoot = XamlRoot
		});
	}

	private async Task PromptRechargeAsync(string message)
	{
		ContentDialogResult result = await DialogGate.ShowAsync(new ContentDialog
		{
			Title = "网易钻石充值",
			Content = message,
			PrimaryButtonText = "打开充值页面",
			CloseButtonText = "取消",
			XamlRoot = XamlRoot
		});
		if (result != ContentDialogResult.Primary)
		{
			return;
		}
		if (OpenRechargePage())
		{
			SetStatus("已打开网易官方充值页，充值后在网页内完成支付。", isError: false);
			MainWindow.Instance?.ShowToast("已打开网易官方充值页");
		}
		else
		{
			await DialogGate.ShowAsync(new ContentDialog
			{
				Title = "无法自动打开浏览器",
				Content = "请手动复制以下地址到浏览器打开：\n\n" + RechargeUrl,
				CloseButtonText = "知道了",
				XamlRoot = XamlRoot
			});
		}
	}

	private static bool OpenRechargePage()
	{
		try
		{
			Process.Start(new ProcessStartInfo(RechargeUrl)
			{
				UseShellExecute = true
			});
			return true;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[商城] 打开充值页面失败");
			return false;
		}
	}

	private void SetStatus(string message, bool isError)
	{
		_statusText.Text = message;
		_statusText.Foreground = isError
			? new SolidColorBrush(Color.FromArgb(255, 232, 106, 106))
			: new SolidColorBrush(Color.FromArgb(204, 128, 128, 128));
	}

	private FrameworkElement BuildUi()
	{
		Grid root = new Grid
		{
			Padding = new Thickness(40.0, 28.0, 40.0, 28.0),
			RowSpacing = 12.0,
			MaxWidth = 1180.0,
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.0, GridUnitType.Auto) });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.0, GridUnitType.Auto) });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.0, GridUnitType.Auto) });

		TextBlock title = new TextBlock
		{
			Text = "商城 / 角色充值",
			FontSize = 28.0,
			FontWeight = FontWeights.Bold
		};
		Grid.SetRow(title, 0);
		root.Children.Add(title);

		StackPanel topRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 20.0
		};
		StackPanel accountCol = new StackPanel
		{
			Orientation = Orientation.Vertical,
			Spacing = 6.0
		};
		accountCol.Children.Add(new TextBlock
		{
			Text = "账号",
			FontWeight = FontWeights.SemiBold
		});
		_accountList = new ListView
		{
			Width = 300.0,
			Height = 132.0,
			SelectionMode = ListViewSelectionMode.Single,
			IsItemClickEnabled = true,
			ItemTemplate = AccountTemplate,
			ItemContainerStyle = LoadLibraryItemStyle(),
			Background = ListBackground()
		};
		_accountList.SelectionChanged += AccountList_SelectionChanged;
		accountCol.Children.Add(_accountList);
		topRow.Children.Add(accountCol);

		StackPanel actionCol = new StackPanel
		{
			Orientation = Orientation.Vertical,
			Spacing = 6.0
		};
		actionCol.Children.Add(new TextBlock
		{
			Text = "服务器",
			FontWeight = FontWeights.SemiBold
		});
		_serverBox = new ComboBox
		{
			MinWidth = 220.0,
			HorizontalAlignment = HorizontalAlignment.Left,
			PlaceholderText = "选择服务器"
		};
		ThemeHelper.SolidComboPopup(_serverBox);
		_serverBox.SelectionChanged += ServerBox_SelectionChanged;
		actionCol.Children.Add(_serverBox);
		_refreshButton = new Button
		{
			Content = "刷新",
			MinHeight = 32.0,
			HorizontalAlignment = HorizontalAlignment.Left
		};
		_refreshButton.Click += RefreshButton_Click;
		actionCol.Children.Add(_refreshButton);
		Button rechargeButton = new Button
		{
			Content = "网易钻石充值",
			MinHeight = 32.0,
			HorizontalAlignment = HorizontalAlignment.Left
		};
		rechargeButton.Click += Recharge_Click;
		actionCol.Children.Add(rechargeButton);
		Button rechargeInfoButton = new Button
		{
			Content = "充值说明",
			MinHeight = 32.0,
			HorizontalAlignment = HorizontalAlignment.Left
		};
		rechargeInfoButton.Click += RechargeInfo_Click;
		actionCol.Children.Add(rechargeInfoButton);
		topRow.Children.Add(actionCol);
		Grid.SetRow(topRow, 1);
		root.Children.Add(topRow);

		Grid body = new Grid
		{
			ColumnSpacing = 16.0
		};
		body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220.0) });
		body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

		Grid categoryPanel = new Grid { RowSpacing = 4.0 };
		categoryPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.0, GridUnitType.Auto) });
		categoryPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		categoryPanel.Children.Add(new TextBlock { Text = "分类", FontWeight = FontWeights.SemiBold });
		_categoryList = new ListView
		{
			SelectionMode = ListViewSelectionMode.Single,
			ItemTemplate = CategoryTemplate,
			Background = ListBackground()
		};
		_categoryList.SelectionChanged += CategoryList_SelectionChanged;
		Border categoryBorder = WrapCard(_categoryList);
		Grid.SetRow(categoryBorder, 1);
		categoryPanel.Children.Add(categoryBorder);
		Grid.SetColumn(categoryPanel, 0);
		body.Children.Add(categoryPanel);

		Grid itemPanel = new Grid { RowSpacing = 4.0 };
		itemPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.0, GridUnitType.Auto) });
		itemPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		itemPanel.Children.Add(new TextBlock { Text = "商品（双击卡片或标签直接购买）", FontWeight = FontWeights.SemiBold });
		_itemList = new ListView
		{
			SelectionMode = ListViewSelectionMode.Single,
			ItemTemplate = ItemTemplate,
			Background = ListBackground()
		};
		_itemList.DoubleTapped += ItemList_DoubleTapped;
		Border itemBorder = WrapCard(_itemList);
		Grid.SetRow(itemBorder, 1);
		itemPanel.Children.Add(itemBorder);
		Grid.SetColumn(itemPanel, 1);
		body.Children.Add(itemPanel);

		Grid.SetRow(body, 2);
		root.Children.Add(body);

		StackPanel footer = new StackPanel { Spacing = 4.0 };
		_currencyText = new TextBlock { Text = "货币：未读取", FontSize = 13.0, Opacity = 0.9 };
		footer.Children.Add(_currencyText);
		_statusText = new TextBlock { Text = "", FontSize = 13.0, TextWrapping = TextWrapping.Wrap };
		footer.Children.Add(_statusText);
		TextBlock noteText = new TextBlock
		{
			Text = "说明：选择安卓端充值前三单九折，仅充值钻石平台互通（充值过程均是网易平台机器受理，此程序概不负责）。",
			FontSize = 12.0,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap
		};
		footer.Children.Add(noteText);
		Grid.SetRow(footer, 3);
		root.Children.Add(footer);

		return root;
	}

	private static Border WrapCard(UIElement child)
	{
		return new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(16, 128, 128, 128)),
			BorderBrush = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128)),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(12.0),
			Padding = new Thickness(8.0),
			Child = child
		};
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Text;

namespace MhyunBox.Pages;

/// <summary>
/// 服务器列表页：通过网易 API 网关（/item/query/available）自动发现账号可用的网络游戏列表，
/// 点选服务器后经 /item-address/get 解析进入地址，一键注册为动态服务器预设，
/// 之后即可在「代理启动 / 快速启动」页像内置服务器一样直接选用。
/// </summary>
public sealed class ServerListPage : Page
{
	private readonly DispatcherQueue _queue;

	private ComboBox _accountBox = null!;

	private TextBox _searchBox = null!;

	private Button _refreshBtn = null!;

	private Button _moreBtn = null!;

	private Button _clearCacheBtn = null!;

	private TextBlock _statusText = null!;

	private GridView _grid = null!;

	private readonly List<NeteaseServerListService.NetGameSummary> _items = new List<NeteaseServerListService.NetGameSummary>();

	private int _offset;

	private const int PageSize = 50;

	private bool _loading;

	private CancellationTokenSource? _cts;

	public ServerListPage()
	{
		_queue = DispatcherQueue.GetForCurrentThread();
		BuildUi();
		Loaded += delegate
		{
			RebuildAccountOptions();
			if (_grid.Items.Count == 0)
			{
				LoadCacheIntoGrid();
			}
			else
			{
				ApplyFilter(); // 刷新「已添加」徽标
			}
			// 进入页面自动加载：无数据时直接拉取服务器列表
			if (_items.Count == 0 && !_loading && SelectedAccount() != null)
			{
				_ = LoadAsync(reset: true);
			}
		};
	}

	private static Brush? ThemeBrush(string key)
	{
		return ThemeHelper.Resolve(key);
	}

	// ---------- UI 构建 ----------

	private void BuildUi()
	{
		ScrollViewer root = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Padding = new Thickness(24.0, 16.0, 24.0, 24.0)
		};
		StackPanel panel = new StackPanel
		{
			Spacing = 14.0,
			MaxWidth = 1160.0,
			HorizontalAlignment = HorizontalAlignment.Stretch
		};

		// 标题
		panel.Children.Add(new StackPanel
		{
			Spacing = 2.0,
			Children =
			{
				new TextBlock
				{
					Text = "服务器列表",
					FontSize = 22.0,
					FontWeight = FontWeights.SemiBold
				},
				new TextBlock
				{
					Text = "自动发现网易入口下账号可用的全部服务器；点选卡片解析进入地址后，可一键添加为服务器预设。",
					FontSize = 12.0,
					Opacity = 0.7,
					TextWrapping = TextWrapping.Wrap
				}
			}
		});

		// 控制卡
		panel.Children.Add(BuildControlCard());

		// 服务器卡片网格
		// 说明：这里刻意不依赖 GridView 的 ItemClick —— 实测点击卡片无反应（选中模式会吃掉点击、
		// 且 Card 内嵌按钮时事件源判定不稳）。改为「卡片内显式按钮 + 按钮直接挂 Click」，
		// 每个操作都有独立可点区域，不依赖容器路由，点击必定有反馈。
		_grid = new GridView
		{
			IsItemClickEnabled = false,
			SelectionMode = ListViewSelectionMode.None,
			Padding = new Thickness(0.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		panel.Children.Add(_grid);

		// 底部状态 + 加载更多
		Grid bottom = new Grid();
		bottom.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		bottom.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		bottom.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		_statusText = new TextBlock
		{
			FontSize = 12.0,
			Opacity = 0.7,
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.Wrap
		};
		Grid.SetColumn(_statusText, 0);
		_moreBtn = new Button
		{
			Content = "加载更多",
			Padding = new Thickness(18.0, 8.0, 18.0, 8.0),
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
			Visibility = Visibility.Collapsed
		};
		_moreBtn.Click += More_Click;
		Grid.SetColumn(_moreBtn, 1);
		_clearCacheBtn = new Button
		{
			Content = "清空缓存",
			Padding = new Thickness(18.0, 8.0, 18.0, 8.0)
		};
		_clearCacheBtn.Click += ClearCache_Click;
		Grid.SetColumn(_clearCacheBtn, 2);
		bottom.Children.Add(_statusText);
		bottom.Children.Add(_moreBtn);
		bottom.Children.Add(_clearCacheBtn);
		panel.Children.Add(bottom);

		root.Content = panel;
		base.Content = root;
	}

	private FrameworkElement BuildControlCard()
	{
		_accountBox = new ComboBox
		{
			MinWidth = 220.0,
			PlaceholderText = "选择网易账号",
			FontSize = 13.0
		};
		ThemeHelper.SolidComboPopup(_accountBox);

		_refreshBtn = new Button
		{
			Content = "加载服务器列表",
			Padding = new Thickness(18.0, 8.0, 18.0, 8.0),
			FontSize = 13.0
		};
		_refreshBtn.Click += Refresh_Click;

		_searchBox = new TextBox
		{
			PlaceholderText = "搜索服务器名 / 服务器号",
			MinWidth = 230.0,
			FontSize = 13.0
		};
		_searchBox.TextChanged += delegate
		{
			ApplyFilter();
		};

		StackPanel row = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10.0
		};
		row.Children.Add(_accountBox);
		row.Children.Add(_refreshBtn);
		row.Children.Add(_searchBox);

		StackPanel card = new StackPanel
		{
			Spacing = 8.0
		};
		card.Children.Add(row);
		return new Border
		{
			Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
			BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(12.0),
			Padding = new Thickness(16.0, 12.0, 16.0, 12.0),
			Child = card
		};
	}

	// ---------- 数据 ----------

	private void RebuildAccountOptions()
	{
		try
		{
			_accountBox.Items.Clear();
			List<AccountStore.LocalAccount> accounts = AccountStore.All.Where((AccountStore.LocalAccount a) => !string.IsNullOrWhiteSpace(a.EntityId) && !string.IsNullOrWhiteSpace(a.Token) || !string.IsNullOrWhiteSpace(a.CookieJson)).ToList();
			if (accounts.Count == 0)
			{
				_statusText.Text = "尚未绑定网易账号：请先到「账号绑定」页登录后再加载服务器列表。";
				return;
			}
			AccountStore.LocalAccount? current = AccountStore.Current;
			int selectIndex = -1;
			for (int i = 0; i < accounts.Count; i++)
			{
				AccountStore.LocalAccount a = accounts[i];
				_accountBox.Items.Add(new ComboBoxItem
				{
					Content = AccountLabel(a),
					Tag = a
				});
				if (current != null && string.Equals(a.Channel, current.Channel, StringComparison.Ordinal) && string.Equals(a.Account, current.Account, StringComparison.Ordinal))
				{
					selectIndex = i;
				}
			}
			_accountBox.SelectedIndex = (selectIndex >= 0) ? selectIndex : 0;
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[服务器列表] 账号列表构建失败");
		}
	}

	private static string AccountLabel(AccountStore.LocalAccount a)
	{
		string text = string.IsNullOrWhiteSpace(a.Nickname) ? AccountStore.Mask(a.Account) : a.Nickname;
		return "[" + a.Channel + "] " + text;
	}

	private AccountStore.LocalAccount? SelectedAccount()
	{
		return (_accountBox.SelectedItem as ComboBoxItem)?.Tag as AccountStore.LocalAccount;
	}

	private void LoadCacheIntoGrid()
	{
		List<NeteaseServerListService.NetGameSummary> cache = NeteaseServerListService.LoadCache();
		if (cache.Count > 0)
		{
			_items.Clear();
			_items.AddRange(cache);
			_offset = _items.Count;
			ApplyFilter();
			_statusText.Text = "已加载本地缓存 " + cache.Count + " 个服务器（点击「加载服务器列表」获取最新）。";
		}
	}

	private async void Refresh_Click(object sender, RoutedEventArgs e)
	{
		await LoadAsync(reset: true);
	}

	private async void More_Click(object sender, RoutedEventArgs e)
	{
		await LoadAsync(reset: false);
	}

	private async Task LoadAsync(bool reset)
	{
		if (_loading)
		{
			return;
		}
		AccountStore.LocalAccount? account = SelectedAccount();
		if (account == null)
		{
			MainWindow.Instance?.ShowToast("请先选择已登录的网易账号（或到「账号绑定」页登录）", false);
			return;
		}
		_cts?.Cancel();
		_cts = new CancellationTokenSource();
		CancellationToken ct = _cts.Token;
		_loading = true;
		_refreshBtn.IsEnabled = false;
		_moreBtn.IsEnabled = false;
		int offset = reset ? 0 : _offset;
		SetStatus(reset ? "正在加载服务器列表…" : "正在加载更多…");
		try
		{
			List<NeteaseServerListService.NetGameSummary> list = await NeteaseServerListService.ListServersAsync(account.EntityId, account.Token, offset, PageSize, null, ct);
			if (reset)
			{
				_items.Clear();
			}
			int added = 0;
			foreach (NeteaseServerListService.NetGameSummary item in list)
			{
				if (!_items.Any((NeteaseServerListService.NetGameSummary x) => string.Equals(x.EntityId, item.EntityId, StringComparison.Ordinal)))
				{
					_items.Add(item);
					added++;
				}
			}
			_offset = offset + list.Count;
			NeteaseServerListService.SaveCache(_items);
			ApplyFilter();
			SetStatus("共 " + _items.Count + " 个服务器（本次新增 " + added + "）。点选卡片解析地址并添加预设。");
			_moreBtn.Visibility = ((list.Count >= PageSize) ? Visibility.Visible : Visibility.Collapsed);
			MainWindow.Instance?.ShowToast("服务器列表加载成功，共 " + _items.Count + " 个", ok: true);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			SetStatus("加载失败：" + ex.Message);
			MainWindow.Instance?.ShowToast("服务器列表加载失败：" + ex.Message, false);
		}
		finally
		{
			_loading = false;
			_refreshBtn.IsEnabled = true;
			_moreBtn.IsEnabled = true;
		}
	}

	private void ClearCache_Click(object sender, RoutedEventArgs e)
	{
		_items.Clear();
		_offset = 0;
		_grid.Items.Clear();
		NeteaseServerListService.SaveCache(_items);
		SetStatus("缓存已清空。");
	}

	private void ApplyFilter()
	{
		string keyword = _searchBox?.Text?.Trim() ?? "";
		List<NeteaseServerListService.NetGameSummary> list = _items;
		if (!string.IsNullOrWhiteSpace(keyword))
		{
			list = list.Where((NeteaseServerListService.NetGameSummary s) => s.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) || s.EntityId.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
		}
		_grid.Items.Clear();
		foreach (NeteaseServerListService.NetGameSummary item in list)
		{
			_grid.Items.Add(BuildCard(item));
		}
		if (list.Count == 0)
		{
			SetStatus(string.IsNullOrWhiteSpace(keyword) ? "暂无数据，请点击「加载服务器列表」。" : ("无匹配 \"" + keyword + "\" 的服务器。"));
		}
	}

	private void SetStatus(string text)
	{
		if (_statusText != null)
		{
			_statusText.Text = text;
		}
	}

	// 异步下载封面并填充卡片（卡片被回收后放弃结果）
	private static async Task LoadCardImageAsync(Image image, Border host, string url)
	{
		try
		{
			byte[]? data = await NeteaseServerListService.DownloadImageAsync(url);
			if (data == null || data.Length == 0)
			{
				return;
			}
			BitmapImage bitmap = new BitmapImage();
			bitmap.DecodePixelWidth = 480;
			using MemoryStream ms = new MemoryStream(data);
			await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
			if (host.Parent == null || !ReferenceEquals(host.Child, image))
			{
				return;
			}
			image.Source = bitmap;
			image.Opacity = 1.0;
		}
		catch
		{
		}
	}

	// ---------- 卡片（重做：显式按钮，点击必定有反馈） ----------

	private FrameworkElement BuildCard(NeteaseServerListService.NetGameSummary item)
	{
		long gameId = ParseGameId(item.EntityId);
		bool added = ServerPresets.ContainsGameId(gameId);

		StackPanel content = new StackPanel
		{
			Spacing = 8.0
		};

		// 封面：有图用图，无图用首字占位（圆角裁剪）
		Border imageBorder = new Border
		{
			Height = 104.0,
			CornerRadius = new CornerRadius(10.0),
			Background = ThemeBrush("ControlFillColorDefaultBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(24, 128, 128, 128)),
			Clip = new RectangleGeometry
			{
				Rect = new Windows.Foundation.Rect(0.0, 0.0, 1000.0, 104.0)
			},
			Child = new TextBlock
			{
				Text = (item.Name ?? "?").Length > 0 ? item.Name.Substring(0, 1) : "?",
				FontSize = 32.0,
				FontWeight = FontWeights.SemiBold,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Opacity = 0.5
			}
		};
		if (!string.IsNullOrWhiteSpace(item.TitleImageUrl))
		{
			try
			{
				Image image = new Image
				{
					Stretch = Stretch.UniformToFill,
					HorizontalAlignment = HorizontalAlignment.Stretch,
					VerticalAlignment = VerticalAlignment.Center
				};
				imageBorder.Child = image;
				_ = LoadCardImageAsync(image, imageBorder, item.TitleImageUrl);
			}
			catch
			{
			}
		}
		content.Children.Add(imageBorder);

		// 名称（最多两行，避免长名撑破卡片）
		content.Children.Add(new TextBlock
		{
			Text = item.Name ?? "未命名服务器",
			FontSize = 14.0,
			FontWeight = FontWeights.SemiBold,
			TextTrimming = TextTrimming.CharacterEllipsis,
			TextWrapping = TextWrapping.Wrap,
			MaxLines = 2
		});

		// 元信息行：服务器号 + 在线人数
		StackPanel meta = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		meta.Children.Add(new TextBlock
		{
			Text = "号 " + item.EntityId,
			FontSize = 11.0,
			Opacity = 0.65,
			FontFamily = new FontFamily("Consolas")
		});
		meta.Children.Add(new TextBlock
		{
			Text = "· 在线 " + (string.IsNullOrWhiteSpace(item.OnlineCount) ? "0" : item.OnlineCount),
			FontSize = 11.0,
			Opacity = 0.65
		});
		content.Children.Add(meta);

		// 状态徽标：已添加 / 未添加，独立一行更醒目
		Border badge = new Border
		{
			CornerRadius = new CornerRadius(8.0),
			Padding = new Thickness(8.0, 2.0, 8.0, 2.0),
			HorizontalAlignment = HorizontalAlignment.Left,
			Background = new SolidColorBrush(added ? Windows.UI.Color.FromArgb(38, 56, 158, 13) : Windows.UI.Color.FromArgb(28, 150, 150, 150)),
			Child = new TextBlock
			{
				Text = added ? "✓ 已添加为预设" : "未添加",
				FontSize = 11.0,
				FontWeight = FontWeights.SemiBold,
				Foreground = new SolidColorBrush(added ? Windows.UI.Color.FromArgb(255, 56, 158, 13) : Windows.UI.Color.FromArgb(255, 150, 150, 150))
			}
		};
		content.Children.Add(badge);

		// 主操作按钮：未添加 → 添加预设；已添加 → 移除预设（内置预设禁用并给出说明）
		ServerPreset? existingPreset = null;
		if (added)
		{
			foreach (ServerPreset p in ServerPresets.All)
			{
				if (p.GameId == gameId)
				{
					existingPreset = p;
					break;
				}
			}
		}
		bool builtin = existingPreset != null && ServerPresets.IsBuiltin(existingPreset);
		Button actionBtn = new Button
		{
			Content = added ? (builtin ? "内置预设（不可移除）" : "移除服务器预设") : "＋ 添加为服务器预设",
			FontSize = 12.5,
			FontWeight = FontWeights.SemiBold,
			Padding = new Thickness(10.0, 7.0, 10.0, 7.0),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Center,
			MinWidth = 0.0,
			IsEnabled = !builtin
		};
		ToolTipService.SetToolTip(actionBtn, added
			? (builtin ? "内置默认预设不可删除" : "从预设列表中移除该服务器，之后可重新添加")
			: "解析进入地址并添加为服务器预设，之后可在代理启动页直接选用");

		// 详情按钮（次要操作）
		HyperlinkButton detailBtn = new HyperlinkButton
		{
			Content = "查看详情 / 简介",
			FontSize = 11.5,
			Padding = new Thickness(2.0),
			MinWidth = 0.0,
			MinHeight = 0.0,
			HorizontalAlignment = HorizontalAlignment.Center
		};

		// 卡片容器（承载本条数据，便于按钮回调里直接拿到）
		Border card = new Border
		{
			Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
			BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(14.0),
			Padding = new Thickness(12.0),
			Width = 232.0,
			Tag = item,
			Child = content
		};

		actionBtn.Click += async delegate
		{
			actionBtn.IsEnabled = false;
			try
			{
				if (added)
				{
					await RemovePresetAsync(item, gameId, card);
				}
				else
				{
					await AddPresetAsync(item, gameId, actionBtn);
				}
			}
			finally
			{
				actionBtn.IsEnabled = !builtin;
			}
		};
		detailBtn.Click += async delegate
		{
			await ShowDetailAsync(item);
		};

		StackPanel actions = new StackPanel
		{
			Spacing = 4.0
		};
		actions.Children.Add(actionBtn);
		actions.Children.Add(detailBtn);
		content.Children.Add(actions);
		return card;
	}

	/// <summary>卡片上的「添加为服务器预设」：解析地址 → 注册预设 → 就地刷新卡片。</summary>
	private async Task AddPresetAsync(NeteaseServerListService.NetGameSummary item, long gameId, Button actionBtn)
	{
		AccountStore.LocalAccount? account = SelectedAccount();
		if (account == null)
		{
			MainWindow.Instance?.ShowToast("请先在上方选择一个已登录的网易账号（或到「账号绑定」页登录）", false);
			return;
		}
		string original = actionBtn.Content as string ?? "";
		actionBtn.Content = "解析地址中…";
		SetStatus("正在解析「" + item.Name + "」的进入地址…");
		NeteaseServerListService.ServerAddressInfo info;
		try
		{
			info = await NeteaseServerListService.ResolveAddressAsync(account.EntityId, account.Token, item.EntityId);
		}
		catch (Exception ex)
		{
			actionBtn.Content = original;
			SetStatus("解析失败：" + ex.Message);
			MainWindow.Instance?.ShowToast("解析进入地址失败：" + ex.Message, false);
			return;
		}
		actionBtn.Content = original;
		if (!info.Ok)
		{
			SetStatus("「" + item.Name + "」" + info.Message);
			MainWindow.Instance?.ShowToast("无法添加「" + item.Name + "」：" + info.Message, false);
			return;
		}
		ServerPreset? preset = ServerPresets.RegisterDiscovered(item.Name, info.Ip, info.Port, gameId);
		if (preset == null)
		{
			MainWindow.Instance?.ShowToast("该服务器已在预设列表中", false);
		}
		else
		{
			MainWindow.Instance?.ShowToast("已添加「" + preset.Name + "」(" + preset.Host + ":" + preset.Port + ")，可在代理启动页选择", ok: true);
			SetStatus("已添加预设：" + preset.Name + " " + preset.Host + ":" + preset.Port);
		}
		ApplyFilter();
	}

	/// <summary>卡片上的「移除服务器预设」：二次确认后删除并就地刷新卡片。</summary>
	private async Task RemovePresetAsync(NeteaseServerListService.NetGameSummary item, long gameId, FrameworkElement card)
	{
		ServerPreset? preset = null;
		foreach (ServerPreset p in ServerPresets.All)
		{
			if (p.GameId == gameId)
			{
				preset = p;
				break;
			}
		}
		if (preset == null)
		{
			ApplyFilter();
			return;
		}
		if (ServerPresets.IsBuiltin(preset))
		{
			MainWindow.Instance?.ShowToast("这是内置默认预设，不可删除", false);
			return;
		}
		StackPanel body = new StackPanel
		{
			Spacing = 8.0,
			MinWidth = 340.0
		};
		body.Children.Add(new TextBlock
		{
			Text = "确定移除服务器预设「" + preset.Name + "」？",
			FontSize = 14.0,
			FontWeight = FontWeights.SemiBold,
			TextWrapping = TextWrapping.Wrap
		});
		body.Children.Add(new TextBlock
		{
			Text = preset.Host + ":" + preset.Port,
			FontSize = 13.0,
			FontFamily = new FontFamily("Consolas"),
			Opacity = 0.8
		});
		body.Children.Add(new TextBlock
		{
			Text = "移除后该服务器不再出现在「代理启动 / 快速启动 / 更新客户端」的预设列表中，之后可从本列表重新添加。",
			FontSize = 12.0,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap
		});
		ContentDialog dialog = new ContentDialog
		{
			Title = "移除服务器预设",
			Content = body,
			PrimaryButtonText = "移除",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = base.XamlRoot,
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default)
		};
		if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
		{
			return;
		}
		if (ServerPresets.Remove(preset))
		{
			MainWindow.Instance?.ShowToast("已移除预设「" + preset.Name + "」", true);
			ApplyFilter();
		}
		else
		{
			MainWindow.Instance?.ShowToast("移除失败，请重试", false);
		}
	}

	private static long ParseGameId(string entityId)
	{
		return long.TryParse(entityId, out long v) ? v : 0L;
	}

	// ---------- 详情弹窗 ----------

	// ---------- 详情弹窗（纯展示，增删预设走卡片按钮） ----------

	private async Task ShowDetailAsync(NeteaseServerListService.NetGameSummary item)
	{
		long gameId = ParseGameId(item.EntityId);
		AccountStore.LocalAccount? account = SelectedAccount();
		if (account == null)
		{
			MainWindow.Instance?.ShowToast("请先选择已登录的网易账号", false);
			return;
		}
		SetStatus("正在解析「" + item.Name + "」的进入地址…");
		NeteaseServerListService.ServerAddressInfo info = await NeteaseServerListService.ResolveAddressAsync(account.EntityId, account.Token, item.EntityId);
		SetStatus(info.Ok ? ("「" + item.Name + "」进入地址：" + info.HostPort) : ("「" + item.Name + "」" + info.Message));
		StackPanel body = new StackPanel
		{
			Spacing = 8.0,
			MinWidth = 360.0
		};
		body.Children.Add(new TextBlock
		{
			Text = "服务器号：" + item.EntityId,
			FontSize = 12.0,
			Opacity = 0.8
		});
		if (info.Ok)
		{
			body.Children.Add(new TextBlock
			{
				Text = info.HostPort,
				FontSize = 20.0,
				FontWeight = FontWeights.SemiBold,
				FontFamily = new FontFamily("Consolas")
			});
			string meta = "";
			if (info.InWhitelist)
			{
				meta += "· 需要白名单 ";
			}
			if (info.GameStatus > 0)
			{
				meta += "· 状态码 " + info.GameStatus + " ";
			}
			if (!string.IsNullOrWhiteSpace(meta))
			{
				body.Children.Add(new TextBlock
				{
					Text = meta.Trim(),
					FontSize = 12.0,
					Opacity = 0.8
				});
			}
		}
		else
		{
			body.Children.Add(new TextBlock
			{
				Text = info.Message,
				FontSize = 13.0,
				Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28))
			});
		}
		if (!string.IsNullOrWhiteSpace(item.BriefSummary))
		{
			body.Children.Add(new TextBlock
			{
				Text = item.BriefSummary,
				FontSize = 12.0,
				Opacity = 0.75,
				TextWrapping = TextWrapping.Wrap,
				MaxHeight = 90.0
			});
		}
		if (!string.IsNullOrWhiteSpace(info.Announcement))
		{
			ScrollViewer annScroller = new ScrollViewer
			{
				MaxHeight = 120.0,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto
			};
			annScroller.Content = new TextBlock
			{
				Text = info.Announcement,
				FontSize = 12.0,
				Opacity = 0.75,
				TextWrapping = TextWrapping.Wrap
			};
			body.Children.Add(annScroller);
		}
		body.Children.Add(new TextBlock
		{
			Text = "添加后可在「代理启动 / 快速启动」页像内置服务器一样选用。",
			FontSize = 11.0,
			Opacity = 0.6,
			TextWrapping = TextWrapping.Wrap
		});
		ContentDialog dialog = new ContentDialog
		{
			Title = item.Name,
			Content = body,
			CloseButtonText = "关闭",
			XamlRoot = base.XamlRoot
		};
		if (info.Ok && !ServerPresets.ContainsGameId(gameId))
		{
			dialog.PrimaryButtonText = "添加为服务器预设";
			dialog.PrimaryButtonCommand = null;
		}
		ContentDialogResult result = await DialogGate.ShowAsync(dialog);
		if (result == ContentDialogResult.Primary && info.Ok)
		{
			ServerPreset? preset = ServerPresets.RegisterDiscovered(item.Name, info.Ip, info.Port, gameId);
			if (preset != null)
			{
				MainWindow.Instance?.ShowToast("已添加「" + preset.Name + "」(" + preset.Host + ":" + preset.Port + ")，可在代理启动页选择", ok: true);
				ApplyFilter();
			}
			else
			{
				MainWindow.Instance?.ShowToast("该服务器已在预设列表中", false);
			}
		}
	}

}

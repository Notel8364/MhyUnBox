using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Services;
using MhyunBox.Services.BjdProxy;
using MhyunBox.Settings;
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
		// 切换账号后重建卡片：服务器列表缓存可能来自旧会话，封面懒加载要用新账号的 token 重试
		_accountBox.SelectionChanged += delegate
		{
			if (_items.Count > 0)
			{
				ApplyFilter();
			}
		};

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

	// code=22（账号在别处登录）自动恢复：用已存 Cookie/账密重登换新会话，刷新账号框并返回新账号对象。
	// 失败返回 null（调用方负责给出明确提示）。
	// 兼容模式（兼容网易发烧启动器）下不自动重登：重登=建立新会话=把官方启动器顶下来，
	// 会形成两边互相顶号的拉锯战；兼容模式由用户自主取舍（提示去手动重登或关闭官方启动器）。
	private async Task<AccountStore.LocalAccount?> RecoverSessionAsync(AccountStore.LocalAccount stale)
	{
		try
		{
			if (SettingsManager.Instance.CompatNeteaseLauncher)
			{
				LogHelper.Log("[会话恢复] code=22 会话被顶，但兼容网易发烧启动器已开启，不自动重登（避免顶回官方会话）");
				MainWindow.Instance?.ShowToast("会话被其他登录占用（兼容模式已让出优先级）。可到账号绑定页手动重登，或关闭该模式后自动抢回。", false);
				return null;
			}
			LogHelper.Log($"[会话恢复] code=22 会话被顶，自动重登 {stale.Channel}/{AccountStore.Mask(stale.Account)}…");
			MainWindow.Instance?.ShowToast("检测到会话被其他登录顶掉（官方启动器在线？），正在自动重登…", false);
			bool ok = await AccountAutoLoginService.ReloginAsync(stale);
			AccountStore.ReloadFromStore();
			RebuildAccountOptions();
			if (!ok)
			{
				LogHelper.Log("[会话恢复] 自动重登失败: " + AccountAutoLoginService.LastReloginMessage);
				return null;
			}
			AccountStore.LocalAccount? fresh = AccountStore.All.FirstOrDefault((AccountStore.LocalAccount a) =>
				string.Equals(a.Channel, stale.Channel, StringComparison.Ordinal)
				&& string.Equals(a.Account, stale.Account, StringComparison.Ordinal));
			if (fresh != null)
			{
				LogHelper.Log($"[会话恢复] 自动重登成功，新 token 长度 {fresh.Token.Length}，EntityId {fresh.EntityId}");
			}
			return fresh;
		}
		catch (Exception ex)
		{
			LogHelper.Log("[会话恢复] 自动重登异常: " + ex.Message);
			return null;
		}
	}

	// 封面懒加载中检测到会话被顶时的去重触发器（整页几十张卡只触发一次重登）
	private int _coverRecovering;

	private async Task HandleCoverSessionKickedAsync()
	{
		if (System.Threading.Interlocked.Exchange(ref _coverRecovering, 1) == 1)
		{
			return;
		}
		try
		{
			AccountStore.LocalAccount? acc = SelectedAccount();
			if (acc == null)
			{
				return;
			}
			AccountStore.LocalAccount? fresh = await RecoverSessionAsync(acc);
			if (fresh != null)
			{
				// 重建卡片：懒加载会用新会话重试封面
				ApplyFilter();
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[会话恢复] 封面重试异常: " + ex.Message);
		}
		finally
		{
			System.Threading.Interlocked.Exchange(ref _coverRecovering, 0);
		}
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
			List<NeteaseServerListService.NetGameSummary> list;
			try
			{
				list = await NeteaseServerListService.ListServersAsync(account.EntityId, account.Token, offset, PageSize, null, ct);
			}
			catch (NeteaseRoleService.NeteaseSessionKickedException sk)
			{
				// 会话被顶：自动重登换新会话后重试一次
				SetStatus("会话被其他登录顶掉，正在自动重登…");
				AccountStore.LocalAccount? fresh = await RecoverSessionAsync(account);
				if (fresh == null)
				{
					SetStatus("会话失效且自动重登失败：" + sk.Message + "。请到「账号绑定」页重新登录该账号（并退出官方启动器）。");
					return;
				}
				account = fresh;
				list = await NeteaseServerListService.ListServersAsync(account.EntityId, account.Token, offset, PageSize, null, ct);
			}
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

	// 封面懒加载：列表接口不带图，先经 /item-details/get_v2 拿 brief_image_urls[0] 再下载展示。
	// 限流 4 并发，避免整页卡片同时打详情接口触发网关风控。
	private static readonly SemaphoreSlim CoverFetchGate = new SemaphoreSlim(4, 4);

	private async Task LoadCoverLazyAsync(Image image, Border host, NeteaseServerListService.NetGameSummary item)
	{
		try
		{
			// 关键：先让出 UI 线程，等 BuildCard 把 imageBorder 挂进可视化树后再做回收检查。
			// 此前同步段检查 host.Parent==null 恒为真（挂树发生在调用之后），每次都被误判"卡片已回收"而跳过。
			await Task.Yield();
			AccountStore.LocalAccount? account = SelectedAccount();
			if (account == null)
			{
				LogHelper.Log($"[服务器列表] 封面懒加载跳过 item={item.EntityId}（当前未选择账号）");
				return;
			}
			if (host.Parent == null || !ReferenceEquals(host.Child, image))
			{
				LogHelper.Log($"[服务器列表] 封面懒加载跳过 item={item.EntityId}（卡片已回收）");
				return;
			}
			await CoverFetchGate.WaitAsync();
			string url;
			try
			{
				url = await NeteaseServerListService.GetCoverUrlAsync(account.EntityId, account.Token, item.EntityId);
			}
			catch (NeteaseRoleService.NeteaseSessionKickedException)
			{
				// 会话被顶：触发一次自动重登 + 卡片重建（内部去重，整页只触发一次）
				_ = HandleCoverSessionKickedAsync();
				return;
			}
			finally
			{
				CoverFetchGate.Release();
			}
			if (string.IsNullOrWhiteSpace(url))
			{
				LogHelper.Log($"[服务器列表] 封面懒加载未取到图 item={item.EntityId}");
				return;
			}
			if (host.Parent == null || !ReferenceEquals(host.Child, image))
			{
				return;
			}
			item.CoverUrl = url;
			await LoadCardImageAsync(image, host, url);
			// 封面落缓存（每次刷新列表时随 _items 一起持久化）
			NeteaseServerListService.SaveCache(_items);
		}
		catch (Exception ex)
		{
			LogHelper.Log("[服务器列表] 封面懒加载异常 item=" + item.EntityId + " => " + ex.Message);
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
		if (!string.IsNullOrWhiteSpace(item.CoverUrl) || !string.IsNullOrWhiteSpace(item.TitleImageUrl))
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
				string url = !string.IsNullOrWhiteSpace(item.TitleImageUrl) ? item.TitleImageUrl : item.CoverUrl;
				if (!string.IsNullOrWhiteSpace(url))
				{
					_ = LoadCardImageAsync(image, imageBorder, url);
				}
				else
				{
					// 列表接口不带图：懒加载 /item-details/get_v2 的 brief_image_urls[0]
					_ = LoadCoverLazyAsync(image, imageBorder, item);
				}
			}
			catch
			{
			}
		}
		else
		{
			// 无封面缓存时同样懒加载一次
			try
			{
				Image image = new Image
				{
					Stretch = Stretch.UniformToFill,
					HorizontalAlignment = HorizontalAlignment.Stretch,
					VerticalAlignment = VerticalAlignment.Center,
					Opacity = 0.0
				};
				imageBorder.Child = image;
				_ = LoadCoverLazyAsync(image, imageBorder, item);
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

		// 角色管理按钮：打开该服务器的角色列表弹窗（创建 / 删除 / 撤回删除）
		Button rolesBtn = new Button
		{
			Content = "👥 角色管理",
			FontSize = 12.0,
			FontWeight = FontWeights.SemiBold,
			Padding = new Thickness(10.0, 6.0, 10.0, 6.0),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Center,
			MinWidth = 0.0
		};
		ToolTipService.SetToolTip(rolesBtn, "查看该账号在此服务器的角色列表，可创建、删除（30 天冷静期）或撤回删除角色");

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
		rolesBtn.Click += async delegate
		{
			await ShowRolesDialogAsync(item);
		};

		StackPanel actions = new StackPanel
		{
			Spacing = 4.0
		};
		actions.Children.Add(actionBtn);
		actions.Children.Add(rolesBtn);
		actions.Children.Add(detailBtn);
		content.Children.Add(actions);
		return card;
	}

	/// <summary>卡片上的「添加为服务器预设」：解析地址 → （布吉岛先过责任协议）→ 注册预设 → 就地刷新卡片。</summary>
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
		if (BjdGate.IsBjd(gameId) && !await ShowBjdAgreementAsync())
		{
			SetStatus("已取消添加（未通过责任协议书）");
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

	/// <summary>
	/// 布吉岛「责任协议书」弹窗：添加布吉岛服务器预设前强制确认。
	/// 输入框必须精确写入「我保证与接受且自愿承担风险」才能点确认；取消 / 关闭 = 拒绝添加。
	/// </summary>
	private async Task<bool> ShowBjdAgreementAsync()
	{
		const string requiredText = "我保证与接受且自愿承担风险";
		TextBox input = new TextBox
		{
			PlaceholderText = "在此输入：" + requiredText,
			AcceptsReturn = false
		};
		StackPanel body = new StackPanel
		{
			Spacing = 8.0,
			MinWidth = 400.0
		};
		body.Children.Add(new TextBlock
		{
			Text = "添加此服务器游玩时我保证以下几点:",
			FontSize = 14.0,
			FontWeight = FontWeights.SemiBold,
			TextWrapping = TextWrapping.Wrap
		});
		string[] clauses = new string[6]
		{
			"1.坚决拥护MhyUnBox，不二次售卖，煽动舆情等",
			"2.不以此额外制作外挂，利用漏洞等去损害他人利益以及破坏他人正常游玩体验",
			"3.使用此产品进入此服务器后同意服务器协议且保证绝不利用外挂，注入等方式去满足私欲而去破坏他人体验",
			"4.以上内容如遭到个人财产及作者被告对于事件大小均由自主承担所有费用与风险和过程(以下输入框写入:'我保证与接受且自愿承担风险')，在此不接受任何反驳以及游戏相关问题",
			"5.风险自担：因使用本工具导致的账号封禁、数据丢失、财产损失等一切后果由使用者自行承担，与作者无关",
			"6.授权可撤销：违反以上任一条款即视为自动放弃本工具的使用授权，作者有权拒绝提供任何支持与更新"
		};
		foreach (string clause in clauses)
		{
			body.Children.Add(new TextBlock
			{
				Text = clause,
				FontSize = 13.0,
				TextWrapping = TextWrapping.Wrap
			});
		}
		body.Children.Add(input);
		ContentDialog dialog = new ContentDialog
		{
			Title = "责任协议书",
			Content = body,
			PrimaryButtonText = "确认",
			CloseButtonText = "取消",
			IsPrimaryButtonEnabled = false,
			XamlRoot = base.XamlRoot
		};
		input.TextChanged += delegate
		{
			dialog.IsPrimaryButtonEnabled = string.Equals(input.Text.Trim(), requiredText, StringComparison.Ordinal);
		};
		ContentDialogResult result = await DialogGate.ShowAsync(dialog);
		bool agreed = result == ContentDialogResult.Primary;
		if (agreed)
		{
			// 留痕：同意时间（含时间戳与版本号）+ 确认文本写入本地日志，争议时可作为用户自愿同意的凭证
			LogHelper.Log("[责任协议书] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
				+ " | " + AppVersion.Display
				+ " | 用户已同意布吉岛责任协议书（确认文本校验通过）");
		}
		return agreed;
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

	// ---------- 角色管理弹窗 ----------

	// 角色操作（创建/删除/撤回删除）全部在本弹窗内完成：
	// DialogGate 串行化所有 ContentDialog，嵌套弹窗会被拒/超时，因此「输入角色名 + 二次确认」用内联面板切换实现。
	// 角色管理弹窗操作成功后，把该服务器的最新角色集精确回写到账号树（快速启动创建组选角色时可见）。
	// 键名优先匹配预设名（树按 preset.Name 取值）；列表卡片名与预设名不一致时按 GameId 找到预设再回写。
	private async Task SyncRolesToStoreAsync(AccountStore.LocalAccount account, NeteaseServerListService.NetGameSummary item)
	{
		try
		{
			List<NeteaseRoleService.RoleInfo> roles = await NeteaseRoleService.QueryRolesAsync(account.EntityId, account.Token, item.EntityId);
			// 与角色管理弹窗同款兜底：列表条目查不到时，查同名预设的 GameId（角色可能挂在同名服务器的另一个条目上）
			if (roles.Count == 0)
			{
				long listGid = ParseGameId(item.EntityId);
				string itemName = (item.Name ?? "").Trim();
				foreach (ServerPreset p in ServerPresets.All)
				{
					if (p.IsPending || p.GameId <= 0 || p.GameId == listGid || (p.Name ?? "").Trim().Length == 0 || itemName.Length == 0)
					{
						continue;
					}
					string pn = p.Name.Trim();
					if (!itemName.Contains(pn, StringComparison.OrdinalIgnoreCase) && !pn.Contains(itemName, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					roles = await NeteaseRoleService.QueryRolesAsync(account.EntityId, account.Token, p.GameId.ToString());
					if (roles.Count > 0)
					{
						LogHelper.Log($"[角色同步] 列表条目 {item.EntityId} 无角色，改用同名预设「{p.Name}」GameId={p.GameId} 的角色集");
						break;
					}
				}
			}
			long gameId = ParseGameId(item.EntityId);
			string key = item.Name ?? "";
			ServerPreset? preset = ServerPresets.All.FirstOrDefault((ServerPreset p) => p.GameId == gameId && !p.IsPending);
			if (preset != null && !string.IsNullOrWhiteSpace(preset.Name))
			{
				key = preset.Name;
			}
			if (string.IsNullOrWhiteSpace(key))
			{
				return;
			}
			Dictionary<string, List<string>> serverRoles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
			{
				[key] = roles.Select((NeteaseRoleService.RoleInfo r) => r.Name).Where((string n) => !string.IsNullOrWhiteSpace(n)).ToList()
			};
			AccountStore.UpdateRoles(account.Channel, account.Account, serverRoles, replace: true);
			LogHelper.Log($"[角色同步] 服务器「{key}」最新角色({roles.Count}个)已回写账号树: " + string.Join(" / ", serverRoles[key]));
		}
		catch (Exception ex)
		{
			LogHelper.Log("[角色同步] 回写账号树失败（不影响角色操作本身）: " + ex.Message);
		}
	}

	private async Task ShowRolesDialogAsync(NeteaseServerListService.NetGameSummary item)
	{
		AccountStore.LocalAccount? account = SelectedAccount();
		if (account == null)
		{
			MainWindow.Instance?.ShowToast("请先在上方选择已登录的网易账号", false);
			return;
		}
		string gameId = item.EntityId;

		TextBlock status = new TextBlock
		{
			FontSize = 12.0,
			Opacity = 0.75,
			TextWrapping = TextWrapping.Wrap,
			Text = "正在加载角色列表…"
		};

		TextBlock summary = new TextBlock
		{
			FontSize = 12.0,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap,
			Text = "账号 [" + AccountLabel(account) + "] · 服务器号 " + gameId
		};

		ListView roleList = new ListView
		{
			SelectionMode = ListViewSelectionMode.Single,
			MaxHeight = 240.0,
			MinHeight = 60.0
		};

		// 创建面板（默认隐藏）：输入角色名 → 确认按钮
		TextBox nameBox = new TextBox
		{
			PlaceholderText = "输入新角色名（2-16 个字符）",
			MaxLength = 16,
			FontSize = 13.0
		};

		// 二次确认行（默认隐藏）：确定创建？
		TextBlock confirmText = new TextBlock
		{
			FontSize = 13.0,
			FontWeight = FontWeights.SemiBold,
			TextWrapping = TextWrapping.Wrap
		};

		Button confirmCreateBtn = new Button
		{
			Content = "✓ 确认创建",
			FontSize = 12.5,
			Padding = new Thickness(12.0, 6.0, 12.0, 6.0)
		};
		Button cancelCreateBtn = new Button
		{
			Content = "返回",
			FontSize = 12.5,
			Padding = new Thickness(12.0, 6.0, 12.0, 6.0)
		};

		StackPanel confirmRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0,
			Visibility = Visibility.Collapsed
		};
		confirmRow.Children.Add(confirmText);
		confirmRow.Children.Add(confirmCreateBtn);
		confirmRow.Children.Add(cancelCreateBtn);

		Button doCreateBtn = new Button
		{
			Content = "✓ 下一步",
			FontSize = 12.5,
			Padding = new Thickness(12.0, 6.0, 12.0, 6.0)
		};
		Button backCreateBtn = new Button
		{
			Content = "取消",
			FontSize = 12.5,
			Padding = new Thickness(12.0, 6.0, 12.0, 6.0)
		};
		StackPanel createRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0,
			Visibility = Visibility.Collapsed
		};
		createRow.Children.Add(nameBox);
		createRow.Children.Add(doCreateBtn);
		createRow.Children.Add(backCreateBtn);

		Button createBtn = new Button
		{
			Content = "＋ 创建角色",
			FontSize = 12.5,
			Padding = new Thickness(12.0, 7.0, 12.0, 7.0)
		};
		Button deleteBtn = new Button
		{
			Content = "删除所选",
			FontSize = 12.5,
			Padding = new Thickness(12.0, 7.0, 12.0, 7.0)
		};
		Button restoreBtn = new Button
		{
			Content = "撤回删除",
			FontSize = 12.5,
			Padding = new Thickness(12.0, 7.0, 12.0, 7.0)
		};
		Button refreshBtn = new Button
		{
			Content = "刷新",
			FontSize = 12.5,
			Padding = new Thickness(12.0, 7.0, 12.0, 7.0)
		};
		StackPanel actionRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		actionRow.Children.Add(createBtn);
		actionRow.Children.Add(deleteBtn);
		actionRow.Children.Add(restoreBtn);
		actionRow.Children.Add(refreshBtn);

		StackPanel body = new StackPanel
		{
			Spacing = 10.0,
			MinWidth = 420.0
		};
		body.Children.Add(summary);
		body.Children.Add(status);
		ScrollViewer listScroller = new ScrollViewer
		{
			MaxHeight = 260.0,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		listScroller.Content = roleList;
		body.Children.Add(listScroller);
		body.Children.Add(actionRow);
		body.Children.Add(createRow);
		body.Children.Add(confirmRow);

		ContentDialog dialog = new ContentDialog
		{
			Title = "角色管理 · " + item.Name,
			Content = body,
			CloseButtonText = "关闭",
			XamlRoot = base.XamlRoot,
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default)
		};

		async Task LoadRolesAsync()
		{
			status.Text = "正在加载角色列表…";
			try
			{
				List<NeteaseRoleService.RoleInfo> roles = await NeteaseRoleService.QueryRolesAsync(account.EntityId, account.Token, gameId);
				string sourceNote = "";
				// 2026-11-20 修复「有角色却显示没有」：服务器列表里同名服务器可能存在多个区服条目（entity_id 不同），
				// 角色挂在其中之一。列表条目查不到角色时，把名字匹配的预设 GameId 也查一遍并合并展示。
				if (roles.Count == 0)
				{
					long listGid = ParseGameId(gameId);
					string itemName = (item.Name ?? "").Trim();
					foreach (ServerPreset p in ServerPresets.All)
					{
						if (p.IsPending || p.GameId <= 0 || p.GameId == listGid)
						{
							continue;
						}
						string pn = (p.Name ?? "").Trim();
						if (pn.Length == 0 || itemName.Length == 0)
						{
							continue;
						}
						if (!itemName.Contains(pn, StringComparison.OrdinalIgnoreCase) && !pn.Contains(itemName, StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
						LogHelper.Log($"[角色管理] 列表条目 {gameId} 无角色，尝试同名预设「{p.Name}」GameId={p.GameId}");
						List<NeteaseRoleService.RoleInfo> extra = await NeteaseRoleService.QueryRolesAsync(account.EntityId, account.Token, p.GameId.ToString());
						foreach (NeteaseRoleService.RoleInfo r in extra)
						{
							if (!roles.Any((NeteaseRoleService.RoleInfo x) => x.EntityId == r.EntityId))
							{
								roles.Add(r);
							}
						}
						if (roles.Count > 0)
						{
							sourceNote = "（角色来自同名服务器条目 " + p.GameId + "）";
							break;
						}
					}
				}
				roleList.Items.Clear();
				if (roles.Count == 0)
				{
					status.Text = "该账号在此服务器还没有角色，点「创建角色」新建一个。";
					return;
				}
				foreach (NeteaseRoleService.RoleInfo role in roles)
				{
					roleList.Items.Add(new StackPanel
					{
						Orientation = Orientation.Horizontal,
						Spacing = 8.0,
						Tag = role,
						Children =
						{
							new TextBlock
							{
								Text = role.IsPendingDelete ? "🗑 " + role.Name : "👤 " + role.Name,
								FontSize = 13.5,
								FontWeight = FontWeights.SemiBold
							},
							new TextBlock
							{
								Text = role.IsPendingDelete ? "删除中·冷静期内可撤回" : "",
								FontSize = 11.5,
								Opacity = 0.65,
								VerticalAlignment = VerticalAlignment.Center
							},
							new TextBlock
							{
								Text = "  id " + role.EntityId,
								FontSize = 11.0,
								Opacity = 0.5,
								FontFamily = new FontFamily("Consolas"),
								VerticalAlignment = VerticalAlignment.Center
							}
						}
					});
				}
				status.Text = "共 " + roles.Count + " 个角色" + sourceNote + "。删除为 30 天冷静期软删除，冷静期内可随时撤回。";
			}
			catch (OperationCanceledException)
			{
			}
			catch (NeteaseRoleService.NeteaseSessionKickedException sk)
			{
				// 会话被顶：自动重登换新会话后重试一次
				status.Text = "会话被其他登录顶掉（" + sk.Message + "），正在自动重登…";
				AccountStore.LocalAccount? fresh = await RecoverSessionAsync(account);
				if (fresh != null)
				{
					account = fresh;
					await LoadRolesAsync();
					return;
				}
				status.Text = "会话失效且自动重登失败：" + sk.Message + "。请到「账号绑定」页重新登录该账号（并退出官方启动器）。";
			}
			catch (Exception ex)
			{
				status.Text = "角色列表加载失败：" + ex.Message;
			}
		}

		NeteaseRoleService.RoleInfo? SelectedRole()
		{
			return (roleList.SelectedItem as StackPanel)?.Tag as NeteaseRoleService.RoleInfo;
		}

		void ShowMainActions(bool show)
		{
			actionRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
			createRow.Visibility = (!show) ? Visibility.Visible : Visibility.Collapsed;
			confirmRow.Visibility = Visibility.Collapsed;
		}

		refreshBtn.Click += async delegate
		{
			await LoadRolesAsync();
		};
		createBtn.Click += delegate
		{
			nameBox.Text = "";
			ShowMainActions(show: false);
			confirmText.Text = "";
			nameBox.Focus(FocusState.Programmatic);
		};
		backCreateBtn.Click += delegate
		{
			ShowMainActions(show: true);
		};
		doCreateBtn.Click += delegate
		{
			string name = nameBox.Text.Trim();
			if (name.Length < 2)
			{
				status.Text = "角色名太短（至少 2 个字符）。";
				return;
			}
			// 二次提醒（内联）：再次确认角色名与目标服务器
			createRow.Visibility = Visibility.Collapsed;
			confirmText.Text = "⚠ 确定在服务器「" + item.Name + "」创建角色「" + name + "」吗？（每个服务器的角色数量有限，请确认名称无误）";
			confirmRow.Visibility = Visibility.Visible;
		};
		cancelCreateBtn.Click += delegate
		{
			ShowMainActions(show: true);
		};
		confirmCreateBtn.Click += async delegate
		{
			string name = nameBox.Text.Trim();
			confirmCreateBtn.IsEnabled = false;
			cancelCreateBtn.IsEnabled = false;
			status.Text = "正在创建角色「" + name + "」…";
			try
			{
				var (ok, message, _) = await NeteaseRoleService.CreateRoleAsync(account.EntityId, account.Token, gameId, name);
				status.Text = message;
				MainWindow.Instance?.ShowToast(message, ok);
				if (ok)
				{
					await SyncRolesToStoreAsync(account, item);
				}
			}
			catch (NeteaseRoleService.NeteaseSessionKickedException sk)
			{
				status.Text = "会话被其他登录顶掉，正在自动重登…";
				AccountStore.LocalAccount? fresh = await RecoverSessionAsync(account);
				if (fresh != null)
				{
					account = fresh;
					var retry = await NeteaseRoleService.CreateRoleAsync(account.EntityId, account.Token, gameId, name);
					status.Text = retry.Message;
					MainWindow.Instance?.ShowToast(retry.Message, retry.Ok);
					if (retry.Ok)
					{
						await SyncRolesToStoreAsync(account, item);
					}
				}
				else
				{
					status.Text = "创建失败：会话失效且自动重登失败（" + sk.Message + "）。请到「账号绑定」页重新登录该账号。";
				}
			}
			catch (Exception ex)
			{
				status.Text = "创建异常：" + ex.Message;
			}
			finally
			{
				confirmCreateBtn.IsEnabled = true;
				cancelCreateBtn.IsEnabled = true;
				ShowMainActions(show: true);
			}
			await LoadRolesAsync();
		};
		deleteBtn.Click += async delegate
		{
			NeteaseRoleService.RoleInfo? role = SelectedRole();
			if (role == null)
			{
				status.Text = "请先在列表中选中一个角色再删除。";
				return;
			}
			if (role.IsPendingDelete)
			{
				status.Text = "角色「" + role.Name + "」已处于删除冷静期，无需重复删除（可直接撤回）。";
				return;
			}
			deleteBtn.IsEnabled = false;
			status.Text = "正在删除角色「" + role.Name + "」…";
			try
			{
				var (ok, message) = await NeteaseRoleService.DeleteRoleAsync(account.EntityId, account.Token, role.EntityId);
				status.Text = message + "（30 天冷静期后正式删除，期间可撤回）";
				MainWindow.Instance?.ShowToast(message, ok);
				if (ok)
				{
					await SyncRolesToStoreAsync(account, item);
				}
			}
			catch (NeteaseRoleService.NeteaseSessionKickedException sk)
			{
				status.Text = "会话被其他登录顶掉，正在自动重登…";
				AccountStore.LocalAccount? fresh = await RecoverSessionAsync(account);
				if (fresh != null)
				{
					account = fresh;
					var retry = await NeteaseRoleService.DeleteRoleAsync(account.EntityId, account.Token, role.EntityId);
					status.Text = retry.Message + "（30 天冷静期后正式删除，期间可撤回）";
					MainWindow.Instance?.ShowToast(retry.Message, retry.Ok);
					if (retry.Ok)
					{
						await SyncRolesToStoreAsync(account, item);
					}
				}
				else
				{
					status.Text = "删除失败：会话失效且自动重登失败（" + sk.Message + "）。请到「账号绑定」页重新登录该账号。";
				}
			}
			catch (Exception ex)
			{
				status.Text = "删除异常：" + ex.Message;
			}
			finally
			{
				deleteBtn.IsEnabled = true;
			}
			await LoadRolesAsync();
		};
		restoreBtn.Click += async delegate
		{
			NeteaseRoleService.RoleInfo? role = SelectedRole();
			if (role == null)
			{
				status.Text = "请先在列表中选中一个角色再撤回删除。";
				return;
			}
			if (!role.IsPendingDelete)
			{
				status.Text = "角色「" + role.Name + "」不在删除冷静期内，无需撤回。";
				return;
			}
			restoreBtn.IsEnabled = false;
			status.Text = "正在撤回删除角色「" + role.Name + "」…";
			try
			{
				var (ok, message) = await NeteaseRoleService.CancelDeleteRoleAsync(account.EntityId, account.Token, role.EntityId);
				status.Text = message;
				MainWindow.Instance?.ShowToast(message, ok);
				if (ok)
				{
					await SyncRolesToStoreAsync(account, item);
				}
			}
			catch (NeteaseRoleService.NeteaseSessionKickedException sk)
			{
				status.Text = "会话被其他登录顶掉，正在自动重登…";
				AccountStore.LocalAccount? fresh = await RecoverSessionAsync(account);
				if (fresh != null)
				{
					account = fresh;
					var retry = await NeteaseRoleService.CancelDeleteRoleAsync(account.EntityId, account.Token, role.EntityId);
					status.Text = retry.Message;
					MainWindow.Instance?.ShowToast(retry.Message, retry.Ok);
					if (retry.Ok)
					{
						await SyncRolesToStoreAsync(account, item);
					}
				}
				else
				{
					status.Text = "撤回失败：会话失效且自动重登失败（" + sk.Message + "）。请到「账号绑定」页重新登录该账号。";
				}
			}
			catch (Exception ex)
			{
				status.Text = "撤回异常：" + ex.Message;
			}
			finally
			{
				restoreBtn.IsEnabled = true;
			}
			await LoadRolesAsync();
		};

		dialog.Closed += delegate
		{
			ApplyFilter();
		};
		// 走 DialogGate 门控展示；角色列表在弹窗可见的同时并行加载（加载过程不开子弹窗，不违反门控）
		Task load = LoadRolesAsync();
		await DialogGate.ShowAsync(dialog);
		await load;
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
			if (BjdGate.IsBjd(gameId) && !await ShowBjdAgreementAsync())
			{
				return;
			}
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

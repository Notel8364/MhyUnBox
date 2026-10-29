using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Services;
using MhyunBox.Settings;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MhyunBox.Pages;

public sealed class ClientUpdatePage : Page
{
	private readonly DispatcherQueue _queue;

	private ComboBox _serverCombo;

	private TextBox _urlBox;

	private TextBlock _warnText;

	private CheckBox _snapCheck;

	private TextBox _pclPathBox;

	private Button _delPkgBtn;

	private Button? _delPresetBtn;

	private Button _pclBrowseBtn;

	private TextBox _dlDirBox;

	private Button _dlBrowseBtn;

	private Button _scanPkgBtn;

	private ListBox _pkgList;

	private Button _usePkgBtn;

	private readonly List<ClientUpdater.LocalPkgInfo> _pkgItems = new List<ClientUpdater.LocalPkgInfo>();

	private ProgressBar _progress;

	private TextBlock _statusText;

	private TextBlock _logBox;

	private ScrollViewer _logScroller;

	private Button _updateBtn;

	private Button _cancelBtn;

	private Button _checkBtn;

	private Button? _pullAllBtn;

	private Button _scanBtn;

	private Button _openLogBtn;

	private Button _openTargetBtn;

	private Button _dragonTrimBtn;

	private Button _dragonMergeBtn;

	private CancellationTokenSource? _cts;

	// 当前 URL 框里地址所属的服务器 gameId：自动获取时记录；手动粘贴/抓包扫描来的记 null（用 URL 内 gameId 判断）。
	// 用于「预设变化检测」：切换服务器/开始更新时发现地址与当前预设对不上 → 清空并重新获取，避免把 A 服的包装到 B 服。
	private long? _urlGameId;

	private static readonly Dictionary<string, (long GameId, string Name, bool Frozen)> Servers = new Dictionary<string, (long, string, bool)>
	{
		["ys"] = (4658261307332877912L, "元素之诗", false),
		["sl"] = (4624172676215685077L, "失落世界", false),
		["custom"] = (0L, "自定义(用 URL 里的 gameId)", false)
	};

	public ClientUpdatePage()
	{
		_queue = base.DispatcherQueue;
		base.Content = BuildUi();
	}

	private FrameworkElement BuildUi()
	{
		Grid grid = new Grid();
		ScrollViewer scrollViewer = new ScrollViewer
		{
			Content = new StackPanel
			{
				Padding = new Thickness(28.0, 24.0, 28.0, 24.0),
				Spacing = 10.0
			}
		};
		grid.Children.Add(scrollViewer);
		StackPanel stackPanel = (StackPanel)scrollViewer.Content;
		stackPanel.Children.Add(new TextBlock
		{
			Text = "更新客户端 / 资源包",
			Style = (Style)Application.Current.Resources["TitleTextBlockStyle"]
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = "服务器"
		});
		_serverCombo = new ComboBox
		{
			Width = 320.0,
			HorizontalAlignment = HorizontalAlignment.Left
		};
		ThemeHelper.SolidComboPopup(_serverCombo);
		_serverCombo.SelectionChanged += delegate
		{
			RefreshServerWarn();
			// 切换预设时清空其它服务器的旧地址，防止「更新资源包」直接复用对不上的 URL
			ClearUrlIfPresetMismatch();
		};
		// 注意：RebuildServerCombo 会触发 SelectionChanged → RefreshServerWarn，
		// 必须等 _warnText/_pclPathBox 等字段全部创建后再调用，否则构造期 NRE 直接闪退
		// 额外预设（服务器列表页添加）可在本页直接删除；内置「元素之诗 / 失落世界」不可删
		_delPresetBtn = new Button
		{
			Content = "🗑 删除所选预设",
			FontSize = 12.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		_delPresetBtn.Click += DelPreset_Click;
		StackPanel serverRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		serverRow.Children.Add(_serverCombo);
		serverRow.Children.Add(_delPresetBtn);
		stackPanel.Children.Add(serverRow);
		_warnText = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Foreground = new SolidColorBrush(Colors.Orange),
			Visibility = Visibility.Collapsed
		};
		stackPanel.Children.Add(_warnText);
		stackPanel.Children.Add(new TextBlock
		{
			Text = "Item 更新包 URL"
		});
		_urlBox = new TextBox
		{
			PlaceholderText = "https://x19.gph.netease.com/item_..._204_v..._....7z",
			TextWrapping = TextWrapping.Wrap,
			AcceptsReturn = false
		};
		stackPanel.Children.Add(_urlBox);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 20.0
		};
		_snapCheck = new CheckBox
		{
			Content = "更新后写 {gameid}.json 快照",
			IsChecked = true
		};
		stackPanel2.Children.Add(_snapCheck);
		stackPanel.Children.Add(stackPanel2);
		stackPanel.Children.Add(new TextBlock
		{
			Text = "下载目录（默认本机「下载」\\MhyunBox资源包，可自定义）"
		});
		StackPanel dlDirRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		_dlDirBox = new TextBox
		{
			Text = ClientUpdater.ResPkgRoot(),
			IsReadOnly = true,
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		dlDirRow.Children.Add(_dlDirBox);
		_dlBrowseBtn = new Button
		{
			Content = "浏览…"
		};
		_dlBrowseBtn.Click += delegate
		{
			DlBrowse_Click();
		};
		dlDirRow.Children.Add(_dlBrowseBtn);
		stackPanel.Children.Add(dlDirRow);
		stackPanel.Children.Add(new TextBlock
		{
			Text = "第二合并路径（PCL 客户端，可选）"
		});
		_pclPathBox = new TextBox
		{
			PlaceholderText = "切换服务器自动填充；失落世界会拼出 PCL 实例目录，元素之诗为 DragonCore 目录，也可手动改或点浏览",
			TextWrapping = TextWrapping.Wrap,
			AcceptsReturn = false
		};
		stackPanel.Children.Add(_pclPathBox);
		StackPanel pclRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		_pclBrowseBtn = new Button
		{
			Content = "浏览…（选 DragonCore / 客户端目录 / PCL）"
		};
		_pclBrowseBtn.Click += delegate
		{
			PclBrowse_Click();
		};
		pclRow.Children.Add(_pclBrowseBtn);
		stackPanel.Children.Add(pclRow);
		StackPanel stackPanel3 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10.0
		};
		_updateBtn = new Button
		{
			Content = "更新资源包"
		};
		_updateBtn.Click += delegate
		{
			RunResourcePackAsync();
		};
		_cancelBtn = new Button
		{
			Content = "取消",
			IsEnabled = false
		};
		_cancelBtn.Click += delegate
		{
			_cts?.Cancel();
		};
		_scanBtn = new Button
		{
			Content = "扫描抓包日志"
		};
		_scanBtn.Click += delegate
		{
			ScanCaptures();
		};
		// 只探测不下载：回传该资源包的「更新时间」并判断本机是否已是最新
		_checkBtn = new Button
		{
			Content = "检测地址（更新时间/是否最新）"
		};
		_checkBtn.Click += delegate
		{
			CheckUrlAsync();
		};
		// 诊断：逐个服务器尝试「取最新 item 包地址 → 下载 .7z」，验证其它服务器的客户端包能否拉取
		_pullAllBtn = new Button
		{
			Content = "⬇ 逐服拉取客户端包（诊断：全部服务器）"
		};
		_pullAllBtn.Click += delegate
		{
			TryPullAllServersAsync();
		};
		_openLogBtn = new Button
		{
			Content = "打开日志目录"
		};
		_openLogBtn.Click += delegate
		{
			OpenDir(Path.Combine(AppContext.BaseDirectory, "MhyunBox_Logs"));
		};
		_openTargetBtn = new Button
		{
			Content = "打开资源包目录"
		};
		_openTargetBtn.Click += delegate
		{
			OpenResPkgDir();
		};
		stackPanel3.Children.Add(_updateBtn);
		stackPanel3.Children.Add(_cancelBtn);
		stackPanel3.Children.Add(_checkBtn);
		stackPanel3.Children.Add(_pullAllBtn);
		stackPanel3.Children.Add(_scanBtn);
		stackPanel3.Children.Add(_openLogBtn);
		stackPanel3.Children.Add(_openTargetBtn);
		stackPanel.Children.Add(stackPanel3);
		StackPanel dragonPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10.0
		};
		_dragonTrimBtn = new Button
		{
			Content = "① 裁剪增量包（本地包VS官方新包 → 产出小 zip）"
		};
		_dragonTrimBtn.Click += DragonCoreTrim_Click;
		_dragonMergeBtn = new Button
		{
			Content = "② 合并增量包（选小 zip → 覆盖进本地大包）"
		};
		_dragonMergeBtn.Click += DragonCoreMerge_Click;
		dragonPanel.Children.Add(_dragonTrimBtn);
		dragonPanel.Children.Add(_dragonMergeBtn);
		stackPanel.Children.Add(dragonPanel);
		// —— 本地资源包（检测/选择已有包，跳过下载直接裁剪合并）——
		StackPanel pkgRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10.0
		};
		_scanPkgBtn = new Button
		{
			Content = "扫描本地资源包"
		};
		_scanPkgBtn.Click += delegate
		{
			ScanLocalPkg_Click();
		};
		_usePkgBtn = new Button
		{
			Content = "用选中的包直接安装/裁剪（跳过下载）",
			IsEnabled = false
		};
		_usePkgBtn.Click += delegate
		{
			UseLocalPkg_Click();
		};
		_delPkgBtn = new Button
		{
			Content = "删除选中的包",
			IsEnabled = false
		};
		_delPkgBtn.Click += delegate
		{
			DeleteLocalPkg_Click();
		};
		pkgRow.Children.Add(_scanPkgBtn);
		pkgRow.Children.Add(_usePkgBtn);
		pkgRow.Children.Add(_delPkgBtn);
		stackPanel.Children.Add(pkgRow);
		_pkgList = new ListBox
		{
			Height = 120.0,
			FontSize = 12.0,
			Background = ThemeHelper.ListSurface(),
			Visibility = Visibility.Collapsed
		};
		_pkgList.SelectionChanged += delegate
		{
			bool has = (_pkgList.SelectedIndex >= 0);
			_usePkgBtn.IsEnabled = has;
			_delPkgBtn.IsEnabled = has;
		};
		stackPanel.Children.Add(_pkgList);
		_progress = new ProgressBar
		{
			IsIndeterminate = true,
			IsEnabled = true,
			Visibility = Visibility.Collapsed
		};
		stackPanel.Children.Add(_progress);
		_statusText = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap
		};
		stackPanel.Children.Add(_statusText);
		_logBox = new TextBlock
		{
			FontFamily = new FontFamily("Consolas"),
			TextWrapping = TextWrapping.Wrap,
			FontSize = 12.0,
			Opacity = 0.85
		};
		_logScroller = new ScrollViewer
		{
			Content = _logBox,
			Height = 300.0,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		stackPanel.Children.Add(_logScroller);
		// 所有 UI 字段已就绪，此时才安全（会触发 SelectionChanged → RefreshServerWarn）
		RebuildServerCombo();
		RefreshServerWarn();
		return grid;
	}

	private (long GameId, string Name, bool Frozen)? Selected()
	{
		string? tag = (_serverCombo.SelectedItem as ComboBoxItem)?.Tag as string;
		if (string.IsNullOrEmpty(tag))
		{
			return null;
		}
		// 额外预设：tag 形如 "d:<gameId>"
		if (tag.StartsWith("d:", StringComparison.Ordinal))
		{
			if (long.TryParse(tag.Substring(2), out long gameId))
			{
				ServerPreset? preset = ServerPresets.All.FirstOrDefault((ServerPreset p) => p.GameId == gameId);
				if (preset != null)
				{
					return (preset.GameId, preset.Name, false);
				}
			}
			return null;
		}
		if (Servers.TryGetValue(tag, out (long, string, bool) value))
		{
			return value;
		}
		return null;
	}

	/// <summary>重建服务器下拉：内置三项（元素之诗/失落世界/自定义）+ 服务器列表页添加的额外预设。</summary>
	private void RebuildServerCombo()
	{
		string? selTag = (_serverCombo.SelectedItem as ComboBoxItem)?.Tag as string;
		_serverCombo.Items.Clear();
		foreach (KeyValuePair<string, (long, string, bool)> server in Servers)
		{
			ComboBoxItem item = new ComboBoxItem
			{
				Content = (server.Value.Item3 ? (server.Value.Item2 + "（冻结）") : server.Value.Item2),
				Tag = server.Key
			};
			_serverCombo.Items.Add(item);
			if (string.Equals(server.Key, selTag ?? "ys", StringComparison.Ordinal))
			{
				_serverCombo.SelectedItem = item;
			}
		}
		foreach (ServerPreset preset in ServerPresets.All)
		{
			if (ServerPresets.IsBuiltin(preset) || preset.IsPending)
			{
				continue;
			}
			string tag = "d:" + preset.GameId.ToString();
			ComboBoxItem item2 = new ComboBoxItem
			{
				Content = preset.Name + "（额外预设）",
				Tag = tag
			};
			_serverCombo.Items.Add(item2);
			if (string.Equals(tag, selTag, StringComparison.Ordinal))
			{
				_serverCombo.SelectedItem = item2;
			}
		}
		if (_serverCombo.SelectedItem == null && _serverCombo.Items.Count > 0)
		{
			_serverCombo.SelectedIndex = 0;
		}
		UpdateDelPresetButton();
	}

	private void UpdateDelPresetButton()
	{
		if (_delPresetBtn == null)
		{
			return;
		}
		string tag = (_serverCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
		_delPresetBtn.IsEnabled = tag.StartsWith("d:", StringComparison.Ordinal);
	}

	private async void DelPreset_Click(object sender, RoutedEventArgs e)
	{
		string tag = (_serverCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
		if (!tag.StartsWith("d:", StringComparison.Ordinal))
		{
			return;
		}
		if (!long.TryParse(tag.Substring(2), out long gameId))
		{
			return;
		}
		ServerPreset? preset = ServerPresets.All.FirstOrDefault((ServerPreset p) => p.GameId == gameId);
		if (preset == null)
		{
			RebuildServerCombo();
			return;
		}
		ContentDialog confirm = new ContentDialog
		{
			Title = "删除服务器预设",
			Content = "确定删除「" + preset.Name + "」（" + preset.Host + ":" + preset.Port + "）的预设吗？",
			PrimaryButtonText = "删除",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Close,
			RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default)
		};
		if (await DialogGate.ShowAsync(confirm) != ContentDialogResult.Primary)
		{
			return;
		}
		if (ServerPresets.Remove(preset))
		{
			MainWindow.Instance?.ShowToast("已删除预设「" + preset.Name + "」", true);
		}
		RebuildServerCombo();
		RefreshServerWarn();
	}

	private void RefreshServerWarn()
	{
		UpdateDelPresetButton();
		if (_warnText == null || _statusText == null)
		{
			return;
		}
		(long, string, bool)? tuple = Selected();
		if (tuple.HasValue && tuple.Value.Item3)
		{
			_warnText.Text = "该服（失落世界）从不更新客户端包，更新将按强制覆盖执行。";
			_warnText.Visibility = Visibility.Visible;
		}
		else
		{
			_warnText.Visibility = Visibility.Collapsed;
		}
		RefreshExtraTarget();
	}

	// 按所选服务器自动填充第二合并路径（PCL 客户端）：
	// 元素之诗 → DragonCore 目录（裁剪路径目标）；失落世界 → 拼出 PCL versions 下的实例目录；
	// 自定义 → 留空由用户手填。路径必须真实存在才填。
	private void RefreshExtraTarget()
	{
		if (_pclPathBox == null)
		{
			return;
		}
		try
		{
			string tag = (_serverCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
			string? path = tag switch
			{
				"ys" => ClientUpdater.GetDragonCoreTargetForUi(),
				"sl" => ClientUpdater.ResolvePclClientRoot(),
				_ => null
			};
			_pclPathBox.Text = path ?? "";
		}
		catch
		{
			_pclPathBox.Text = "";
		}
	}

	/// <summary>判断 URL 框里的地址是否与当前所选服务器对不上：自动获取的按记录的 gameId 比对；手动/扫描来的按 URL 内 gameId 比对。</summary>
	private bool UrlMismatchSelected()
	{
		long gameId = Selected()?.GameId ?? 0;
		string url = _urlBox.Text.Trim();
		if (url.Length == 0 || gameId <= 0)
		{
			return false;
		}
		if (_urlGameId.HasValue)
		{
			return _urlGameId.Value != gameId;
		}
		return !url.Contains(gameId.ToString(), StringComparison.Ordinal);
	}

	/// <summary>预设变化检测：切换服务器后原地址对不上 → 清空，避免「更新资源包」直接复用别家的包。</summary>
	private void ClearUrlIfPresetMismatch()
	{
		try
		{
			if (_urlBox == null || _statusText == null || !UrlMismatchSelected())
			{
				return;
			}
			_urlBox.Text = "";
			_urlGameId = null;
			_statusText.Text = "已切换服务器：原资源包地址与当前所选不匹配，已清空（点「更新资源包」将自动获取当前服务器的最新地址）";
		}
		catch
		{
		}
	}

	private void AppendLog(string msg)
	{
		_queue.TryEnqueue(delegate
		{
			TextBlock logBox = _logBox;
			logBox.Text = logBox.Text + msg + "\n";
			_logScroller.ChangeView(null, _logScroller.ScrollableHeight, null, disableAnimation: true);
		});
	}

	private void SetBusy(bool busy, string status)
	{
		_queue.TryEnqueue(delegate
		{
			_progress.Visibility = ((!busy) ? Visibility.Collapsed : Visibility.Visible);
				_updateBtn.IsEnabled = !busy;
				_scanBtn.IsEnabled = !busy;
				_checkBtn.IsEnabled = !busy;
				_cancelBtn.IsEnabled = busy;
			_statusText.Text = status;
		});
	}

	private string InstallRoot()
	{
		string text = "E:\\MCLDownload";
		if (!Directory.Exists(text))
		{
			string[] array = new string[3] { "E:\\", "D:\\", "C:\\" };
			string[] array2 = array;
			foreach (string path in array2)
			{
				if (Directory.Exists(Path.Combine(path, "MCLDownload")))
				{
					text = Path.Combine(path, "MCLDownload");
					break;
				}
			}
		}
		return text;
	}

	private void OpenDir(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = "explorer.exe",
					Arguments = "\"" + path + "\"",
					UseShellExecute = false
				});
			}
		}
		catch
		{
		}
	}

	private void OpenResPkgDir()
	{
		try
		{
			string path = ClientUpdater.ResPkgRoot();
			Directory.CreateDirectory(path);
			OpenDir(path);
			_statusText.Text = "资源包目录：" + path;
		}
		catch (Exception ex)
		{
			_statusText.Text = "打开资源包目录失败：" + ex.Message;
		}
	}

	// 自定义资源包下载目录
	private async void DlBrowse_Click()
	{
		string? dir = await DragonCoreTrimDialogs.PickFolderAsync();
		if (string.IsNullOrWhiteSpace(dir))
		{
			return;
		}
		try
		{
			SettingsManager.Instance.ResPkgDownloadDir = dir;
			SettingsManager.Save(SettingsManager.Instance);
			_dlDirBox.Text = dir;
			_statusText.Text = "下载目录已改为：" + dir + "（已有旧包不会自动搬移，可在下方扫描后按原路径使用）";
		}
		catch (Exception ex)
		{
			_statusText.Text = "保存下载目录失败：" + ex.Message;
		}
	}

	// 扫描本地资源包（下载目录 + 旧 temp 目录），MD5 校验后列出
	private async void ScanLocalPkg_Click()
	{
		_scanPkgBtn.IsEnabled = false;
		_usePkgBtn.IsEnabled = false;
		try
		{
			_statusText.Text = "扫描本地资源包中（MD5 校验大文件会花点时间）...";
			List<ClientUpdater.LocalPkgInfo> list = await Task.Run(delegate
			{
				CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(10.0));
				return ClientUpdater.ScanLocalPackagesAsync(AppendLog, cts.Token);
			});
			_pkgItems.Clear();
			_pkgList.Items.Clear();
			foreach (ClientUpdater.LocalPkgInfo item in list)
			{
				_pkgItems.Add(item);
				_pkgList.Items.Add($"{item.PackageName}   {item.Size / 1048576.0:N0} MB   {item.Modified:yyyy-MM-dd HH:mm}   MD5 {(!item.Md5Checkable ? "- 无法校验（非MD5命名）" : (item.Md5Ok ? "✓通过" : "✗不符"))}");
			}
			_pkgList.Visibility = ((list.Count > 0) ? Visibility.Visible : Visibility.Collapsed);
			_statusText.Text = ((list.Count == 0) ? "下载目录里没有资源包，点「更新资源包」先下一次" : $"共 {list.Count} 个本地资源包。选中一个点右边按钮可跳过下载直接安装/裁剪；「更新资源包」也会自动复用同版本包。");
		}
		catch (Exception ex2)
		{
			_statusText.Text = "扫描失败：" + ex2.Message;
		}
		finally
		{
			_scanPkgBtn.IsEnabled = true;
		}
	}

	// 用选中的本地包直接安装（校验 MD5 → 解压合并 → 元素之诗走 DragonCore 裁剪）
	private async void UseLocalPkg_Click()
	{
		int selectedIndex = _pkgList.SelectedIndex;
		if (selectedIndex < 0 || selectedIndex >= _pkgItems.Count)
		{
			_statusText.Text = "先在列表里选一个资源包";
			return;
		}
		ClientUpdater.LocalPkgInfo pkg = _pkgItems[selectedIndex];
		_cts?.Cancel();
		_cts = new CancellationTokenSource();
		SetBusy(busy: true, "本地包安装中...");
		try
		{
			long gameId = Selected()?.GameId ?? 0;
			string extraTarget = _pclPathBox.Text.Trim();
			await Task.Run(async delegate
			{
				await ClientUpdater.RunFromPackageAsync(pkg.Path, gameId, InstallRoot(), _snapCheck.IsChecked == true, AppendLog, _cts.Token, extraTarget);
			});
			_statusText.Text = "完成（本地包 " + pkg.PackageName + "）";
		}
		catch (OperationCanceledException)
		{
			AppendLog("已取消");
			_statusText.Text = "已取消";
		}
		catch (Exception ex3)
		{
			Exception ex4 = ex3;
			AppendLog("ERROR: " + ex4.Message);
			_statusText.Text = "失败：" + ex4.Message;
		}
		finally
		{
			SetBusy(busy: false, _statusText.Text);
		}
	}

	// 删除选中的本地资源包（移入回收站语义太重，直接删除；下载目录与 temp 残包都可删）
	private void DeleteLocalPkg_Click()
	{
		int selectedIndex = _pkgList.SelectedIndex;
		if (selectedIndex < 0 || selectedIndex >= _pkgItems.Count)
		{
			return;
		}
		ClientUpdater.LocalPkgInfo pkg = _pkgItems[selectedIndex];
		try
		{
			File.Delete(pkg.Path);
			string marker = pkg.Path + ".installed";
			if (File.Exists(marker))
			{
				File.Delete(marker);
			}
			_pkgItems.RemoveAt(selectedIndex);
			_pkgList.Items.RemoveAt(selectedIndex);
			AppendLog("已删除本地包：" + pkg.PackageName);
			_statusText.Text = "已删除：" + pkg.PackageName;
			if (_pkgItems.Count == 0)
			{
				_pkgList.Visibility = Visibility.Collapsed;
				_statusText.Text = "已删除最后一个本地包";
			}
		}
		catch (Exception ex2)
		{
			_statusText.Text = "删除失败：" + ex2.Message;
			AppendLog("删除失败: " + ex2.Message);
		}
	}

	// PCL 合并路径浏览：选 DragonCore → 记为元素之诗裁剪目标并反推同 versions 下失落实例；
	// 选版本目录/.minecraft/PCL 目录 → 自动推导失落实例
	private async void PclBrowse_Click()
	{
		string? dir = await DragonCoreTrimDialogs.PickFolderAsync();
		if (string.IsNullOrWhiteSpace(dir))
		{
			return;
		}
		string? text = ClientUpdater.NormalizePclPick(dir, AppendLog);
		if (!string.IsNullOrEmpty(text) && Directory.Exists(text))
		{
			_pclPathBox.Text = text;
			_statusText.Text = "已识别并填入：" + text;
		}
		else
		{
			_pclPathBox.Text = dir;
			_statusText.Text = "所选目录结构未识别出实例目录，已原样填入（可手动调整）";
		}
	}

	private async void DragonCoreTrim_Click(object sender, RoutedEventArgs e)
	{
		if (DragonCoreTrimDialogs.Busy || _dragonTrimBtn == null || _dragonTrimBtn.IsEnabled == false)
		{
			return;
		}
		SetDragonButtons(busy: true);
		try
		{
			await DragonCoreTrimDialogs.ShowTrimFlowAsync(XamlRoot);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "DragonCore 裁剪流程异常");
			_statusText.Text = "裁剪流程异常：" + ex.Message;
		}
		finally
		{
			SetDragonButtons(busy: false);
		}
	}

	private async void DragonCoreMerge_Click(object sender, RoutedEventArgs e)
	{
		if (DragonCoreTrimDialogs.Busy || _dragonMergeBtn == null || _dragonMergeBtn.IsEnabled == false)
		{
			return;
		}
		SetDragonButtons(busy: true);
		try
		{
			await DragonCoreTrimDialogs.ShowMergeFlowAsync(XamlRoot);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "DragonCore 合并流程异常");
			_statusText.Text = "合并流程异常：" + ex.Message;
		}
		finally
		{
			SetDragonButtons(busy: false);
		}
	}

	private void SetDragonButtons(bool busy)
	{
		_dragonTrimBtn.IsEnabled = !busy;
		_dragonMergeBtn.IsEnabled = !busy;
	}

	// 诊断拉取：把下拉里所有服务器（内置 + 额外预设，跳过「自定义」）逐个走一遍
	// 「会话刷新 → 取 item 下载地址 → 下载 .7z」，验证其它服务器的客户端包能否拉取；
	// 成功的包存到 程序目录\MhyunBox_Clients\<服务器>\ 下，取不到的在日志里说明原因。
	private async Task TryPullAllServersAsync()
	{
		if (_pullAllBtn == null)
		{
			return;
		}
		_cts?.Cancel();
		CancellationTokenSource cts = new CancellationTokenSource();
		_cts = cts;
		SetBusy(busy: true, "逐服拉取客户端包（诊断）中...");
		try
		{
			List<(long GameId, string Name)> targets = new List<(long, string)>();
			foreach (KeyValuePair<string, (long, string, bool)> s in Servers)
			{
				if (s.Value.Item1 > 0)
				{
					targets.Add((s.Value.Item1, s.Value.Item2));
				}
			}
			foreach (ServerPreset p in ServerPresets.All)
			{
				if (!ServerPresets.IsBuiltin(p) && !p.IsPending && !targets.Any((t) => t.GameId == p.GameId))
				{
					targets.Add((p.GameId, p.Name));
				}
			}
			List<AccountStore.LocalAccount> accounts = AccountStore.All.Where((AccountStore.LocalAccount a) => !string.IsNullOrWhiteSpace(a.CookieJson)).Take(3).ToList();
			if (accounts.Count == 0)
			{
				_statusText.Text = "尚未绑定网易账号，请先到「账号绑定」页完成绑定后再试";
				return;
			}
			string root = Path.Combine(AppContext.BaseDirectory, "MhyunBox_Clients");
			Directory.CreateDirectory(root);
			int okCount = 0;
			foreach ((long gameId, string name) in targets)
			{
				cts.Token.ThrowIfCancellationRequested();
				AppendLog($"[诊断] ===== {name}（gameId={gameId}）=====");
				string? url = null;
				foreach (AccountStore.LocalAccount acc in accounts)
				{
					NeteaseLoginResult session;
					using (NeteaseLogin login = new NeteaseLogin())
					{
						session = await login.LoginWithCookieAsync(acc.CookieJson, acc.Channel);
					}
					if (!session.Success || string.IsNullOrWhiteSpace(session.EntityId) || string.IsNullOrWhiteSpace(session.Token))
					{
						AppendLog("[诊断] 会话刷新失败(" + AccountStore.Mask(acc.Account) + ")：" + session.Message);
						continue;
					}
					url = await NeteaseItemService.FetchLatestItemUrlAsync(gameId, session.EntityId, session.Token, AppendLog, cts.Token);
					if (!string.IsNullOrWhiteSpace(url))
					{
						break;
					}
				}
				if (string.IsNullOrWhiteSpace(url))
				{
					AppendLog("[诊断] ✗ " + name + "：取不到客户端包地址（账号可能未包含该服，或该服没有可下载的 item 包）");
					continue;
				}
				string dir = Path.Combine(root, string.Join("", name.Split(Path.GetInvalidFileNameChars())));
				Directory.CreateDirectory(dir);
				string fileName = (url.Contains('/') ? url.Substring(url.LastIndexOf('/') + 1) : ("item_" + gameId + ".7z"));
				string dest = Path.Combine(dir, fileName);
				AppendLog("[诊断] ↓ 下载 " + name + " 客户端包 → " + dest);
				using (HttpClient http = new HttpClient())
				{
					using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
					resp.EnsureSuccessStatusCode();
					long total = resp.Content.Headers.ContentLength ?? -1L;
					using (Stream src = await resp.Content.ReadAsStreamAsync(cts.Token))
					{
						using (FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
						{
							byte[] buf = new byte[262144];
							long done = 0L;
							long nextLog = 0L;
							int n;
							while ((n = await src.ReadAsync(buf, cts.Token)) > 0)
							{
								await fs.WriteAsync(buf.AsMemory(0, n), cts.Token);
								done += n;
								if (total > 0 && done >= nextLog)
								{
									AppendLog($"[诊断] {name} {done / 1048576.0:F1} / {total / 1048576.0:F1} MB");
									nextLog = done + 209715200L;
								}
							}
						}
					}
				}
				AppendLog($"[诊断] ✓ {name} 客户端包已下载：{new FileInfo(dest).Length / 1048576.0:F1} MB");
				okCount++;
			}
			_statusText.Text = "诊断完成：成功拉取 " + okCount + " / " + targets.Count + " 个服务器客户端包（存于 MhyunBox_Clients\\，详见下方日志）";
		}
		catch (OperationCanceledException)
		{
			AppendLog("已取消");
			_statusText.Text = "已取消";
		}
		catch (Exception ex)
		{
			AppendLog("ERROR: " + ex.Message);
			_statusText.Text = "拉取失败：" + ex.Message;
		}
		finally
		{
			SetBusy(busy: false, _statusText.Text);
		}
	}

	// 自动获取最新 item 更新地址（对应 EchooQt「脱盒」从网易侧请求资源包地址的能力）：
	// 账号绑定页已绑定的网易账号 → LoginWithCookieAsync 刷新游戏会话(EntityId/Token) →
	// NeteaseItemService 调网易 API 网关 /user-item-download-v2 拿当前有效的下载 URL。
	// 返回 null 表示未取到（调用方提示并结束）。
	private async Task<string?> FetchLatestUrlCoreAsync(CancellationToken ct)
	{
		(long, string, bool)? sel = Selected();
		long gameId = sel?.Item1 ?? 0;
		if (gameId <= 0)
		{
			_statusText.Text = "「自定义」没有固定 gameId，无法自动获取；请选具体服务器或手动粘贴 URL";
			return null;
		}
		List<AccountStore.LocalAccount> accounts = AccountStore.All.Where((AccountStore.LocalAccount a) => !string.IsNullOrWhiteSpace(a.CookieJson)).Take(3).ToList();
		if (accounts.Count == 0)
		{
			_statusText.Text = "尚未绑定网易账号，请先到「账号绑定」页完成绑定后再更新资源包";
			return null;
		}
		foreach (AccountStore.LocalAccount acc in accounts)
		{
			ct.ThrowIfCancellationRequested();
			AppendLog($"[自动获取] 使用账号 {acc.Nickname}/{acc.Account} 刷新会话...");
			NeteaseLoginResult session;
			using (NeteaseLogin login = new NeteaseLogin())
			{
				session = await login.LoginWithCookieAsync(acc.CookieJson, acc.Channel);
			}
			if (!session.Success || string.IsNullOrWhiteSpace(session.EntityId) || string.IsNullOrWhiteSpace(session.Token))
			{
				AppendLog("[自动获取] 会话刷新失败：" + session.Message);
				continue;
			}
			string? url = await NeteaseItemService.FetchLatestItemUrlAsync(gameId, session.EntityId, session.Token, AppendLog, ct);
			if (!string.IsNullOrWhiteSpace(url))
			{
				return url;
			}
		}
		_statusText.Text = "所有已绑定账号均未能获取该服务器的最新地址（详见日志；可改用手动粘贴或「扫描抓包日志」）";
		return null;
	}

	// 「检测地址」：只做 HEAD 探测，不下载 —— 回报该资源包的更新时间，并判断本机是否已是最新
	private async Task CheckUrlAsync()
	{
		string url = _urlBox.Text.Trim();
		if (url.Length == 0)
		{
			_statusText.Text = "请先填入 Item 更新包 URL（或点「扫描抓包日志」自动填入）";
			return;
		}
		_cts?.Cancel();
		CancellationTokenSource cts = new CancellationTokenSource();
		_cts = cts;
		SetBusy(busy: true, "检测中...");
		try
		{
			string result = await Task.Run(async delegate
			{
				return await ClientUpdater.CheckAsync(url, InstallRoot(), AppendLog, cts.Token);
			});
			string[] lines = result.Split(new char[1] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
			string updateLine = lines.FirstOrDefault((string l) => l.Contains("更新时间")) ?? "";
			string verLine = lines.FirstOrDefault((string l) => l.Contains("已是最新") || l.Contains("有新版本") || l.Contains("尚未安装")) ?? "";
			_statusText.Text = string.Join("  |  ", new string[2] { updateLine.Trim(), verLine.Trim() }.Where((string s) => s.Length > 0));
			AppendLog(result);
		}
		catch (OperationCanceledException)
		{
			AppendLog("已取消");
			_statusText.Text = "已取消";
		}
		catch (Exception ex)
		{
			AppendLog("ERROR: " + ex.Message);
			_statusText.Text = "检测失败：" + ex.Message;
		}
		finally
		{
			SetBusy(busy: false, _statusText.Text);
		}
	}

	// 一键「更新资源包」：输入框为空 → 自动向网易取最新地址；然后下载 → 校验 →
	// 元素之诗走 DragonCore 裁剪（对比/去重/清冗余/桌面导出增量包），失落世界等直接覆盖合并。
	private async Task RunResourcePackAsync()
	{
		_cts?.Cancel();
		_cts = new CancellationTokenSource();
		SetBusy(busy: true, "更新资源包中...");
		try
		{
			long gameId = Selected()?.GameId ?? 0;
			string url = _urlBox.Text.Trim();
			// 预设变化检测：地址与当前所选服务器对不上（切换预设前的残留/手滑粘了别服链接）→ 不复用，重新获取
			if (url.Length > 0 && UrlMismatchSelected())
			{
				AppendLog("⚠️ 现有地址与当前所选服务器不匹配（疑似其它服务器的旧地址），已清空并自动重新获取");
				_urlBox.Text = "";
				_urlGameId = null;
				url = "";
			}
			if (url.Length == 0)
			{
				_statusText.Text = "URL 为空，正在向网易自动获取最新地址...";
				string? fetched = await FetchLatestUrlCoreAsync(_cts.Token);
				if (string.IsNullOrWhiteSpace(fetched))
				{
					return;
				}
				_urlBox.Text = fetched;
				_urlGameId = gameId;
				url = fetched;
				_statusText.Text = "已自动获取最新地址，开始更新...";
			}
			bool snap = _snapCheck.IsChecked == true;
			string extraTarget = _pclPathBox.Text.Trim();
			// 卡顿提醒：下载/解压占满带宽与磁盘 IO，游戏可能卡顿掉线
			AppendLog("⚠️ 提醒：接下来下载（16 线程）与解压会占满带宽和磁盘，正在运行的游戏可能出现卡顿/掉线，建议先退出游戏，等更新完成再进服。");
			MainWindow.Instance?.ShowToast("下载期间游戏可能卡顿，建议先退出游戏，更新完成后再进服");
			await Task.Run(async delegate
			{
				await ClientUpdater.RunAsync(url, gameId, InstallRoot(), force: true, snap, AppendLog, _cts.Token, extraTarget);
			});
			_statusText.Text = "完成";
		}
		catch (OperationCanceledException)
		{
			AppendLog("已取消");
			_statusText.Text = "已取消";
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			AppendLog("ERROR: " + ex3.Message);
			_statusText.Text = "失败：" + ex3.Message;
		}
		finally
		{
			SetBusy(busy: false, _statusText.Text);
		}
	}

	private void ScanCaptures()
	{
		List<string> paths = new List<string>();
		string baseDirectory = AppContext.BaseDirectory;
		string text = Path.Combine(baseDirectory, "ProxyCaptures");
		if (Directory.Exists(text))
		{
			paths.Add(text);
		}
		string text2 = Path.Combine(baseDirectory, "MhyunBox_Logs");
		if (Directory.Exists(text2))
		{
			paths.Add(text2);
		}
		if (paths.Count == 0)
		{
			_statusText.Text = "没有找到 ProxyCaptures 目录";
			return;
		}
		SetBusy(busy: true, "扫描抓包日志...");
		_queue.TryEnqueue(delegate
		{
			try
			{
				string text3 = ClientUpdater.Scan(string.Join(";", paths), AppendLog);
				(long GameId, string Name, bool Frozen)? sel = Selected();
				string text4 = (from l in text3.Split('\n')
					select l.Trim()).FirstOrDefault((string l) => l.StartsWith("http") && (!sel.HasValue || sel.Value.GameId <= 0 || l.Contains(sel.Value.GameId.ToString())));
				if (text4 != null)
				{
					_urlBox.Text = text4;
					_urlGameId = null;
					_statusText.Text = "已自动填入找到的 URL，点「更新资源包」即可";
				}
				else if (text3.Contains("http"))
				{
					_statusText.Text = "扫描完成，未匹配所选服务器的 URL（请选“自定义”再扫）";
				}
				else
				{
					_statusText.Text = "扫描完成，未发现 item_ URL";
				}
			}
			catch (Exception ex)
			{
				_statusText.Text = "扫描失败：" + ex.Message;
			}
			finally
			{
				SetBusy(busy: false, "");
			}
		});
	}
}

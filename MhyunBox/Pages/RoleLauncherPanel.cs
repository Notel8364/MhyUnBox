using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using MhyunBox.Services;
using MhyunBox.Services.BjdProxy;
using MhyunBox.Settings;

namespace MhyunBox.Pages;

/// <summary>
/// 首页 / 代理启动 / 快速启动 共用的「客户端配置与启动」区块：
///
/// - 下拉里列出服务器预设 + 额外启动卡，末尾有「＋ 新建启动卡…」；
/// - 切换预设时同步切换该预设保存的客户端目录 / 直连地址；
/// - 布吉岛预设：启动假启动器 + 认证代理进服（每个多开的客户端各自独立）；
/// - 其它预设 / 启动卡：只按配置里的客户端直连地址启动对应客户端；
/// - 允许多开，不再限制同一时刻只能有一个客户端。
/// </summary>
public sealed class RoleLauncherPanel
{
	private const string AddCardItem = "＋ 新建启动卡…";

	private const string NoPresetItem = "（不指定）";

	private const string CardPrefix = "启动卡 · ";

	private static readonly object Gate = new object();

	private static bool _busy;

	private readonly StackPanel _root = new StackPanel
	{
		Spacing = 6.0
	};

	/// <summary>内存 / Java 参数配置区：默认不挂在客户端配置卡里，
	/// 由「启动客户端」页单独放到下方卡片（构造参数 includeTuningInRoot=true 时也可内嵌）。</summary>
	private readonly StackPanel _tuningRoot = new StackPanel
	{
		Spacing = 6.0
	};

	public FrameworkElement TuningRoot => _tuningRoot;

	private readonly ComboBox _entryBox = new ComboBox
	{
		MinWidth = 280.0,
		HorizontalAlignment = HorizontalAlignment.Left
	};

	private readonly TextBox _clientBox = new TextBox
	{
		Width = 360.0,
		PlaceholderText = "客户端 .minecraft 目录（留空自动查找）"
	};

	/// <summary>版本目录（versions\&lt;name&gt;）；自动 = 由启动器挑最合适的版本。</summary>
	private readonly ComboBox _versionBox = new ComboBox
	{
		MinWidth = 280.0,
		HorizontalAlignment = HorizontalAlignment.Left
	};

	private readonly TextBox _directBox = new TextBox
	{
		Width = 300.0,
		PlaceholderText = "例如 19198"
	};

	/// <summary>JRE 下拉（2026-11-20 新增，对齐 PCL：列出本机找到的全部 JRE/JDK，自动 = 按版本需求自动选）。</summary>
	private readonly ComboBox _jreBox = new ComboBox
	{
		MinWidth = 280.0,
		HorizontalAlignment = HorizontalAlignment.Left
	};

	/// <summary>自选 JRE 解释器路径（兜底；留空则用下拉选择/自动）。</summary>
	private readonly TextBox _jreCustomBox = new TextBox
	{
		Width = 300.0,
		PlaceholderText = "自选 JRE 解释器路径（留空则用上方选择/自动）"
	};

	/// <summary>
	/// 内存配置（对齐 PCL）：全局配置 / 自动配置 / 自定义，右侧总内存拖动条。
	/// </summary>
	private readonly RadioButton _memGlobal = new RadioButton { Content = "全局配置", GroupName = "mhy_mem_mode", FontSize = 12.5 };
	private readonly RadioButton _memAuto = new RadioButton { Content = "自动配置", GroupName = "mhy_mem_mode", FontSize = 12.5 };
	private readonly RadioButton _memCustom = new RadioButton { Content = "自定义", GroupName = "mhy_mem_mode", FontSize = 12.5 };
	private readonly Slider _memSlider = new Slider { Minimum = 1.0, Maximum = 32.0, StepFrequency = 0.5, Width = 240.0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
	private readonly TextBlock _memLabel = new TextBlock { FontSize = 12.0, Opacity = 0.85, MinWidth = 330.0, HorizontalAlignment = HorizontalAlignment.Left, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
	private DispatcherQueueTimer? _memSaveTimer;
	private bool _syncingMem;

	/// <summary>附加 Java 参数（追加到 -Xmx 之后、主类之前）。</summary>
	private readonly TextBox _jvmArgsBox = new TextBox
	{
		Width = 420.0,
		PlaceholderText = "附加 Java 参数（空格分隔，如 -XX:+UseG1GC -Dfoo=bar）"
	};

	private bool _syncingJava;

	private readonly TextBlock _status = new TextBlock
	{
		FontSize = 12.0,
		Opacity = 0.8,
		TextWrapping = TextWrapping.Wrap
	};

	private readonly Button _launch = new Button
	{
		Content = "启动客户端"
	};

	private readonly Button _stop = new Button
	{
		Content = "关闭客户端",
		IsEnabled = false
	};

	private readonly HyperlinkButton _deleteCard = new HyperlinkButton
	{
		Content = "删除启动卡",
		FontSize = 12.0,
		VerticalAlignment = VerticalAlignment.Center,
		Visibility = Visibility.Collapsed
	};

	/// <summary>启动卡「自启动客户端」：程序启动时按这张卡的配置自动拉起客户端。</summary>
	private readonly CheckBox _autoStartCardCheck = new CheckBox
	{
		Content = "自启动客户端",
		FontSize = 12.5,
		VerticalAlignment = VerticalAlignment.Center,
		Visibility = Visibility.Collapsed
	};

	private readonly DispatcherQueueTimer? _timer;

	/// <summary>与下拉项一一对应的显示文本。</summary>
	private List<string> _items = new List<string>();

	/// <summary>当前选中的显示文本（下拉重建后用它恢复选择）。</summary>
	private string _selected = "";

	private int _lastValidIndex;

	private bool _syncing;

	public FrameworkElement Root => _root;

	public RoleLauncherPanel(bool includeTuningInRoot = false)
	{
		_root.Children.Add(new TextBlock
		{
			Text = "客户端配置与启动",
			FontWeight = FontWeights.SemiBold,
			FontSize = 14.0
		});

		StackPanel entryRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		entryRow.Children.Add(_entryBox);
		entryRow.Children.Add(_launch);
		entryRow.Children.Add(_stop);
		entryRow.Children.Add(_deleteCard);
		ToolTipService.SetToolTip(_autoStartCardCheck, "程序启动后自动按这张启动卡的配置拉起客户端（每张卡独立）。");
		entryRow.Children.Add(_autoStartCardCheck);
		_root.Children.Add(entryRow);

		StackPanel clientRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		clientRow.Children.Add(new TextBlock
		{
			Text = "客户端目录",
			FontSize = 12.0,
			Opacity = 0.7,
			VerticalAlignment = VerticalAlignment.Center
		});
		clientRow.Children.Add(_clientBox);
		Button pickClient = new Button
		{
			Content = "选择…"
		};
		pickClient.Click += PickClient_Click;
		clientRow.Children.Add(pickClient);
		_root.Children.Add(clientRow);

		// —— 版本目录：自动 = 启动器自选；机器人宿主必须选带 MythosNexus 的挂号版 ——
		StackPanel versionRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		versionRow.Children.Add(new TextBlock
		{
			Text = "版本目录",
			FontSize = 12.0,
			Opacity = 0.7,
			VerticalAlignment = VerticalAlignment.Center,
			MinWidth = 58.0
		});
		versionRow.Children.Add(_versionBox);
		Button refreshVersion = new Button
		{
			Content = "刷新"
		};
		ToolTipService.SetToolTip(refreshVersion, "重新扫描客户端目录下的 versions。机器人只能挂在带 MythosNexus 的挂号版里，选普通版会挂不进游戏。");
		refreshVersion.Click += (object s, RoutedEventArgs e) => ReloadVersionOptions(SelectedVersionName());
		versionRow.Children.Add(refreshVersion);
		_root.Children.Add(versionRow);

		StackPanel directRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		directRow.Children.Add(new TextBlock
		{
			Text = "直连端口",
			FontSize = 12.0,
			Opacity = 0.7,
			VerticalAlignment = VerticalAlignment.Center
		});
		directRow.Children.Add(_directBox);
		_root.Children.Add(directRow);

		// —— Java 运行时（2026-11-20）：下拉列出本机全部 JRE（自动 = 默认自动选），自选路径兜底 ——
		StackPanel jreRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		jreRow.Children.Add(new TextBlock
		{
			Text = "JRE",
			FontSize = 12.0,
			Opacity = 0.7,
			VerticalAlignment = VerticalAlignment.Center,
			MinWidth = 58.0
		});
		jreRow.Children.Add(_jreBox);
		Button refreshJre = new Button
		{
			Content = "重新扫描"
		};
		ToolTipService.SetToolTip(refreshJre, "重新扫描本机的 JRE/JDK 列表（与 PCL 相同的搜索面）。");
		refreshJre.Click += (object s, RoutedEventArgs e) => _ = LoadJreListAsync();
		jreRow.Children.Add(refreshJre);
		_root.Children.Add(jreRow);

		StackPanel jreCustomRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		jreCustomRow.Children.Add(new TextBlock
		{
			Text = "自选路径",
			FontSize = 12.0,
			Opacity = 0.7,
			VerticalAlignment = VerticalAlignment.Center,
			MinWidth = 58.0
		});
		jreCustomRow.Children.Add(_jreCustomBox);
		Button pickJre = new Button
		{
			Content = "选择…"
		};
		ToolTipService.SetToolTip(pickJre, "手动指定 JRE 目录（bin 的上级或 bin 均可）。填了优先用这里；留空则用下拉选择，都没填走自动。");
		pickJre.Click += PickJre_Click;
		jreCustomRow.Children.Add(pickJre);
		_root.Children.Add(jreCustomRow);

		// —— 内存配置（对齐 PCL）：左侧三种模式从上到下，右侧总内存拖动条 ——
		Grid memGrid = new Grid
		{
			ColumnSpacing = 14.0,
			Margin = new Thickness(0.0, 2.0, 0.0, 2.0)
		};
		memGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		memGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		StackPanel memModes = new StackPanel { Spacing = 2.0 };
		memModes.Children.Add(new TextBlock
		{
			Text = "内存大小",
			FontSize = 12.0,
			Opacity = 0.7
		});
		memModes.Children.Add(_memGlobal);
		memModes.Children.Add(_memAuto);
		memModes.Children.Add(_memCustom);
		Grid.SetColumn(memModes, 0);
		memGrid.Children.Add(memModes);
		StackPanel memSliderPanel = new StackPanel
		{
			Spacing = 2.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		memSliderPanel.Children.Add(_memSlider);
		memSliderPanel.Children.Add(_memLabel);
		Grid.SetColumn(memSliderPanel, 1);
		memGrid.Children.Add(memSliderPanel);
		_tuningRoot.Children.Add(memGrid);

		// —— 启动参数（附加 Java 参数，追加在 -Xmx 之后、主类之前）——
		StackPanel jvmArgsRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		jvmArgsRow.Children.Add(new TextBlock
		{
			Text = "Java 参数",
			FontSize = 12.0,
			Opacity = 0.7,
			VerticalAlignment = VerticalAlignment.Center,
			MinWidth = 58.0
		});
		jvmArgsRow.Children.Add(_jvmArgsBox);
		Button resetJvm = new Button
		{
			Content = "恢复推荐"
		};
		ToolTipService.SetToolTip(resetJvm, "清空附加参数，恢复内置的 G1GC 推荐参数。");
		resetJvm.Click += (object s, RoutedEventArgs e) =>
		{
			_jvmArgsBox.Text = "";
			SaveJvmArgs();
		};
		jvmArgsRow.Children.Add(resetJvm);
		_tuningRoot.Children.Add(jvmArgsRow);

		if (includeTuningInRoot)
		{
			_root.Children.Add(_tuningRoot);
		}
		_root.Children.Add(_status);

		_launch.Click += Launch_Click;
		_stop.Click += Stop_Click;
		_entryBox.SelectionChanged += EntryBox_SelectionChanged;
		_clientBox.LostFocus += ConfigBox_LostFocus;
		_directBox.LostFocus += ConfigBox_LostFocus;
		_versionBox.SelectionChanged += VersionBox_SelectionChanged;
		_jreCustomBox.LostFocus += JreCustomBox_LostFocus;
		_jreBox.SelectionChanged += JreBox_SelectionChanged;
		_jvmArgsBox.LostFocus += (object s, RoutedEventArgs e) => SaveJvmArgs();
		_memGlobal.Checked += MemMode_Checked;
		_memAuto.Checked += MemMode_Checked;
		_memCustom.Checked += MemMode_Checked;
		_memSlider.ValueChanged += MemSlider_ValueChanged;
		_deleteCard.Click += DeleteCard_Click;
		_autoStartCardCheck.Click += AutoStartCardCheck_Click;
		ThemeHelper.SolidComboPopup(_entryBox);
		ThemeHelper.SolidComboPopup(_jreBox);
		ThemeHelper.SolidComboPopup(_versionBox);
		_ = LoadJreListAsync();
		InitMemoryUi();
		_jreCustomBox.Text = SettingsManager.Instance.OfficialClientJavaHome ?? "";
		_jvmArgsBox.Text = SettingsManager.Instance.OfficialClientJvmArgs ?? "";

		DispatcherQueue? queue = MainWindow.UIQueue;
		if (queue != null)
		{
			_timer = queue.CreateTimer();
			_timer.Interval = TimeSpan.FromSeconds(2.0);
			_timer.Tick += delegate
			{
				Refresh();
			};
			_timer.Start();
		}
		_root.Loaded += delegate
		{
			_timer?.Start();
		};
		_root.Unloaded += delegate
		{
			_timer?.Stop();
		};
		Refresh();
	}

	private static List<ServerPreset> Presets()
	{
		try
		{
			return ServerPresets.All.Where((ServerPreset p) => !p.IsPending).ToList();
		}
		catch
		{
			return new List<ServerPreset>();
		}
	}

	private static List<ClientLaunchCard> Cards()
	{
		try
		{
			return SettingsManager.Instance.ClientLaunchCards ?? new List<ClientLaunchCard>();
		}
		catch
		{
			return new List<ClientLaunchCard>();
		}
	}

	/// <summary>该预设当前在跑的代理端口（0=没有）。</summary>
	public static int ResolveProxyPort(string host)
	{
		try
		{
			foreach (ProxyCaptureService.ActiveBindingInfo b in ProxyCaptureService.ActiveBindingsSnapshot())
			{
				if (!string.IsNullOrWhiteSpace(host) && string.Equals(b.TargetHost, host, StringComparison.OrdinalIgnoreCase))
				{
					return b.ListenPort;
				}
			}
		}
		catch
		{
		}
		return 0;
	}

	/// <summary>直连输入只填端口（纯数字）时，自动用预设的内网地址补全 host；否则原样返回。</summary>
	private static string ExpandDirect(ServerPreset? preset, string text)
	{
		string s = (text ?? "").Trim();
		if (s.Length == 0 || !int.TryParse(s, out int port) || port <= 0 || port > 65535)
		{
			return s;
		}
		string host = preset?.Host ?? "";
		return (host.Length > 0) ? (host + ":" + port) : (":" + port);
	}

	/// <summary>直连地址如果是本机回环地址（127.0.0.1:端口），返回该端口；否则 0。</summary>
	public static int ResolveLoopbackPort(string directAddress)
	{
		try
		{
			string s = (directAddress ?? "").Trim();
			int idx = s.LastIndexOf(':');
			if (idx <= 0)
			{
				return 0;
			}
			string host = s.Substring(0, idx).Trim();
			if (!(string.Equals(host, "127.0.0.1", StringComparison.Ordinal) || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)))
			{
				return 0;
			}
			if (!int.TryParse(s.Substring(idx + 1).Trim(), out int port))
			{
				return 0;
			}
			return (port > 0 && port <= 65535) ? port : 0;
		}
		catch
		{
			return 0;
		}
	}

	private string SelectedDisplay()
	{
		return (_entryBox.SelectedItem is string s) ? s : "";
	}

	private void Refresh()
	{
		try
		{
			List<ServerPreset> presets = Presets();
			List<ClientLaunchCard> cards = Cards();
			List<string> names = presets.Select((ServerPreset p) => p.Name).ToList();
			names.AddRange(cards.Select((ClientLaunchCard c) => CardPrefix + c.Name));
			names.Add(AddCardItem);

			string want = _selected;
			if (want.Length == 0)
			{
				AppSettings settings = SettingsManager.Instance;
				want = (!string.IsNullOrWhiteSpace(settings.OfficialClientPreset) && names.Contains(settings.OfficialClientPreset))
					? settings.OfficialClientPreset
					: ((names.Count > 1) ? names[0] : "");
			}
			bool same = names.Count == _items.Count;
			if (same)
			{
				for (int i = 0; i < names.Count; i++)
				{
					if (!string.Equals(names[i], _items[i], StringComparison.Ordinal))
					{
						same = false;
						break;
					}
				}
			}
			if (!same)
			{
				_syncing = true;
				_entryBox.Items.Clear();
				foreach (string n in names)
				{
					_entryBox.Items.Add(n);
				}
				_items = names;
				int idx = names.IndexOf(want);
				if (idx < 0 || idx >= names.Count - 1) idx = (names.Count > 1) ? 0 : -1;
				if (idx >= 0)
				{
					_entryBox.SelectedIndex = idx;
				}
				_syncing = false;
				if (idx >= 0)
				{
					_selected = names[idx];
					_lastValidIndex = idx;
					LoadCurrentToUi();
				}
			}

			bool hasEntry = TryGetEntry(out ServerPreset? preset, out ClientLaunchCard? card);
			_deleteCard.Visibility = (hasEntry && card != null) ? Visibility.Visible : Visibility.Collapsed;
			_autoStartCardCheck.Visibility = hasEntry ? Visibility.Visible : Visibility.Collapsed;
			if (hasEntry)
			{
				_autoStartCardCheck.IsChecked = (card != null)
					? card.AutoStart
					: IsPresetAutoStart(preset?.Name ?? "");
			}
			_launch.IsEnabled = !_busy && TryGetEntry(out _, out _);
			_stop.IsEnabled = !_busy && OfficialClientLauncher.RunningCount > 0;
			UpdateStatus(preset, card);
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动客户端] 刷新失败");
		}
	}

	/// <summary>解析当前下拉项：预设项返回 preset；启动卡项返回 card（card.PresetName 可能为空）。</summary>
	private bool TryGetEntry(out ServerPreset? preset, out ClientLaunchCard? card)
	{
		preset = null;
		card = null;
		string display = SelectedDisplay();
		if (display.Length == 0 || display == AddCardItem)
		{
			return false;
		}
		if (display.StartsWith(CardPrefix, StringComparison.Ordinal))
		{
			string name = display.Substring(CardPrefix.Length);
			card = Cards().FirstOrDefault((ClientLaunchCard c) => string.Equals(c.Name, name, StringComparison.Ordinal));
			if (card == null)
			{
				return false;
			}
			preset = ServerPresets.Find(card.PresetName ?? "");
			return true;
		}
		preset = ServerPresets.Find(display);
		return preset != null;
	}

	private bool IsBjd(ServerPreset? preset)
	{
		try
		{
			return BjdGate.IsBjd(preset);
		}
		catch
		{
			return false;
		}
	}

	private void UpdateStatus(ServerPreset? preset, ClientLaunchCard? card)
	{
		int running = OfficialClientLauncher.RunningCount;
		if (running > 0)
		{
			IReadOnlyList<int> pids = OfficialClientLauncher.RunningPids;
			_status.Text = "客户端运行中：" + running + " 个" + ((pids.Count > 0) ? ("（pid " + string.Join(" / ", pids) + "）") : "");
			return;
		}
		if (preset == null && card == null)
		{
			_status.Text = "请选择服务器预设或启动卡";
			return;
		}
		if (IsBjd(preset))
		{
			int port = ResolveLoopbackPort((_directBox.Text ?? "").Trim());
			if (port == 0)
			{
				port = ResolveProxyPort(preset!.Host);
			}
			_status.Text = (port > 0)
				? ("「" + preset!.Name + "」代理 127.0.0.1:" + port + "，点「启动客户端」脱盒进服")
				: ("「" + preset!.Name + "」暂无运行中的代理，请先启动该服务器代理");
			return;
		}
		string label = (card != null) ? ("启动卡「" + card.Name + "」") : ("「" + preset!.Name + "」");
		string direct = ExpandDirect(preset, (_directBox.Text ?? "").Trim());
		// 没有保存具体直连地址时不显示代理信息，只说明会停在主菜单。
		_status.Text = (direct.Length > 0) ? (label + "直连 " + direct) : (label + "未保存直连地址（启动后停在主菜单）");
	}

	private void LoadCurrentToUi()
	{
		if (!TryGetEntry(out ServerPreset? preset, out ClientLaunchCard? card))
		{
			return;
		}
		_syncing = true;
		try
		{
			if (card != null)
			{
				_clientBox.Text = card.ClientRoot ?? "";
				_directBox.Text = card.DirectAddress ?? "";
				ReloadVersionOptions(card.VersionDir ?? "");
				return;
			}
			AppSettings settings = SettingsManager.Instance;
			string root = "";
			if (settings.OfficialClientRootByPreset != null && settings.OfficialClientRootByPreset.TryGetValue(preset!.Name, out string? saved))
			{
				root = saved ?? "";
			}
			if (root.Length == 0)
			{
				root = settings.OfficialClientRoot ?? "";
			}
			_clientBox.Text = root;
			_directBox.Text = (settings.OfficialClientDirectByPreset != null && settings.OfficialClientDirectByPreset.TryGetValue(preset.Name, out string? direct)) ? (direct ?? "") : "";
			string version = (settings.OfficialClientVersionByPreset != null && settings.OfficialClientVersionByPreset.TryGetValue(preset.Name, out string? savedVersion)) ? (savedVersion ?? "") : "";
			ReloadVersionOptions(version);
		}
		finally
		{
			_syncing = false;
		}
	}

	private void SaveCurrentConfig()
	{
		if (_syncing || !TryGetEntry(out ServerPreset? preset, out ClientLaunchCard? card))
		{
			return;
		}
		try
		{
			string root = (_clientBox.Text ?? "").Trim();
			string direct = (_directBox.Text ?? "").Trim();
			string version = SelectedVersionName();
			AppSettings settings = SettingsManager.Instance;
			if (card != null)
			{
				if (string.Equals(card.ClientRoot ?? "", root, StringComparison.Ordinal)
					&& string.Equals(card.DirectAddress ?? "", direct, StringComparison.Ordinal)
					&& string.Equals(card.VersionDir ?? "", version, StringComparison.Ordinal))
				{
					return;
				}
				card.ClientRoot = root;
				card.DirectAddress = direct;
				card.VersionDir = version;
				SettingsManager.Save(settings);
				LogHelper.Log("[启动客户端] 启动卡「" + card.Name + "」配置已保存（客户端目录=" + (root.Length == 0 ? "(自动)" : root)
					+ "，版本=" + (version.Length == 0 ? "(自动)" : version) + "，直连=" + (direct.Length == 0 ? "(无)" : direct) + "）");
				return;
			}
			settings.OfficialClientRootByPreset ??= new Dictionary<string, string>();
			settings.OfficialClientDirectByPreset ??= new Dictionary<string, string>();
			string name = preset!.Name;
			bool changed = false;
			string oldRoot = settings.OfficialClientRootByPreset.TryGetValue(name, out string? r0) ? (r0 ?? "") : "";
			if (!string.Equals(oldRoot, root, StringComparison.Ordinal))
			{
				if (root.Length == 0)
				{
					settings.OfficialClientRootByPreset.Remove(name);
				}
				else
				{
					settings.OfficialClientRootByPreset[name] = root;
				}
				if (root.Length > 0)
				{
					settings.OfficialClientRoot = root;
				}
				changed = true;
			}
			string oldDirect = settings.OfficialClientDirectByPreset.TryGetValue(name, out string? d0) ? (d0 ?? "") : "";
			if (!string.Equals(oldDirect, direct, StringComparison.Ordinal))
			{
				if (direct.Length == 0)
				{
					settings.OfficialClientDirectByPreset.Remove(name);
				}
				else
				{
					settings.OfficialClientDirectByPreset[name] = direct;
				}
				changed = true;
			}
			settings.OfficialClientVersionByPreset ??= new Dictionary<string, string>();
			string oldVersion = settings.OfficialClientVersionByPreset.TryGetValue(name, out string? v0) ? (v0 ?? "") : "";
			if (!string.Equals(oldVersion, version, StringComparison.Ordinal))
			{
				if (version.Length == 0)
				{
					settings.OfficialClientVersionByPreset.Remove(name);
				}
				else
				{
					settings.OfficialClientVersionByPreset[name] = version;
				}
				changed = true;
			}
			if (changed)
			{
				SettingsManager.Save(settings);
				LogHelper.Log("[启动客户端] 预设「" + name + "」配置已保存（客户端目录=" + (root.Length == 0 ? "(自动)" : root) + "，直连=" + (direct.Length == 0 ? "(无)" : direct) + "）");
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动客户端] 保存配置失败");
		}
	}

	private void ConfigBox_LostFocus(object sender, RoutedEventArgs e)
	{
		string keep = SelectedVersionName();
		SaveCurrentConfig();
		ReloadVersionOptions(keep);
	}

	private const string VersionAutoItem = "自动（按客户端目录选择）";

	private string SelectedVersionName()
	{
		return (_versionBox.SelectedIndex > 0) ? ((_versionBox.SelectedItem as string) ?? "").Trim() : "";
	}

	/// <summary>按当前客户端目录重扫 versions；selectName 为空 = 自动。</summary>
	private void ReloadVersionOptions(string selectName)
	{
		bool prev = _syncing;
		_syncing = true;
		try
		{
			string keep = (selectName ?? "").Trim();
			_versionBox.Items.Clear();
			_versionBox.Items.Add(VersionAutoItem);
			foreach (string dir in EnumerateVersionDirs())
			{
				_versionBox.Items.Add(Path.GetFileName(dir));
			}
			int index = 0;
			if (keep.Length > 0)
			{
				for (int i = 1; i < _versionBox.Items.Count; i++)
				{
					if (string.Equals(_versionBox.Items[i] as string, keep, StringComparison.OrdinalIgnoreCase))
					{
						index = i;
						break;
					}
				}
			}
			_versionBox.SelectedIndex = index;
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动客户端] 刷新版本目录失败");
		}
		finally
		{
			_syncing = prev;
		}
	}

	/// <summary>列出当前客户端目录下可启动的 versions\&lt;name&gt;。</summary>
	private List<string> EnumerateVersionDirs()
	{
		var list = new List<string>();
		try
		{
			string hint = (_clientBox.Text ?? "").Trim();
			PclClientInfo? info = PclClientLauncher.Detect(hint);
			if (info == null)
			{
				return list;
			}
			string versionsDir = Path.Combine(info.McRoot, "versions");
			if (!Directory.Exists(versionsDir))
			{
				return list;
			}
			foreach (string dir in Directory.GetDirectories(versionsDir))
			{
				if (Directory.GetFiles(dir, "*.json").Length > 0 && Directory.GetFiles(dir, "*.jar").Length > 0)
				{
					list.Add(dir);
				}
			}
			list.Sort(StringComparer.OrdinalIgnoreCase);
		}
		catch
		{
		}
		return list;
	}

	/// <summary>把「版本目录」换算成启动器要的 clientRoot（版本目录本身）。</summary>
	private static string ResolveLaunchRoot(string clientRoot, string versionName)
	{
		if (versionName.Length == 0)
		{
			return clientRoot;
		}
		try
		{
			string root = (clientRoot ?? "").Trim().TrimEnd('\\');
			string mcRoot = "";
			string? parent = Path.GetDirectoryName(root);
			if (root.Length > 0 && !string.IsNullOrEmpty(parent)
				&& string.Equals(Path.GetFileName(parent), "versions", StringComparison.OrdinalIgnoreCase))
			{
				mcRoot = Path.GetDirectoryName(parent) ?? "";
			}
			else if (root.Length > 0 && Directory.Exists(Path.Combine(root, "versions")))
			{
				mcRoot = root;
			}
			else
			{
				mcRoot = PclClientLauncher.Detect(root)?.McRoot ?? "";
			}
			string dir = Path.Combine(mcRoot, "versions", versionName);
			if (mcRoot.Length > 0 && Directory.Exists(dir))
			{
				return dir;
			}
		}
		catch
		{
		}
		return clientRoot;
	}

	private void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncing)
		{
			return;
		}
		SaveCurrentConfig();
	}

	private async void PickClient_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string? dir = await DragonCoreTrimDialogs.PickFolderAsync();
			if (!string.IsNullOrWhiteSpace(dir))
			{
				_clientBox.Text = dir;
				ReloadVersionOptions("");
				SaveCurrentConfig();
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动客户端] 选择客户端目录失败");
		}
	}

	// ---- Java 运行时（JRE 下拉 + 自选路径兜底，对齐 PCL）----

	private const string JreAutoItem = "自动（按版本需求选择）";

	/// <summary>后台扫描本机 JRE 并填充下拉；保留当前选择。</summary>
	private async Task LoadJreListAsync()
	{
		try
		{
			List<(string Home, string Exe, int Major)> javas = await Task.Run(() => PclClientLauncher.EnumerateJavas());
			DispatcherQueue? queue = MainWindow.UIQueue;
			if (queue == null)
			{
				return;
			}
			queue.TryEnqueue(() =>
			{
				try
				{
					_syncingJava = true;
					string want = (_jreBox.SelectedItem is string cur && cur != JreAutoItem) ? cur : (SettingsManager.Instance.OfficialClientJreSelected ?? "");
					_jreBox.Items.Clear();
					_jreBox.Items.Add(JreAutoItem);
					foreach ((string Home, string Exe, int Major) j in javas)
					{
						string label = "Java " + j.Major + "  ·  " + j.Home;
						_jreBox.Items.Add(label);
					}
					int idx = 0;
					for (int i = 1; i < javas.Count + 1; i++)
					{
						if (javas[i - 1].Home.TrimEnd('\\').Equals((want ?? "").Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
						{
							idx = i;
							break;
						}
					}
					_jreBox.SelectedIndex = idx;
					if (idx <= 0)
					{
						// 下拉未命中（设置里的目录不在列表里也视为自动）
						if ((SettingsManager.Instance.OfficialClientJreSelected ?? "").Trim().Length > 0)
						{
							_jreBox.SelectedIndex = 0;
						}
					}
				}
				finally
				{
					_syncingJava = false;
				}
			});
		}
		catch (Exception exJre)
		{
			LogHelper.DebugError(exJre, "[启动客户端] JRE 列表扫描失败");
		}
	}

	private void JreBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingJava)
		{
			return;
		}
		try
		{
			AppSettings settings = SettingsManager.Instance;
			string label = (_jreBox.SelectedItem as string) ?? "";
			// 自动 / 空 = 清空下拉选择；否则把该 JRE 目录写回（自选路径优先级更高，互不覆盖）
			settings.OfficialClientJreSelected = (label.Length > 0 && label != JreAutoItem)
				? label.Substring(label.IndexOf("  ·  ", StringComparison.Ordinal) + 5).Trim()
				: "";
			SettingsManager.Save(settings);
			LogHelper.Log("[启动客户端] JRE 选择 => " + (settings.OfficialClientJreSelected.Length == 0 ? "自动" : settings.OfficialClientJreSelected));
		}
		catch (Exception exSel)
		{
			LogHelper.DebugError(exSel, "[启动客户端] 保存 JRE 选择失败");
		}
	}

	private void JreCustomBox_LostFocus(object sender, RoutedEventArgs e)
	{
		try
		{
			AppSettings settings = SettingsManager.Instance;
			string val = (_jreCustomBox.Text ?? "").Trim();
			if (string.Equals(settings.OfficialClientJavaHome, val, StringComparison.Ordinal))
			{
				return;
			}
			settings.OfficialClientJavaHome = val;
			SettingsManager.Save(settings);
			LogHelper.Log("[启动客户端] 自选 JRE 路径 => " + (val.Length == 0 ? "(空，走下拉/自动)" : val));
		}
		catch (Exception exCustom)
		{
			LogHelper.DebugError(exCustom, "[启动客户端] 保存自选 JRE 路径失败");
		}
	}

	private async void PickJre_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string? dir = await DragonCoreTrimDialogs.PickFolderAsync();
			if (!string.IsNullOrWhiteSpace(dir))
			{
				_jreCustomBox.Text = dir;
				JreCustomBox_LostFocus(sender, e);
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动客户端] 选择 JRE 目录失败");
		}
	}

	// ---- 内存配置 / Java 参数 ----

	/// <summary>本机物理内存（GB，供 UI 显示；取 GC 可见总内存）。</summary>
	private static double PhysicalMemoryGb()
	{
		try
		{
			return Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0, 1);
		}
		catch
		{
			return 0.0;
		}
	}

	/// <summary>自动配置：按本机总内存给一个 PCL 风格的推荐值。</summary>
	public static int AutoMemoryMb()
	{
		double totalGb = PhysicalMemoryGb();
		if (totalGb <= 3.0)
		{
			return 1024;
		}
		if (totalGb <= 6.0)
		{
			return 2048;
		}
		if (totalGb <= 10.0)
		{
			return 3072;
		}
		if (totalGb <= 16.0)
		{
			return 4096;
		}
		if (totalGb <= 24.0)
		{
			return 6144;
		}
		return 8192;
	}

	/// <summary>解析本次启动使用的内存（MB）：全局配置 / 自动配置 / 自定义。</summary>
	public static int ResolveMemoryMb()
	{
		AppSettings settings = SettingsManager.Instance;
		string mode = (settings.OfficialClientMemoryMode ?? "global").Trim().ToLowerInvariant();
		int mb;
		if (mode == "auto")
		{
			mb = AutoMemoryMb();
		}
		else if (mode == "custom")
		{
			mb = settings.OfficialClientMemoryMb;
			if (mb <= 0)
			{
				mb = 4096;
			}
		}
		else
		{
			mb = settings.OfficialClientGlobalMemoryMb;
			if (mb <= 0)
			{
				mb = 4096;
			}
		}
		return Math.Clamp(mb, 512, 65536);
	}

	/// <summary>解析附加 Java 参数（追加在 -Xmx 之后、主类之前）。</summary>
	public static string ResolveJvmArgs()
	{
		return (SettingsManager.Instance.OfficialClientJvmArgs ?? "").Trim();
	}

	private void InitMemoryUi()
	{
		try
		{
			AppSettings settings = SettingsManager.Instance;
			string mode = (settings.OfficialClientMemoryMode ?? "global").Trim().ToLowerInvariant();
			_syncingMem = true;
			_memGlobal.IsChecked = mode != "auto" && mode != "custom";
			_memAuto.IsChecked = mode == "auto";
			_memCustom.IsChecked = mode == "custom";
			double total = PhysicalMemoryGb();
			if (total >= 2.0)
			{
				_memSlider.Maximum = Math.Min(64.0, Math.Max(2.0, Math.Round(total)));
			}
			_syncingMem = false;
			RefreshMemoryUi();
			if (_memSaveTimer == null && MainWindow.UIQueue != null)
			{
				_memSaveTimer = MainWindow.UIQueue.CreateTimer();
				_memSaveTimer.Interval = TimeSpan.FromMilliseconds(400.0);
				_memSaveTimer.IsRepeating = false;
				_memSaveTimer.Tick += delegate
				{
					try
					{
						_memSaveTimer?.Stop();
					}
					catch
					{
					}
					SaveMemoryValue();
				};
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动客户端] 初始化内存配置失败");
		}
	}

	private string CurrentMemoryMode()
	{
		if (_memAuto.IsChecked == true)
		{
			return "auto";
		}
		if (_memCustom.IsChecked == true)
		{
			return "custom";
		}
		return "global";
	}

	private void RefreshMemoryUi()
	{
		try
		{
			AppSettings settings = SettingsManager.Instance;
			string mode = CurrentMemoryMode();
			int mb = (mode == "auto") ? AutoMemoryMb()
				: (mode == "custom" ? settings.OfficialClientMemoryMb : settings.OfficialClientGlobalMemoryMb);
			if (mb <= 0)
			{
				mb = 4096;
			}
			_syncingMem = true;
			_memSlider.IsEnabled = mode != "auto";
			_memSlider.Value = Math.Clamp(mb / 1024.0, _memSlider.Minimum, _memSlider.Maximum);
			_syncingMem = false;
			UpdateMemoryLabel(mb);
		}
		catch
		{
		}
	}

	private void UpdateMemoryLabel(int mb)
	{
		try
		{
			string mode = CurrentMemoryMode();
			string modeLabel = mode == "auto" ? "自动配置" : (mode == "custom" ? "自定义" : "全局配置");
			double total = PhysicalMemoryGb();
			_memLabel.Text = modeLabel + "：" + (mb / 1024.0).ToString("0.0") + " GB（-Xmx" + mb + "M）"
				+ (total > 0 ? "｜本机 " + total.ToString("0.0") + " GB" : "");
			// 数值变化用短暂淡入过渡，避免拖动时文字“跳/漂”；
			// 固定宽度 + 等宽字体让括号里的 MB 位数变化不推动整行。
			try
			{
				DoubleAnimation fade = new DoubleAnimation
				{
					From = 0.45,
					To = 0.85,
					Duration = TimeSpan.FromMilliseconds(140.0),
					EnableDependentAnimation = true
				};
				Storyboard.SetTarget(fade, _memLabel);
				Storyboard.SetTargetProperty(fade, "Opacity");
				Storyboard storyboard = new Storyboard();
				storyboard.Children.Add(fade);
				storyboard.Begin();
			}
			catch
			{
			}
		}
		catch
		{
		}
	}

	private void MemMode_Checked(object sender, RoutedEventArgs e)
	{
		if (_syncingMem)
		{
			return;
		}
		try
		{
			AppSettings settings = SettingsManager.Instance;
			string mode = CurrentMemoryMode();
			if (!string.Equals(settings.OfficialClientMemoryMode, mode, StringComparison.Ordinal))
			{
				settings.OfficialClientMemoryMode = mode;
				SettingsManager.Save(settings);
			}
			RefreshMemoryUi();
			LogHelper.Log("[启动客户端] 内存配置模式 => " + mode);
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动客户端] 保存内存配置模式失败");
		}
	}

	private void MemSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
	{
		if (_syncingMem)
		{
			return;
		}
		UpdateMemoryLabel((int)Math.Round(e.NewValue * 1024.0));
		try
		{
			_memSaveTimer?.Stop();
			_memSaveTimer?.Start();
		}
		catch
		{
		}
	}

	private void SaveMemoryValue()
	{
		try
		{
			string mode = CurrentMemoryMode();
			if (mode == "auto")
			{
				return;
			}
			int mb = (int)Math.Round(_memSlider.Value * 1024.0);
			AppSettings settings = SettingsManager.Instance;
			if (mode == "custom")
			{
				settings.OfficialClientMemoryMb = mb;
			}
			else
			{
				settings.OfficialClientGlobalMemoryMb = mb;
			}
			SettingsManager.Save(settings);
			UpdateMemoryLabel(mb);
			LogHelper.Log("[启动客户端] 内存配置 => " + mode + " / -Xmx" + mb + "M");
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动客户端] 保存内存大小失败");
		}
	}

	private void SaveJvmArgs()
	{
		try
		{
			AppSettings settings = SettingsManager.Instance;
			string text = (_jvmArgsBox.Text ?? "").Trim();
			if (string.Equals(settings.OfficialClientJvmArgs ?? "", text, StringComparison.Ordinal))
			{
				return;
			}
			settings.OfficialClientJvmArgs = text;
			SettingsManager.Save(settings);
			LogHelper.Log("[启动客户端] Java 参数 => " + (text.Length == 0 ? "(推荐默认)" : text));
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动客户端] 保存 Java 参数失败");
		}
	}

	private void EntryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncing)
		{
			return;
		}
		string display = SelectedDisplay();
		if (display == AddCardItem)
		{
			_ = CreateCardAsync(_lastValidIndex);
			return;
		}
		if (display.Length == 0)
		{
			return;
		}
		_selected = display;
		_lastValidIndex = _entryBox.SelectedIndex;
		if (TryGetEntry(out ServerPreset? preset, out ClientLaunchCard? card))
		{
			if (card == null && preset != null)
			{
				AppSettings settings = SettingsManager.Instance;
				if (!string.Equals(settings.OfficialClientPreset, preset.Name, StringComparison.Ordinal))
				{
					settings.OfficialClientPreset = preset.Name;
					SettingsManager.Save(settings);
				}
			}
			LoadCurrentToUi();
			UpdateStatus(preset, card);
		}
	}

	private void RestoreSelection(int index)
	{
		try
		{
			_syncing = true;
			if (index >= 0 && index < _entryBox.Items.Count)
			{
				_entryBox.SelectedIndex = index;
			}
			else if (_entryBox.Items.Count > 0)
			{
				_entryBox.SelectedIndex = 0;
			}
			_syncing = false;
			_selected = SelectedDisplay();
			_lastValidIndex = _entryBox.SelectedIndex;
		}
		finally
		{
			_syncing = false;
		}
	}

	private XamlRoot? FindXamlRoot()
	{
		try
		{
			return MainWindow.Instance?.GetRootGrid()?.XamlRoot;
		}
		catch
		{
			return null;
		}
	}

	private async Task CreateCardAsync(int restoreIndex)
	{
		try
		{
			AppSettings settings = SettingsManager.Instance;
			List<ServerPreset> presets = Presets();

			StackPanel body = new StackPanel
			{
				Spacing = 8.0,
				MinWidth = 440.0,
				MaxWidth = 470.0
			};

			TextBox nameBox = new TextBox
			{
				Width = 400.0,
				PlaceholderText = "启动卡名称",
				Text = RandomCardName(settings)
			};
			body.Children.Add(nameBox);

			StackPanel presetRow = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Spacing = 8.0
			};
			presetRow.Children.Add(new TextBlock
			{
				Text = "服务器预设",
				FontSize = 12.0,
				Opacity = 0.7,
				VerticalAlignment = VerticalAlignment.Center
			});
			ComboBox presetBox = new ComboBox
			{
				MinWidth = 240.0,
				HorizontalAlignment = HorizontalAlignment.Left
			};
			presetBox.Items.Add(NoPresetItem);
			foreach (ServerPreset p in presets)
			{
				presetBox.Items.Add(p.Name);
			}
			presetBox.SelectedIndex = 0;
			ThemeHelper.SolidComboPopup(presetBox);
			presetRow.Children.Add(presetBox);
			body.Children.Add(presetRow);

			StackPanel clientRow = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Spacing = 8.0
			};
			TextBox rootBox = new TextBox
			{
				Width = 340.0,
				PlaceholderText = "客户端 .minecraft 目录（留空自动查找）",
				Text = settings.OfficialClientRoot ?? ""
			};
			clientRow.Children.Add(rootBox);
			Button pickRoot = new Button
			{
				Content = "选择…"
			};
			pickRoot.Click += async delegate
			{
				try
				{
					string? dir = await DragonCoreTrimDialogs.PickFolderAsync();
					if (!string.IsNullOrWhiteSpace(dir))
					{
						rootBox.Text = dir;
					}
				}
				catch
				{
				}
			};
			clientRow.Children.Add(pickRoot);
			body.Children.Add(clientRow);

			TextBox directBox = new TextBox
			{
				Width = 400.0,
				PlaceholderText = "直连端口（留空则不自动进服，例如 19198）"
			};
			body.Children.Add(directBox);

			ContentDialog dialog = new ContentDialog
			{
				Title = "新建启动卡",
				Content = body,
				PrimaryButtonText = "创建",
				CloseButtonText = "取消",
				DefaultButton = ContentDialogButton.Primary,
				XamlRoot = FindXamlRoot(),
				RequestedTheme = (MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default)
			};
			if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
			{
				RestoreSelection(restoreIndex);
				return;
			}

			string name = (nameBox.Text ?? "").Trim();
			if (name.Length == 0)
			{
				name = RandomCardName(settings);
			}
			settings.ClientLaunchCards ??= new List<ClientLaunchCard>();
			string unique = name;
			int suffix = 2;
			while (settings.ClientLaunchCards.Any((ClientLaunchCard c) => string.Equals(c.Name, unique, StringComparison.Ordinal)))
			{
				unique = name + " (" + suffix + ")";
				suffix++;
			}
			int presetIndex = presetBox.SelectedIndex;
			ClientLaunchCard card = new ClientLaunchCard
			{
				Name = unique,
				PresetName = (presetIndex > 0 && presetIndex <= presets.Count) ? presets[presetIndex - 1].Name : "",
				ClientRoot = (rootBox.Text ?? "").Trim(),
				DirectAddress = (directBox.Text ?? "").Trim()
			};
			settings.ClientLaunchCards.Add(card);
			SettingsManager.Save(settings);
			_selected = CardPrefix + card.Name;
			LogHelper.Log("[启动客户端] 已新建启动卡「" + card.Name + "」（预设=" + (card.PresetName.Length == 0 ? "(不指定)" : card.PresetName) + "）");
			Refresh();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[启动客户端] 新建启动卡失败");
			MainWindow.Instance?.ShowToast("新建启动卡失败：" + ex.Message, false);
			RestoreSelection(restoreIndex);
		}
	}

	/// <summary>新建启动卡的随机默认名（2026-11-20）：从趣味名单里随机取一个，尽量避开已用过的名字。</summary>
	private static string RandomCardName(AppSettings settings)
	{
		string[] pool = new string[]
		{
			"洛琪希是我老婆", "希露菲是我老婆", "艾莉丝是我老婆",
			"五条悟", "宿傩", "宿傩之战", "一拳超人", "埼玉",
			"唐三", "小舞", "刚门视频", "大肥鱼", "克劳德", "龙宝", "GLM"
		};
		try
		{
			List<string> used = (settings.ClientLaunchCards ?? new List<ClientLaunchCard>())
				.Select((ClientLaunchCard c) => c.Name ?? "").ToList();
			List<string> free = pool.Where((string n) => !used.Contains(n, StringComparer.Ordinal)).ToList();
			List<string> source = (free.Count > 0) ? free : pool.ToList();
			return source[Random.Shared.Next(source.Count)];
		}
		catch
		{
			return pool[Random.Shared.Next(pool.Length)];
		}
	}

	/// <summary>启动卡 / 内置预设的「自启动客户端」开关。</summary>
	private void AutoStartCardCheck_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (!TryGetEntry(out ServerPreset? preset, out ClientLaunchCard? card))
			{
				return;
			}
			bool on = _autoStartCardCheck.IsChecked == true;
			if (card != null)
			{
				if (card.AutoStart == on)
				{
					return;
				}
				card.AutoStart = on;
				SettingsManager.Save(SettingsManager.Instance);
				LogHelper.Log("[启动客户端] 启动卡「" + card.Name + "」自启动客户端 = " + (on ? "开" : "关"));
				return;
			}
			if (preset == null)
			{
				return;
			}
			AppSettings settings = SettingsManager.Instance;
			settings.AutoStartClientPresets ??= new List<string>();
			bool exists = settings.AutoStartClientPresets.Contains(preset.Name, StringComparer.Ordinal);
			if (exists == on)
			{
				return;
			}
			if (on)
			{
				settings.AutoStartClientPresets.Add(preset.Name);
			}
			else
			{
				settings.AutoStartClientPresets.RemoveAll(n => string.Equals(n, preset.Name, StringComparison.Ordinal));
			}
			SettingsManager.Save(settings);
			LogHelper.Log("[启动客户端] 预设「" + preset.Name + "」自启动客户端 = " + (on ? "开" : "关"));
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动客户端] 保存自启动客户端开关失败");
		}
	}

	/// <summary>该内置预设是否勾了「自启动客户端」。</summary>
	private static bool IsPresetAutoStart(string presetName)
	{
		if (string.IsNullOrWhiteSpace(presetName))
		{
			return false;
		}
		List<string>? list = SettingsManager.Instance.AutoStartClientPresets;
		return list != null && list.Contains(presetName, StringComparer.Ordinal);
	}

	private void DeleteCard_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (!TryGetEntry(out _, out ClientLaunchCard? card) || card == null)
			{
				return;
			}
			AppSettings settings = SettingsManager.Instance;
			int removed = settings.ClientLaunchCards?.RemoveAll((ClientLaunchCard c) => string.Equals(c.Name, card.Name, StringComparison.Ordinal)) ?? 0;
			if (removed > 0)
			{
				SettingsManager.Save(settings);
				LogHelper.Log("[启动客户端] 已删除启动卡「" + card.Name + "」");
			}
			_selected = "";
			Refresh();
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动客户端] 删除启动卡失败");
		}
	}

	/// <summary>从代理绑定里取该端口的角色名 / uid / 账号索引。</summary>
	private static void ResolveBindingRole(int proxyPort, out string roleName, out long uid, out int accountIndex)
	{
		roleName = "";
		uid = 0L;
		accountIndex = -1;
		try
		{
			ProxyCaptureService.ActiveBindingInfo? binding = ProxyCaptureService.ActiveBindingsSnapshot()
				.FirstOrDefault((ProxyCaptureService.ActiveBindingInfo b) => b.ListenPort == proxyPort);
			if (binding != null)
			{
				roleName = binding.LoginName ?? "";
				accountIndex = binding.AuthAccountIndex;
			}
			ResolveAccount(roleName, out uid);
		}
		catch
		{
		}
	}

	private static void ResolveAccount(string roleName, out long uid)
	{
		uid = 0L;
		try
		{
			string target = roleName ?? "";
			if (target.Length == 0)
			{
				return;
			}
			AccountStore.LocalAccount? acc = AccountStore.All.FirstOrDefault((AccountStore.LocalAccount a) =>
				(a.Nickname ?? "") == target || (a.RoleNames != null && a.RoleNames.Contains(target))
				|| (a.ServerRoles != null && a.ServerRoles.Values.Any((List<string> v) => v != null && v.Contains(target))));
			if (acc != null && long.TryParse(acc.EntityId, out long parsed) && parsed > 0)
			{
				uid = parsed;
			}
		}
		catch
		{
		}
	}

	private async void Launch_Click(object sender, RoutedEventArgs e)
	{
		await LaunchCurrentAsync();
	}

	/// <summary>外部入口（快速启动页角色组/启动卡栏目）：按显示名载入条目（服务器预设名或「启动卡 · 名称」），不启动。</summary>
	public void SelectEntryFromOutside(string display)
	{
		try
		{
			int idx = _items.IndexOf(display);
			if (idx < 0 && !display.StartsWith(CardPrefix, StringComparison.Ordinal))
			{
				// 兼容直接传启动卡名（不带前缀）
				idx = _items.IndexOf(CardPrefix + display);
			}
			if (idx < 0)
			{
				Refresh();
				idx = _items.IndexOf(display);
				if (idx < 0 && !display.StartsWith(CardPrefix, StringComparison.Ordinal))
				{
					idx = _items.IndexOf(CardPrefix + display);
				}
			}
			if (idx < 0)
			{
				return;
			}
			_syncing = true;
			_entryBox.SelectedIndex = idx;
			_syncing = false;
			_selected = _items[idx];
			_lastValidIndex = idx;
			LoadCurrentToUi();
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[启动客户端] 条目载入失败");
		}
	}

	/// <summary>
	/// 按启动卡保存的配置静默拉起客户端（供程序启动时的「自启动客户端」调用）。
	/// </summary>
	public static OfficialClientLaunchResult LaunchCardSilent(ClientLaunchCard card)
	{
		if (card == null)
		{
			return new OfficialClientLaunchResult { Message = "启动卡为空" };
		}
		return LaunchEntrySilent(ServerPresets.Find(card.PresetName), card);
	}

	/// <summary>按内置服务器预设保存的配置静默拉起客户端（预设卡的自启动）。</summary>
	public static OfficialClientLaunchResult LaunchPresetSilent(ServerPreset preset)
	{
		if (preset == null)
		{
			return new OfficialClientLaunchResult { Message = "服务器预设为空" };
		}
		return LaunchEntrySilent(preset, null);
	}

	/// <summary>预设 / 启动卡共用的静默启动实现（目录、版本、直连、内存、JRE 解析一致）。</summary>
	private static OfficialClientLaunchResult LaunchEntrySilent(ServerPreset? preset, ClientLaunchCard? card)
	{
		OfficialClientLaunchResult result = new OfficialClientLaunchResult();
		try
		{
			AppSettings settings = SettingsManager.Instance;
			string clientRoot;
			string versionName;
			string direct;
			string javaHome;
			if (card != null)
			{
				clientRoot = (card.ClientRoot ?? "").Trim();
				versionName = (card.VersionDir ?? "").Trim();
				direct = card.DirectAddress ?? "";
				javaHome = (card.JavaHome ?? "").Trim();
			}
			else
			{
				string presetName = preset!.Name;
				clientRoot = (settings.OfficialClientRootByPreset != null
					&& settings.OfficialClientRootByPreset.TryGetValue(presetName, out string? root)) ? (root ?? "") : "";
				if (clientRoot.Trim().Length == 0)
				{
					clientRoot = (settings.OfficialClientRoot ?? "").Trim();
				}
				versionName = (settings.OfficialClientVersionByPreset != null
					&& settings.OfficialClientVersionByPreset.TryGetValue(presetName, out string? version)) ? (version ?? "") : "";
				direct = (settings.OfficialClientDirectByPreset != null
					&& settings.OfficialClientDirectByPreset.TryGetValue(presetName, out string? savedDirect)) ? (savedDirect ?? "") : "";
				javaHome = "";
			}
			string launchRoot = ResolveLaunchRoot(clientRoot, versionName);
			string expandedDirect = ExpandDirect(preset, direct);
			string memoryArg = "-Xmx" + ResolveMemoryMb() + "M";
			string extraJvm = ResolveJvmArgs();
			if (javaHome.Length == 0)
			{
				javaHome = (settings.OfficialClientJavaHome ?? "").Trim();
			}
			if (javaHome.Length == 0)
			{
				javaHome = (settings.OfficialClientJreSelected ?? "").Trim();
			}
			string role = (settings.OfficialClientRole ?? "").Trim();
			ResolveAccount(role, out long uid);
			if (BjdGate.IsBjd(preset))
			{
				int proxyPort = ResolveLoopbackPort(expandedDirect);
				if (proxyPort == 0)
				{
					proxyPort = ResolveProxyPort(preset!.Host);
				}
				if (proxyPort == 0)
				{
					result.Message = "「" + preset!.Name + "」没有运行中的代理";
					return result;
				}
				ResolveBindingRole(proxyPort, out string bindingRole, out long bindingUid, out int accountIndex);
				FakeLauncherService fake = FakeLauncherService.Start(0, "127.0.0.1", proxyPort, preset.Name);
				result = OfficialClientLauncher.Launch(proxyPort,
					(bindingRole.Length == 0) ? null : bindingRole,
					accountIndex,
					(launchRoot.Length == 0) ? null : launchRoot,
					false, null, 2, fake.Port, bindingUid, null,
					(javaHome.Length == 0) ? null : javaHome, null, memoryArg, extraJvm);
				if (!result.Ok)
				{
					FakeLauncherService.StopInstance(fake);
				}
				return result;
			}
			int anyProxyPort = (preset != null) ? ResolveProxyPort(preset.Host) : 0;
			if (anyProxyPort > 0)
			{
				ResolveBindingRole(anyProxyPort, out string bindingRole, out long bindingUid, out _);
				if (bindingRole.Length > 0)
				{
					role = bindingRole;
				}
				if (bindingUid > 0)
				{
					uid = bindingUid;
				}
			}
			result = OfficialClientLauncher.Launch(0, role.Length == 0 ? null : role, -1,
				(launchRoot.Length == 0) ? null : launchRoot,
				false, null, 1, 0, uid, null,
				(javaHome.Length == 0) ? null : javaHome,
				(expandedDirect.Length == 0) ? "" : expandedDirect, memoryArg, extraJvm);
			return result;
		}
		catch (Exception ex)
		{
			result.Message = ex.Message;
			LogHelper.LogError(ex, "[启动客户端] 静默启动失败");
			return result;
		}
	}

	/// <summary>外部入口（快速启动页角色组/启动卡栏目）：按显示名选中条目并立即启动。</summary>
	public async void LaunchEntryFromOutside(string display)
	{
		try
		{
			SelectEntryFromOutside(display);
			await LaunchCurrentAsync();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[启动客户端] 条目快捷启动失败");
		}
	}

	public async Task LaunchCurrentAsync()
	{
		lock (Gate)
		{
			if (_busy)
			{
				return;
			}
			_busy = true;
		}
		try
		{
			if (!TryGetEntry(out ServerPreset? preset, out ClientLaunchCard? card))
			{
				_status.Text = "请选择服务器预设或启动卡";
				return;
			}
			SaveCurrentConfig();
			AppSettings settings = SettingsManager.Instance;
			string label = (card != null) ? card.Name : preset!.Name;
			string clientRoot = (_clientBox.Text ?? "").Trim();
			// 指定版本目录时直接以 versions\<name> 作为启动根目录，避免自动挑到不含 MythosNexus 的普通版。
			string launchRoot = ResolveLaunchRoot(clientRoot, SelectedVersionName());
			string directText = ExpandDirect(preset, (_directBox.Text ?? "").Trim());
			// JRE 优先级（2026-11-20）：启动卡 JavaHome > 自选路径（OfficialClientJavaHome）> 下拉选择（OfficialClientJreSelected）> 自动
			string javaHome = ((card?.JavaHome ?? "") ?? "").Trim();
			if (javaHome.Length == 0)
			{
				javaHome = (settings.OfficialClientJavaHome ?? "").Trim();
			}
			if (javaHome.Length == 0)
			{
				javaHome = (settings.OfficialClientJreSelected ?? "").Trim();
			}
			// 内存配置（全局 / 自动 / 自定义）+ 附加 Java 参数
			int memMb = ResolveMemoryMb();
			string memoryArg = "-Xmx" + memMb + "M";
			string extraJvm = ResolveJvmArgs();
			LogHelper.Log("[启动客户端] 本次启动内存参数: " + memoryArg
				+ "（模式=" + (settings.OfficialClientMemoryMode ?? "global") + "），附加参数="
				+ (extraJvm.Length == 0 ? "(无)" : extraJvm));

			if (IsBjd(preset))
			{
				// 布吉岛：必须用假启动器 + 认证代理进服（多开时各自独立一套）。
				// 直连地址写的是本机 127.0.0.1:端口时，用它指定走哪个代理端口（多角色多开就靠这个区分）。
				int proxyPort = ResolveLoopbackPort(directText);
				if (proxyPort == 0)
				{
					proxyPort = ResolveProxyPort(preset!.Host);
				}
				if (proxyPort == 0)
				{
					_status.Text = "「" + preset!.Name + "」还没有运行中的代理，请先启动该服务器代理";
					return;
				}
				ResolveBindingRole(proxyPort, out string roleName, out long uid, out int accountIndex);
				FakeLauncherService fake = FakeLauncherService.Start(0, "127.0.0.1", proxyPort, preset.Name);
				_status.Text = "正在启动客户端（假启动器 127.0.0.1:" + fake.Port + " → 代理 127.0.0.1:" + proxyPort + "）…";
				OfficialClientLaunchResult r = await Task.Run(() => OfficialClientLauncher.Launch(
					proxyPort,
					(roleName.Length == 0) ? null : roleName,
					accountIndex,
					(launchRoot.Length == 0) ? null : launchRoot,
					false,
					null,
					2,
					fake.Port,
					uid,
					null,
					(javaHome.Length == 0) ? null : javaHome,
					null,
					memoryArg,
					extraJvm));
				if (!r.Ok)
				{
					FakeLauncherService.StopInstance(fake);
					_status.Text = "启动失败：" + r.Message;
				}
				else
				{
					_status.Text = "已启动（pid " + r.Pid + "）→ 代理 127.0.0.1:" + proxyPort;
				}
				return;
			}

			// 非布吉岛：不用假启动器，只按配置里的客户端直连地址启动对应客户端。
			long uid2 = 0L;
			string role2 = (settings.OfficialClientRole ?? "").Trim();
			ResolveAccount(role2, out uid2);
			int anyProxyPort = (preset != null) ? ResolveProxyPort(preset.Host) : 0;
			if (anyProxyPort > 0)
			{
				ResolveBindingRole(anyProxyPort, out string bindingRole, out long bindingUid, out _);
				if (bindingRole.Length > 0)
				{
					role2 = bindingRole;
				}
				if (bindingUid > 0)
				{
					uid2 = bindingUid;
				}
			}
			_status.Text = (directText.Length == 0)
				? ("正在启动客户端「" + label + "」（未保存直连地址，将停在主菜单）…")
				: ("正在启动客户端「" + label + "」（直连 " + directText + "）…");
			OfficialClientLaunchResult r2 = await Task.Run(() => OfficialClientLauncher.Launch(
				0,
				(role2.Length == 0) ? null : role2,
				-1,
				(clientRoot.Length == 0) ? null : clientRoot,
				false,
				null,
				1,
				0,
				uid2,
				null,
				(javaHome.Length == 0) ? null : javaHome,
				(directText.Length == 0) ? "" : directText,
				memoryArg,
				extraJvm));
			if (!r2.Ok)
			{
				_status.Text = "启动失败：" + r2.Message;
			}
			else
			{
				_status.Text = "已启动（pid " + r2.Pid + "）" + ((directText.Length == 0) ? "（未自动进服）" : ("→ 直连 " + directText));
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[启动客户端] 启动失败");
			_status.Text = "启动失败：" + ex.Message;
		}
		finally
		{
			lock (Gate)
			{
				_busy = false;
			}
			Refresh();
		}
	}

	private void Stop_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			OfficialClientLauncher.Stop();
			FakeLauncherService.StopCurrent();
			_status.Text = "已关闭全部客户端";
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[启动客户端] 关闭失败");
		}
		Refresh();
	}
}

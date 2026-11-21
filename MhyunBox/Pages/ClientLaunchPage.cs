using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MhyunBox.Services;
using MhyunBox.Settings;

namespace MhyunBox.Pages;

/// <summary>
/// 「启动客户端」独立页：复用快速启动页的客户端配置与启动卡片，
/// 内存大小 / Java 参数单独放在下方卡片，并提供各页面是否显示客户端配置卡的选择。
/// </summary>
public sealed class ClientLaunchPage : Page
{
	private readonly RoleLauncherPanel _panel = new RoleLauncherPanel();
	private readonly StackPanel _visibilityChecks = new StackPanel
	{
		Orientation = Orientation.Horizontal,
		Spacing = 14.0
	};

	public ClientLaunchPage()
	{
		StackPanel root = new StackPanel
		{
			Spacing = 12.0,
			Padding = new Thickness(24.0, 18.0, 24.0, 24.0)
		};
		root.Children.Add(new TextBlock
		{
			Text = "启动客户端",
			FontSize = 22.0,
			FontWeight = FontWeights.Bold
		});
		root.Children.Add(new TextBlock
		{
			Text = "选择服务器预设或启动卡后可直接拉起客户端；内存大小与 Java 参数在下方单独配置。",
			FontSize = 12.5,
			Opacity = 0.7,
			TextWrapping = TextWrapping.Wrap
		});

		root.Children.Add(Card(_panel.Root));
		root.Children.Add(Card(BuildTuningCard()));
		root.Children.Add(Card(BuildVisibilityCard()));
		root.Children.Add(Card(BuildClientCardsCard()));

		Content = new ScrollViewer
		{
			Content = root,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		};
	}

	private static Border Card(FrameworkElement child)
	{
		return new Border
		{
			Background = ThemeHelper.CardBackground(),
			BorderBrush = ThemeHelper.Resolve("CardStrokeColorDefaultBrush"),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(12.0),
			Padding = new Thickness(14.0, 12.0, 14.0, 12.0),
			Child = child
		};
	}

	private FrameworkElement BuildTuningCard()
	{
		StackPanel content = new StackPanel { Spacing = 8.0 };
		content.Children.Add(new TextBlock
		{
			Text = "启动参数与内存",
			FontSize = 14.0,
			FontWeight = FontWeights.SemiBold
		});
		content.Children.Add(new TextBlock
		{
			Text = "内存大小支持全局配置 / 自动配置 / 自定义；附加 Java 参数会插在主类之前。",
			FontSize = 12.0,
			Opacity = 0.65,
			TextWrapping = TextWrapping.Wrap
		});
		content.Children.Add(_panel.TuningRoot);
		return content;
	}

	private FrameworkElement BuildVisibilityCard()
	{
		StackPanel content = new StackPanel { Spacing = 8.0 };
		content.Children.Add(new TextBlock
		{
			Text = "客户端配置卡显示位置",
			FontSize = 14.0,
			FontWeight = FontWeights.SemiBold
		});
		content.Children.Add(new TextBlock
		{
			Text = "勾选表示在对应页面显示「客户端配置与启动」卡片；取消勾选后该页面不再显示。",
			FontSize = 12.0,
			Opacity = 0.65,
			TextWrapping = TextWrapping.Wrap
		});
		content.Children.Add(_visibilityChecks);
		_visibilityChecks.Children.Add(VisibilityCheck("HomePage", "首页"));
		_visibilityChecks.Children.Add(VisibilityCheck("GroupsQuickPage", "快速启动"));
		return content;
	}

	private FrameworkElement BuildClientCardsCard()
	{
		StackPanel content = new StackPanel { Spacing = 8.0 };
		content.Children.Add(new TextBlock
		{
			Text = "角色组与客户端卡",
			FontSize = 14.0,
			FontWeight = FontWeights.SemiBold
		});
		content.Children.Add(new TextBlock
		{
			Text = "客户端卡（服务器预设 + 启动卡，点击「启动」一键进服 /「载入」到上方面板）",
			FontSize = 12.0,
			Opacity = 0.65
		});
		StackPanel row = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8.0
		};
		int count = 0;
		foreach (ServerPreset preset in ServerPresets.All)
		{
			if (preset == null || preset.IsPending || string.IsNullOrWhiteSpace(preset.Host))
			{
				continue;
			}
			row.Children.Add(BuildClientCardTile(preset.Name, TailPath(preset.Host + ":" + preset.Port, 48), "服务器预设"));
			count++;
		}
		foreach (ClientLaunchCard card in SettingsManager.Instance.ClientLaunchCards ?? new List<ClientLaunchCard>())
		{
			string direct = (card.DirectAddress ?? "").Trim();
			row.Children.Add(BuildClientCardTile("启动卡 · " + card.Name,
				"目录：" + TailPath(card.ClientRoot),
				"直连：" + (direct.Length == 0 ? "未保存" : direct)));
			count++;
		}
		if (count == 0)
		{
			content.Children.Add(new TextBlock
			{
				Text = "还没有可用的客户端卡。",
				FontSize = 12.0,
				Opacity = 0.65
			});
		}
		else
		{
			content.Children.Add(new ScrollViewer
			{
				Content = row,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
				HorizontalScrollMode = ScrollMode.Auto
			});
		}
		return content;
	}

	private FrameworkElement BuildClientCardTile(string display, string line1, string line2)
	{
		StackPanel body = new StackPanel { Spacing = 3.0 };
		body.Children.Add(new TextBlock
		{
			Text = display,
			FontSize = 13.0,
			FontWeight = FontWeights.SemiBold,
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxWidth = 220.0
		});
		body.Children.Add(new TextBlock
		{
			Text = line1,
			FontSize = 11.5,
			Opacity = 0.72,
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxWidth = 220.0
		});
		body.Children.Add(new TextBlock
		{
			Text = line2,
			FontSize = 11.5,
			Opacity = 0.72,
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxWidth = 220.0
		});
		StackPanel buttons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6.0,
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
		};
		Button launch = new Button
		{
			Content = "启动",
			FontSize = 12.0,
			MinHeight = 28.0,
			Padding = new Thickness(10.0, 0.0, 10.0, 0.0)
		};
		launch.Click += (_, _) => _panel.LaunchEntryFromOutside(display);
		buttons.Children.Add(launch);
		Button load = new Button
		{
			Content = "载入",
			FontSize = 12.0,
			MinHeight = 28.0,
			Padding = new Thickness(10.0, 0.0, 10.0, 0.0)
		};
		load.Click += (_, _) => _panel.SelectEntryFromOutside(display);
		buttons.Children.Add(load);
		body.Children.Add(buttons);
		return new Border
		{
			Background = ThemeHelper.Resolve("ControlFillColorSecondaryBrush"),
			BorderBrush = ThemeHelper.Resolve("ControlStrokeColorDefaultBrush"),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(8.0),
			Padding = new Thickness(10.0, 8.0, 10.0, 8.0),
			Child = body
		};
	}

	private static string TailPath(string? path, int maxLen = 42)
	{
		string value = (path ?? "").Trim();
		if (value.Length == 0)
		{
			return "（留空自动查找）";
		}
		return value.Length <= maxLen ? value : "…" + value[^maxLen..];
	}

	private CheckBox VisibilityCheck(string pageKey, string label)
	{
		bool hidden = SettingsManager.Instance.ClientLauncherHiddenPages?.Contains(pageKey) == true;
		CheckBox check = new CheckBox
		{
			Content = label,
			IsChecked = !hidden,
			FontSize = 13.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		check.Click += (_, _) =>
		{
			try
			{
				AppSettings settings = SettingsManager.Instance;
				settings.ClientLauncherHiddenPages ??= new List<string>();
				bool show = check.IsChecked == true;
				settings.ClientLauncherHiddenPages.RemoveAll(x => string.Equals(x, pageKey, StringComparison.OrdinalIgnoreCase));
				if (!show)
				{
					settings.ClientLauncherHiddenPages.Add(pageKey);
				}
				SettingsManager.Save(settings);
				MainWindow.Instance?.ReloadCachedPages();
				MainWindow.Instance?.ShowToast(show
					? ("已显示「" + label + "」的客户端配置卡")
					: ("已隐藏「" + label + "」的客户端配置卡"));
			}
			catch (Exception ex)
			{
				LogHelper.Log("[启动客户端] 保存配置卡显示位置失败：" + ex.Message);
			}
		};
		return check;
	}
}

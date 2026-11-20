using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace MhyunBox.Pages;

/// <summary>
/// 会话多开的两个提示文案（2026-11-18 定版）：
///  - 首次打开的使用前提说明（带「复制群号 / 复制 QQ」按钮）；
///  - 元素之诗机器人代理超过 5 个时的提醒。
/// 文案集中在这里，页面弹窗和启动时的一次性演示弹窗共用同一份内容。
/// </summary>
internal static class NexusNotice
{
	/// <summary>联系群号。</summary>
	public const string GroupNumber = "1124833900";

	/// <summary>群主 QQ。</summary>
	public const string OwnerQq = "2998155760";

	/// <summary>首次打开「会话多开」的说明文案（纯文本版，供复制 / 日志使用）。</summary>
	public const string AgreementText =
		"会话多开会在本机为每个角色开一个 127.0.0.1 端口，把游戏客户端的连接转发到对应服务器，"
		+ "并让 MythosNexus 在同一个客户端里挂着这些角色（该功能主要面向失落世界服务器玩家）。\n\n"
		+ "请先看清楚这几点：\n"
		+ "· 机器人号、服务器规则都由你自主负责。多开被服务器判定为违规、封号、回档，本程序不承担任何责任。\n"
		+ "· 目前已能做到均会话 200MB（会话可理解为角色/代理为一个会话）且单实例；"
		+ "自主进入服务器最高可开 10 个机器人会话，即 10 个角色为上限。"
		+ "开启第 11 个时会出现游戏爆内存和崩溃的情形。使用此功能后默认自主承担以上风险。\n"
		+ "· 如有任何疑惑、问题或建议，可加群 " + GroupNumber + " 提问群主，或添加 QQ " + OwnerQq + "。";

	/// <summary>构造首次打开说明的弹窗内容（含两个复制按钮）。</summary>
	public static UIElement BuildAgreementContent()
	{
		var panel = new StackPanel { Spacing = 10 };
		panel.Children.Add(new TextBlock
		{
			Text = "会话多开会在本机为每个角色开一个 127.0.0.1 端口，把游戏客户端的连接转发到对应服务器，"
				+ "并让 MythosNexus 在同一个客户端里挂着这些角色（该功能主要面向失落世界服务器玩家）。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13
		});
		panel.Children.Add(new TextBlock
		{
			Text = "请先看清楚这几点：",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0, 2, 0, 0)
		});
		panel.Children.Add(Bullet("机器人号、服务器规则都由你自主负责。多开被服务器判定为违规、封号、回档，本程序不承担任何责任。"));
		panel.Children.Add(Bullet("目前已能做到均会话 200MB（会话可理解为角色/代理为一个会话）且单实例；"
			+ "自主进入服务器最高可开 10 个机器人会话，即 10 个角色为上限。"
			+ "开启第 11 个时会出现游戏爆内存和崩溃的情形。使用此功能后默认自主承担以上风险。"));
		panel.Children.Add(Bullet("如有任何疑惑、问题或建议，可加群或添加 QQ 联系群主。"));

		var contacts = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			Margin = new Thickness(0, 4, 0, 0)
		};
		contacts.Children.Add(ContactChip("群号", GroupNumber));
		contacts.Children.Add(ContactChip("QQ", OwnerQq));
		panel.Children.Add(contacts);
		return panel;
	}

	/// <summary>构造元素之诗超限提醒的弹窗内容。</summary>
	public static UIElement BuildElementPoetryContent(int willBeCount)
	{
		var panel = new StackPanel { Spacing = 9 };
		panel.Children.Add(new TextBlock
		{
			Text = "这是第 " + willBeCount + " 个元素之诗机器人代理。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13,
			FontWeight = FontWeights.SemiBold
		});
		panel.Children.Add(new TextBlock
		{
			Text = "元素之诗服务侧目前已知对同一个出口 IP、同一台客户端上的并发账号比较敏感，上限为五个账号。"
				+ "超过后踢包返回信息是：「启动的账号已达上限！」，此时全部已连账号都会一起被踢下线。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13
		});
		panel.Children.Add(new TextBlock
		{
			Text = "元素之诗服务器目前已知的敏感地带：恶性多开账号打金、多开号进服占用服务器资源、"
				+ "多账号角色重连卡在等待大厅并多次重复重连 —— 均会遭到指定惩处以及封号。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13
		});
		panel.Children.Add(new TextBlock
		{
			Text = "本程序不会拦你，请自主控制节奏。使用此功能后默认自主承担以上风险。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13,
			Opacity = 0.8
		});
		return panel;
	}

	/// <summary>单客户端会话达到 10 个时的提醒（不拦截，只说明风险与自动过渡结果）。</summary>
	public static UIElement BuildSessionLimitContent(string clientName, int count, bool overflowed)
	{
		var panel = new StackPanel { Spacing = 9 };
		panel.Children.Add(new TextBlock
		{
			Text = "「" + clientName + "」当前已有 " + count + " 个机器人会话。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13,
			FontWeight = FontWeights.SemiBold
		});
		panel.Children.Add(new TextBlock
		{
			Text = "单个客户端开满 10 个会话已经是实测极限：每个会话约 200MB，"
				+ "第 11 个开始会让游戏渲染池的直存一次性吃满，表现就是爆内存后直接崩溃、"
				+ "画面变蓝屏、之后连机器人都挂不上。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13
		});
		panel.Children.Add(new TextBlock
		{
			Text = overflowed
				? "本程序不会锁你的操作：超出的会话已经自动过渡到第二个同服客户端，"
					+ "请到那台客户端里继续增加会话数。两台客户端都要保持运行，机器人才能全部在线。"
				: "本程序不会锁你的操作，但当前没有第二个同服客户端可以承接超出的会话。"
					+ "继续增加只会让这一台客户端爆内存；建议在 PCL 里再复制一份同名挂号版实例，"
					+ "程序会自动把超出 10 个的会话过渡过去，会话数可以继续往上加。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13
		});
		panel.Children.Add(new TextBlock
		{
			Text = "本提醒每个客户端最多出现 3 次。使用此功能后默认自主承担以上风险。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 12.5,
			Opacity = 0.75
		});
		return panel;
	}

	private static TextBlock Bullet(string text)
	{
		return new TextBlock
		{
			Text = "· " + text,
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13,
			Margin = new Thickness(6, 0, 0, 0),
			Opacity = 0.92
		};
	}

	/// <summary>「群号 1124833900 复制」这类小条：数字右侧紧跟一个复制按钮。</summary>
	private static FrameworkElement ContactChip(string label, string value)
	{
		var border = new Border
		{
			CornerRadius = new CornerRadius(6),
			BorderThickness = new Thickness(1),
			BorderBrush = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)),
			Padding = new Thickness(10, 4, 4, 4),
			Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128))
		};
		var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
		row.Children.Add(new TextBlock
		{
			Text = label,
			FontSize = 12.5,
			Opacity = 0.65,
			VerticalAlignment = VerticalAlignment.Center
		});
		row.Children.Add(new TextBlock
		{
			Text = value,
			FontSize = 13.5,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			IsTextSelectionEnabled = true
		});
		var copy = new Button
		{
			Content = "复制",
			FontSize = 11.5,
			Padding = new Thickness(8, 2, 8, 2),
			MinHeight = 24
		};
		copy.Click += (_, _) =>
		{
			try
			{
				var package = new DataPackage();
				package.SetText(value);
				Clipboard.SetContent(package);
				Clipboard.Flush();
				MainWindow.Instance?.ShowToast("已复制" + label + "：" + value);
				copy.Content = "已复制";
			}
			catch (Exception ex)
			{
				MainWindow.Instance?.ShowToast("复制失败：" + ex.Message, ok: false);
			}
		};
		row.Children.Add(copy);
		border.Child = row;
		return border;
	}
}

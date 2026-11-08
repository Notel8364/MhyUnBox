using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MhyunBox.Services;

/// <summary>
/// 社区 / 反馈通道：QQ 群是更新 DLL、反馈崩溃与功能建议的唯一入口。
///
/// 加群方式：复制群号 + 唤起已登录的 QQ 主窗口，用户在顶部搜索框粘贴群号回车加入。
/// 新版 QQ（NT 9.9.x）已移除 mqqapi 群名片支持，继续用旧协议只会弹
/// 「当前QQ暂不支持该内容」，因此这里不再使用任何 mqqapi:// 链接。
/// </summary>
public static class CommunityChannel
{
	/// <summary>反馈 / 更新群号。</summary>
	public const string QqGroupNumber = "1124833900";

	/// <summary>QQ 群官网（浏览器入口，仅作参考页）。</summary>
	public const string QqGroupWebUrl = "https://qun.qq.com/";

	/// <summary>加群提示统一文案。</summary>
	public const string JoinHint = "群号 " + QqGroupNumber + "（更新 DLL / 反馈问题 / 崩溃日志）";

	/// <summary>
	/// 一键加群：新版 QQ（NT 9.9.x）已不再支持 mqqapi 群名片，直接唤会弹
	/// 「当前QQ暂不支持该内容」。改为复制群号并唤起 QQ 主窗口，
	/// 用户在顶部搜索框粘贴群号回车即可找到本群。
	/// </summary>
	public static void OpenQqGroup()
	{
		bool copied = TryCopyGroupNumber();
		bool opened = ActivateOrStartQq();
		string tip = copied
			? (opened
				? "已复制群号 " + QqGroupNumber + "，在 QQ 顶部搜索框粘贴后回车即可加群"
				: "已复制群号 " + QqGroupNumber + "，请打开 QQ 搜索该群")
			: "请手动记下群号 " + QqGroupNumber + "，在 QQ 中搜索加群";
		LogHelper.Log("[加群] " + tip);
		MainWindow.Instance?.ShowToast(tip, copied && opened);
	}

	/// <summary>把已登录的 QQ 窗口拉到前台；没有运行则尝试启动本机 QQ。</summary>
	private static bool ActivateOrStartQq()
	{
		try
		{
			foreach (Process process in Process.GetProcessesByName("QQ"))
			{
				if (process.MainWindowHandle != IntPtr.Zero)
				{
					ShowWindow(process.MainWindowHandle, 9);
					SetForegroundWindow(process.MainWindowHandle);
					return true;
				}
			}
			string? exe = FindQqExecutable();
			if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
			{
				Process.Start(new ProcessStartInfo(exe)
				{
					UseShellExecute = true
				});
				return true;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[加群] 唤起 QQ 失败");
		}
		return false;
	}

	/// <summary>定位 QQ.exe：优先已运行进程，其次常见安装目录。</summary>
	private static string? FindQqExecutable()
	{
		foreach (Process process in Process.GetProcessesByName("QQ"))
		{
			try
			{
				string? path = process.MainModule?.FileName;
				if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
				{
					return path;
				}
			}
			catch
			{
			}
		}
		string[] candidates = new string[]
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "QQNT", "QQ.exe"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "QQNT", "QQ.exe"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "QQ", "Bin", "QQ.exe"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tencent", "QQ", "QQ.exe")
		};
		foreach (string candidate in candidates)
		{
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}
		return null;
	}

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	/// <summary>复制群号到剪贴板（供反馈时粘贴）。</summary>
	public static bool TryCopyGroupNumber()
	{
		try
		{
			DataPackage package = new DataPackage();
			package.SetText(QqGroupNumber);
			Clipboard.SetContent(package);
			Clipboard.Flush();
			return true;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[加群] 复制群号失败");
			return false;
		}
	}

	/// <summary>复制群号并提示（按钮点击用）。</summary>
	public static void CopyGroupNumberWithToast()
	{
		bool ok = TryCopyGroupNumber();
		MainWindow.Instance?.ShowToast(ok ? "已复制 QQ 群号 " + QqGroupNumber : "复制失败，请手动记下群号 " + QqGroupNumber, ok);
	}

	/// <summary>加群提示弹窗：构建时间不一致 / 版本落后时使用。</summary>
	public static async Task ShowJoinPromptAsync(string title, string message, string primaryText = "一键加群")
	{
		try
		{
			MainWindow window = MainWindow.Instance;
			if (window == null || window.Content == null || window.Content.XamlRoot == null)
			{
				window?.ShowToast(message + " " + JoinHint);
				return;
			}
			ContentDialog dialog = new ContentDialog
			{
				Title = title,
				Content = message,
				PrimaryButtonText = primaryText,
				CloseButtonText = "稍后",
				DefaultButton = ContentDialogButton.Primary,
				XamlRoot = window.Content.XamlRoot
			};
			ContentDialogResult result = await DialogGate.ShowAsync(dialog);
			if (result == ContentDialogResult.Primary)
			{
				OpenQqGroup();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[加群] 弹窗打开失败");
		}
	}
}

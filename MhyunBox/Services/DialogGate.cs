using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.UI;

namespace MhyunBox.Services;

public static class DialogGate
{
	private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

	private static bool _dialogVisible;

	private const int HTCAPTION = 2;

	private const uint WM_NCLBUTTONDOWN = 161u;

	public static bool IsDialogVisible => _dialogVisible;

	public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
	{
		if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5.0)))
		{
			LogHelper.LogError(new TimeoutException("DialogGate 门控等待超时（可能有弹窗未正常关闭）"), "弹窗打开失败");
			MainWindow.Instance?.ShowToast("弹窗打开异常，请重试", false);
		}
		try
		{
			_dialogVisible = true;
			try
			{
				return await dialog.ShowAsync();
			}
			// HResult 是 int（0x8000FFFF 这类在 int 里是负数），直接和 uint 常量比较永远不成立，
			// 导致这条「弹窗宿主已销毁」的兜底从来没生效过；先转成无符号再比。
			catch (COMException ex) when (unchecked((uint)ex.HResult) == 2147483673u)
			{
				return ContentDialogResult.None;
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "弹窗显示失败");
				MainWindow.Instance?.ShowToast("弹窗打开失败：" + ex.Message, false);
				return ContentDialogResult.None;
			}
			finally
			{
				_dialogVisible = false;
			}
		}
		finally
		{
			try
			{
				_gate.Release();
			}
			catch (SemaphoreFullException)
			{
			}
		}
	}

	public static FrameworkElement CreateDragLabel(string title, string? hint = null)
	{
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 6.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		TextBlock item = new TextBlock
		{
			Text = title,
			FontSize = 15.0,
			FontWeight = FontWeights.SemiBold,
			TextWrapping = TextWrapping.Wrap
		};
		stackPanel.Children.Add(item);
		stackPanel.Children.Add(CreateDragBar("按住此条拖动窗口"));
		if (!string.IsNullOrWhiteSpace(hint))
		{
			stackPanel.Children.Add(new TextBlock
			{
				Text = hint,
				FontSize = 12.0,
				Opacity = 0.7,
				TextWrapping = TextWrapping.Wrap
			});
		}
		return stackPanel;
	}

	public static FrameworkElement CreateDragBar(string hint = "按住此处拖动窗口")
	{
		Grid bar = new Grid
		{
			Height = 32.0,
			Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
			CornerRadius = new CornerRadius(6.0),
			VerticalAlignment = VerticalAlignment.Top
		};
		bar.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		bar.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		TextBlock textBlock = new TextBlock
		{
			Text = "≡",
			FontSize = 16.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10.0, 0.0, 6.0, 0.0),
			Opacity = 0.7
		};
		Grid.SetColumn(textBlock, 0);
		bar.Children.Add(textBlock);
		TextBlock textBlock2 = new TextBlock
		{
			Text = hint,
			FontSize = 12.0,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			Opacity = 0.55
		};
		Grid.SetColumn(textBlock2, 1);
		bar.Children.Add(textBlock2);
		bool dragging = false;
		bar.AddHandler(UIElement.PointerPressedEvent, (PointerEventHandler)delegate(object sender, PointerRoutedEventArgs e)
		{
			if (!dragging && e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed)
			{
				nint num = MainWindowHandle();
				if (num != IntPtr.Zero)
				{
					dragging = true;
					SendMessageW(num, 161u, 2, IntPtr.Zero);
					dragging = false;
				}
			}
		}, handledEventsToo: true);
		PointerEventHandler handler = delegate
		{
			dragging = false;
		};
		bar.AddHandler(UIElement.PointerReleasedEvent, handler, handledEventsToo: true);
		bar.AddHandler(UIElement.PointerCanceledEvent, handler, handledEventsToo: true);
		bar.Unloaded += delegate
		{
			dragging = false;
		};
		return bar;
	}

	private static nint MainWindowHandle()
	{
		try
		{
			if (MainWindow.Instance != null)
			{
				return WindowNative.GetWindowHandle(MainWindow.Instance);
			}
		}
		catch
		{
		}
		return IntPtr.Zero;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
}

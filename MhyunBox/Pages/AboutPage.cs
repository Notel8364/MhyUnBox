using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using MhyunBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace MhyunBox.Pages;

public sealed partial class AboutPage : Page
{
	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel AboutRoot;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Grid AboutCardGrid;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Border UsageCard;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private StackPanel RightCol;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Border CopyrightCard;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Border DonateCard;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private TextBlock DonateQrPlaceholder;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Image DonateQr;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private Image AppIcon;

	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	private bool _contentLoaded;

	public AboutPage()
	{
		InitializeComponent();
		string text = Path.Combine(AppContext.BaseDirectory, "Assets", "reimu.jpg");
		if (File.Exists(text))
		{
			AppIcon.Source = new BitmapImage(new Uri(text));
		}
		string text2 = Path.Combine(AppContext.BaseDirectory, "Assets", "收款码.png");
		if (File.Exists(text2))
		{
			DonateQr.Source = new BitmapImage(new Uri(text2));
			DonateQr.Visibility = Visibility.Visible;
			DonateQrPlaceholder.Visibility = Visibility.Collapsed;
		}
		// 爱发电支持入口（收款码下方）
		try
		{
			StackPanel stackPanel = DonateCard.Child as StackPanel;
			if (stackPanel != null)
			{
				stackPanel.Children.Add(new HyperlinkButton
				{
					Content = "♥ 爱发电支持（afdian.com/a/Notel）",
					NavigateUri = new Uri("https://afdian.com/a/Notel"),
					HorizontalAlignment = HorizontalAlignment.Center,
					FontSize = 13.0
				});
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "添加爱发电支持入口失败");
		}
		// 开源版：移除版权信息卡片与署名文本
		try
		{
			RightCol?.Children.Remove(CopyrightCard);
			if (AboutRoot != null)
			{
				for (int i = AboutRoot.Children.Count - 1; i >= 0; i--)
				{
					if (AboutRoot.Children[i] is TextBlock tb && (tb.Text ?? "").StartsWith("Powered By", StringComparison.Ordinal))
					{
						AboutRoot.Children.RemoveAt(i);
					}
				}
			}
		}
		catch (Exception ex2)
		{
			LogHelper.LogError(ex2, "关于页开源化调整失败");
		}
		// 「使用须知」整段去掉，只保留「反馈情况」（.xbf 里的内容运行时无法重编，这里在可视树上裁）
		try
		{
			if (UsageCard?.Child is StackPanel usage)
			{
				int feedbackIdx = -1;
				for (int i = 0; i < usage.Children.Count; i++)
				{
					if (usage.Children[i] is TextBlock fb && fb.Text == "反馈情况")
					{
						feedbackIdx = i;
						break;
					}
				}
				if (feedbackIdx > 0)
				{
					for (int i = feedbackIdx - 1; i >= 0; i--)
					{
						usage.Children.RemoveAt(i);
					}
				}
			}
		}
		catch (Exception ex3)
		{
			LogHelper.LogError(ex3, "关于页使用须知裁剪失败");
		}
		// 底部署名：Powered By OpenCode & TRAE · AI IDE
		try
		{
			StackPanel powered = new StackPanel
			{
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(0.0, 4.0, 0.0, 10.0),
				Orientation = Orientation.Horizontal,
				Spacing = 2.0
			};
			powered.Children.Add(new TextBlock
			{
				Text = "Powered By ",
				FontSize = 12.0,
				Opacity = 0.65,
				VerticalAlignment = VerticalAlignment.Center
			});
			HyperlinkButton openCodeLink = new HyperlinkButton
			{
				Content = "OpenCode",
				NavigateUri = new Uri("https://opencode.ai/"),
				FontSize = 12.0,
				FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
				Padding = new Thickness(2.0),
				MinWidth = 0.0,
				MinHeight = 0.0
			};
			ToolTipService.SetToolTip(openCodeLink, "OpenCode · 开源 AI 编程助手");
			powered.Children.Add(openCodeLink);
			powered.Children.Add(new TextBlock
			{
				Text = " & ",
				FontSize = 12.0,
				Opacity = 0.65,
				VerticalAlignment = VerticalAlignment.Center
			});
			HyperlinkButton traeLink = new HyperlinkButton
			{
				Content = "TRAE",
				NavigateUri = new Uri("https://www.trae.cn/"),
				FontSize = 12.0,
				Padding = new Thickness(2.0),
				MinWidth = 0.0,
				MinHeight = 0.0
			};
			ToolTipService.SetToolTip(traeLink, "TRAE · AI IDE");
			powered.Children.Add(traeLink);
			powered.Children.Add(new TextBlock
			{
				Text = "&",
				FontSize = 12.0,
				Opacity = 0.45,
				Margin = new Thickness(2.0, 0.0, 2.0, 0.0),
				VerticalAlignment = VerticalAlignment.Center
			});
			// WorkBuddy 署名：与前面两个 IDE 署名同排，用强调色+半粗体区分，点击跳转官网
			HyperlinkButton workBuddyLink = new HyperlinkButton
			{
				Content = "WorkBuddy",
				NavigateUri = new Uri("https://www.workbuddy.cn/"),
				FontSize = 12.0,
				FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
				Padding = new Thickness(3.0, 2.0, 3.0, 2.0),
				MinWidth = 0.0,
				MinHeight = 0.0,
				CornerRadius = new CornerRadius(4.0)
			};
			ToolTipService.SetToolTip(workBuddyLink, "WorkBuddy · AI 编程助手（本版本优化与维护）");
			// 强调色资源在部分主题/运行时下可能缺失，取不到就用默认前景色，不因此丢掉整条署名
			if (Application.Current?.Resources != null && Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out object accent) && accent is Microsoft.UI.Xaml.Media.Brush accentBrush)
			{
				workBuddyLink.Foreground = accentBrush;
			}
			powered.Children.Add(workBuddyLink);
			powered.Children.Add(new TextBlock
			{
				Text = "· AI IDE",
				FontSize = 12.0,
				Opacity = 0.65,
				VerticalAlignment = VerticalAlignment.Center
			});
			AboutRoot?.Children.Add(powered);
		}
		catch (Exception ex4)
		{
			LogHelper.LogError(ex4, "关于页 Powered By 添加失败");
		}
		base.Loaded += delegate
		{
			AppVersion.ApplyTo(AboutRoot);
			RelayoutAbout(base.ActualWidth);
		};
	}

	private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		RelayoutAbout(e.NewSize.Width);
	}

	private void RelayoutAbout(double width)
	{
		bool flag = width >= 1200.0;
		AboutRoot.HorizontalAlignment = (flag ? HorizontalAlignment.Center : HorizontalAlignment.Left);
		AboutRoot.MaxWidth = (flag ? 1000 : 640);
		AboutCardGrid.RowDefinitions.Clear();
		AboutCardGrid.ColumnDefinitions.Clear();
		if (flag)
		{
			AboutCardGrid.ColumnDefinitions.Add(new ColumnDefinition());
			AboutCardGrid.ColumnDefinitions.Add(new ColumnDefinition());
			Grid.SetRow(UsageCard, 0);
			Grid.SetColumn(UsageCard, 0);
			Grid.SetRow(RightCol, 0);
			Grid.SetColumn(RightCol, 1);
		}
		else
		{
			AboutCardGrid.ColumnDefinitions.Add(new ColumnDefinition());
			AboutCardGrid.RowDefinitions.Add(new RowDefinition());
			AboutCardGrid.RowDefinitions.Add(new RowDefinition());
			Grid.SetRow(UsageCard, 0);
			Grid.SetColumn(UsageCard, 0);
			Grid.SetRow(RightCol, 1);
			Grid.SetColumn(RightCol, 0);
		}
	}

	private async void DonateQr_Tapped(object sender, TappedRoutedEventArgs e)
	{
		MainWindow win = MainWindow.Instance;
		if (!(win == null) && !(DonateQr.Source == null))
		{
			double boxWidth = 560.0;
			double boxHeight = 560.0;
			if (base.XamlRoot != null && base.XamlRoot.Size.Width > 240.0 && base.XamlRoot.Size.Height > 240.0)
			{
				boxWidth = Math.Min(560.0, Math.Max(200.0, base.XamlRoot.Size.Width - 140.0));
				boxHeight = Math.Min(560.0, Math.Max(200.0, base.XamlRoot.Size.Height - 180.0));
			}
			ImageSource imageSource = await PrepareCrispQrAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "收款码.png"), boxWidth, boxHeight);
			bool flag = imageSource != null;
			if ((object)imageSource == null)
			{
				imageSource = DonateQr.Source;
			}
			double num = boxWidth;
			double num2 = boxHeight;
			BitmapSource bitmapSource = imageSource as BitmapSource;
			if ((object)bitmapSource != null && bitmapSource.PixelWidth > 0 && bitmapSource.PixelHeight > 0)
			{
				num = bitmapSource.PixelWidth;
				num2 = bitmapSource.PixelHeight;
			}
			Grid overlay = new Grid();
			Border border = new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0))
			};
			border.Tapped += delegate
			{
				win.CloseWindowOverlay(overlay);
			};
			Border border2 = new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(245, 32, 32, 32)),
				BorderBrush = new SolidColorBrush(Color.FromArgb(90, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
				BorderThickness = new Thickness(1.0),
				CornerRadius = new CornerRadius(16.0),
				Padding = new Thickness(24.0),
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				MaxWidth = boxWidth + 60.0,
				MaxHeight = boxHeight + 100.0
			};
			Grid grid = new Grid();
			Image item = new Image
			{
				Source = imageSource,
				Stretch = ((!flag) ? Stretch.Uniform : Stretch.None),
				Width = (flag ? num : boxWidth),
				Height = (flag ? num2 : boxHeight)
			};
			Button button = new Button
			{
				Content = "✕",
				FontSize = 14.0,
				Width = 36.0,
				Height = 36.0,
				Padding = new Thickness(0.0),
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0.0, 8.0, 8.0, 0.0)
			};
			ToolTipService.SetToolTip(button, "关闭（Esc）");
			button.Click += delegate
			{
				win.CloseWindowOverlay(overlay);
			};
			grid.Children.Add(item);
			grid.Children.Add(button);
			border2.Child = grid;
			overlay.Children.Add(border);
			overlay.Children.Add(border2);
			win.ShowWindowOverlay(overlay);
		}
	}

	private static async Task<ImageSource?> PrepareCrispQrAsync(string path, double boxWidth, double boxHeight)
	{
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}
			using IRandomAccessStream fileStream = await (await StorageFile.GetFileFromPathAsync(path)).OpenAsync(FileAccessMode.Read);
			BitmapDecoder bitmapDecoder = await BitmapDecoder.CreateAsync(fileStream);
			int width = (int)bitmapDecoder.PixelWidth;
			int height = (int)bitmapDecoder.PixelHeight;
			if (width <= 0 || height <= 0)
			{
				return null;
			}
			int scale = (int)Math.Floor(Math.Min(boxWidth / (double)width, boxHeight / (double)height));
			if (scale <= 1)
			{
				return null;
			}
			byte[] array = (await bitmapDecoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
			int num = width * scale;
			int num2 = height * scale;
			int num3 = num * 4;
			byte[] array2 = new byte[num3 * num2];
			for (int i = 0; i < num2; i++)
			{
				int num4 = i / scale;
				int num5 = i * num3;
				for (int j = 0; j < num; j++)
				{
					int num6 = (num4 * width + j / scale) * 4;
					int num7 = num5 + j * 4;
					array2[num7] = array[num6];
					array2[num7 + 1] = array[num6 + 1];
					array2[num7 + 2] = array[num6 + 2];
					array2[num7 + 3] = array[num6 + 3];
				}
			}
			WriteableBitmap writeableBitmap = new WriteableBitmap(num, num2);
			using (Stream stream = writeableBitmap.PixelBuffer.AsStream())
			{
				stream.Write(array2, 0, array2.Length);
			}
			writeableBitmap.Invalidate();
			return writeableBitmap;
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			LogHelper.LogError(ex2, "收款码放大渲染失败");
			return null;
		}
	}

	
	
	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
	[DebuggerNonUserCode]
	public IComponentConnector GetBindingConnector(int connectionId, object target)
	{
		return null;
	}
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Settings;
using MhyunBox.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace MhyunBox.Pages;

// 网速测试页：纯 C# 复刻 LibreSpeed 原理（下载=多线程拉数据计时 / 上传=POST 随机数据 / ping=TTFB）。
// 主视觉为 12 秒固定窗口的实时速率面积图（当前值 + 峰值 + 波动趋势），
// 末尾给出「是否适合开始高强度长时间攻略副本」的推荐值判定。
public sealed class SpeedTestPage : Page
{
	private readonly DispatcherQueue _queue;

	private TextBox _urlBox;

	private ComboBox _presetCombo;

	private Button _useLastBtn;

	private Button _startBtn;

	private Button _stopBtn;

	// 实时速率曲线（已移除原半圆仪表盘/指针：仪表只有一个瞬时值，看不出波动；
	// 改为 12 秒固定窗口的面积图，能同时看到当前值、峰值和整段稳定性）
	private Canvas _chartCanvas;

	private Path _areaPath;

	private Path _linePath;

	private TextBlock _speedNowText;

	private TextBlock _peakText;

	private readonly List<TextBlock> _chartYLabels = new List<TextBlock>();

	// 采样序列：(相对秒, Mbps)
	private readonly List<(double Sec, double Mbps)> _samples = new List<(double, double)>();

	private readonly Stopwatch _chartSw = new Stopwatch();

	private double _chartMax = 10.0;

	private TextBlock _pingText;

	private TextBlock _downText;

	private TextBlock _upText;

	private TextBlock _verdictText;

	private TextBlock _logText;

	private ScrollViewer _logScroller;

	private CancellationTokenSource? _cts;

	// 曲线画布几何：固定 12 秒窗口（与下载测速时长一致），左侧留 Y 轴、底部留 X 轴
	private const double ChartW = 560.0;

	private const double ChartH = 190.0;

	private const double PadL = 46.0;

	private const double PadR = 10.0;

	private const double PadT = 12.0;

	private const double PadB = 22.0;

	private const double WindowSec = 12.0;

	public SpeedTestPage()
	{
		_queue = base.DispatcherQueue;
		Content = BuildUi();
	}

	private FrameworkElement BuildUi()
	{
		Grid root = new Grid();
		ScrollViewer scroller = new ScrollViewer
		{
			Content = new StackPanel
			{
				Padding = new Thickness(28.0, 24.0, 28.0, 24.0),
				Spacing = 10.0
			}
		};
		root.Children.Add(scroller);
		StackPanel sp = (StackPanel)scroller.Content;
		sp.Children.Add(new TextBlock
		{
			Text = "网速测试",
			Style = (Style)Application.Current.Resources["TitleTextBlockStyle"]
		});
		sp.Children.Add(new TextBlock
		{
			Text = "原理：与开源 LibreSpeed 相同 —— 下载测速=多线程拉取数据计时，上传=POST 随机数据，延迟=请求首字节时间。默认测 Cloudflare 节点；把「更新客户端」页的 item_*.7z 链接粘贴到下面，可测到网易下载服务器的真实速度。",
			FontSize = 12.0,
			Opacity = 0.8,
			TextWrapping = TextWrapping.Wrap
		});
		// —— 测速节点预设：国内网络对 Cloudflare 不稳定，默认给国内可达的节点 ——
		_presetCombo = new ComboBox
		{
			Header = "测速节点",
			MinWidth = 220.0,
			SelectedIndex = 0
		};
		ThemeHelper.SolidComboPopup(_presetCombo);
		_presetCombo.Items.Add("腾讯软件源（国内推荐）");
		_presetCombo.Items.Add("Cloudflare（国际）");
		_presetCombo.Items.Add("网易下载 CDN（自定义链接测）");
		_presetCombo.Items.Add("自定义 URL");
		sp.Children.Add(_presetCombo);
		_urlBox = new TextBox
		{
			PlaceholderText = "自定义测速 URL（选「自定义 URL」时生效；粘贴 item_*.7z 链接即测网易 CDN）",
			Header = "自定义地址（可选）"
		};
		sp.Children.Add(_urlBox);
		_presetCombo.SelectionChanged += delegate
		{
			// 选中「自定义 URL」或「网易下载 CDN」时聚焦输入框，其余预设自动填参考地址
			int idx = _presetCombo.SelectedIndex;
			if (idx == 1 && _urlBox.Text.Length == 0)
			{
				_urlBox.Text = "https://speed.cloudflare.com/__down?bytes=104857600";
			}
		};
		_useLastBtn = new Button
		{
			Content = "使用最近资源包地址"
		};
		_useLastBtn.Click += delegate
		{
			string text = SettingsManager.Instance.LastResPkgUrl;
			if (!string.IsNullOrWhiteSpace(text))
			{
				_urlBox.Text = text;
			}
			else
			{
				AppendLog("还没有最近使用的资源包地址（先在「更新客户端」页更新一次）");
			}
		};
		StackPanel sp2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 10.0
		};
		_startBtn = new Button
		{
			Content = "开始测速",
			MinWidth = 110.0
		};
		_startBtn.Click += delegate
		{
			StartAsync();
		};
		_stopBtn = new Button
		{
			Content = "停止",
			IsEnabled = false
		};
		_stopBtn.Click += delegate
		{
			_cts?.Cancel();
		};
		sp2.Children.Add(_startBtn);
		sp2.Children.Add(_stopBtn);
		sp2.Children.Add(_useLastBtn);
		sp.Children.Add(sp2);
		// —— 实时速率曲线卡片（替代原半圆仪表盘） ——
		Border card = new Border
		{
			CornerRadius = new CornerRadius(10.0),
			Padding = new Thickness(18.0, 14.0, 18.0, 10.0),
			Background = new SolidColorBrush(Color.FromArgb(30, 127, 127, 127)),
			HorizontalAlignment = HorizontalAlignment.Center
		};
		StackPanel cardStack = new StackPanel
		{
			Spacing = 2.0
		};
		// 顶部读数：当前速率（大字）+ 峰值（右对齐小字）
		Grid headGrid = new Grid();
		headGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		headGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		StackPanel headLeft = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6.0
		};
		_speedNowText = new TextBlock
		{
			Text = "0.0",
			FontSize = 38.0,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
		};
		headLeft.Children.Add(_speedNowText);
		headLeft.Children.Add(new TextBlock
		{
			Text = "Mbps",
			FontSize = 14.0,
			Opacity = 0.7,
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(0.0, 0.0, 0.0, 7.0)
		});
		Grid.SetColumn(headLeft, 0);
		headGrid.Children.Add(headLeft);
		_peakText = new TextBlock
		{
			Text = "峰值 —",
			FontSize = 12.0,
			Opacity = 0.75,
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		};
		Grid.SetColumn(_peakText, 1);
		headGrid.Children.Add(_peakText);
		cardStack.Children.Add(headGrid);
		// 曲线画布：网格线/坐标轴由 BuildChartChrome 一次性铺好，曲线两条 Path 动态重绘
		_chartCanvas = new Canvas
		{
			Width = ChartW,
			Height = ChartH
		};
		BuildChartChrome();
		_areaPath = new Path();
		LinearGradientBrush areaBrush = new LinearGradientBrush
		{
			StartPoint = new Windows.Foundation.Point(0.0, 0.0),
			EndPoint = new Windows.Foundation.Point(0.0, 1.0)
		};
		areaBrush.GradientStops.Add(new GradientStop
		{
			Color = Color.FromArgb(110, 0, 120, 212),
			Offset = 0.0
		});
		areaBrush.GradientStops.Add(new GradientStop
		{
			Color = Color.FromArgb(0, 0, 120, 212),
			Offset = 1.0
		});
		_areaPath.Fill = areaBrush;
		_linePath = new Path
		{
			Stroke = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212)),
			StrokeThickness = 2.0,
			StrokeLineJoin = PenLineJoin.Round,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round
		};
		_chartCanvas.Children.Add(_areaPath);
		_chartCanvas.Children.Add(_linePath);
		cardStack.Children.Add(_chartCanvas);
		cardStack.Children.Add(new TextBlock
		{
			Text = "实时速率（0–12 秒）",
			FontSize = 11.0,
			Opacity = 0.6
		});
		card.Child = cardStack;
		sp.Children.Add(card);
		// —— 结果 ——
		StackPanel sp3 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 30.0,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		_pingText = ResultText();
		_downText = ResultText();
		_upText = ResultText();
		sp3.Children.Add(WrapResult("延迟 / 抖动", _pingText));
		sp3.Children.Add(WrapResult("下载", _downText));
		sp3.Children.Add(WrapResult("上传", _upText));
		sp.Children.Add(sp3);
		_verdictText = new TextBlock
		{
			Text = "",
			FontSize = 16.0,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			TextWrapping = TextWrapping.Wrap,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		sp.Children.Add(_verdictText);
		_logText = new TextBlock
		{
			FontFamily = new FontFamily("Consolas"),
			FontSize = 12.0,
			Opacity = 0.85,
			TextWrapping = TextWrapping.Wrap
		};
		_logScroller = new ScrollViewer
		{
			Content = _logText,
			Height = 160.0,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		sp.Children.Add(_logScroller);
		return root;
	}

	private static TextBlock ResultText()
	{
		return new TextBlock
		{
			Text = "—",
			FontSize = 18.0,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
		};
	}

	private static StackPanel WrapResult(string label, TextBlock value)
	{
		return new StackPanel
		{
			Children =
			{
				new TextBlock
				{
					Text = label,
					FontSize = 12.0,
					Opacity = 0.7
				},
				value
			}
		};
	}

	// 曲线底衬：4 条水平网格线 + Y 轴 3 档刻度 + X 轴 0/6/12s。
	// 网格线按「归一化比例」固定位置，纵向量程变化时只改 Y 标签文字，不必重画网格。
	private void BuildChartChrome()
	{
		double plotH = ChartH - PadT - PadB;
		double plotW = ChartW - PadL - PadR;
		for (int i = 0; i <= 4; i++)
		{
			double f = (double)i / 4.0;
			double y = PadT + plotH * (1.0 - f);
			_chartCanvas.Children.Add(new Line
			{
				X1 = PadL,
				Y1 = y,
				X2 = PadL + plotW,
				Y2 = y,
				Stroke = new SolidColorBrush(Color.FromArgb(40, 127, 127, 127)),
				StrokeThickness = 1.0
			});
			// i=0/2/4 → 底部(0)、中部(半程)、顶部(满量程)
			if (i % 2 == 0)
			{
				TextBlock yLabel = new TextBlock
				{
					Text = "0",
					FontSize = 10.0,
					Opacity = 0.7
				};
				Canvas.SetLeft(yLabel, 8.0);
				Canvas.SetTop(yLabel, y - 7.0);
				_chartCanvas.Children.Add(yLabel);
				_chartYLabels.Add(yLabel);
			}
		}
		string[] xLabels = new string[3] { "0s", "6s", "12s" };
		for (int j = 0; j < xLabels.Length; j++)
		{
			double fx = (double)j / (double)(xLabels.Length - 1);
			TextBlock xLabel = new TextBlock
			{
				Text = xLabels[j],
				FontSize = 10.0,
				Opacity = 0.7
			};
			Canvas.SetLeft(xLabel, PadL + plotW * fx - ((j == xLabels.Length - 1) ? 20.0 : 4.0));
			Canvas.SetTop(xLabel, ChartH - PadB + 4.0);
			_chartCanvas.Children.Add(xLabel);
		}
		UpdateYLabels();
	}

	// 纵向量程变化时刷新 Y 轴 3 档文字（顺序：底 0 / 中 半程 / 顶 满量程）
	private void UpdateYLabels()
	{
		if (_chartYLabels.Count < 3)
		{
			return;
		}
		_chartYLabels[0].Text = "0";
		_chartYLabels[1].Text = (_chartMax / 2.0).ToString("0");
		_chartYLabels[2].Text = _chartMax.ToString("0");
	}

	// 每轮测速开始：清空采样、重置量程、重启计时（仅 UI 线程调用）
	private void ResetChart()
	{
		_samples.Clear();
		_chartMax = 10.0;
		UpdateYLabels();
		_areaPath.Data = null;
		_linePath.Data = null;
		_speedNowText.Text = "0.0";
		_peakText.Text = "峰值 —";
		_chartSw.Restart();
	}

	// 采样入口（后台线程亦可）：记一个 (秒, Mbps) 点 → 刷新大数字/峰值/曲线
	private void PushSample(double mbps)
	{
		if (mbps < 0.0)
		{
			mbps = 0.0;
		}
		double sec = Math.Min(_chartSw.Elapsed.TotalSeconds, WindowSec);
		_queue.TryEnqueue(delegate
		{
			_samples.Add((sec, mbps));
			if (mbps > _chartMax)
			{
				_chartMax = NiceCeil(mbps);
				UpdateYLabels();
			}
			_speedNowText.Text = mbps.ToString("0.0");
			double peak = 0.0;
			foreach ((double, double) s in _samples)
			{
				if (s.Item2 > peak)
				{
					peak = s.Item2;
				}
			}
			_peakText.Text = "峰值 " + peak.ToString("0.0") + " Mbps";
			RedrawChart();
		});
	}

	// 按 12 秒固定窗口把采样画成「渐变面积 + 折线」（仅 UI 线程调用）
	private void RedrawChart()
	{
		if (_samples.Count < 2)
		{
			return;
		}
		double plotW = ChartW - PadL - PadR;
		double plotH = ChartH - PadT - PadB;
		double baseline = PadT + plotH;
		PointCollection points = new PointCollection();
		foreach ((double, double) s in _samples)
		{
			double x = PadL + Math.Clamp(s.Item1 / WindowSec, 0.0, 1.0) * plotW;
			double y = baseline - Math.Clamp(s.Item2 / _chartMax, 0.0, 1.0) * plotH;
			points.Add(new Windows.Foundation.Point(x, y));
		}
		// 折线
		PathFigure lineFigure = new PathFigure
		{
			StartPoint = points[0],
			IsClosed = false
		};
		PolyLineSegment lineSeg = new PolyLineSegment();
		for (int i = 1; i < points.Count; i++)
		{
			lineSeg.Points.Add(points[i]);
		}
		lineFigure.Segments.Add(lineSeg);
		PathGeometry lineGeo = new PathGeometry();
		lineGeo.Figures.Add(lineFigure);
		_linePath.Data = lineGeo;
		// 面积：同一串点 + 收尾落回基线并闭合
		PathFigure areaFigure = new PathFigure
		{
			StartPoint = new Windows.Foundation.Point(points[0].X, baseline),
			IsClosed = true
		};
		PolyLineSegment areaSeg = new PolyLineSegment();
		for (int j = 0; j < points.Count; j++)
		{
			areaSeg.Points.Add(points[j]);
		}
		areaSeg.Points.Add(new Windows.Foundation.Point(points[points.Count - 1].X, baseline));
		areaFigure.Segments.Add(areaSeg);
		PathGeometry areaGeo = new PathGeometry();
		areaGeo.Figures.Add(areaFigure);
		_areaPath.Data = areaGeo;
	}

	// 收尾：大数字定格为最终测得值（曲线保持原样，不再补点）
	private void SetFinalSpeed(double mbps)
	{
		_queue.TryEnqueue(delegate
		{
			_speedNowText.Text = mbps.ToString("0.0");
		});
	}

	// 满刻度取整：10/20/50/100/200/500/1000/2000...
	private static double NiceCeil(double v)
	{
		double[] steps = new double[9] { 10.0, 20.0, 50.0, 100.0, 200.0, 500.0, 1000.0, 2000.0, 5000.0 };
		foreach (double num in steps)
		{
			if (v <= num)
			{
				return num;
			}
		}
		return Math.Ceiling(v / 10000.0) * 10000.0;
	}

	private void AppendLog(string msg)
	{
		_queue.TryEnqueue(delegate
		{
			TextBlock logText = _logText;
			logText.Text = logText.Text + msg + "\n";
			_logScroller.ChangeView(null, _logScroller.ScrollableHeight, null, disableAnimation: true);
		});
	}

	private async void StartAsync()
	{
		if (_cts != null)
		{
			return;
		}
		_cts = new CancellationTokenSource();
		CancellationToken ct = _cts.Token;
		SetBusy(busy: true);
		// 节点解析：0=腾讯软件源（默认，国内稳定） 1=Cloudflare 2=网易CDN（取最近资源包地址） 3=自定义
		int preset = _presetCombo?.SelectedIndex ?? 0;
		string url = _urlBox.Text.Trim();
		if (preset == 0)
		{
			url = "https://dldir1.qq.com/weixin/Windows/WeChatSetup.exe";
		}
		else if (preset == 2)
		{
			url = SettingsManager.Instance.LastResPkgUrl;
			if (string.IsNullOrWhiteSpace(url))
			{
				AppendLog("还没有最近资源包地址：先在「更新客户端」页更新一次，或直接选「自定义 URL」粘贴链接");
				SetBusy(busy: false);
				_cts = null;
				return;
			}
		}
		else if (preset == 3 || (preset == 1 && url.Length == 0))
		{
			if (url.Length == 0)
			{
				url = "https://speed.cloudflare.com/__down?bytes=104857600";
			}
		}
		try
		{
			using HttpClient http = new HttpClient();
			http.Timeout = TimeSpan.FromSeconds(30.0);
			http.DefaultRequestHeaders.UserAgent.ParseAdd("MhyunBox/7.7.0 SpeedTest");
			AppendLog("== 开始测速（节点：" + (preset == 0 ? "腾讯软件源" : preset == 2 ? "网易CDN" : url) + "）==");
			ResetChart();
			// 1) 延迟
			SetStatus("测延迟中...");
			(double, double) tuple = await MeasurePingAsync(http, url, ct);
			double item = tuple.Item1;
			double item2 = tuple.Item2;
			_queue.TryEnqueue(delegate
			{
				_pingText.Text = $"{item:N0} / {item2:N0} ms";
			});
			AppendLog($"延迟 {item:N1} ms，抖动 {item2:N1} ms");
			// 2) 下载（4 线程，最长 12s）
			SetStatus("测下载中...");
			double num = await MeasureDownloadAsync(http, url, ct);
			_queue.TryEnqueue(delegate
			{
				_downText.Text = num.ToString("N1") + " Mbps";
			});
			AppendLog($"下载 {num:N1} Mbps（{_lastDownMb:N0} MB / {_lastDownSec:N1}s）");
			// 3) 上传（上传端点只有 Cloudflare 提供，节点不可达时警告但不中断）
			SetStatus("测上传中...");
			double num2 = await MeasureUploadAsync(http, ct);
			_queue.TryEnqueue(delegate
			{
				_upText.Text = ((num2 > 0.0) ? (num2.ToString("N1") + " Mbps") : "不可用");
			});
			AppendLog((num2 > 0.0) ? $"上传 {num2:N1} Mbps" : "上传测速不可用（上传端点 speed.cloudflare.com 不可达，不影响下载/延迟结果）");
			SetFinalSpeed(num);
			// 4) 推荐值判定
			Verdict(num, item);
			SetStatus("完成");
		}
		catch (OperationCanceledException)
		{
			AppendLog("已停止");
			SetStatus("已停止");
		}
		catch (Exception ex)
		{
			AppendLog("ERROR: " + ex.Message);
			SetStatus("失败：" + ex.Message);
		}
		finally
		{
			SetBusy(busy: false);
			_cts = null;
		}
	}

	private void SetStatus(string s)
	{
		_queue.TryEnqueue(delegate
		{
			_verdictText.Text = s;
			_verdictText.Foreground = new SolidColorBrush(Colors.DimGray);
			_verdictText.FontSize = 14.0;
			_verdictText.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
		});
	}

	private void SetBusy(bool busy)
	{
		_queue.TryEnqueue(delegate
		{
			_startBtn.IsEnabled = !busy;
			_useLastBtn.IsEnabled = !busy;
			_stopBtn.IsEnabled = busy;
		});
	}

	// 推荐值判定：达标线（下载 ≥50 Mbps 且 延迟 ≤60ms）才建议开始高强度长时间攻略副本
	private void Verdict(double downMbps, double pingMs)
	{
		double num = 800.0 * 8.0 / Math.Max(downMbps, 0.1) / 60.0;
		string text = (downMbps >= 50.0 && pingMs <= 60.0) ? $"✅ 网速达标（下载 ≥50 Mbps，延迟 ≤60ms），可以开始高强度长时间攻略副本。整个 800MB 资源包约 {num:N0} 分钟下完。" : ((downMbps >= 20.0 && pingMs <= 100.0) ? $"⚠️ 基本够用（下载 {downMbps:N0} Mbps / 延迟 {pingMs:N0} ms），短时副本没问题；长时高强度副本可能偶有波动，800MB 资源包约 {num:N0} 分钟。" : $"❌ 不建议现在开始高强度长时间攻略：下载 {downMbps:N0} Mbps / 延迟 {pingMs:N0} ms 偏低，游戏卡顿掉线风险高，资源包也要 {num:N0} 分钟以上。");
		_queue.TryEnqueue(delegate
		{
			_verdictText.Text = text;
			_verdictText.FontSize = 15.0;
			_verdictText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
			_verdictText.Foreground = new SolidColorBrush(text[0] switch
			{
				'✅' => Colors.SeaGreen,
				'⚠' => Colors.DarkGoldenrod,
				_ => Colors.IndianRed
			});
		});
	}

	private double _lastDownMb;

	private double _lastDownSec;

	// 延迟：6 次小请求取 TTFB 平均（去掉第一次预热），抖动=相邻差平均；单次 6s 超时，全失败给明确报错
	private async Task<(double Avg, double Jitter)> MeasurePingAsync(HttpClient http, string url, CancellationToken ct)
	{
		List<double> list = new List<double>();
		int fails = 0;
		for (int i = 0; i < 6; i++)
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				using CancellationTokenSource per = CancellationTokenSource.CreateLinkedTokenSource(ct);
				per.CancelAfter(TimeSpan.FromSeconds(6.0));
				Stopwatch stopwatch = Stopwatch.StartNew();
				using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
				using HttpResponseMessage httpResponseMessage = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, per.Token);
				Stream stream = await httpResponseMessage.Content.ReadAsStreamAsync(per.Token);
				await stream.ReadAsync(new byte[64], per.Token);
				stream.Dispose();
				stopwatch.Stop();
				list.Add(stopwatch.Elapsed.TotalMilliseconds);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				fails++;
				if (fails == 1)
				{
					AppendLog("延迟探测失败：" + ex.Message + "（继续重试...）");
				}
			}
			await Task.Delay(120, ct);
		}
		if (list.Count == 0)
		{
			throw new InvalidOperationException("节点完全不可达（6 次延迟探测全部失败）——请在「测速节点」里换一个节点再试");
		}
		List<double> list2 = list.Skip(1).ToList();
		if (list2.Count == 0)
		{
			list2 = list;
		}
		double num = list2.Average();
		double num2 = 0.0;
		for (int j = 1; j < list2.Count; j++)
		{
			num2 += Math.Abs(list2[j] - list2[j - 1]);
		}
		return (num, ((list2.Count > 1) ? (num2 / (list2.Count - 1)) : 0.0));
	}

	// 下载：4 线程并发拉流 12 秒 + 独立采样器每 200ms 刷新仪表盘；最终取末段 7 秒滑动窗口。
	// 失败不再静默吞掉：全部失败时给出明确报错（这就是之前「0.0Mbps 指针不动」的根因）
	private async Task<double> MeasureDownloadAsync(HttpClient http, string url, CancellationToken ct)
	{
		long total = 0L;
		int fails = 0;
		object sync = new object();
		List<(double t, long bytes)> samples = new List<(double, long)>();
		Stopwatch sw = Stopwatch.StartNew();
		CancellationTokenSource cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
		ct = cts2.Token;
		// 独立采样器：每 200ms 记一次「累计字节数」，用滚动窗口首末差算瞬时速率。
		// 注意：不能再用下面的 samples —— 那份只在一条响应「完整读完」时才写入，
		// 大文件（100MB 级）12 秒窗口内根本读不完，历史点恒为空 → 速率恒算出 0
		// （这正是「大数字/峰值一直是 0、曲线贴底」的根因）。
		List<(double t, long bytes)> hist = new List<(double, long)>();
		Task sampler = Task.Run(async delegate
		{
			while (!ct.IsCancellationRequested && sw.Elapsed.TotalSeconds < 12.0)
			{
				long bytes;
				lock (sync)
				{
					bytes = total;
				}
				double t = sw.Elapsed.TotalMilliseconds;
				hist.Add((t, bytes));
				// 只保留最近 3 秒的窗口，避免用太久以前的低速启动段拉低读数
				while (hist.Count > 2 && t - hist[0].t > 3000.0)
				{
					hist.RemoveAt(0);
				}
				double mbps = 0.0;
				if (hist.Count >= 2)
				{
					(double, long) first = hist[0];
					double dt = (t - first.Item1) / 1000.0;
					if (dt > 0.2)
					{
						mbps = (double)(bytes - first.Item2) * 8.0 / 1048576.0 / dt;
					}
				}
				PushSample(mbps);
				try
				{
					await Task.Delay(200, ct);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		}, ct);
		List<Task> list = new List<Task>();
		for (int i = 0; i < 4; i++)
		{
			list.Add(Task.Run(async delegate
			{
				while (!ct.IsCancellationRequested && sw.Elapsed.TotalSeconds < 12.0)
				{
					try
					{
						using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
						using HttpResponseMessage httpResponseMessage = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
						if (!httpResponseMessage.IsSuccessStatusCode)
						{
							throw new InvalidOperationException("HTTP " + (int)httpResponseMessage.StatusCode);
						}
						using Stream stream = await httpResponseMessage.Content.ReadAsStreamAsync(ct);
						byte[] array = new byte[65536];
						int num;
						while ((num = await stream.ReadAsync(array, ct)) > 0)
						{
							lock (sync)
							{
								total += num;
							}
						}
						lock (sync)
						{
							samples.Add((sw.Elapsed.TotalMilliseconds, total));
						}
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception ex)
					{
						lock (sync)
						{
							fails++;
						}
						if (fails == 1)
						{
							AppendLog("下载线程失败：" + ex.Message + "（自动重试，若持续失败请换测速节点）");
						}
						try
						{
							await Task.Delay(300, ct);
						}
						catch (OperationCanceledException)
						{
							break;
						}
					}
				}
			}, ct));
		}
		try
		{
			await Task.WhenAll(list);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			cts2.Cancel();
			try
			{
				await sampler;
			}
			catch
			{
			}
		}
		long num4;
		double num3;
		lock (sync)
		{
			num4 = total;
			num3 = sw.Elapsed.TotalSeconds;
		}
		if (num4 <= 0L)
		{
			throw new InvalidOperationException("下载测速失败：0 字节——节点不可达或被拦截（" + fails + " 次连接失败）。请换测速节点（国内推荐「腾讯软件源」）");
		}
		double num5 = 0.0;
		foreach (var item2 in samples)
		{
			if (item2.t >= (num3 - 7.0) * 1000.0)
			{
				num5 = item2.bytes;
				break;
			}
		}
		double num6 = ((num3 > 7.0) ? ((num4 - num5) / (num3 - 7.0) * 8.0 / 1048576.0) : (num4 / Math.Max(num3, 0.5) * 8.0 / 1048576.0));
		_lastDownMb = num4 / 1048576.0;
		_lastDownSec = num3;
		return num6;
	}

	// 上传：4 线程向 Cloudflare __up POST 随机数据 10 秒；不可达时返回 0（由调用方提示），不中断测速
	private async Task<double> MeasureUploadAsync(HttpClient http, CancellationToken ct)
	{
		long total = 0L;
		object sync = new object();
		Stopwatch sw = Stopwatch.StartNew();
		byte[] array = new byte[1048576];
		new Random(42).NextBytes(array);
		List<Task> list = new List<Task>();
		for (int i = 0; i < 4; i++)
		{
			list.Add(Task.Run(async delegate
			{
				using MemoryStream memoryStream = new MemoryStream(array);
				while (!ct.IsCancellationRequested && sw.Elapsed.TotalSeconds < 10.0)
				{
					try
					{
						memoryStream.Seek(0L, SeekOrigin.Begin);
						using StreamContent content = new StreamContent(memoryStream);
						using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://speed.cloudflare.com/__up")
						{
							Content = content
						};
						using HttpResponseMessage httpResponseMessage = await http.SendAsync(request, ct);
						lock (sync)
						{
							total += array.Length;
						}
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch
					{
						try
						{
							await Task.Delay(300, ct);
						}
						catch (OperationCanceledException)
						{
							break;
						}
					}
				}
			}, ct));
		}
		try
		{
			await Task.WhenAll(list);
		}
		catch (OperationCanceledException)
		{
		}
		return total / Math.Max(sw.Elapsed.TotalSeconds, 0.5) * 8.0 / 1048576.0;
	}
}

using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MhyunBox.Pages;

/// <summary>
/// 恶搞按钮「不要点击」—— 机制参考 PCL 的愚人节彩蛋（AprilScaleTrans）。
///
/// PCL 原版做法（`Modules/ModMain.vb:754`）是：
///   · 按钮持续躲避鼠标（按鼠标反方向加速）；
///   · 移动时按方向做**非对称缩放**形成果冻拉扯：
///       ScaleX = (1 - (|Dir.X| - |Dir.Y|) * (Speed/160)).Clamp(0.2, 1.8)
///       ScaleY = (1 - (|Dir.Y| - |Dir.X|) * (Speed/100)).Clamp(0.2, 1.8)
///   · 撞到边界反弹；
///   · 累计移动距离达标后随机弹嘲讽提示；
///   · 留一个「小白旗」出口可以投降还原。
///
/// 这里用 C# 在 WinUI 3 上复刻同样的效果，并加了一点自己的东西：
/// 文字会随躲避进度升级，真被点到会"投降"并抖一下。
/// </summary>
public sealed class PrankButton : UserControl
{
	private readonly Button _btn;
	private readonly TranslateTransform _pos = new TranslateTransform();
	private readonly ScaleTransform _scale = new ScaleTransform();
	private readonly DispatcherQueueTimer _timer;
	private readonly Random _rnd = new Random();

	// 当前位置 / 速度（相对父容器左上角）
	private double _x;
	private double _y;
	private double _vx;
	private double _vy;

	// 鼠标相对按钮中心的向量
	private double _mouseDx;
	private double _mouseDy;
	private bool _mouseInside;

	private double _traveled;
	private int _tauntStage;
	private bool _gaveUp;
	private int _surrenderCount;
	private bool _warned;
	private Grid? _stage;

	private Grid? _root;
	private bool _rootHooked;
	private DateTime _lastTick = DateTime.UtcNow;

	/// <summary>真被点到时触发（宿主可以决定要不要做点别的）。</summary>
	public event Action? Surrendered;

	private static readonly string[] Taunts = new string[6]
	{
		"⚠ 不要点击",
		"说 了 别 点",
		"你追不上的 :)",
		"还在点？",
		"放弃抵抗吧",
		"真的…别点…"
	};

	private static readonly string[] Hints = new string[4]
	{
		"放弃吧，点右下角也追不上我的。",
		"看到没？我可是会跑的。",
		"这里建议放弃追逐呢.jpg",
		"我永远会在你手指到不了的地方等着你……"
	};

	public PrankButton()
	{
		_btn = new Button
		{
			Content = Taunts[0],
			MinWidth = 108.0,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top
		};
		// 一个控件只能有一个 RenderTransform，所以先套 Translate 再套 Scale 放进 TransformGroup
		TransformGroup group = new TransformGroup();
		group.Children.Add(_pos);
		group.Children.Add(_scale);
		_btn.RenderTransform = group;
		_btn.RenderTransformOrigin = new Point(0.5, 0.5);
		_btn.Click += OnClick;

		// 容器足够大，按钮才有地方躲（限制在一个 320x120 的小场地里）
		// 注意只能 Add 一次：集合初始化器再加一次会抛"元素已是另一个控件的子级"
		Grid stage = new Grid
		{
			Width = 320.0,
			Height = 120.0
		};
		stage.Children.Add(_btn);
		Content = stage;
		_stage = stage;

		// 指针监听必须挂到**窗口根容器**，不能只挂在按钮自己身上：
		// 只挂自己的话，鼠标在空白处移动时按钮完全收不到事件，
		// 表现就是"按钮纹丝不动"（实测现象）。
		Unloaded += (object sender, RoutedEventArgs e) =>
		{
			_timer?.Stop();
			UnhookRoot();
		};
		Loaded += (object sender, RoutedEventArgs e) =>
		{
			HookRoot();
			if (!_gaveUp)
			{
				_timer?.Start();
			}
		};

		_timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
		_timer.Interval = TimeSpan.FromMilliseconds(16.0);
		_timer.Tick += OnTick;
		_timer.Start();
	}

	/// <summary>把指针监听挂到窗口根容器，这样鼠标在界面任何位置（含空白处）都能感知。</summary>
	private void HookRoot()
	{
		if (_rootHooked)
		{
			return;
		}
		try
		{
			Grid? root = MainWindow.Instance?.GetRootGrid();
			if (root != null)
			{
				_root = root;
				_root.PointerMoved += OnRootPointerMoved;
				_rootHooked = true;
			}
		}
		catch
		{
		}
	}

	private void UnhookRoot()
	{
		try
		{
			if (_root != null && _rootHooked)
			{
				_root.PointerMoved -= OnRootPointerMoved;
			}
		}
		catch
		{
		}
		_root = null;
		_rootHooked = false;
	}

	private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (_gaveUp || _root == null)
		{
			return;
		}
		try
		{
			// 先取鼠标在根容器里的坐标，再换算到本控件坐标系
			Point pRoot = e.GetCurrentPoint(_root).Position;
			Point p = _root.TransformToVisual(this).TransformPoint(pRoot);
			_mouseInside = true;
			double cx = _x + _btn.ActualWidth / 2.0;
			double cy = _y + _btn.ActualHeight / 2.0;
			_mouseDx = cx - p.X;
			_mouseDy = cy - p.Y;
		}
		catch
		{
		}
	}

	private void OnTick(object sender, object e)
	{
		if (_gaveUp)
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		double dt = Math.Min(0.05, (now - _lastTick).TotalSeconds);
		_lastTick = now;
		if (dt <= 0)
		{
			return;
		}

		const double danger = 114.0;   // 鼠标进入这个半径就开始跑（按需求取 114 px）
		double dist = Math.Sqrt(_mouseDx * _mouseDx + _mouseDy * _mouseDy);

		if (_mouseInside && dist < danger && dist > 0.001)
		{
			// 越近跑得越快（归一化后用平方衰减，靠近时急加速）
			double force = (danger - dist) / danger;
			force *= force * 2600.0;
			_vx += (_mouseDx / dist) * force * dt;
			_vy += (_mouseDy / dist) * force * dt;
		}

		// 阻尼
		_vx *= 0.90;
		_vy *= 0.90;

		double speed = Math.Sqrt(_vx * _vx + _vy * _vy);
		if (speed < 0.5)
		{
			_vx = 0.0;
			_vy = 0.0;
			speed = 0.0;
		}

		_x += _vx * dt;
		_y += _vy * dt;

		// 边界反弹（场地 320x120，按按钮实际大小留边）
		double maxX = 320.0 - _btn.ActualWidth;
		double maxY = 120.0 - _btn.ActualHeight;
		if (maxX < 0.0)
		{
			maxX = 0.0;
		}
		if (maxY < 0.0)
		{
			maxY = 0.0;
		}
		if (_x < 0.0)
		{
			_x = 0.0;
			_vx = Math.Abs(_vx) * 0.8;
		}
		else if (_x > maxX)
		{
			_x = maxX;
			_vx = 0.0 - Math.Abs(_vx) * 0.8;
		}
		if (_y < 0.0)
		{
			_y = 0.0;
			_vy = Math.Abs(_vy) * 0.8;
		}
		else if (_y > maxY)
		{
			_y = maxY;
			_vy = 0.0 - Math.Abs(_vy) * 0.8;
		}

		_pos.X = _x;
		_pos.Y = _y;

		// 膈应人：按钮越躲，容器越长，页面被越撑越高、滚动条越来越长。
		// 用真实 Height（而不是 RenderTransform）才会真正影响布局。上限 600，别撑爆页面。
		if (_stage != null)
		{
			double want = 120.0 + Math.Min(600.0, _traveled / 6.0);
			if (Math.Abs(_stage.Height - want) > 4.0)
			{
				_stage.Height = want;
			}
		}

		// 果冻拉扯：按移动方向做非对称缩放（PCL 的 AprilScaleTrans 同款思路）
		double sv = Math.Min(60.0, speed);
		double nx = speed > 0.001 ? Math.Abs(_vx / speed) : 0.0;
		double ny = speed > 0.001 ? Math.Abs(_vy / speed) : 0.0;
		_scale.ScaleX = Clamp(1.0 - (nx - ny) * (sv / 160.0), 0.2, 1.8);
		_scale.ScaleY = Clamp(1.0 - (ny - nx) * (sv / 100.0), 0.2, 1.8);

		// 累计跑动距离 → 升级嘲讽
		_traveled += speed * dt;
		int stage = (int)(_traveled / 900.0);
		if (stage != _tauntStage && stage < Taunts.Length)
		{
			_tauntStage = stage;
			_btn.Content = Taunts[_tauntStage];
		}
	}

	private async void OnClick(object sender, RoutedEventArgs e)
	{
		// 首次触发前必须弹光敏警告：后面全是快速闪烁 / 旋转 / 缩放，
		// 对光敏性癫痫人群有风险，不能不打招呼就放。
		if (!_warned)
		{
			bool go = await ShowEpilepsyWarningAsync();
			if (!go)
			{
				_btn.Content = "明智的选择";
				return;
			}
			_warned = true;
		}
		if (_gaveUp)
		{
			// 已经投降过了：**直接放下一个效果**。
			// 注意这里**不能再调 Reset()** —— 那会让按钮重新开始躲避鼠标，
			// 用户想点第二下时它一直在跑，根本点不到，看起来就像"没反应、没轮换"。
			// 投降后按钮就该老实待在原地，方便反复点。
			PrankEffectsNext();
			return;
		}
		// 真被逮到了：投降，回到原位并**停止躲避**（后面就固定在这，随便点）
		_gaveUp = true;
		_surrenderCount = 0;
		_timer.Stop();
		_vx = 0.0;
		_vy = 0.0;
		_x = 0.0;
		_y = 0.0;
		_pos.X = 0.0;
		_pos.Y = 0.0;
		_scale.ScaleX = 1.0;
		_scale.ScaleY = 1.0;
		_btn.Content = "…你偏要点是吧";
		// 抖一下
		Shake();
		Surrendered?.Invoke();
	}

	/// <summary>
	/// 光敏性癫痫警告。后续效果含高频闪烁与旋转，必须先告知。
	/// 返回 true 表示用户确认继续。
	/// </summary>
	private async Task<bool> ShowEpilepsyWarningAsync()
	{
		try
		{
			TextBlock body = new TextBlock
			{
				Text = "⚠ 光敏性癫痫警告\n\n"
					+ "接下来的效果包含：窗口快速闪烁、高速旋转、剧烈缩放与彩虹强色变化。\n\n"
					+ "如果你或身边的人有光敏性癫痫、眩晕、偏头痛史，请**不要继续**。\n"
					+ "效果仅持续数秒，窗口会自动还原，不会影响程序本身。",
				TextWrapping = TextWrapping.Wrap,
				FontSize = 13.0
			};
			ContentDialog dlg = new ContentDialog
			{
				Title = "⚠ 光敏性癫痫警告",
				Content = body,
				PrimaryButtonText = "我已知晓，继续",
				CloseButtonText = "算了",
				DefaultButton = ContentDialogButton.Close,
				XamlRoot = XamlRoot
			};
			ContentDialogResult r = await dlg.ShowAsync();
			return r == ContentDialogResult.Primary;
		}
		catch
		{
			// 拿不到 XamlRoot 时弹窗无法显示，放行（至少别把按钮卡死）
			return true;
		}
	}

	/// <summary>投降过之后，每点一次轮换放一个彩蛋效果。</summary>
	private void PrankEffectsNext()
	{
		try
		{
			if (PrankEffects.IsPlaying)
			{
				// 上一个效果还没播完。别默默 return —— 给个反馈，否则用户以为点了没用
				_btn.Content = "等会儿…";
				return;
			}
			_surrenderCount++;
			// 取模 3（而不是 4）：保证**每点一次都有一种效果**，
			// 不会出现点了却什么都没发生的空档。
			switch (_surrenderCount % 4)
			{
				case 1:
					_btn.Content = "抓不住我吧~";
					PrankEffects.FlyToCorners(delegate
					{
						_btn.Content = "回来啦";
					});
					break;
				case 2:
					_btn.Content = "彩虹时间！";
					PrankEffects.RainbowPulse(delegate
					{
						_btn.Content = "回来啦";
					});
					break;
				case 3:
					_btn.Content = "被冲走了…";
					PrankEffects.FlushDown(delegate
					{
						_btn.Content = "又转回来了";
					});
					break;
				default:
					_btn.Content = "整个窗口也躲你";
					PrankEffects.FleeFromMouse(delegate
					{
						_btn.Content = "躲累了";
					});
					break;
			}
		}
		catch
		{
		}
	}

	/// <summary>重置成初始状态，可以再玩一次。</summary>
	private void Reset()
	{
		_gaveUp = false;
		_vx = 0.0;
		_vy = 0.0;
		_x = 0.0;
		_y = 0.0;
		_pos.X = 0.0;
		_pos.Y = 0.0;
		_scale.ScaleX = 1.0;
		_scale.ScaleY = 1.0;
		_traveled = 0.0;
		_tauntStage = 0;
		_btn.Content = Taunts[0];
		if (_stage != null)
		{
			_stage.Height = 120.0;
		}
		_lastTick = DateTime.UtcNow;
		_timer.Start();
	}

	/// <summary>被点中后小幅抖动，纯视觉效果。</summary>
	private async void Shake()
	{
		for (int i = 0; i < 6; i++)
		{
			_pos.X = (i % 2 == 0) ? 4.0 : -4.0;
			await System.Threading.Tasks.Task.Delay(28);
		}
		_pos.X = 0.0;
	}

	/// <summary>当前是否已经投降（供宿主判断是否提示）。</summary>
	public bool HasSurrendered => _gaveUp;

	/// <summary>随机一句嘲讽提示。</summary>
	public string NextHint() => Hints[_rnd.Next(Hints.Length)];

	private static double Clamp(double v, double lo, double hi)
	{
		if (v < lo)
		{
			return lo;
		}
		return (v > hi) ? hi : v;
	}
}

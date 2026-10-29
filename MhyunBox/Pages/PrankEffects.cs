using System;
using Windows.Foundation;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace MhyunBox.Pages;

/// <summary>
/// 彩蛋效果集（机制参考 PCL）。
///
/// · 「屏幕四角乱飞」借鉴 PCL 愚人节彩蛋的躲避 + 边界反弹思路，
///   只不过把躲鼠标换成真的把窗口甩到屏幕四个角。
/// · 「冲厕所旋转消失」直接照搬 PCL 关闭窗口的动画配方
///   （`FormMain.xaml.vb` 的 "Form Close"）：
///     PanBack.RenderTransform = TransformGroup { Rotate, Translate, Scale }
///     + 透明度递减 + 位移，最后把窗口挪走。
///   PCL 那套最终会真退出程序；这里**不退出**，转完自动转回来，
///   保证「界面还在」，不会把用户的程序弄丢。
/// · 「彩虹 + 缩放」是自己加的：在根容器上盖一层彩虹渐变并循环变色。
/// </summary>
public static class PrankEffects
{
	private static DispatcherQueueTimer? _timer;
	private static Action? _onDone;

	// 记录原始状态，效果结束后必须还原
	private static Transform? _savedTransform;
	private static double _savedOpacity;
	private static PointInt32 _savedPos;
	private static SizeInt32 _savedSize;
	private static Border? _rainbowLayer;

	/// <summary>效果是否正在播放（避免叠加触发）。</summary>
	public static bool IsPlaying => _timer != null;

	// ---------- 1) 屏幕四角乱飞 ----------

	/// <summary>把窗口快速甩到屏幕四角，同时内容闪烁缩放。</summary>
	public static void FlyToCorners(Action? onDone = null)
	{
		MainWindow mw = GetWindow();
		if (mw == null || _timer != null)
		{
			onDone?.Invoke();
			return;
		}
		AppWindow? aw = mw.GetAppWindow();
		Grid? root = mw.GetRootGrid();
		if (aw == null || root == null)
		{
			onDone?.Invoke();
			return;
		}

		// 最大化状态下 Move 无效，必须先还原成普通窗口，否则只会看到"里面在变"
		EnsureNormalState(aw);
		SaveState(root, aw);
		_onDone = onDone;

		ScaleTransform sc = new ScaleTransform();
		root.RenderTransform = new TransformGroup
		{
			Children = { sc }
		};
		root.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);

		// 屏幕工作区。WinUI 3 里没有 GetForCurrentView（那是 UWP 的 API），
		// 用主显示器即可满足"甩到四角"的视觉效果。
		DisplayArea area = DisplayArea.Primary;
		RectInt32 work = area.WorkArea;
		int margin = 40;
		// 四角坐标：左上 / 右上 / 左下 / 右下
		PointInt32[] corners = new PointInt32[4]
		{
			new PointInt32(work.X + margin, work.Y + margin),
			new PointInt32(work.X + work.Width - aw.Size.Width - margin, work.Y + margin),
			new PointInt32(work.X + margin, work.Y + work.Height - aw.Size.Height - margin),
			new PointInt32(work.X + work.Width - aw.Size.Width - margin, work.Y + work.Height - aw.Size.Height - margin)
		};

		Random rnd = new Random();
		int frames = 0;
		StartTimer(delegate
		{
			frames++;
			// 每 6 帧换一个角，制造"嗖嗖"乱飞的感觉
			if (frames % 6 == 0)
			{
				PointInt32 target = corners[rnd.Next(corners.Length)];
				try
				{
					MoveResizeWindow(aw, target.X, target.Y, aw.Size.Width, aw.Size.Height);
				}
				catch
				{
				}
			}
			// 内容闪烁缩放
			double s = 0.72 + 0.28 * Math.Abs(Math.Sin(frames * 0.55));
			sc.ScaleX = s;
			sc.ScaleY = s;
			root.Opacity = 0.65 + 0.35 * Math.Abs(Math.Sin(frames * 0.9));
			if (frames >= 60)
			{
				Finish(root, aw);
			}
		}, TimeSpan.FromMilliseconds(28.0));
	}

	// ---------- 2) 彩虹 + 缩放 ----------

	/// <summary>整个界面盖一层循环变色的彩虹，同时内容脉动缩放。</summary>
	public static void RainbowPulse(Action? onDone = null)
	{
		MainWindow mw = GetWindow();
		if (mw == null || _timer != null)
		{
			onDone?.Invoke();
			return;
		}
		AppWindow? aw = mw.GetAppWindow();
		Grid? root = mw.GetRootGrid();
		if (root == null)
		{
			onDone?.Invoke();
			return;
		}
		// 同 FlyToCorners：最大化下 Resize 不生效，先还原
		if (aw != null)
		{
			EnsureNormalState(aw);
		}
		SaveState(root, aw);
		_onDone = onDone;

		ScaleTransform sc = new ScaleTransform();
		root.RenderTransform = new TransformGroup
		{
			Children = { sc }
		};
		root.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);

		// 彩虹覆盖层：多段渐变，靠逐帧改 GradientStop 的颜色来"流动"
		LinearGradientBrush brush = new LinearGradientBrush
		{
			StartPoint = new Windows.Foundation.Point(0.0, 0.0),
			EndPoint = new Windows.Foundation.Point(1.0, 1.0)
		};
		for (int i = 0; i < 7; i++)
		{
			brush.GradientStops.Add(new GradientStop
			{
				Offset = i / 6.0,
				Color = HsvToRgb(i / 6.0 * 360.0, 1.0, 1.0)
			});
		}
		_rainbowLayer = new Border
		{
			Background = brush,
			Opacity = 0.55,
			IsHitTestVisible = false
		};
		// 盖在最上层（Children 末尾 = 最前）
		root.Children.Add(_rainbowLayer);

		int baseW = _savedSize.Width;
		int baseH = _savedSize.Height;
		if (baseW <= 0 || baseH <= 0)
		{
			// 拿不到窗口尺寸时退回用屏幕工作区估算一个，避免除零
			baseW = 1000;
			baseH = 680;
		}
		int frames = 0;
		StartTimer(delegate
		{
			frames++;
			// 色相整体流动
			for (int j = 0; j < brush.GradientStops.Count; j++)
			{
				double hue = ((j / 6.0) * 360.0 + frames * 6.0) % 360.0;
				brush.GradientStops[j].Color = HsvToRgb(hue, 1.0, 1.0);
			}
			// 脉动
			double s = 0.80 + 0.20 * Math.Sin(frames * 0.22);
			sc.ScaleX = s;
			sc.ScaleY = s;
			// **让窗口本身也跟着呼吸**：只缩放内部内容的话，窗口边框不动，
			// 看着就很怪（"窗口大小没变，只是里面在变"）。这里同时 Resize 真实窗口，
			// 并反向修正位置以保持中心不跑偏。
			if (aw != null)
			{
				try
				{
					int w = (int)(baseW * s);
					int h = (int)(baseH * s);
					MoveResizeWindow(aw, _savedPos.X - (w - baseW) / 2, _savedPos.Y - (h - baseH) / 2, w, h);
				}
				catch
				{
				}
			}
			if (frames >= 70)
			{
				Finish(root, aw);
			}
		}, TimeSpan.FromMilliseconds(28.0));
	}

	// ---------- 3) 冲厕所旋转消失 ----------

	/// <summary>
	/// 旋转 + 缩小 + 下坠 + 淡出（PCL "Form Close" 同款配方），
	/// 然后把窗口挪到屏幕外假装消失；停一下再自动转回来。
	/// **不会真的退出程序** —— 界面还在，转回来即可继续用。
	/// </summary>
	public static void FlushDown(Action? onDone = null)
	{
		MainWindow mw = GetWindow();
		if (mw == null || _timer != null)
		{
			onDone?.Invoke();
			return;
		}
		AppWindow? aw = mw.GetAppWindow();
		Grid? root = mw.GetRootGrid();
		if (aw == null || root == null)
		{
			onDone?.Invoke();
			return;
		}
		// 同 FlyToCorners：最大化下 Move/Resize 不生效，先还原
		EnsureNormalState(aw);
		SaveState(root, aw);
		_onDone = onDone;

		RotateTransform rot = new RotateTransform();
		TranslateTransform pos = new TranslateTransform();
		ScaleTransform scale = new ScaleTransform();
		root.RenderTransform = new TransformGroup
		{
			Children = { rot, pos, scale }
		};
		root.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);

		const int total = 66;   // 约 1.8 秒
		int frames = 0;
		StartTimer(delegate
		{
			frames++;
			double t = Math.Min(1.0, frames / (double)total);
			// 缓入：越转越快，像被漩涡吸下去
			double ease = t * t;
			rot.Angle = 720.0 * ease;          // 转两圈
			scale.ScaleX = 1.0 - 0.97 * ease;
			scale.ScaleY = 1.0 - 0.97 * ease;
			pos.Y = 220.0 * ease;              // 往下坠
			root.Opacity = 1.0 - ease;
			if (frames >= total)
			{
				// 真的挪出可视区（PCL 用 Top = -10000），但**不退出程序**
				try
				{
					MoveResizeWindow(aw, -30000, -30000, _savedSize.Width, _savedSize.Height);
				}
				catch
				{
				}
				// 停 1.2 秒再转回来，避免用户以为程序没了
				DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
				{
					await System.Threading.Tasks.Task.Delay(1200);
					Finish(root, aw);
				});
				StopTimer();
			}
		}, TimeSpan.FromMilliseconds(28.0));
	}

	// ---------- 4) 整窗口躲鼠标 ----------

	private static Point _mouse;                 // 鼠标在根容器坐标系里的位置
	private static bool _fleeHooked;
	private static Grid? _fleeRoot;

	/// <summary>
	/// 整窗口躲鼠标：鼠标一靠近，窗口就往反方向挪开。
	/// 思路与按钮躲避一致，只是作用在窗口本身。
	/// 持续约 7 秒后自动停止并还原位置。
	/// </summary>
	public static void FleeFromMouse(Action? onDone = null)
	{
		MainWindow mw = GetWindow();
		if (mw == null || _timer != null)
		{
			onDone?.Invoke();
			return;
		}
		AppWindow? aw = mw.GetAppWindow();
		Grid? root = mw.GetRootGrid();
		if (aw == null || root == null)
		{
			onDone?.Invoke();
			return;
		}
		EnsureNormalState(aw);
		SaveState(root, aw);
		_onDone = onDone;

		// 记录鼠标位置（挂在根容器上，界面任何地方移动都能拿到）
		if (!_fleeHooked)
		{
			_fleeRoot = root;
			_fleeRoot.PointerMoved += delegate (object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
			{
				try
				{
					_mouse = e.GetCurrentPoint(_fleeRoot).Position;
				}
				catch
				{
				}
			};
			_fleeHooked = true;
		}

		int frames = 0;
		int cx = _savedPos.X + _savedSize.Width / 2;
		int cy = _savedPos.Y + _savedSize.Height / 2;
		StartTimer(delegate
		{
			frames++;
			// 鼠标离窗口中心太近就往反方向挪
			double dx = cx - _mouse.X;
			double dy = cy - _mouse.Y;
			double dist = Math.Sqrt(dx * dx + dy * dy);
			if (dist > 0.001 && dist < 420.0)
			{
				double push = (420.0 - dist) / 420.0 * 90.0;
				int nx = _savedPos.X + (int)(dx / dist * push);
				int ny = _savedPos.Y + (int)(dy / dist * push);
				// 别挪出屏幕太多
				nx = Math.Max(-40, Math.Min(nx, 3000));
				ny = Math.Max(-40, Math.Min(ny, 2000));
				MoveResizeWindow(aw, nx, ny, _savedSize.Width, _savedSize.Height);
			}
			if (frames >= 250)
			{
				Finish(root, aw);
			}
		}, TimeSpan.FromMilliseconds(28.0));
	}

	// ---------- 公共工具 ----------

	/// <summary>
	/// 保证窗口处于**非最大化**状态。
	/// 这一步是关键：窗口最大化时 `AppWindow.Move()` / `Resize()` 完全不生效，
	/// 结果就是"里面在变、窗口纹丝不动"（实测现象）。
	/// </summary>
	private static void EnsureNormalState(AppWindow aw)
	{
		try
		{
			if (aw.Presenter is OverlappedPresenter op
				&& op.State == OverlappedPresenterState.Maximized)
			{
				op.Restore();
			}
		}
		catch
		{
		}
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

	private const uint SwpNoZOrder = 0x0004u;
	private const uint SwpNoActivate = 0x0010u;

	/// <summary>
	/// 移动/缩放窗口。
	/// **双保险**：先走 AppWindow 的 Move/Resize，再用 SetWindowPos 直接操作 HWND。
	/// 只用 AppWindow 那套在部分窗口状态下会静默失效（表现就是"里面在变、窗口不动"），
	/// SetWindowPos 是最底层、最可靠的一条路。
	/// </summary>
	private static void MoveResizeWindow(AppWindow aw, int x, int y, int w, int h)
	{
		if (w > 0 && h > 0)
		{
			try
			{
				aw.Resize(new SizeInt32(w, h));
			}
			catch
			{
			}
		}
		try
		{
			aw.Move(new PointInt32(x, y));
		}
		catch
		{
		}
		try
		{
			IntPtr hwnd = Win32Interop.GetWindowFromWindowId(aw.Id);
			if (hwnd != IntPtr.Zero)
			{
				SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SwpNoZOrder | SwpNoActivate);
			}
		}
		catch
		{
		}
	}

	private static MainWindow? GetWindow()
	{
		try
		{
			return MainWindow.Instance;
		}
		catch
		{
			return null;
		}
	}

	private static void SaveState(Grid root, AppWindow? aw)
	{
		_savedTransform = root.RenderTransform;
		_savedOpacity = root.Opacity;
		if (aw != null)
		{
			try
			{
				_savedPos = aw.Position;
				_savedSize = aw.Size;
			}
			catch
			{
			}
		}
	}

	private static void Finish(Grid root, AppWindow? aw)
	{
		StopTimer();
		// 清彩虹层
		if (_rainbowLayer != null)
		{
			try
			{
				root.Children.Remove(_rainbowLayer);
			}
			catch
			{
			}
			_rainbowLayer = null;
		}
		// 还原变换与透明度
		root.RenderTransform = _savedTransform;
		root.Opacity = _savedOpacity;
		// 还原窗口位置
		if (aw != null)
		{
			try
			{
				MoveResizeWindow(aw, _savedPos.X, _savedPos.Y, _savedSize.Width, _savedSize.Height);
			}
			catch
			{
			}
		}
		Action? done = _onDone;
		_onDone = null;
		done?.Invoke();
	}

	private static void StartTimer(Action tick, TimeSpan interval)
	{
		StopTimer();
		_timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
		_timer.Interval = interval;
		_timer.Tick += delegate
		{
			try
			{
				tick();
			}
			catch
			{
				StopTimer();
			}
		};
		_timer.Start();
	}

	private static void StopTimer()
	{
		try
		{
			_timer?.Stop();
		}
		catch
		{
		}
		_timer = null;
	}

	/// <summary>HSV → RGB（用于生成彩虹色）。</summary>
	private static Windows.UI.Color HsvToRgb(double h, double s, double v)
	{
		h = ((h % 360.0) + 360.0) % 360.0;
		double c = v * s;
		double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
		double m = v - c;
		double r = 0.0;
		double g = 0.0;
		double b = 0.0;
		if (h < 60.0)
		{
			r = c;
			g = x;
		}
		else if (h < 120.0)
		{
			r = x;
			g = c;
		}
		else if (h < 180.0)
		{
			g = c;
			b = x;
		}
		else if (h < 240.0)
		{
			g = x;
			b = c;
		}
		else if (h < 300.0)
		{
			r = x;
			b = c;
		}
		else
		{
			r = c;
			b = x;
		}
		return Windows.UI.Color.FromArgb(255,
			(byte)((r + m) * 255.0),
			(byte)((g + m) * 255.0),
			(byte)((b + m) * 255.0));
	}
}

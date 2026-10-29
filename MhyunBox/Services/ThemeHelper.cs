using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MhyunBox.Services;

/// <summary>
/// 全项目统一主题解析中心。
/// 背景：WinUI3 里代码后置（C# 构建 UI）拿不到元素级主题解析——
/// RootGrid.RequestedTheme 只影响 XAML 的 {ThemeResource}，
/// 而 Application.Current.Resources 的代码查找停留在应用级（启动时）主题，
/// 且 Application.Resources.ThemeDictionaries 在 App.xaml 未定义时为空。
/// 因此代码构建的页面必须经过这里按“当前生效主题”取色。
/// </summary>
internal static class ThemeHelper
{
	private static readonly Dictionary<string, ThemeColorPair> Palette = new Dictionary<string, ThemeColorPair>
	{
		// 卡片 / 容器填充（与 WinUI 主题资源值一致）
		{ "CardBackgroundFillColorDefaultBrush", new ThemeColorPair(0xB3FFFFFFu, 0x0DFFFFFFu) },
		{ "CardBackgroundFillColorSecondaryBrush", new ThemeColorPair(0x80F6F6F6u, 0x0DFFFFFFu) },
		{ "LayerFillColorDefaultBrush", new ThemeColorPair(0xB3FFFFFFu, 0x3FFFFFFFu) },
		{ "AcrylicBackgroundFillColorDefaultBrush", new ThemeColorPair(0xB3F9F9F9u, 0x9BF0F0F0u) },
		// 边框 / 描边
		{ "CardStrokeColorDefaultBrush", new ThemeColorPair(0x0F000000u, 0x19FFFFFFu) },
		{ "ControlStrokeColorDefaultBrush", new ThemeColorPair(0x0F000000u, 0x12FFFFFFu) },
		// 控件填充
		{ "ControlFillColorSecondaryBrush", new ThemeColorPair(0x80F9F9F9u, 0x15FFFFFFu) },
		{ "SubtleFillColorSecondaryBrush", new ThemeColorPair(0x09000000u, 0x0FFFFFFF) },
		// 状态色
		{ "SystemFillColorCautionBrush", new ThemeColorPair(0xFF9D5D00u, 0xFFFCE100) },
		// 文本（代码后置 Foreground 兜底用）
		{ "TextFillColorPrimaryBrush", new ThemeColorPair(0xE4000000u, 0xFFFFFFFFu) },
		{ "TextFillColorSecondaryBrush", new ThemeColorPair(0x9B000000u, 0xC5FFFFFF) },
		// 页面背景
		{ "ApplicationPageBackgroundThemeBrush", new ThemeColorPair(0xFFF3F3F3u, 0xFF202020) },
		{ "SolidBackgroundFillColorBase", new ThemeColorPair(0xFFF3F3F3u, 0xFF202020) }
	};

	/// <summary>当前是否深色主题（跟随 RootGrid 实际生效主题）。</summary>
	public static bool IsDark
	{
		get
		{
			try
			{
				ElementTheme theme = MainWindow.Instance?.AppElementTheme ?? ElementTheme.Default;
				if (theme != ElementTheme.Default)
				{
					return theme == ElementTheme.Dark;
				}
			}
			catch
			{
			}
			try
			{
				return Application.Current?.RequestedTheme == ApplicationTheme.Dark;
			}
			catch
			{
				return false;
			}
		}
	}

	/// <summary>
	/// 按当前主题解析主题资源键。返回的画笔始终非 null。
	/// 顺序：强调色特殊处理 → 内置调色板（深浅成对） → 框架资源查找 → 中性兜底。
	/// </summary>
	public static Brush Resolve(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return NeutralFill();
		}
		bool dark = IsDark;
		if (string.Equals(key, "AccentFillColorDefaultBrush", StringComparison.Ordinal))
		{
			Brush accent = ResolveSystemColor(dark ? "SystemAccentColorLight2" : "SystemAccentColorDark1");
			if (accent != null)
			{
				return accent;
			}
		}
		if (Palette.TryGetValue(key, out ThemeColorPair pair))
		{
			return new SolidColorBrush(dark ? pair.Dark : pair.Light);
		}
		// 框架资源兜底：浅色主题下代码查找与应用级主题一致，可直接用；
		// 深色主题下代码查找可能拿到浅色画笔（应用级主题不会跟随切换），只有结果明显是浅色系时才改用中性深色。
		try
		{
			if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush result)
			{
				if (!dark || !LooksLikeLightBrush(result))
				{
					return result;
				}
			}
		}
		catch
		{
		}
		return NeutralFill(key);
	}

	/// <summary>内容卡片（页面容器）背景，按当前主题实时解析。</summary>
	public static Brush CardBackground()
	{
		return Resolve("CardBackgroundFillColorDefaultBrush");
	}

	/// <summary>
	/// 全局修复下拉/弹层背景穿透：ComboBox 下拉默认用 AcrylicInAppFillColorDefaultBrush，
	/// 在部分环境（无系统级合成支持/深色主题）下几乎全透明，选项文字与页面内容糊在一起。
	/// 这里在应用级 ThemeDictionaries 里把弹层背景和 In-App Acrylic 本体都覆盖为不透明实色，
	/// 随主题切换自动生效。需在 App 启动早期调用一次。
	/// </summary>
	public static void ApplySolidComboPopup()
	{
		try
		{
			ResourceDictionary light = new ResourceDictionary
			{
				["ComboBoxDropDownBackground"] = (Brush)new SolidColorBrush(Color.FromArgb(255, 250, 250, 250)),
				["ComboBoxDropDownBorderBrush"] = (Brush)new SolidColorBrush(Color.FromArgb(51, 0, 0, 0)),
				["AcrylicInAppFillColorDefaultBrush"] = (Brush)new SolidColorBrush(Color.FromArgb(255, 249, 249, 249)),
				["AcrylicInAppFillColorBaseBrush"] = (Brush)new SolidColorBrush(Color.FromArgb(255, 243, 243, 243))
			};
			ResourceDictionary dark = new ResourceDictionary
			{
				["ComboBoxDropDownBackground"] = (Brush)new SolidColorBrush(Color.FromArgb(255, 43, 43, 43)),
				["ComboBoxDropDownBorderBrush"] = (Brush)new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)),
				["AcrylicInAppFillColorDefaultBrush"] = (Brush)new SolidColorBrush(Color.FromArgb(255, 43, 43, 43)),
				["AcrylicInAppFillColorBaseBrush"] = (Brush)new SolidColorBrush(Color.FromArgb(255, 32, 32, 32))
			};
			ResourceDictionary defaultDict = new ResourceDictionary
			{
				["ComboBoxDropDownBackground"] = (Brush)new SolidColorBrush(Color.FromArgb(255, 250, 250, 250)),
				["ComboBoxDropDownBorderBrush"] = (Brush)new SolidColorBrush(Color.FromArgb(51, 0, 0, 0)),
				["AcrylicInAppFillColorDefaultBrush"] = (Brush)new SolidColorBrush(Color.FromArgb(255, 249, 249, 249)),
				["AcrylicInAppFillColorBaseBrush"] = (Brush)new SolidColorBrush(Color.FromArgb(255, 243, 243, 243))
			};
			IDictionary<object, object> themeDictionaries = Application.Current.Resources.ThemeDictionaries;
			themeDictionaries["Light"] = light;
			themeDictionaries["Dark"] = dark;
			themeDictionaries["Default"] = defaultDict;
			LogHelper.Log("[主题] 已应用全局不透明弹层/列表背景覆盖（修复下拉与列表穿透）");
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[主题] 应用不透明弹层覆盖失败");
		}
	}

	/// <summary>元素级兜底：给单个 ComboBox 的下拉弹层强制不透明背景（应用级覆盖未生效时仍可靠）。</summary>
	public static void SolidComboPopup(ComboBox? box)
	{
		try
		{
			if (box == null)
			{
				return;
			}
			bool dark = IsDark;
			box.Resources["ComboBoxDropDownBackground"] = new SolidColorBrush(dark ? Color.FromArgb(255, 43, 43, 43) : Color.FromArgb(255, 250, 250, 250));
			box.Resources["ComboBoxDropDownBorderBrush"] = new SolidColorBrush(dark ? Color.FromArgb(51, 255, 255, 255) : Color.FromArgb(51, 0, 0, 0));
		}
		catch
		{
		}
	}

	/// <summary>列表容器（ListView/ListBox）的底色：必须不透明，否则深色下半透明层色与页面文字糊在一起。</summary>
	public static Brush ListSurface()
	{
		return new SolidColorBrush(IsDark ? Color.FromArgb(255, 36, 36, 36) : Color.FromArgb(255, 255, 255, 255));
	}

	private static Brush NeutralFill(string key = null)
	{
		// 未知键给一个与当前主题协调的中性色，避免出现刺眼的纯白/纯黑
		bool dark = IsDark;
		if (!string.IsNullOrEmpty(key) && key.IndexOf("Stroke", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return new SolidColorBrush(dark ? Color.FromArgb(25, 255, 255, 255) : Color.FromArgb(15, 0, 0, 0));
		}
		if (!string.IsNullOrEmpty(key) && key.IndexOf("Caution", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return new SolidColorBrush(dark ? Color.FromArgb(255, 252, 225, 0) : Color.FromArgb(255, 157, 93, 0));
		}
		if (!string.IsNullOrEmpty(key) && key.IndexOf("Text", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return new SolidColorBrush(dark ? Color.FromArgb(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue) : Color.FromArgb(228, 0, 0, 0));
		}
		return new SolidColorBrush(dark ? Color.FromArgb(13, 255, 255, 255) : Color.FromArgb(179, 255, 255, 255));
	}

	private static bool LooksLikeLightBrush(Brush brush)
	{
		if (brush is SolidColorBrush solid)
		{
			Color c = solid.Color;
			// 透明度很低（深色磨砂层）不算浅色
			if (c.A <= 40)
			{
				return false;
			}
			return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 > 160;
		}
		return false;
	}

	private static Brush ResolveSystemColor(string key)
	{
		try
		{
			if (Application.Current.Resources.TryGetValue(key, out var value) && value is Color color)
			{
				return new SolidColorBrush(color);
			}
		}
		catch
		{
		}
		return null;
	}

	private readonly struct ThemeColorPair
	{
		public readonly Color Light;

		public readonly Color Dark;

		public ThemeColorPair(uint light, uint dark)
		{
			Light = Color.FromArgb((byte)(light >> 24), (byte)(light >> 16), (byte)(light >> 8), (byte)light);
			Dark = Color.FromArgb((byte)(dark >> 24), (byte)(dark >> 16), (byte)(dark >> 8), (byte)dark);
		}
	}
}

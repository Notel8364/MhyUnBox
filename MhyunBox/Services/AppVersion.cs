using System;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MhyunBox.Services;

public static class AppVersion
{
	public const string Display = "v7.8.0";

	private static readonly Regex VersionPattern = new Regex("^[vV]?\\d+\\.\\d+(\\.\\d+)?(\\.\\d+)?$", RegexOptions.Compiled);

	public static int ApplyTo(DependencyObject? root)
	{
		if (root == null)
		{
			return 0;
		}
		int num = 0;
		try
		{
			if (root is TextBlock { Text: var text } textBlock)
			{
				string text2 = text?.Trim() ?? "";
				if (text2.Length > 0 && VersionPattern.IsMatch(text2))
				{
					textBlock.Text = Display;
					num++;
				}
			}
			int childrenCount = VisualTreeHelper.GetChildrenCount(root);
			for (int i = 0; i < childrenCount; i++)
			{
				num += ApplyTo(VisualTreeHelper.GetChild(root, i));
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[版本] 替换界面版本号失败");
		}
		return num;
	}
}

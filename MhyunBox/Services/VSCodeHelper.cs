using System;
using System.Diagnostics;
using System.IO;

namespace MhyunBox.Services;

public static class VSCodeHelper
{
	private static string? _cached;

	private static string? ResolveCodeLauncher(string found)
	{
		if (found.EndsWith("Code.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(found))
		{
			return found;
		}
		int num = found.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase);
		if (num >= 0)
		{
			string text = found.Substring(0, num) + "\\Code.exe";
			if (File.Exists(text))
			{
				return text;
			}
		}
		return null;
	}

}

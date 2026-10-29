using System;
using System.Diagnostics;
using System.IO;

namespace MhyunBox.Services;

public static class VSCodeHelper
{
	private static string? _cached;

	public static string? FindVSCode()
	{
		if (_cached != null)
		{
			return _cached;
		}
		string[] array = new string[3]
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
			"C:\\Program Files\\Microsoft VS Code\\Code.exe",
			"C:\\Program Files (x86)\\Microsoft VS Code\\Code.exe"
		};
		string[] array2 = array;
		foreach (string text in array2)
		{
			if (File.Exists(text))
			{
				_cached = text;
				return text;
			}
		}
		try
		{
			using Process process = Process.Start(new ProcessStartInfo("where", "code")
			{
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			});
			if (process != null)
			{
				string text2 = process.StandardOutput.ReadLine();
				process.WaitForExit(2000);
				if (!string.IsNullOrWhiteSpace(text2))
				{
					string text3 = ResolveCodeLauncher(text2.Trim());
					if (!string.IsNullOrWhiteSpace(text3))
					{
						_cached = text3;
						return text3;
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

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

	public static bool OpenInVSCode(string filePath)
	{
		string text = FindVSCode();
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		try
		{
			Process.Start(new ProcessStartInfo(text, "\"" + filePath + "\"")
			{
				UseShellExecute = false
			});
			return true;
		}
		catch
		{
			return false;
		}
	}
}

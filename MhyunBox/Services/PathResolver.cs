using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MhyunBox.Services;

public static class PathResolver
{
	private static readonly string[] KnownPclRoots = new string[3] { "PCL", "Plain Craft Launcher", "PCL2" };

	private static readonly string[] KnownMclRoots = new string[2] { "MCLDownload", "MCLDownload GPU加速缓存" };

	public static string? FindPclExecutable()
	{
		string[] array = new string[4] { "G:\\PCL", "E:\\PCL", "D:\\PCL", "C:\\PCL" };
		string[] array2 = array;
		foreach (string path in array2)
		{
			string text = Path.Combine(path, "PCL2.exe");
			if (File.Exists(text))
			{
				return text;
			}
			text = Path.Combine(path, "PCL.exe");
			if (File.Exists(text))
			{
				return text;
			}
			text = Path.Combine(path, "Plain Craft Launcher.exe");
			if (File.Exists(text))
			{
				return text;
			}
		}
		DriveInfo[] drives = DriveInfo.GetDrives();
		for (int j = 0; j < drives.Length; j++)
		{
			string path2 = drives[j].Name.TrimEnd('\\');
			array = KnownPclRoots;
			string[] array3 = array;
			foreach (string path3 in array3)
			{
				string path4 = Path.Combine(path2, path3);
				if (!Directory.Exists(path4))
				{
					continue;
				}
				foreach (string item in Directory.EnumerateFiles(path4, "*.exe", SearchOption.TopDirectoryOnly))
				{
					if (item.IndexOf("PCL", StringComparison.OrdinalIgnoreCase) >= 0 || item.IndexOf("Launcher", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						return item;
					}
				}
			}
		}
		return null;
	}

}

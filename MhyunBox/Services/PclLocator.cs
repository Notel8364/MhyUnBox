using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MhyunBox.Settings;

namespace MhyunBox.Services;

// PCL 启动器定位（原代理启动页私有逻辑，抽出来供更新页第二合并路径复用）。
// 查找顺序：代理页记住的 PCL 路径 → G:\PCL\PCL2.exe → 注册表卸载项 + 全盘浅扫。
internal static class PclLocator
{
	/// <summary>按代理启动页同样的规则解析 PCL 启动器 exe 路径（找不到返回空串）。</summary>
	public static string ResolvePclExePath()
	{
		string text = SettingsManager.Instance.PclPath?.Trim() ?? "";
		if (!string.IsNullOrWhiteSpace(text) && File.Exists(text))
		{
			return text;
		}
		if (File.Exists("G:\\PCL\\PCL2.exe"))
		{
			return "G:\\PCL\\PCL2.exe";
		}
		List<string> list = FindPclCandidatesFast();
		if (list.Count > 0)
		{
			return list[0];
		}
		return "";
	}

	/// <summary>从 PCL 启动器 exe 推导 .minecraft 根目录列表（按优先级）。</summary>
	/// <remarks>PCL 约定 .minecraft 与启动器放同一目录；也兼容 versions 就在 exe 旁的布局。</remarks>
	public static List<string> GetMinecraftRoots()
	{
		List<string> list = new List<string>();
		void AddRoot(string? dir)
		{
			try
			{
				if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
				{
					string fullPath = Path.GetFullPath(dir);
					if (!list.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
					{
						list.Add(fullPath);
					}
				}
			}
			catch
			{
			}
		}
		// 固定候选（E:/G:/D:\PCL）与代理页保持一致
		string[] array = new string[3] { "E:", "G:", "D:" };
		foreach (string text in array)
		{
			AddRoot(text + "\\PCL\\.minecraft");
		}
		// 代理页记住的 / 自动找到的 PCL 启动器 → 同目录推导
		string text2 = ResolvePclExePath();
		if (!string.IsNullOrEmpty(text2))
		{
			try
			{
				string? dirName = Path.GetDirectoryName(Path.GetFullPath(text2));
				if (!string.IsNullOrEmpty(dirName))
				{
					AddRoot(Path.Combine(dirName, ".minecraft"));
					// exe 直接放在 .minecraft 里的布局
					AddRoot(dirName);
				}
			}
			catch
			{
			}
		}
		return list;
	}

	/// <summary>快速候选：注册表卸载项 + 各固定盘根目录两层浅扫（深度扫描留给手动「查找」按钮）。</summary>
	public static List<string> FindPclCandidatesFast()
	{
		List<string> list = new List<string>();
		string[] uninstallKeys = new string[2]
		{
			"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
			"Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall"
		};
		Microsoft.Win32.RegistryKey[] roots = new Microsoft.Win32.RegistryKey[2]
		{
			Microsoft.Win32.Registry.CurrentUser,
			Microsoft.Win32.Registry.LocalMachine
		};
		foreach (Microsoft.Win32.RegistryKey root in roots)
		{
			string[] array = uninstallKeys;
			foreach (string keyName in array)
			{
				try
				{
					using Microsoft.Win32.RegistryKey uninstallKey = root.OpenSubKey(keyName);
					if (uninstallKey == null)
					{
						continue;
					}
					string[] subKeyNames = uninstallKey.GetSubKeyNames();
					foreach (string subName in subKeyNames)
					{
						try
						{
							using Microsoft.Win32.RegistryKey entryKey = uninstallKey.OpenSubKey(subName);
							if (entryKey == null)
							{
								continue;
							}
							string raw = ((entryKey.GetValue("InstallLocation") as string) ?? "") + ";" + ((entryKey.GetValue("DisplayIcon") as string) ?? "") + ";" + ((entryKey.GetValue("UninstallString") as string) ?? "");
							if (!raw.Contains("PCL", StringComparison.OrdinalIgnoreCase))
							{
								continue;
							}
							string[] array2 = raw.Split(';');
							foreach (string item in array2)
							{
								string text2 = item?.Trim().Trim('"');
								int num = text2?.IndexOf(',') ?? -1;
								if (num > 0)
								{
									text2 = text2.Substring(0, num);
								}
								if (string.IsNullOrWhiteSpace(text2))
								{
									continue;
								}
								if (File.Exists(text2) && Path.GetFileName(text2).StartsWith("PCL", StringComparison.OrdinalIgnoreCase))
								{
									list.Add(text2);
								}
								else
								{
									TryAddPclFromDir(Directory.Exists(text2) ? text2 : Path.GetDirectoryName(text2), list);
								}
							}
						}
						catch
						{
						}
					}
				}
				catch
				{
				}
			}
		}
		try
		{
			DriveInfo[] drives = DriveInfo.GetDrives();
			foreach (DriveInfo driveInfo in drives)
			{
				if (driveInfo.IsReady && driveInfo.DriveType == DriveType.Fixed)
				{
					ScanShallowForPcl(new DirectoryInfo(driveInfo.RootDirectory.FullName), 0, list);
				}
			}
		}
		catch
		{
		}
		// 已知位置定向扫描：用户目录（Desktop/Downloads/Documents 浅层）+ QQ FileRecv（PCL 常见落地位置）
		ScanKnownLocations(list);
		return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>定向扫描用户常见目录：浅层扫 Desktop/Downloads/Documents，并对 Documents\Tencent Files\*\FileRecv 做一层枚举。</summary>
	private static void ScanKnownLocations(List<string> result)
	{
		try
		{
			string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string[] dirs = new string[3]
			{
				Path.Combine(profile, "Desktop"),
				Path.Combine(profile, "Downloads"),
				Path.Combine(profile, "Documents")
			};
			string[] array = dirs;
			foreach (string text in array)
			{
				if (Directory.Exists(text))
				{
					ScanShallowForPcl(new DirectoryInfo(text), 0, result, maxDepth: 3);
				}
			}
			string tencentFiles = Path.Combine(profile, "Documents", "Tencent Files");
			if (Directory.Exists(tencentFiles))
			{
				foreach (DirectoryInfo qqDir in new DirectoryInfo(tencentFiles).EnumerateDirectories())
				{
					try
					{
						string fileRecv = Path.Combine(qqDir.FullName, "FileRecv");
						if (!Directory.Exists(fileRecv))
						{
							continue;
						}
						TryAddPclFromDir(fileRecv, result);
						foreach (DirectoryInfo sub in new DirectoryInfo(fileRecv).EnumerateDirectories())
						{
							TryAddPclFromDir(sub.FullName, result);
							// FileRecv\<某目录>\ 下再找一层 .minecraft 同级的启动器
							try
							{
								foreach (DirectoryInfo sub2 in sub.EnumerateDirectories())
								{
									TryAddPclFromDir(sub2.FullName, result);
								}
							}
							catch
							{
							}
						}
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
	}

	public static void TryAddPclFromDir(string? dir, List<string> result)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
			{
				return;
			}
			string[] array = new string[4] { "PCL2.exe", "PCL.exe", "PCLCE.exe", "Plain Craft Launcher.exe" };
			foreach (string text in array)
			{
				string path = Path.Combine(dir, text);
				if (File.Exists(path))
				{
					result.Add(path);
					return;
				}
			}
		}
		catch
		{
		}
	}

	public static void ScanShallowForPcl(DirectoryInfo dir, int depth, List<string> result, int maxDepth = 2)
	{
		if (depth > maxDepth)
		{
			return;
		}
		try
		{
			foreach (FileInfo item2 in dir.EnumerateFiles("PCL*.exe"))
			{
				if (!item2.Name.Contains("unins", StringComparison.OrdinalIgnoreCase))
				{
					result.Add(item2.FullName);
				}
			}
			// PCL 完整名（Plain Craft Launcher.exe 不匹配 PCL* 通配）
			foreach (FileInfo item3 in dir.EnumerateFiles("Plain*.exe"))
			{
				if (item3.Name.StartsWith("Plain Craft Launcher", StringComparison.OrdinalIgnoreCase))
				{
					result.Add(item3.FullName);
				}
			}
			if (depth >= maxDepth)
			{
				return;
			}
			foreach (DirectoryInfo item4 in dir.EnumerateDirectories())
			{
				string name = item4.Name;
				if (!name.StartsWith("$", StringComparison.Ordinal) && !name.Equals("Windows", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase) && !name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) && !name.Equals("AppData", StringComparison.OrdinalIgnoreCase))
				{
					ScanShallowForPcl(item4, depth + 1, result, maxDepth);
				}
			}
		}
		catch
		{
		}
	}
}

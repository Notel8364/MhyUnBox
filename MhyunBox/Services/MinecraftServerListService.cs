using System;
using System.Collections.Generic;
using System.IO;

namespace MhyunBox.Services;

public static class MinecraftServerListService
{
	public sealed class Row
	{
		public int Port;

		public string Name = "";
	}

	public const int PortRangeStart = 25565;

	public const int PortRangeEnd = 25799;

	private static readonly string[] PclRoots = new string[3] { "PCL", "Plain Craft Launcher", "PCL2" };

	private static readonly string[] MclRoots = new string[2] { "MCLDownload", "MCLDownload GPU加速缓存" };

	public static List<string> FindMinecraftDirs()
	{
		List<string> result = new List<string>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		AddIfDir(result, seen, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft"));
		string text = PathResolver.FindPclExecutable();
		if (!string.IsNullOrWhiteSpace(text))
		{
			AddIfDir(result, seen, Path.Combine(Path.GetDirectoryName(text) ?? "", ".minecraft"));
		}
		DriveInfo[] array;
		try
		{
			array = DriveInfo.GetDrives();
		}
		catch
		{
			array = Array.Empty<DriveInfo>();
		}
		DriveInfo[] array2 = array;
		DriveInfo[] array3 = array2;
		foreach (DriveInfo driveInfo in array3)
		{
			string path;
			try
			{
				path = driveInfo.Name.TrimEnd('\\');
			}
			catch
			{
				continue;
			}
			string[] pclRoots = PclRoots;
			string[] array4 = pclRoots;
			foreach (string path2 in array4)
			{
				AddIfDir(result, seen, Path.Combine(path, path2, ".minecraft"));
				AddIfDir(result, seen, Path.Combine(path, path2, "Game", ".minecraft"));
			}
			pclRoots = MclRoots;
			string[] array5 = pclRoots;
			foreach (string path3 in array5)
			{
				string path4 = Path.Combine(path, path3);
				AddIfDir(result, seen, Path.Combine(path4, ".minecraft"));
				AddIfDir(result, seen, Path.Combine(path4, "Game", ".minecraft"));
				string path5 = Path.Combine(path4, "cache", "game");
				try
				{
					if (!Directory.Exists(path5))
					{
						continue;
					}
					foreach (string item in Directory.EnumerateDirectories(path5))
					{
						AddIfDir(result, seen, Path.Combine(item, ".minecraft"));
					}
				}
				catch
				{
				}
			}
		}
		return result;
	}

	private static void AddIfDir(List<string> result, HashSet<string> seen, string? path)
	{
		if (!string.IsNullOrWhiteSpace(path))
		{
			string fullPath;
			try
			{
				fullPath = Path.GetFullPath(path);
			}
			catch
			{
				return;
			}
			if (Directory.Exists(fullPath) && seen.Add(fullPath))
			{
				result.Add(fullPath);
			}
		}
	}

	public static (int Changed, List<string> Details) Sync(IReadOnlyList<Row> active, IReadOnlyList<string>? minecraftDirs = null)
	{
		List<string> list = new List<string>();
		int num = 0;
		Dictionary<int, string> dictionary = new Dictionary<int, string>();
		foreach (Row item in active ?? Array.Empty<Row>())
		{
			if (item.Port >= 25565 && item.Port <= 25799)
			{
				dictionary[item.Port] = item.Name ?? "";
			}
		}
		foreach (string item2 in minecraftDirs ?? FindMinecraftDirs())
		{
			string text;
			try
			{
				text = Path.Combine(item2, "servers.dat");
			}
			catch
			{
				continue;
			}
			try
			{
				bool flag = File.Exists(text);
				NbtValue nbtValue = NbtIo.TryLoadFile(text);
				if (flag && nbtValue == null)
				{
					list.Add(Path.GetFileName(item2) + " 解析失败，已跳过");
					continue;
				}
				Dictionary<string, NbtValue> dictionary2 = ((nbtValue?.Type == 10) ? nbtValue.AsCompound() : new Dictionary<string, NbtValue>(StringComparer.Ordinal));
				List<NbtValue> list2;
				if (dictionary2.TryGetValue("servers", out var value) && value.Type == 9)
				{
					list2 = value.AsList();
				}
				else
				{
					list2 = new List<NbtValue>();
					dictionary2["servers"] = NbtValue.OfList(list2);
				}
				List<NbtValue> list3 = new List<NbtValue>();
				bool flag2 = false;
				foreach (NbtValue item3 in list2)
				{
					if (item3.Type != 10)
					{
						list3.Add(item3);
						continue;
					}
					Dictionary<string, NbtValue> dictionary3 = item3.AsCompound();
					int num2 = ParseLocalPort(dictionary3.TryGetValue("ip", out var value2) ? value2.AsString() : "");
					string value3;
					if (num2 < 0)
					{
						list3.Add(item3);
					}
					else if (dictionary.TryGetValue(num2, out value3))
					{
						if (!dictionary3.TryGetValue("name", out var value4) || !string.Equals(value4.AsString(), value3, StringComparison.Ordinal))
						{
							dictionary3["name"] = NbtValue.OfString(value3);
							flag2 = true;
						}
						list3.Add(item3);
					}
					else
					{
						flag2 = true;
					}
				}
				foreach (KeyValuePair<int, string> item4 in dictionary)
				{
					bool flag3 = false;
					foreach (NbtValue item5 in list3)
					{
						if (item5.Type == 10 && ParseLocalPort(item5.AsCompound().TryGetValue("ip", out var value5) ? value5.AsString() : "") == item4.Key)
						{
							flag3 = true;
							break;
						}
					}
					if (!flag3)
					{
						Dictionary<string, NbtValue> map = new Dictionary<string, NbtValue>(StringComparer.Ordinal)
						{
							["name"] = NbtValue.OfString(item4.Value),
							["ip"] = NbtValue.OfString("127.0.0.1:" + item4.Key)
						};
						list3.Add(NbtValue.OfCompound(map));
						flag2 = true;
					}
				}
				if (list3.Count != list2.Count)
				{
					flag2 = true;
					list2.Clear();
					list2.AddRange(list3);
				}
				if (flag2)
				{
					NbtIo.SaveFile(text, NbtValue.OfCompound(dictionary2));
					num++;
					list.Add(item2 + " 已更新 " + list3.Count + " 条");
				}
			}
			catch (Exception ex)
			{
				LogHelper.DebugError(ex, "[服务器列表] 同步异常: " + text);
			}
		}
		return (Changed: num, Details: list);
	}

	private static int ParseLocalPort(string ip)
	{
		if (string.IsNullOrWhiteSpace(ip))
		{
			return -1;
		}
		string text = ip.Trim();
		int num = text.LastIndexOf(':');
		if (num < 0)
		{
			return -1;
		}
		string a = text.Substring(0, num).Trim();
		string s = text.Substring(num + 1).Trim();
		if (!string.Equals(a, "127.0.0.1", StringComparison.OrdinalIgnoreCase) && !string.Equals(a, "localhost", StringComparison.OrdinalIgnoreCase))
		{
			return -1;
		}
		if (!int.TryParse(s, out var result))
		{
			return -1;
		}
		if (result < 25565 || result > 25799)
		{
			return -1;
		}
		return result;
	}
}

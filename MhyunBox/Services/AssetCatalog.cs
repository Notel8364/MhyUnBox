using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MhyunBox.Services;

public static class AssetCatalog
{
	public static string ToolPacksDir => Path.Combine(AppContext.BaseDirectory, "ResourcePacks");

	public static List<PackItem> ScanPacks()
	{
		Dictionary<string, PackItem> dictionary = new Dictionary<string, PackItem>(StringComparer.OrdinalIgnoreCase);
		try
		{
			Directory.CreateDirectory(ToolPacksDir);
		}
		catch
		{
		}
		AddPacksFrom(dictionary, ToolPacksDir, toolOwned: true);
		string text = FindNeteaseGameDir();
		if (text != null)
		{
			try
			{
				string[] directories = Directory.GetDirectories(text);
				for (int i = 0; i < directories.Length; i++)
				{
					string text2 = Path.Combine(directories[i], ".minecraft", "resourcepacks");
					if (Directory.Exists(text2))
					{
						AddPacksFrom(dictionary, text2, toolOwned: false);
					}
				}
			}
			catch
			{
			}
		}
		return dictionary.Values.OrderBy<PackItem, string>((PackItem p) => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	public static PackItem? FindPack(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return null;
		}
		foreach (PackItem item in ScanPacks())
		{
			if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				return item;
			}
			if (string.Equals(item.Name + ".zip", name, StringComparison.OrdinalIgnoreCase))
			{
				return item;
			}
		}
		return null;
	}

	public static byte[]? ZipFolderPack(string folder)
	{
		if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
		{
			return null;
		}
		try
		{
			using MemoryStream memoryStream = new MemoryStream();
			using (ZipArchive zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
			{
				string[] files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
				string[] array = files;
				foreach (string path in array)
				{
					string entryName = Path.GetRelativePath(folder, path).Replace('\\', '/');
					using Stream destination = zipArchive.CreateEntry(entryName).Open();
					using FileStream fileStream = File.OpenRead(path);
					fileStream.CopyTo(destination);
				}
			}
			return memoryStream.ToArray();
		}
		catch
		{
			return null;
		}
	}

	private static string? FindNeteaseGameDir()
	{
		// 网易客户端 game 目录：常见 MCLDownload 安装位置逐盘探测
		string[] candidates = new string[3]
		{
			"E:\\MCLDownload\\cache\\game",
			"C:\\MCLDownload\\cache\\game",
			"D:\\MCLDownload\\cache\\game"
		};
		string[] array = candidates;
		foreach (string text3 in array)
		{
			if (Directory.Exists(text3))
			{
				return text3;
			}
		}
		return null;
	}

	private static void AddPacksFrom(Dictionary<string, PackItem> map, string root, bool toolOwned)
	{
		if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
		{
			return;
		}
		try
		{
			string[] fileSystemEntries = Directory.GetFileSystemEntries(root);
			string[] array = fileSystemEntries;
			foreach (string text in array)
			{
				string fileName = Path.GetFileName(text);
				if (string.IsNullOrWhiteSpace(fileName) || fileName == "__MACOSX" || map.ContainsKey(fileName))
				{
					continue;
				}
				bool flag = Directory.Exists(text);
				string sizeText;
				if (flag)
				{
					try
					{
						sizeText = Directory.GetFiles(text, "*", SearchOption.AllDirectories).Length + " 个文件";
					}
					catch
					{
						sizeText = "";
					}
				}
				else
				{
					try
					{
						sizeText = FormatBytes(new FileInfo(text).Length);
					}
					catch
					{
						sizeText = "";
					}
				}
				map[fileName] = new PackItem
				{
					Name = fileName,
					Path = text,
					IsFolder = flag,
					IsToolOwned = toolOwned,
					SizeText = sizeText
				};
			}
		}
		catch
		{
		}
	}

	private static string ComputeSha256Hex(string path)
	{
		using FileStream inputStream = File.OpenRead(path);
		using SHA256 sHA = SHA256.Create();
		byte[] array = sHA.ComputeHash(inputStream);
		StringBuilder stringBuilder = new StringBuilder(array.Length * 2);
		byte[] array2 = array;
		byte[] array3 = array2;
		foreach (byte b in array3)
		{
			stringBuilder.Append(b.ToString("x2"));
		}
		return stringBuilder.ToString();
	}

	private static string FormatBytes(long bytes)
	{
		if (bytes >= 1048576)
		{
			return ((double)bytes / 1048576.0).ToString("0.0") + " MB";
		}
		if (bytes >= 1024)
		{
			return ((double)bytes / 1024.0).ToString("0.0") + " KB";
		}
		return bytes + " B";
	}
}

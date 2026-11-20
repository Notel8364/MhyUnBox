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

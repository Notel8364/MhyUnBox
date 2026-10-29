using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;

namespace MhyunBox.Services;

/// <summary>
/// ZIP 解压的公共安全约束：拒绝绝对路径、../、盘符与 ADS 冒号，并限制累计解压体积，
/// 防止 Zip Slip（写出目标目录）与压缩炸弹（解压到磁盘占满）。
/// </summary>
internal static class ArchiveSafety
{
	/// <summary>单次解压的累计上限；正常资源包远小于此值，但足以拦住百 GB 级炸弹。</summary>
	internal const long MaxTotalExtractedBytes = 16L * 1024 * 1024 * 1024;

	private const int CopyBufferSize = 1024 * 1024;

	internal static bool IsSafeEntryPath(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return false;
		}
		string normalized = name!.Replace('/', '\\');
		if (normalized.IndexOf('\0') >= 0)
		{
			return false;
		}
		// 绝对路径、UNC、盘符、Windows 备用数据流（file.txt:stream）一律拒绝。
		if (normalized.StartsWith("\\", StringComparison.Ordinal) || normalized.IndexOf(':') >= 0 || Path.IsPathRooted(normalized))
		{
			return false;
		}
		foreach (string segment in normalized.Split('\\'))
		{
			if (segment == "..")
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>把条目相对路径解析到 rootDir 内，越界时抛 InvalidDataException。</summary>
	internal static string GetSafeDestination(string rootDir, string entryName)
	{
		if (!IsSafeEntryPath(entryName))
		{
			throw new InvalidDataException("压缩包内出现非法路径：" + entryName);
		}
		string root = Path.GetFullPath(rootDir);
		string dest = Path.GetFullPath(Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar)));
		string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (!dest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("压缩包内出现越界路径：" + entryName);
		}
		return dest;
	}

	/// <summary>安全解压整个 zip。目录条目跳过；累计解压超过上限立即中止。</summary>
	internal static void ExtractZipToDirectory(string zipPath, string destDir, bool overwrite)
	{
		Directory.CreateDirectory(destDir);
		long total = 0L;
		using ZipArchive archive = ZipFile.OpenRead(zipPath);
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			if (string.IsNullOrEmpty(entry.Name))
			{
				continue;
			}
			string destPath = GetSafeDestination(destDir, entry.FullName);
			Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
			ExtractEntry(entry, destPath, overwrite, ref total);
		}
	}

	/// <summary>流式解压单个条目并累计体积；不依赖中央目录里可能造假的 UncompressedLength。</summary>
	internal static void ExtractEntry(ZipArchiveEntry entry, string destPath, bool overwrite, ref long total)
	{
		using Stream input = entry.Open();
		using FileStream output = new FileStream(destPath, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
		byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
		try
		{
			int read;
			while ((read = input.Read(buffer, 0, CopyBufferSize)) > 0)
			{
				total += read;
				if (total > MaxTotalExtractedBytes)
				{
					throw new InvalidDataException("压缩包解压后超过安全上限 " + MaxTotalExtractedBytes + "B（疑似压缩炸弹）");
				}
				output.Write(buffer, 0, read);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}
}

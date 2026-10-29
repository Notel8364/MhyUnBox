using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>
/// DragonCore 资源包增量裁剪：
/// 基准包（旧版全量）vs 当前包（服务器最新全量）逐文件 SHA256 对比，
/// 裁出「新增 + 变更」的文件并保持原目录结构，压成小体积增量 zip。
/// 别的用户把增量 zip 解压覆盖到自己的 DragonCore 目录即完成合并更新。
/// 删除操作被忽略（不影响老用户已有资源）。
/// </summary>
public static class DragonCoreTrimService
{
	public sealed class TrimProgress
	{
		public string Phase = "";

		public int Done;

		public int Total;

		public string CurrentFile = "";
	}

	public sealed class TrimResult
	{
		public bool Success;

		public string Error = "";

		public int BaselineFiles;

		public int CurrentFiles;

		public int Added;

		public int Changed;

		public long AddedBytes;

		public List<string> Samples = new List<string>();

		public string OutputZip = "";

		public TimeSpan Elapsed;
	}

	public sealed class MergeResult
	{
		public bool Success;

		public string Error = "";

		public int Extracted;

		public string TargetDir = "";
	}

	/// <summary>候选的本地 DragonCore 目录（按优先级）。</summary>
	public static List<string> DefaultCandidateDirs()
	{
		List<string> list = new List<string>();
		string[] array = new string[3] { "E:", "G:", "D:" };
		foreach (string text in array)
		{
			string text2 = text + "\\PCL\\.minecraft\\versions\\元素芝士？启动！\\resourcepacks\\DragonCore";
			if (Directory.Exists(text2))
			{
				list.Add(text2);
			}
		}
		// 复用代理启动页的 PCL 定位：从 PCL 启动器 exe 同目录推导所有版本下的 resourcepacks\DragonCore
		try
		{
			string? best = null;
			foreach (string mcRoot in PclLocator.GetMinecraftRoots())
			{
				string versionsDir = Path.Combine(mcRoot, "versions");
				if (!Directory.Exists(versionsDir))
				{
					continue;
				}
				foreach (string item in Directory.EnumerateDirectories(versionsDir))
				{
					string candidate = Path.Combine(item, "resourcepacks", "DragonCore");
					if (!Directory.Exists(candidate))
					{
						continue;
					}
					if (Path.GetFileName(item).IndexOf("元素芝士", StringComparison.Ordinal) >= 0)
					{
						best = candidate;
						break;
					}
					best = best ?? candidate;
				}
				if (best != null)
				{
					break;
				}
			}
			if (best != null && !list.Contains(best, StringComparer.OrdinalIgnoreCase))
			{
				list.Add(best);
			}
		}
		catch
		{
		}
		return list;
	}

	public static string OutputDir => Path.Combine(AppContext.BaseDirectory, "DragonCorePacks");

	/// <summary>本进程通过 ResolvePackDir 创建的临时解压目录；CleanupTempDir 只允许删除这些目录。</summary>
	private static readonly ConcurrentDictionary<string, byte> TempDirs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	public static async Task<TrimResult> TrimAsync(string baselineDir, string currentDir, string? outputZipPath, Action<TrimProgress>? progress, CancellationToken ct)
	{
		TrimResult trimResult = new TrimResult();
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			baselineDir = Path.GetFullPath(baselineDir);
			currentDir = Path.GetFullPath(currentDir);
			if (!Directory.Exists(baselineDir))
			{
				throw new DirectoryNotFoundException("基准包目录不存在：" + baselineDir);
			}
			if (!Directory.Exists(currentDir))
			{
				throw new DirectoryNotFoundException("当前包目录不存在：" + currentDir);
			}
			if (string.Equals(baselineDir.TrimEnd('\\'), currentDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("基准包与当前包不能是同一个目录");
			}
			Report(progress, "扫描基准包文件…", 0, 0, "");
			Dictionary<string, string> dictionary = await Task.Run(() => HashTree(baselineDir, progress, "基准包", ct), ct);
			trimResult.BaselineFiles = dictionary.Count;
			Report(progress, "扫描当前包并对比…", 0, 0, "");
			Dictionary<string, string> dictionary2 = await Task.Run(() => HashTree(currentDir, progress, "当前包", ct), ct);
			trimResult.CurrentFiles = dictionary2.Count;
			List<string> list = new List<string>();
			List<string> list2 = new List<string>();
			long num = 0L;
			foreach (KeyValuePair<string, string> item in dictionary2)
			{
				ct.ThrowIfCancellationRequested();
				if (!dictionary.TryGetValue(item.Key, out string value))
				{
					list.Add(item.Key);
				}
				else if (!string.Equals(value, item.Value, StringComparison.Ordinal))
				{
					list2.Add(item.Key);
				}
			}
			list.Sort(StringComparer.Ordinal);
			list2.Sort(StringComparer.Ordinal);
			trimResult.Added = list.Count;
			trimResult.Changed = list2.Count;
			foreach (string item2 in list)
			{
				num += new FileInfo(Path.Combine(currentDir, item2)).Length;
			}
			foreach (string item3 in list2)
			{
				num += new FileInfo(Path.Combine(currentDir, item3)).Length;
			}
			trimResult.AddedBytes = num;
			if (list.Count == 0 && list2.Count == 0)
			{
				throw new InvalidOperationException("对比完成：没有发现任何新增或变更的文件，无需生成增量包。");
			}
			trimResult.Samples = list.Concat(list2).Take(30).ToList();
			string text = outputZipPath;
			if (string.IsNullOrWhiteSpace(text))
			{
				Directory.CreateDirectory(OutputDir);
				text = Path.Combine(OutputDir, "DragonCore_增量_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip");
			}
			Report(progress, "打包增量文件…", 0, list.Count + list2.Count, "");
			string text2 = Path.Combine(Path.GetTempPath(), "mhyunbox_dragoncore_trim_" + Guid.NewGuid().ToString("N"));
			try
			{
				Directory.CreateDirectory(text2);
				int num2 = 0;
				int total = list.Count + list2.Count;
				foreach (string item4 in list.Concat(list2))
				{
					ct.ThrowIfCancellationRequested();
					string text3 = Path.Combine(currentDir, item4);
					// zip 内嵌套一层 DragonCore 文件夹：解压即得 DragonCore 目录，可直接拖进 resourcepacks
					string text4 = Path.Combine(text2, "DragonCore", item4);
					Directory.CreateDirectory(Path.GetDirectoryName(text4)!);
					File.Copy(text3, text4, overwrite: true);
					num2++;
					if (num2 % 50 == 0)
					{
						Report(progress, "打包增量文件…", num2, total, item4);
					}
				}
				string contents = JsonSerializer.Serialize(new
				{
					generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
					baseline = baselineDir,
					current = currentDir,
					baseline_files = trimResult.BaselineFiles,
					current_files = trimResult.CurrentFiles,
					added = trimResult.Added,
					changed = trimResult.Changed,
					bytes = trimResult.AddedBytes,
					usage = "用法一：解压本 zip 得到 DragonCore 文件夹，直接拖进资源包文件夹（resourcepacks）里；用法二：用 MhyunBox 的「合并增量包」功能解压覆盖进已有 DragonCore 目录"
				}, new JsonSerializerOptions
				{
					WriteIndented = true
				});
				File.WriteAllText(Path.Combine(text2, "_mhy_incremental_manifest.json"), contents);
				Report(progress, "压缩增量包…", 0, 0, "");
				string text5 = text + ".tmp";
				if (File.Exists(text5))
				{
					File.Delete(text5);
				}
				await Task.Run(delegate
				{
					ZipFile.CreateFromDirectory(text2, text5, CompressionLevel.Optimal, includeBaseDirectory: false);
				}, ct);
				if (File.Exists(text))
				{
					File.Delete(text);
				}
				File.Move(text5, text);
				trimResult.OutputZip = text;
				trimResult.Success = true;
			}
			finally
			{
				try
				{
					Directory.Delete(text2, recursive: true);
				}
				catch
				{
				}
			}
		}
		catch (OperationCanceledException)
		{
			trimResult.Error = "已取消";
		}
		catch (Exception ex)
		{
			trimResult.Error = ex.Message;
			LogHelper.LogError(ex, "[DragonCore裁剪] 生成增量包失败");
		}
		stopwatch.Stop();
		trimResult.Elapsed = stopwatch.Elapsed;
		return trimResult;
	}

	private static Dictionary<string, string> HashTree(string rootDir, Action<TrimProgress>? progress, string label, CancellationToken ct)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(2048, StringComparer.Ordinal);
		List<string> list = Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories).ToList();
		int total = list.Count;
		int done = 0;
		object sync = new object();
		Parallel.ForEach(list, new ParallelOptions
		{
			CancellationToken = ct,
			MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
		}, delegate(string file)
		{
			ct.ThrowIfCancellationRequested();
			string key = Path.GetRelativePath(rootDir, file).Replace('/', '\\');
			string value = Sha256(file);
			lock (sync)
			{
				dictionary[key] = value;
				done++;
				if (done % 200 == 0)
				{
					Report(progress, "扫描" + label + "并计算哈希…", done, total, key);
				}
			}
		});
		Report(progress, "扫描" + label + "完成", total, total, "");
		return dictionary;
	}

	private static string Sha256(string file)
	{
		FileStream fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
		try
		{
			using SHA256 sHA = SHA256.Create();
			return Convert.ToHexString(sHA.ComputeHash(fileStream));
		}
		finally
		{
			fileStream.Dispose();
		}
	}

	public static async Task<MergeResult> MergeAsync(string zipPath, string dragonCoreDir, CancellationToken ct)
	{
		MergeResult mergeResult = new MergeResult();
		try
		{
			if (!File.Exists(zipPath))
			{
				throw new FileNotFoundException("增量包不存在：" + zipPath);
			}
			if (!Directory.Exists(dragonCoreDir))
			{
				throw new DirectoryNotFoundException("DragonCore 目录不存在：" + dragonCoreDir);
			}
			int count = 0;
			long declaredBytes = 0L;
			await Task.Run(delegate
			{
				using ZipArchive zipArchive = ZipFile.OpenRead(zipPath);
				foreach (ZipArchiveEntry entry in zipArchive.Entries)
				{
					if (string.IsNullOrEmpty(entry.Name))
					{
						continue;
					}
					string? rel = NormalizeMergeEntryName(entry.FullName);
					if (rel == null)
					{
						continue;
					}
					declaredBytes += entry.Length;
					if (declaredBytes > ArchiveSafety.MaxTotalExtractedBytes)
					{
						throw new InvalidDataException("增量包解压后超过安全上限（疑似压缩炸弹）");
					}
					count++;
				}
			}, ct);
			await Task.Run(delegate
			{
				// 逐条解压：兼容新旧两种格式（新格式 zip 内嵌套 DragonCore 文件夹，剥掉前缀直接落进目标目录）
				long extractedBytes = 0L;
				using ZipArchive zipArchive = ZipFile.OpenRead(zipPath);
				foreach (ZipArchiveEntry entry in zipArchive.Entries)
				{
					if (string.IsNullOrEmpty(entry.Name))
					{
						continue;
					}
					string? rel = NormalizeMergeEntryName(entry.FullName);
					if (rel == null)
					{
						continue;
					}
					string destPath = ArchiveSafety.GetSafeDestination(dragonCoreDir, rel);
					Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
					ArchiveSafety.ExtractEntry(entry, destPath, overwrite: true, ref extractedBytes);
				}
			}, ct);
		mergeResult.Extracted = count;
		mergeResult.TargetDir = dragonCoreDir;
		mergeResult.Success = true;
		LogHelper.Force("[DragonCore裁剪] 已合并增量包 " + Path.GetFileName(zipPath) + " => " + dragonCoreDir);
		// 合并成功后：增量包属于「文件/包」，仅在开启自动清理时移入回收站
		// （默认保留 —— 同一份增量包常要分发给其他用户，删了就没了）
		CleanupHelper.RecycleIfEnabled(zipPath, delegate (string m)
		{
			LogHelper.Force("[DragonCore裁剪] " + m);
		}, "已合并的增量包");
		}
		catch (Exception ex)
		{
			mergeResult.Error = ex.Message;
			LogHelper.LogError(ex, "[DragonCore裁剪] 合并增量包失败");
		}
		return mergeResult;
	}

	/// <summary>增量包条目的相对路径：剥掉可选的 DragonCore 前缀并校验是否越界。</summary>
	private static string? NormalizeMergeEntryName(string fullName)
	{
		string rel = fullName.Replace('/', '\\');
		if (rel.StartsWith("DragonCore\\", StringComparison.OrdinalIgnoreCase))
		{
			rel = rel.Substring("DragonCore\\".Length);
		}
		if (string.IsNullOrWhiteSpace(rel))
		{
			return null;
		}
		if (!ArchiveSafety.IsSafeEntryPath(rel))
		{
			throw new InvalidDataException("zip 内出现非法路径：" + fullName);
		}
		return rel;
	}

	private static void Report(Action<TrimProgress>? progress, string phase, int done, int total, string currentFile)
	{
		if (progress == null)
		{
			return;
		}
		try
		{
			progress(new TrimProgress
			{
				Phase = phase,
				Done = done,
				Total = total,
				CurrentFile = currentFile
			});
		}
		catch
		{
		}
	}

	/// <summary>判断目录是否像 DragonCore 资源包根目录（含 gui/models/sound 子目录或 .data 文件）。</summary>
	public static bool LooksLikeDragonCoreDir(string dir)
	{
		try
		{
			if (!Directory.Exists(dir))
			{
				return false;
			}
			string[] markers = new string[3] { "gui", "models", "sound" };
			if (markers.Any((string m) => Directory.Exists(Path.Combine(dir, m))))
			{
				return true;
			}
			return Directory.EnumerateFiles(dir, "*.data", SearchOption.TopDirectoryOnly).Any();
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// 把用户选择解析成 DragonCore 资源包目录：
	/// 本身就是 DragonCore 目录 → 原样返回；zip → 解压到临时目录后返回；版本客户端/上级文件夹 → 自动拼接/浅搜索 DragonCore 路径。
	/// 返回 null 表示解析失败。zip 解压的临时目录需要调用方用 CleanupTempDir 清理。
	/// </summary>
	public static string? ResolvePackDir(string? selected, Action<TrimProgress>? progress = null)
	{
		if (string.IsNullOrWhiteSpace(selected))
		{
			return null;
		}
		selected = selected.Trim().Trim('"');
		if (File.Exists(selected) && string.Equals(Path.GetExtension(selected), ".zip", StringComparison.OrdinalIgnoreCase))
		{
			string tempDir = Path.Combine(Path.GetTempPath(), "MhyDragonTrim_" + Guid.NewGuid().ToString("N").Substring(0, 8));
			string tempZip = Path.Combine(tempDir, "pack.zip");
			try
			{
				Directory.CreateDirectory(tempDir);
				TempDirs[tempDir] = 0;
				File.Copy(selected, tempZip, overwrite: true);
				progress?.Invoke(new TrimProgress { Phase = "解压新包压缩包…" });
				ArchiveSafety.ExtractZipToDirectory(tempZip, tempDir, overwrite: true);
				try
				{
					File.Delete(tempZip);
				}
				catch
				{
				}
				// zip 根可能直接就是包内容，也可能多包一层 DragonCore 文件夹
				if (LooksLikeDragonCoreDir(tempDir))
				{
					return tempDir;
				}
				string inner = Path.Combine(tempDir, "DragonCore");
				if (LooksLikeDragonCoreDir(inner))
				{
					return inner;
				}
				return tempDir;
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[DragonCore裁剪] 解压所选压缩包失败: " + Path.GetFileName(selected));
				CleanupTempDir(tempDir);
				return null;
			}
		}
		if (!Directory.Exists(selected))
		{
			return null;
		}
		if (LooksLikeDragonCoreDir(selected))
		{
			return selected;
		}
		string[] composed = new string[4]
		{
			Path.Combine(selected, "resourcepacks", "DragonCore"),
			Path.Combine(selected, ".minecraft", "resourcepacks", "DragonCore"),
			Path.Combine(selected, ".minecraft", "versions"),
			Path.Combine(selected, "versions")
		};
		foreach (string c in composed)
		{
			if (LooksLikeDragonCoreDir(c))
			{
				return c;
			}
		}
		// 浅搜索（深度 ≤3）找名为 DragonCore 且像资源包的目录
		try
		{
			DirectoryInfo root = new DirectoryInfo(selected);
			foreach (DirectoryInfo l1 in root.EnumerateDirectories())
			{
				if (LooksLikeDragonCoreDir(Path.Combine(l1.FullName, "DragonCore")))
				{
					return Path.Combine(l1.FullName, "DragonCore");
				}
				if (LooksLikeDragonCoreDir(l1.FullName))
				{
					return l1.FullName;
				}
				foreach (DirectoryInfo l2 in SafeSubDirs(l1))
				{
					if (LooksLikeDragonCoreDir(Path.Combine(l2.FullName, "DragonCore")))
					{
						return Path.Combine(l2.FullName, "DragonCore");
					}
					// 版本客户端三层结构：versions\<版本名>\resourcepacks\DragonCore
					if (LooksLikeDragonCoreDir(Path.Combine(l2.FullName, "resourcepacks", "DragonCore")))
					{
						return Path.Combine(l2.FullName, "resourcepacks", "DragonCore");
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static IEnumerable<DirectoryInfo> SafeSubDirs(DirectoryInfo dir)
	{
		try
		{
			return dir.EnumerateDirectories();
		}
		catch
		{
			return Array.Empty<DirectoryInfo>();
		}
	}

	/// <summary>删除 ResolvePackDir 创建的临时解压目录；只接受本进程登记过的临时根，避免误删用户目录。</summary>
	public static void CleanupTempDir(string? dir)
	{
		if (string.IsNullOrWhiteSpace(dir))
		{
			return;
		}
		try
		{
			string full = Path.GetFullPath(dir!);
			foreach (string root in TempDirs.Keys)
			{
				string rootFull = Path.GetFullPath(root);
				string prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
				if (!string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (TempDirs.TryRemove(root, out _) && Directory.Exists(rootFull))
				{
					Directory.Delete(rootFull, recursive: true);
				}
				return;
			}
			LogHelper.Debug("[DragonCore裁剪] 拒绝清理非本程序创建的临时目录: " + full);
		}
		catch
		{
		}
	}
}

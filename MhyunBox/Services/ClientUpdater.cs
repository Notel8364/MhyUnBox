using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Settings;

namespace MhyunBox.Services;

public static class ClientUpdater
{
	private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
	{
		AutomaticDecompression = (DecompressionMethods.GZip | DecompressionMethods.Deflate),
		UseProxy = true
	});

	private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "MhyunBox_Logs", "ClientUpdate.log");

	public static async Task<string> RunAsync(string url, long gameId, string installRoot, bool force, bool writeSnapshot, Action<string> log, CancellationToken ct, string? extraMergeTarget = null)
	{
		ItemUrl parsed = ItemUrl.Parse(url);
		if (parsed == null)
		{
			throw new InvalidOperationException("URL 不符合 item_<gameid>_<m>_v<ver>_<hash>.7z 格式");
		}
		long targetId = ((gameId > 0) ? gameId : parsed.GameId);
		if (gameId > 0 && parsed.GameId != targetId)
		{
			throw new InvalidOperationException($"URL 的 gameId {parsed.GameId} 与所选服务器 {gameId} 不一致");
		}
		L(log, $"parsed: {parsed.PackageName} gameId={parsed.GameId} module={parsed.Module} ver={parsed.Ver} hash={parsed.Hash}");
		ServerEntry serverEntry = (ServerRegistry.Load().Entries.TryGetValue(targetId, out var value) ? value : null);
		if (serverEntry != null)
		{
			L(log, $"server: {serverEntry.Name} (module={serverEntry.Module}, frozen={serverEntry.Frozen})");
			if (serverEntry.Frozen && !force)
			{
				throw new InvalidOperationException(serverEntry.Name + " 已冻结（该服从不更新客户端包），勾选“强制覆盖”可绕过。");
			}
		}
		else
		{
			L(log, "server unknown, treated as active");
		}
		// 与本机已装版本比对：明确告诉用户「已是最新 / 有新版本」，而不只是「这个地址能不能下」
		if (serverEntry != null && !string.IsNullOrWhiteSpace(serverEntry.LastHash) && parsed.Hash.Length == 32)
		{
			string installedAt = serverEntry.InstalledAt.HasValue ? serverEntry.InstalledAt.Value.ToString("yyyy-MM-dd HH:mm") : "未知";
			if (string.Equals(serverEntry.LastHash, parsed.Hash, StringComparison.OrdinalIgnoreCase))
			{
				L(log, $"已是最新：本机已装的 hash 与本次地址一致（{serverEntry.LastHash}，安装于 {installedAt}）");
			}
			else
			{
				L(log, $"检测到新版本：本机已装 hash={serverEntry.LastHash}（安装于 {installedAt}）→ 本次 hash={parsed.Hash}");
			}
		}
		string fullPath = Path.GetFullPath(installRoot);
		string instanceMc = Path.Combine(fullPath, "cache\\game", targetId.ToString(), ".minecraft");
		string text = Path.Combine(fullPath, "Game", ".minecraft");
		string target = (Directory.Exists(instanceMc) ? instanceMc : text);
		L(log, "install target: " + target);
		string work = ResPkgWorkDir(targetId);
		Directory.CreateDirectory(work);
		string pkg = Path.Combine(work, parsed.PackageName);
		string marker = pkg + ".installed";
		if (File.Exists(marker) && !force)
		{
			throw new InvalidOperationException($"该包已安装到 {target}（删除 {marker} 或勾选“强制覆盖”）");
		}
		// 本地复用：下载目录里已有同名包（上次下载过）→ 跳过下载直接校验安装
		// .part 残留 = 上次下载中断的痕迹 → 直接删除重新下载
		string partPath = pkg + ".part";
		try
		{
			if (File.Exists(partPath))
			{
				L(log, "发现上次中断的下载残留（" + Path.GetFileName(partPath) + "），删除后重新下载");
				File.Delete(partPath);
			}
		}
		catch
		{
		}
		bool reused = File.Exists(pkg);
		if (reused)
		{
			FileInfo local = new FileInfo(pkg);
			L(log, $"检测到本地已有该资源包（{local.Length:N0} 字节，{local.LastWriteTime:yyyy-MM-dd HH:mm}），跳过下载直接校验");
		}
		string md5 = (reused ? Md5File(pkg) : "");
		if (!reused || (parsed.Hash.Length == 32 && !string.Equals(md5, parsed.Hash, StringComparison.OrdinalIgnoreCase)))
		{
			if (!reused)
			{
				L(log, "checking " + url);
				HttpResponseMessage httpResponseMessage = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), HttpCompletionOption.ResponseHeadersRead, ct);
				L(log, $"HEAD {(int)httpResponseMessage.StatusCode} {httpResponseMessage.ReasonPhrase}");
				if (httpResponseMessage.StatusCode == HttpStatusCode.NotFound)
				{
					throw new InvalidOperationException("404 - 该 item 版本在 CDN 上已过期（hash 过期）。需要当前 hash。");
				}
				ct.ThrowIfCancellationRequested();
				if (!httpResponseMessage.IsSuccessStatusCode)
				{
					throw new InvalidOperationException($"unexpected HEAD status {(int)httpResponseMessage.StatusCode}");
				}
				Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MCLDownload/1.15.28.11160 Minecraft/1.12.2");
				long totalLen = httpResponseMessage.Content.Headers.ContentLength ?? -1L;
				bool acceptRanges = httpResponseMessage.Headers.AcceptRanges.Contains("bytes");
				// 远端更新时间：CDN 的 Last-Modified 即该资源包的发布/更新时间
				DateTimeOffset? remoteModified = httpResponseMessage.Content.Headers.LastModified ?? httpResponseMessage.Headers.Date;
				if (remoteModified.HasValue)
				{
					L(log, "资源包更新时间（CDN Last-Modified）：" + remoteModified.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
				}
				else
				{
					L(log, "资源包更新时间：CDN 未返回 Last-Modified，无法获取");
				}
				L(log, "downloading to " + pkg + (acceptRanges && totalLen > 52428800L ? $"（多线程 {ParallelThreads} 分块加速）" : "（单线程）"));
				// 统一先写 .part 临时文件，下载完成校验大小后改回正式名 —— 中断/断电不再产生半截正式包
				Stopwatch sw = Stopwatch.StartNew();
				if (acceptRanges && totalLen > 52428800L)
				{
					await ParallelDownloadAsync(url, partPath, totalLen, log, ct);
				}
				else
				{
					using HttpResponseMessage res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
					using Stream src = await res.Content.ReadAsStreamAsync(ct);
					using FileStream dst = File.Create(partPath);
					await CopyWithTimeoutAsync(src, dst, ct);
				}
				ct.ThrowIfCancellationRequested();
				if (totalLen > 0 && new FileInfo(partPath).Length != totalLen)
				{
					throw new InvalidOperationException($"下载不完整（期望 {totalLen:N0} 字节，实际 {new FileInfo(partPath).Length:N0}），.part 已保留，请重新点击「更新资源包」续下");
				}
				File.Move(partPath, pkg, overwrite: true);
				sw.Stop();
				FileInfo fileInfo = new FileInfo(pkg);
				L(log, $"downloaded {fileInfo.Length:N0} bytes in {sw.Elapsed.TotalSeconds:N1}s");
				md5 = Md5File(pkg);
			}
			else
			{
				L(log, "本地包 MD5 不符，删除后重新下载");
				try
				{
					File.Delete(pkg);
				}
				catch
				{
				}
				throw new InvalidOperationException("本地已有包校验失败已删除，请再点一次「更新资源包」重新下载");
			}
		}
		L(log, "md5=" + md5);
		if (parsed.Hash.Length == 32)
		{
			if (!string.Equals(md5, parsed.Hash, StringComparison.OrdinalIgnoreCase))
			{
				L(log, "MD5 校验失败（期望 " + parsed.Hash + "，实际 " + md5 + "），拒绝安装并清理下载包");
				try
				{
					File.Delete(pkg);
				}
				catch
				{
				}
				throw new InvalidOperationException($"下载包 MD5 校验失败（期望 {parsed.Hash}，实际 {md5}），已拒绝安装，请检查更新源/网络（可能被篡改）。");
			}
			L(log, "MD5 校验通过，与 URL hash 一致");
		}
		else
		{
			L(log, "URL hash 非 32 位 md5 格式（" + parsed.Hash + "），跳过完整性校验");
		}
		SettingsManager.Instance.LastResPkgUrl = url;
		try
		{
			SettingsManager.Save(SettingsManager.Instance);
		}
		catch
		{
		}
		return await InstallFromPackageAsync(parsed, pkg, targetId, installRoot, writeSnapshot, log, ct, extraMergeTarget, url, work, md5);
	}

	// 资源包下载/工作目录：自定义 > 本机「下载」目录\MhyunBox资源包（旧 temp 目录仅扫描时兼容）
	public static string ResPkgRoot()
	{
		string text = SettingsManager.Instance.ResPkgDownloadDir?.Trim() ?? "";
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text;
		}
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MhyunBox资源包");
	}

	public static string ResPkgWorkDir(long gameId)
	{
		return Path.Combine(ResPkgRoot(), gameId.ToString());
	}

	// 多线程分块下载：CDN 返回 Accept-Ranges 且文件 >50MB 时 16 线程并发拉取，失败回退单线程
	public const int ParallelThreads = 16;

	// 单次读取超时：CDN 偶发连接僵死时，ResponseHeadersRead 之后的响应体读取不受 HttpClient.Timeout
	// 保护，会永久挂起（14:17 日志 9.5 分钟无进度即此因）。90 秒读不到任何数据就中止，由上层回退重下。
	private const int ReadTimeoutMs = 90_000;

	private static async Task<int> ReadWithTimeoutAsync(Stream src, byte[] buffer, CancellationToken ct)
	{
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
		linked.CancelAfter(ReadTimeoutMs);
		try
		{
			return await src.ReadAsync(buffer, linked.Token);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			throw new InvalidOperationException($"下载连接 {ReadTimeoutMs / 1000} 秒无新数据（疑似 CDN 僵死），已中止等待");
		}
	}

	private static async Task CopyWithTimeoutAsync(Stream src, Stream dst, CancellationToken ct)
	{
		byte[] buffer = new byte[1048576];
		int n;
		while ((n = await ReadWithTimeoutAsync(src, buffer, ct)) > 0)
		{
			await dst.WriteAsync(buffer.AsMemory(0, n), ct);
		}
	}

	private static async Task ParallelDownloadAsync(string url, string pkg, long totalLen, Action<string> log, CancellationToken ct)
	{
		long part = totalLen / ParallelThreads;
		using (FileStream fileStream = new FileStream(pkg, FileMode.Create, FileAccess.Write, FileShare.Write, 1048576))
		{
			fileStream.SetLength(totalLen);
		}
		long done = 0L;
		object sync = new object();
		Stopwatch sw = Stopwatch.StartNew();
		List<Task> list = new List<Task>();
		for (int i = 0; i < ParallelThreads; i++)
		{
			long num = i * part;
			long num2 = ((i == ParallelThreads - 1) ? (totalLen - 1) : (num + part - 1));
			if (num > num2)
			{
				continue;
			}
			list.Add(Task.Run(async delegate
			{
				using FileStream fileStream2 = new FileStream(pkg, FileMode.Open, FileAccess.Write, FileShare.Write, 1048576, FileOptions.Asynchronous);
				using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
				request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(num, num2);
				using HttpResponseMessage httpResponseMessage = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
				httpResponseMessage.EnsureSuccessStatusCode();
				using Stream stream = await httpResponseMessage.Content.ReadAsStreamAsync(ct);
				fileStream2.Seek(num, SeekOrigin.Begin);
				byte[] array = new byte[1048576];
				int num3;
				while ((num3 = await ReadWithTimeoutAsync(stream, array, ct)) > 0)
				{
					await fileStream2.WriteAsync(array.AsMemory(0, num3), ct);
					lock (sync)
					{
						done += num3;
					}
				}
			}, ct));
		}
		Task task = Task.WhenAll(list);
		Task progressTask = Task.Run(async delegate
		{
			double lastDone = -1.0;
			int stallTicks = 0;
			try
			{
				while (!task.IsCompleted)
			{
				await Task.Delay(3000, ct);
				double num4 = Interlocked.Read(ref done) / 1048576.0;
				if (num4 <= lastDone + 0.01)
					{
						stallTicks++;
						if (stallTicks >= 3)
						{
							L(log, $"  ⚠ 已 {stallTicks * 3} 秒无新数据（累计 {num4:N0}/{totalLen / 1048576.0:N0} MB），若持续 90 秒将中止并回退重下");
						}
						continue;
					}
					stallTicks = 0;
					lastDone = num4;
					double num5b = num4 / sw.Elapsed.TotalSeconds;
					L(log, $"  下载中 {num4:N0}/{totalLen / 1048576.0:N0} MB（{num5b:N1} MB/s，{ParallelThreads} 线程）");
				}
			}
			catch
			{
			}
		}, ct);
		try
		{
			await task;
		}
		catch (Exception) when (!ct.IsCancellationRequested)
		{
			// 分块下载失败（CDN 不稳定等）：回退单线程整体下载
			L(log, "多线程下载异常，回退单线程重新下载...");
			using HttpResponseMessage res2 = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
			using Stream src2 = await res2.Content.ReadAsStreamAsync(ct);
			using FileStream dst2 = File.Create(pkg);
			await CopyWithTimeoutAsync(src2, dst2, ct);
		}
	}

	// 本地资源包信息（更新页「扫描本地资源包」用）
	public sealed class LocalPkgInfo
	{
		public string Path = "";

		public string PackageName = "";

		public long GameId;

		public string Ver = "";

		public string Hash = "";

		public long Size;

		public DateTime Modified;

		public string Md5 = "";

		public bool Md5Ok;

		public bool Md5Checkable;
	}

	// 扫描本地资源包：下载目录 + 旧 temp 目录（兼容历史下载），逐个 md5 校验，按时间倒序
	public static async Task<List<LocalPkgInfo>> ScanLocalPackagesAsync(Action<string>? log, CancellationToken ct)
	{
		List<LocalPkgInfo> list = new List<LocalPkgInfo>();
		List<string> list2 = new List<string>();
		try
		{
			list2.Add(ResPkgRoot());
		}
		catch
		{
		}
		try
		{
			list2.Add(Path.Combine(Path.GetTempPath(), "mhyunbox_respkg"));
		}
		catch
		{
		}
		List<string> list3 = new List<string>();
		foreach (string item in list2)
		{
			if (!Directory.Exists(item))
			{
				continue;
			}
			try
			{
				foreach (string item2 in Directory.EnumerateFiles(item, "item_*.7z", SearchOption.AllDirectories))
				{
					if (!list3.Any((string p) => string.Equals(p, item2, StringComparison.OrdinalIgnoreCase)))
					{
						list3.Add(item2);
					}
				}
			}
			catch
			{
			}
		}
		foreach (string path in list3)
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				Match match = Regex.Match(Path.GetFileName(path), "^item_(\\d+)_(\\d+)_(v\\d+)_([0-9A-Za-z]+)\\.7z$", RegexOptions.IgnoreCase);
				if (!match.Success)
				{
					continue;
				}
				LocalPkgInfo localPkgInfo = new LocalPkgInfo
				{
					Path = path,
					PackageName = Path.GetFileName(path),
					GameId = long.Parse(match.Groups[1].Value),
					Ver = match.Groups[3].Value,
					Hash = match.Groups[4].Value,
					Size = new FileInfo(path).Length,
					Modified = File.GetLastWriteTime(path)
				};
				localPkgInfo.Md5 = await Task.Run(() => Md5File(path), ct);
				localPkgInfo.Md5Ok = (localPkgInfo.Hash.Length == 32 && string.Equals(localPkgInfo.Md5, localPkgInfo.Hash, StringComparison.OrdinalIgnoreCase));
				localPkgInfo.Md5Checkable = localPkgInfo.Hash.Length == 32;
				list.Add(localPkgInfo);
				string md5Text = (!localPkgInfo.Md5Checkable) ? "无法校验（非MD5命名）" : (localPkgInfo.Md5Ok ? "通过" : "不符");
				log?.Invoke($"[本地包] {localPkgInfo.PackageName}  {localPkgInfo.Size / 1048576.0:N0} MB  {localPkgInfo.Modified:MM-dd HH:mm}  MD5 " + md5Text);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
			}
		}
		return list.OrderByDescending((LocalPkgInfo p) => p.Modified).ToList();
	}

	// 从本地已有资源包直接安装（跳过下载）：校验 MD5 → 解压合并 →（元素之诗）DragonCore 裁剪
	public static async Task<string> RunFromPackageAsync(string pkgPath, long gameId, string installRoot, bool writeSnapshot, Action<string> log, CancellationToken ct, string? extraMergeTarget = null)
	{
		if (!File.Exists(pkgPath))
		{
			throw new FileNotFoundException("资源包不存在：" + pkgPath);
		}
		Match match = Regex.Match(Path.GetFileName(pkgPath), "^item_(\\d+)_(\\d+)_(v\\d+)_([0-9A-Za-z]+)\\.7z$", RegexOptions.IgnoreCase);
		if (!match.Success)
		{
			throw new InvalidOperationException("文件名不符合 item_<gameid>_<m>_v<ver>_<hash>.7z，无法识别（请用原始下载文件名）");
		}
		ItemUrl parsed = new ItemUrl
		{
			GameId = long.Parse(match.Groups[1].Value),
			Module = match.Groups[2].Value,
			Ver = match.Groups[3].Value,
			Hash = match.Groups[4].Value,
			PackageName = Path.GetFileName(pkgPath)
		};
		long targetId = ((gameId > 0) ? gameId : parsed.GameId);
		if (gameId > 0 && parsed.GameId != targetId)
		{
			throw new InvalidOperationException($"资源包的 gameId {parsed.GameId} 与所选服务器 {gameId} 不一致");
		}
		L(log, "本地包安装：" + pkgPath);
		string text = Md5File(pkgPath);
		L(log, "md5=" + text);
		if (parsed.Hash.Length == 32 && !string.Equals(text, parsed.Hash, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException($"本地包 MD5 校验失败（期望 {parsed.Hash}，实际 {text}），包可能不完整或被篡改");
		}
		if (parsed.Hash.Length != 32)
		{
			// URL hash 非 MD5 命名（如 mjitou8i）：无法用 MD5 校验，用 7z 完整性测试兜底，
			// 拦下「下载中断的半截包」（否则解压到一半才报 ERROR）
			L(log, "hash 非 MD5 命名，用 7z 完整性测试代替校验...");
			string sevenZip = Resolve7z();
			using Process p = Process.Start(new ProcessStartInfo
			{
				FileName = sevenZip,
				Arguments = "t \"" + pkgPath + "\"",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}) ?? throw new InvalidOperationException("无法启动 7z 完整性测试进程");
			Task<string> testOutTask = p.StandardOutput.ReadToEndAsync(ct);
			Task<string> testErrTask = p.StandardError.ReadToEndAsync(ct);
			string output = await testOutTask;
			string testError = await testErrTask;
			await p.WaitForExitAsync(ct);
			if (p.ExitCode != 0)
			{
				throw new InvalidOperationException("资源包完整性测试失败（7z t 返回 " + p.ExitCode + "）——这个包极可能是下载中断的残留文件，请在「扫描本地资源包」里删掉它后重新点「更新资源包」下载。日志尾部：" + string.Join(" | ", (output + "\n" + testError).Split('\n').Select((string s) => s.Trim()).Where((string s) => s.Length > 0).TakeLast(3)));
			}
			L(log, "7z 完整性测试通过");
		}
		L(log, "MD5 校验通过");
		string text2 = Path.GetDirectoryName(Path.GetFullPath(pkgPath))!;
		return await InstallFromPackageAsync(parsed, pkgPath, targetId, installRoot, writeSnapshot, log, ct, extraMergeTarget, "(本地包 " + parsed.PackageName + ")", text2, text);
	}

	// 安装阶段（下载/本地包共用）：解压 → 合并进网易实例 → 第二合并目标 →（元素之诗）DragonCore 裁剪 → 快照/标记
	private static async Task<string> InstallFromPackageAsync(ItemUrl parsed, string pkg, long targetId, string installRoot, bool writeSnapshot, Action<string> log, CancellationToken ct, string? extraMergeTarget, string urlForRecord, string work, string md5)
	{
		ServerEntry serverEntry = (ServerRegistry.Load().Entries.TryGetValue(targetId, out var value) ? value : null);
		string fullPath = Path.GetFullPath(installRoot);
		string instanceMc = Path.Combine(fullPath, "cache\\game", targetId.ToString(), ".minecraft");
		string text = Path.Combine(fullPath, "Game", ".minecraft");
		string target = (Directory.Exists(instanceMc) ? instanceMc : text);
		L(log, "install target: " + target);
		Directory.CreateDirectory(target);
		string text2 = Resolve7z();
		L(log, "7z: " + text2);
		// 解压前先校验包内路径，避免 7z 的绝对路径/.. 条目写出 stage 目录。
		await EnsureArchiveEntriesSafeAsync(text2, pkg, ct);
		string marker = pkg + ".installed";
		// —— 元素之诗专用：DragonCore 裁剪更新路径 ——
		// 每次更新只是往 DragonCore 模型文件夹里塞新贴图（10-100MB），却要全量下载 800MB
		// 再粗放合并。这里改为：只从包里提取 DragonCore 部分 → 只复制新增/变更文件进
		// PCL 客户端资源包路径的 DragonCore 目录（记忆目录或自动候选），其余大体积贴图不再合并。
		// 失落世界等服务器贴图直接在资源包里加载，维持原有直接覆盖路径。
		bool isElementPoetry = targetId == 4658261307332877912L || string.Equals(serverEntry?.Name, "元素之诗", StringComparison.Ordinal);
		string? dragonTarget = (isElementPoetry ? ResolveDragonCoreTarget() : null);
		bool trimmed = false;
		if (isElementPoetry && dragonTarget == null)
		{
			L(log, "元素之诗：未找到 PCL 客户端 DragonCore 目录（无记忆路径也无自动候选），回退全量合并进网易实例");
		}
		if (dragonTarget != null)
		{
			trimmed = await TryElementPoetryTrimAsync(text2, pkg, work, dragonTarget, target, log, ct);
			if (!trimmed)
			{
				L(log, "DragonCore 裁剪未命中（包内无 DragonCore 或提取失败），回退全量合并");
			}
		}
		string stage = Path.Combine(work, "stage");
		if (!trimmed)
		{
			if (Directory.Exists(stage))
			{
				Directory.Delete(stage, recursive: true);
			}
			Directory.CreateDirectory(stage);
			L(log, "extracting...");
			await ExtractAsync(text2, pkg, stage, null, ct);
			ct.ThrowIfCancellationRequested();
			L(log, "extracted");
			foreach (var item in MergeDirs(stage, target, parsed.Hash))
			{
				int value3 = MergeCopy(item.Item1, item.Item2, target, work, parsed.Hash);
				L(log, $"  {Path.GetFileName(item.Item2)}: {value3} files");
			}
			int num = MergeLoose(stage, target, parsed.Hash);
			if (num > 0)
			{
				L(log, $"  loose files: {num}");
			}
			// 第二合并目标（PCL 客户端实例，失落世界等）：网易实例合并完成后同样合并一份进 PCL
			if (!string.IsNullOrWhiteSpace(extraMergeTarget) && Directory.Exists(extraMergeTarget) && !string.Equals(Path.GetFullPath(extraMergeTarget), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
			{
				L(log, "第二合并目标（PCL 客户端）: " + extraMergeTarget);
				foreach (var item2 in MergeDirs(stage, extraMergeTarget, parsed.Hash))
				{
					int pclFiles = MergeCopy(item2.Item1, item2.Item2, extraMergeTarget, work, parsed.Hash);
					L(log, $"  [PCL] {Path.GetFileName(item2.Item2)}: {pclFiles} files");
				}
				int num2 = MergeLoose(stage, extraMergeTarget, parsed.Hash);
				if (num2 > 0)
				{
					L(log, $"  [PCL] loose files: {num2}");
				}
				try
				{
					SettingsManager.Instance.PclClientRootDir = extraMergeTarget;
					SettingsManager.Save(SettingsManager.Instance);
				}
				catch
				{
				}
			}
			else if (!string.IsNullOrWhiteSpace(extraMergeTarget))
			{
				L(log, $"第二合并目标无效或不存在，跳过：{extraMergeTarget}");
			}
		}
		if (writeSnapshot)
		{
			WriteSnapshot(instanceMc, parsed.PackageName);
		}
		File.WriteAllText(marker, $"url={urlForRecord}\ninstalled={DateTime.Now:yyyy-MM-dd HH:mm:ss}\ntarget={target}\nmd5={md5}");
		ServerRegistry serverRegistry = ServerRegistry.Load();
		serverEntry = (serverRegistry.Entries.TryGetValue(targetId, out var value4) ? value4 : new ServerEntry
		{
			GameId = targetId
		});
		ServerEntry serverEntry2 = serverEntry;
		if (serverEntry2.Name == null)
		{
			serverEntry2.Name = "unknown";
		}
		serverEntry2 = serverEntry;
		if (serverEntry2.Module == null)
		{
			serverEntry2.Module = parsed.Module;
		}
		serverEntry.LastUrl = urlForRecord;
		serverEntry.LastHash = parsed.Hash;
		serverEntry.InstalledAt = DateTime.Now;
		serverRegistry.Entries[targetId] = serverEntry;
		serverRegistry.Save();
		// 收尾清理：解压暂存目录是程序自己产出的中间物，无条件删除；
		// 下载到的资源包属于「文件/包」，仅在开启自动清理时移入回收站（默认保留，便于重装/复用）
		CleanupHelper.RemoveTempDir(Path.Combine(work, "stage"), log);
		CleanupHelper.RemoveTempDir(Path.Combine(work, "stage_dragon"), log);
		CleanupHelper.RemoveTempDir(Path.Combine(work, "stage_rest"), log);
		CleanupHelper.RecycleIfEnabled(pkg, log, "已安装的资源包");
		L(log, "DONE. Restart Minecraft so the new resources load.");
		return "DONE  hash=" + md5;
	}

	/// <summary>HEAD 探测到的远端资源包信息，含「更新时间」。</summary>
	public sealed class RemotePackageInfo
	{
		public int Status;

		public string Reason = "";

		public long ContentLength = -1L;

		public bool AcceptRanges;

		/// <summary>CDN 的 Last-Modified —— 即该资源包在服务器上的发布/更新时间；服务端未返回时为 null。</summary>
		public DateTimeOffset? LastModified;

		/// <summary>更新时间的可读文本（无则给说明）。</summary>
		public string UpdateTimeText => LastModified.HasValue
			? LastModified.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
			: "未知（CDN 未返回 Last-Modified）";
	}

	// HEAD 探测：除状态码/长度外，额外取回 CDN 的 Last-Modified（= 资源包更新时间）
	public static async Task<RemotePackageInfo> ProbeAsync(string url, Action<string> log, CancellationToken ct)
	{
		RemotePackageInfo info = new RemotePackageInfo();
		Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MCLDownload/1.15.28.11160 Minecraft/1.12.2");
		L(log, "checking " + url);
		using HttpResponseMessage r = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), HttpCompletionOption.ResponseHeadersRead, ct);
		info.Status = (int)r.StatusCode;
		info.Reason = r.ReasonPhrase ?? "";
		info.ContentLength = r.Content.Headers.ContentLength ?? -1L;
		info.AcceptRanges = r.Headers.AcceptRanges.Contains("bytes");
		info.LastModified = r.Content.Headers.LastModified ?? r.Headers.Date;
		L(log, $"HEAD {info.Status} {info.Reason}");
		if (r.StatusCode == HttpStatusCode.NotFound)
		{
			L(log, "404 —— 该 item 版本在 CDN 上已过期（hash 过期），需要当前 hash");
		}
		else if (r.IsSuccessStatusCode && info.ContentLength > 0)
		{
			L(log, $"可下载（{info.ContentLength:N0} 字节）");
		}
		// 更新时间：这是「网易什么时候发布的这个包」，区别于本地文件的下载时间
		L(log, "资源包更新时间：" + info.UpdateTimeText);
		return info;
	}

	// 检测一个 item 地址：返回状态码 + 更新时间 + 与本机已装版本的比对结论
	public static async Task<string> CheckAsync(string url, string installRoot, Action<string> log, CancellationToken ct)
	{
		ItemUrl itemUrl = ItemUrl.Parse(url);
		if (itemUrl == null)
		{
			throw new InvalidOperationException("URL 不符合 item_<gameid>_<m>_v<ver>_<hash>.7z 格式");
		}
		L(log, $"parsed: {itemUrl.PackageName} gameId={itemUrl.GameId} module={itemUrl.Module} ver={itemUrl.Ver} hash={itemUrl.Hash}");
		ServerEntry? installed = null;
		if (ServerRegistry.Load().Entries.TryGetValue(itemUrl.GameId, out var value))
		{
			installed = value;
			L(log, $"server: {value.Name} (module={value.Module}, frozen={value.Frozen})");
		}
		RemotePackageInfo info = await ProbeAsync(url, log, ct);
		StringBuilder sb = new StringBuilder();
		sb.Append($"HEAD {info.Status} {info.Reason}");
		if (info.Status == 404)
		{
			sb.Append("   ← hash 已过期，当前包在 CDN 上不存在");
		}
		else if (info.Status >= 200 && info.Status < 300)
		{
			sb.Append("   ← 可下载");
			if (info.ContentLength > 0)
			{
				sb.Append($" ({info.ContentLength:N0} 字节)");
			}
		}
		sb.AppendLine();
		sb.AppendLine("资源包更新时间：" + info.UpdateTimeText);
		if (installed != null)
		{
			string installedAt = installed.InstalledAt.HasValue ? installed.InstalledAt.Value.ToString("yyyy-MM-dd HH:mm") : "未知";
			if (string.IsNullOrWhiteSpace(installed.LastHash))
			{
				sb.AppendLine("本机尚未安装过该服的资源包");
			}
			else if (string.Equals(installed.LastHash, itemUrl.Hash, StringComparison.OrdinalIgnoreCase))
			{
				sb.AppendLine($"已是最新：本机已装的就是这个版本（hash {installed.LastHash}，安装于 {installedAt}）");
			}
			else
			{
				sb.AppendLine($"有新版本：本机已装 hash={installed.LastHash}（安装于 {installedAt}）→ 本次 hash={itemUrl.Hash}");
			}
		}
		return sb.ToString();
	}

	public static string Scan(string dir, Action<string> log)
	{
		L(log, "scanning " + dir + " ...");
		StringBuilder stringBuilder = new StringBuilder();
		Dictionary<long, HashSet<string>> dictionary = new Dictionary<long, HashSet<string>>();
		if (Directory.Exists(dir))
		{
			foreach (string item in Directory.EnumerateFiles(dir, "*.log", SearchOption.AllDirectories))
			{
				try
				{
					string[] array = File.ReadAllLines(item);
					for (int i = 0; i < array.Length; i++)
					{
						Match match = Regex.Match(array[i], "https?://[^\\s']*?item_(\\d+)_(\\d+)_(v\\d+)_([0-9A-Za-z]+)\\.7z", RegexOptions.IgnoreCase);
						if (match.Success)
						{
							long key = long.Parse(match.Groups[1].Value);
							if (!dictionary.TryGetValue(key, out var value))
							{
								HashSet<string> hashSet = (dictionary[key] = new HashSet<string>());
								HashSet<string> hashSet3 = hashSet;
								value = hashSet3;
							}
							value.Add(match.Value);
						}
					}
				}
				catch
				{
				}
			}
		}
		ServerRegistry serverRegistry = ServerRegistry.Load();
		bool flag = false;
		foreach (KeyValuePair<long, HashSet<string>> item2 in dictionary)
		{
			flag = true;
			string value2 = (serverRegistry.Entries.TryGetValue(item2.Key, out var value3) ? value3.Name : "unknown");
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(9, 2, stringBuilder2);
			handler.AppendLiteral("== ");
			handler.AppendFormatted(value2);
			handler.AppendLiteral(" (");
			handler.AppendFormatted(item2.Key);
			handler.AppendLiteral(") ==");
			stringBuilder3.AppendLine(ref handler);
			foreach (string item3 in item2.Value)
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder4 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(2, 1, stringBuilder2);
				handler.AppendLiteral("  ");
				handler.AppendFormatted(item3);
				stringBuilder4.AppendLine(ref handler);
			}
		}
		if (!flag)
		{
			stringBuilder.AppendLine("未找到 item_ URL。之前的会话未发生版本更新下发。");
		}
		string text = stringBuilder.ToString().TrimEnd();
		L(log, text);
		return text;
	}

	private static void L(Action<string> log, string msg)
	{
		string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
		try
		{
			log?.Invoke(text);
		}
		catch
		{
		}
		// 接入设置里的调试模式：与主日志同门控（关调试不产出，关调试时 ClearLogs 一并清空）
		if (!LogHelper.DebugEnabled)
		{
			return;
		}
		try
		{
			LogHelper.Debug("[资源包] " + msg);
		}
		catch
		{
		}
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
			File.AppendAllText(LogPath, text + Environment.NewLine, Encoding.UTF8);
		}
		catch
		{
		}
	}

	private static string Resolve7z()
	{
		// 内置 7-Zip 独立版 7zr.exe（官方、免安装、专解 .7z）优先，解压不再依赖外部安装
		string[] array = new string[6]
		{
			Path.Combine(AppContext.BaseDirectory, "7zr.exe"),
			Path.Combine(AppContext.BaseDirectory, "7za.exe"),
			Path.Combine(AppContext.BaseDirectory, "7z.exe"),
			"E:\\QQ_Information\\2998155760\\FileRecv\\EchooQt 1.1.2\\7z.exe",
			"C:\\Program Files\\7-Zip\\7z.exe",
			"C:\\Program Files (x86)\\7-Zip\\7z.exe"
		};
		string[] array2 = array;
		foreach (string text in array2)
		{
			if (File.Exists(text))
			{
				return text;
			}
		}
		throw new InvalidOperationException("找不到内置 7zr.exe（程序目录），且未安装 7-Zip，请重新获取完整程序");
	}

	// 运行 7z x 解压；includes 为 null 时全量解压，否则按通配只提取匹配条目
	private static async Task ExtractAsync(string sevenZip, string pkg, string outDir, string[]? includes, CancellationToken ct)
	{
		ProcessStartInfo processStartInfo = new ProcessStartInfo(sevenZip)
		{
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		processStartInfo.ArgumentList.Add("x");
		processStartInfo.ArgumentList.Add("-y");
		processStartInfo.ArgumentList.Add("-o" + outDir);
		// 7z 语法：x -y -o<dir> <压缩包> [文件过滤...] —— 过滤参数必须在压缩包之后，
		// 放在前面会被 7z 当作压缩包名解析（exit=7 "Cannot find archive"）
		processStartInfo.ArgumentList.Add(pkg);
		if (includes != null)
		{
			foreach (string inc in includes)
			{
				processStartInfo.ArgumentList.Add(inc);
			}
		}
		using Process proc = Process.Start(processStartInfo) ?? throw new InvalidOperationException("无法启动 7z 解压进程");
		Task<string> outTask = proc.StandardOutput.ReadToEndAsync(ct);
		Task<string> errTask = proc.StandardError.ReadToEndAsync(ct);
		string o = await outTask;
		string e = await errTask;
		await proc.WaitForExitAsync(ct);
		if (proc.ExitCode != 0)
		{
			throw new InvalidOperationException($"7z exit={proc.ExitCode} {o} {e}");
		}
	}

	/// <summary>解压前先列出 7z 条目并校验路径，防止绝对路径 / .. 条目写到目标目录之外。</summary>
	private static async Task EnsureArchiveEntriesSafeAsync(string sevenZip, string pkg, CancellationToken ct)
	{
		List<string> entries = await ListArchiveAsync(sevenZip, pkg, ct);
		foreach (string entry in entries)
		{
			if (entry.Length == 0)
			{
				continue;
			}
			if (!ArchiveSafety.IsSafeEntryPath(entry))
			{
				throw new InvalidDataException("资源包内出现非法路径，已拒绝解压：" + entry);
			}
		}
	}

	// 7z l -slt 列出包内全部条目路径（不解压，秒级完成）
	private static async Task<List<string>> ListArchiveAsync(string sevenZip, string pkg, CancellationToken ct)
	{
		ProcessStartInfo psi = new ProcessStartInfo(sevenZip)
		{
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		psi.ArgumentList.Add("l");
		psi.ArgumentList.Add("-slt");
		psi.ArgumentList.Add(pkg);
		using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z 列表进程");
		Task<string> outTask = proc.StandardOutput.ReadToEndAsync(ct);
		Task<string> errTask = proc.StandardError.ReadToEndAsync(ct);
		string o = await outTask;
		string e = await errTask;
		await proc.WaitForExitAsync(ct);
		if (proc.ExitCode != 0)
		{
			throw new InvalidOperationException($"7z l exit={proc.ExitCode} {e}");
		}
		List<string> list = new List<string>();
		// 分隔线之前是压缩包自身的元信息（其中也有一行 “Path = 压缩包路径”），只解析其后的条目。
		bool inEntries = false;
		bool sawSeparator = false;
		foreach (string raw in o.Split('\n'))
		{
			string text = raw.TrimEnd('\r');
			if (!inEntries)
			{
				if (text.StartsWith("----------", StringComparison.Ordinal))
				{
					inEntries = true;
					sawSeparator = true;
				}
				continue;
			}
			if (text.StartsWith("Path = ", StringComparison.Ordinal))
			{
				string path = text.Substring(7).Trim();
				if (path.Length > 0)
				{
					list.Add(path);
				}
			}
		}
		if (!sawSeparator)
		{
			throw new InvalidOperationException("无法解析 7z 条目列表（未找到条目分隔线），已停止解压");
		}
		return list;
	}

	// 元素之诗客户端 DragonCore 合并目标：程序记忆的路径 > 自动候选（E:/G:/D:\PCL\...）
	private static string? ResolveDragonCoreTarget()
	{
		try
		{
			string remembered = SettingsManager.Instance.DragonCoreTargetDir;
			if (!string.IsNullOrWhiteSpace(remembered) && Directory.Exists(remembered))
			{
				return remembered;
			}
			// 记忆路径失效时，尝试更新页第二合并目标里记的 PCL DragonCore（历史选过的真实客户端目录）
			string? rememberedPcl = SettingsManager.Instance.PclClientRootDir;
			if (!string.IsNullOrWhiteSpace(rememberedPcl)
				&& string.Equals(Path.GetFileName(rememberedPcl.TrimEnd(Path.DirectorySeparatorChar)), "DragonCore", StringComparison.OrdinalIgnoreCase)
				&& Directory.Exists(rememberedPcl))
			{
				return rememberedPcl;
			}
			return DragonCoreTrimService.DefaultCandidateDirs().FirstOrDefault();
		}
		catch
		{
			return null;
		}
	}

	// 供更新页自动填充“第二合并路径”展示用
	public static string? GetDragonCoreTargetForUi() => ResolveDragonCoreTarget();

	// 浏览选择的规范化：选 DragonCore → 记为元素之诗裁剪目标 + 反推同 versions 下失落实例；
	// 选 .minecraft/PCL 目录（含 versions）→ 枚举实例；选的本身就是版本实例目录 → 原样返回。
	// 返回 null 表示没识别出实例目录。
	public static string? NormalizePclPick(string picked, Action<string>? log = null)
	{
		try
		{
			string full = Path.GetFullPath(picked);
			string name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
			if (string.Equals(name, "DragonCore", StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
			{
				try
				{
					SettingsManager.Instance.DragonCoreTargetDir = full;
					SettingsManager.Save(SettingsManager.Instance);
					log?.Invoke("[浏览] 所选 DragonCore 已记为元素之诗裁剪目标：" + full);
				}
				catch
				{
				}
				string? text = Path.GetDirectoryName(full);
				while (text != null && !string.Equals(Path.GetFileName(text), "versions", StringComparison.OrdinalIgnoreCase))
				{
					text = Path.GetDirectoryName(text);
				}
				if (text != null)
				{
					return PickInstanceFromVersions(text, log);
				}
				log?.Invoke("[浏览] DragonCore 上层没有 versions 目录，未推导出失落实例");
				return null;
			}
			if (Directory.Exists(Path.Combine(full, "mods")) || Directory.Exists(Path.Combine(full, "config")) || Directory.Exists(Path.Combine(full, "resourcepacks")))
			{
				return full;
			}
			string? text2 = null;
			if (string.Equals(name, "versions", StringComparison.OrdinalIgnoreCase))
			{
				text2 = full;
			}
			else if (Directory.Exists(Path.Combine(full, "versions")))
			{
				text2 = Path.Combine(full, "versions");
			}
			else if (Directory.Exists(Path.Combine(full, ".minecraft", "versions")))
			{
				text2 = Path.Combine(full, ".minecraft", "versions");
			}
			if (text2 != null)
			{
				return PickInstanceFromVersions(text2, log);
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	// 在 versions 目录里挑实例：失落优先 → 带 mods/config/resourcepacks 的第一个 → 任意第一个
	private static string? PickInstanceFromVersions(string versionsDir, Action<string>? log = null)
	{
		string? text = null;
		string? text2 = null;
		foreach (string item in Directory.EnumerateDirectories(versionsDir))
		{
			string fileName = Path.GetFileName(item);
			if (fileName.IndexOf("元素芝士", StringComparison.Ordinal) >= 0)
			{
				continue;
			}
			bool flag = Directory.Exists(Path.Combine(item, "mods")) || Directory.Exists(Path.Combine(item, "config")) || Directory.Exists(Path.Combine(item, "resourcepacks"));
			if (!flag)
			{
				text = text ?? item;
				continue;
			}
			if (fileName.IndexOf("失落", StringComparison.Ordinal) >= 0)
			{
				log?.Invoke("[浏览] versions 下识别出失落实例：" + item);
				return item;
			}
			text2 = text2 ?? item;
		}
		return text2 ?? text;
	}

	// 失落世界的 PCL 客户端路径：复用代理启动页的 PCL 定位（Settings.PclPath / 注册表 / 全盘浅扫），
	// 从 PCL 启动器 exe 同目录推导 .minecraft\versions\* 找实例目录（目录名含“失落”优先，
	// 其次带 mods/config 的版本目录，再次任意版本目录），要求路径真实有效才返回。
	public static string? ResolvePclClientRoot()
	{
		try
		{
			string remembered = SettingsManager.Instance.PclClientRootDir;
			if (!string.IsNullOrWhiteSpace(remembered) && Directory.Exists(remembered))
			{
				return remembered;
			}
			string? fallback = null;
			foreach (string mcRoot in PclLocator.GetMinecraftRoots())
			{
				string versionsDir = Path.Combine(mcRoot, "versions");
				if (!Directory.Exists(versionsDir))
				{
					continue;
				}
				string? firstVersion = null;
				foreach (string item in Directory.EnumerateDirectories(versionsDir))
				{
					string fileName = Path.GetFileName(item);
					if (fileName.IndexOf("元素芝士", StringComparison.Ordinal) >= 0)
					{
						continue;
					}
					bool flag = Directory.Exists(Path.Combine(item, "mods"));
					bool flag2 = Directory.Exists(Path.Combine(item, "config"));
					if (!flag && !flag2)
					{
						// 无隔离的版本目录也记下来做兜底
						firstVersion = firstVersion ?? item;
						continue;
					}
					if (fileName.IndexOf("失落", StringComparison.Ordinal) >= 0)
					{
						return item;
					}
					fallback = fallback ?? item;
				}
				// versions 下没有带 mods/config 的目录：可能没开版本隔离，兜底用 .minecraft 根
				if (fallback == null && (Directory.Exists(Path.Combine(mcRoot, "mods")) || Directory.Exists(Path.Combine(mcRoot, "config"))))
				{
					fallback = mcRoot;
				}
				if (fallback == null)
				{
					fallback = firstVersion;
				}
			}
			return fallback;
		}
		catch
		{
			return null;
		}
	}

	// 递归找出 stage 下名为 DragonCore 的目录
	private static List<string> FindDragonCoreDirs(string root)
	{
		List<string> list = new List<string>();
		try
		{
			foreach (string item in Directory.EnumerateDirectories(root))
			{
				if (item.EndsWith("DragonCore", StringComparison.OrdinalIgnoreCase))
				{
					list.Add(item);
				}
				else
				{
					list.AddRange(FindDragonCoreDirs(item));
				}
			}
		}
		catch
		{
		}
		return list;
	}

	// 增量复制：目标缺失或体积不同的文件才复制（贴图更新场景够用，避免整目录 800MB 复制）
	// 对比 + 去重：目标已有同名同大小同 MD5 的文件跳过，只复制新增/变更文件。
	// 返回本次复制的 (源文件, 相对路径) 列表（供增量包导出与冗余比对复用）。
	private static List<(string SrcFile, string RelPath)> MergeChangedTree(string src, string dst)
	{
		List<(string, string)> list = new List<(string, string)>();
		Directory.CreateDirectory(dst);
		foreach (string item in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
		{
			string relPath = Path.GetRelativePath(src, item);
			string text2 = Path.Combine(dst, relPath);
			Directory.CreateDirectory(Path.GetDirectoryName(text2));
			FileInfo fileInfo = new FileInfo(item);
			FileInfo fileInfo2 = new FileInfo(text2);
			if (!fileInfo2.Exists)
			{
				File.Copy(item, text2, overwrite: true);
				list.Add((item, relPath));
			}
			else if (fileInfo2.Length != fileInfo.Length || !string.Equals(Md5File(text2), Md5File(item), StringComparison.OrdinalIgnoreCase))
			{
				File.Copy(item, text2, overwrite: true);
				list.Add((item, relPath));
			}
		}
		return list;
	}

	// 清冗余：客户端 DragonCore 里有、但最新包里已不存在的文件 → 移入旁边的备份目录
	//（不直接删，可随时回滚），让客户端贴图与服务端包严格一致。
	private static int MoveRedundantToBackup(string dragonTarget, HashSet<string> pkgRel, Action<string> log)
	{
		List<string> list = (from f in Directory.EnumerateFiles(dragonTarget, "*", SearchOption.AllDirectories)
				select Path.GetRelativePath(dragonTarget, f)).Where((string rel) => !pkgRel.Contains(rel)).ToList();
		if (list.Count == 0)
		{
			return 0;
		}
		string text = Path.Combine(Directory.GetParent(dragonTarget)!.FullName, "DragonCore_冗余备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
		foreach (string relPath in list)
		{
			string text2 = Path.Combine(text, relPath);
			Directory.CreateDirectory(Path.GetDirectoryName(text2));
			File.Move(Path.Combine(dragonTarget, relPath), text2);
		}
		L(log, $"冗余清理：{list.Count} 个最新包里已不存在的旧文件移入备份目录（可回滚）：{text}");
		// 超期的历史冗余备份按保留天数清理（需开启自动清理，且先进回收站）
		CleanupHelper.PurgeRedundantBackups(Directory.GetParent(dragonTarget)!.FullName, SettingsManager.Instance.RedundantBackupKeepDays, log);
		return list.Count;
	}

	// 导出增量资源包到桌面：zip 内含 DragonCore/<相对路径> 的新增/变更文件 + manifest，
	// 其他用户可直接解压拖进 resourcepacks，或用「合并增量包」合并进自己的 DragonCore。
	private static void ExportIncrementalZipToDesktop(List<(string SrcFile, string RelPath)> changed, string pkg, Action<string> log)
	{
		try
		{
			if (changed.Count == 0)
			{
				L(log, "本次无新增/变更文件，跳过增量包导出");
				return;
			}
			Match match = Regex.Match(Path.GetFileName(pkg), "_v(\\d+)_");
			string text = ((match.Success && match.Groups[1].Success) ? ("_v" + match.Groups[1].Value) : "");
			string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			string path = Path.Combine(desktopPath, "元素之诗_DragonCore增量" + text + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip");
			string text2 = Path.Combine(Path.GetTempPath(), "mhyunbox_dragoncore_export_" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(text2);
			try
			{
				foreach ((string, string) item in changed)
				{
					string text3 = Path.Combine(text2, "DragonCore", item.Item2);
					Directory.CreateDirectory(Path.GetDirectoryName(text3));
					File.Copy(item.Item1, text3, overwrite: true);
				}
				File.WriteAllText(Path.Combine(text2, "_mhy_incremental_manifest.json"), JsonSerializer.Serialize(new
				{
					generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
					files = changed.Count,
					bytes = changed.Sum(((string SrcFile, string RelPath) c) => new FileInfo(c.SrcFile).Length),
					usage = "用法一：解压本 zip 得到 DragonCore 文件夹，直接拖进资源包文件夹（resourcepacks）里；用法二：用 MhyunBox 的「合并增量包」功能解压覆盖进已有 DragonCore 目录"
				}, new JsonSerializerOptions
				{
					WriteIndented = true
				}));
				string text4 = path + ".tmp";
				if (File.Exists(text4))
				{
					File.Delete(text4);
				}
				ZipFile.CreateFromDirectory(text2, text4, CompressionLevel.Optimal, includeBaseDirectory: false);
				if (File.Exists(path))
				{
					File.Delete(path);
				}
				File.Move(text4, path);
				L(log, $"增量资源包已导出到桌面：{path}（{changed.Count} 个文件，可发给其他用户合并）");
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
		catch (Exception ex)
		{
			L(log, "增量包导出失败（不影响本次更新）: " + ex.Message);
		}
	}

	// 元素之诗裁剪更新：只提取包内 DragonCore → 只复制新增/变更文件进 PCL 客户端；
	// 包内若有 mods/config 等小目录仍合并进网易实例；resourcepacks 大目录不再全量合并。
	// 返回 false 表示裁剪未命中（调用方回退全量合并）。
	private static async Task<bool> TryElementPoetryTrimAsync(string sevenZip, string pkg, string work, string dragonTarget, string neteaseMc, Action<string> log, CancellationToken ct)
	{
		try
		{
			L(log, "元素之诗：进入 DragonCore 裁剪更新路径（只合并模型贴图，跳过 800MB 全量合并）");
			List<string> entries = await ListArchiveAsync(sevenZip, pkg, ct);
			if (!entries.Any((string e) => e.IndexOf("DragonCore", StringComparison.OrdinalIgnoreCase) >= 0))
			{
				L(log, "包内未发现 DragonCore 条目");
				return false;
			}
			string text = Path.Combine(work, "stage_dragon");
			if (Directory.Exists(text))
			{
				Directory.Delete(text, recursive: true);
			}
			Directory.CreateDirectory(text);
			// 7z 的 * 通配不跨路径分隔符（"*DragonCore*" 匹配不到 .minecraft\resourcepacks\DragonCore\…），
			// 因此从条目列表推导 DragonCore 目录的精确前缀掩码（<前缀>\DragonCore\*）
			List<string> dragonMasks = new List<string>();
			foreach (string entry in entries)
			{
				string entryPath = entry.Replace('/', '\\');
				if (entryPath.StartsWith("DragonCore", StringComparison.OrdinalIgnoreCase) && (entryPath.Length == 10 || entryPath[10] == '\\'))
				{
					dragonMasks.Add("DragonCore\\*");
					continue;
				}
				int idx = entryPath.IndexOf("\\DragonCore", StringComparison.OrdinalIgnoreCase);
				while (idx >= 0)
				{
					int end = idx + "\\DragonCore".Length;
					if (end == entryPath.Length || entryPath[end] == '\\')
					{
						dragonMasks.Add(entryPath.Substring(0, end) + "\\*");
						break;
					}
					idx = entryPath.IndexOf("\\DragonCore", end, StringComparison.OrdinalIgnoreCase);
				}
			}
			string[] includes = dragonMasks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			if (includes.Length == 0)
			{
				L(log, "包内未发现 DragonCore 目录前缀，回退全量合并");
				return false;
			}
			L(log, $"只提取包内 DragonCore 部分（{includes.Length} 个前缀掩码）…");
			await ExtractAsync(sevenZip, pkg, text, includes, ct);
			List<(string SrcFile, string RelPath)> changed = new List<(string, string)>();
			List<string> list2 = FindDragonCoreDirs(text).ToList();
			if (list2.Count == 0)
			{
				L(log, "DragonCore 通配提取未产出目录，回退全量合并");
				return false;
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string[] array = list2.ToArray();
			foreach (string text2 in array)
			{
				changed.AddRange(MergeChangedTree(text2, dragonTarget));
				foreach (string item in Directory.EnumerateFiles(text2, "*", SearchOption.AllDirectories))
				{
					hashSet.Add(Path.GetRelativePath(text2, item));
				}
			}
			L(log, $"DragonCore 裁剪合并完成：{changed.Count} 个新增/变更文件 → {dragonTarget}（进服后 O+P 重新加载模型生效）");
			// 清冗余：客户端里有、最新包里已不存在的文件 → 移入备份目录（不直接删）
			try
			{
				MoveRedundantToBackup(dragonTarget, hashSet, log);
			}
			catch (Exception ex3)
			{
				L(log, "冗余清理失败（不影响更新）: " + ex3.Message);
			}
			// 导出增量资源包到桌面（zip 内 DragonCore/<相对路径> + manifest），可发给其他用户合并
			ExportIncrementalZipToDesktop(changed, pkg, log);
			// 包内若有 mods/config 等小目录，仍按原路径合并进网易实例
			string text3 = Path.Combine(work, "stage_rest");
			string[] array2 = new string[7] { "mods", "config", "scripts", "server-resource-packs", "shaderpacks", "particles", "structures" };
			string[] array3 = array2;
			foreach (string text4 in array3)
			{
				if (entries.Any((string e) => e.Equals(text4, StringComparison.OrdinalIgnoreCase) || e.StartsWith(text4 + "\\", StringComparison.OrdinalIgnoreCase)))
				{
					if (Directory.Exists(text3))
					{
						Directory.Delete(text3, recursive: true);
					}
					Directory.CreateDirectory(text3);
					try
					{
						await ExtractAsync(sevenZip, pkg, text3, new string[2] { text4, text4 + "\\*" }, ct);
						string text5 = FindDir(text3, text4, 0);
						if (text5 != null)
						{
							int num2 = MergeCopy(text5, Path.Combine(neteaseMc, text4), neteaseMc, work, "trim");
							L(log, $"  {text4}: {num2} files → 网易实例");
						}
					}
					catch (Exception ex)
					{
						L(log, $"  {text4} 提取失败（不影响贴图更新）: {ex.Message}");
					}
				}
			}
			return true;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			L(log, "裁剪路径异常: " + ex2.GetType().Name + ": " + ex2.Message);
			return false;
		}
	}

	private static List<(string, string)> MergeDirs(string stage, string destMc, string hash)
	{
		string[] array = new string[8] { "mods", "resourcepacks", "config", "scripts", "server-resource-packs", "shaderpacks", "particles", "structures" };
		List<(string, string)> list = new List<(string, string)>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string[] array2 = array;
		string[] array3 = array2;
		foreach (string text in array3)
		{
			string text2 = FindDir(stage, text, 0);
			if (text2 != null && hashSet.Add(Path.GetFileName(text2)))
			{
				list.Add((text2, Path.Combine(destMc, text)));
			}
		}
		return list;
	}

	private static string? FindDir(string root, string name, int depth)
	{
		if (depth > 4 || !Directory.Exists(root))
		{
			return null;
		}
		string text = Path.Combine(root, name);
		if (Directory.Exists(text) && Path.GetFileName(text).Equals(name, StringComparison.OrdinalIgnoreCase))
		{
			return text;
		}
		foreach (string item in Directory.EnumerateDirectories(root))
		{
			string text2 = FindDir(item, name, depth + 1);
			if (text2 != null)
			{
				return text2;
			}
		}
		return null;
	}

	private static int MergeCopy(string src, string dst, string target, string work, string hash)
	{
		int num = 0;
		if (!Directory.Exists(src))
		{
			return 0;
		}
		Directory.CreateDirectory(dst);
		string text = Path.Combine(target, ".mhyunbox_respkg_bak_" + hash);
		Directory.CreateDirectory(text);
		foreach (string item in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
		{
			string relativePath = Path.GetRelativePath(src, item);
			string text2 = Path.Combine(dst, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(text2));
			if (File.Exists(text2))
			{
				string text3 = Path.Combine(text, Path.GetFileName(relativePath));
				Directory.CreateDirectory(Path.GetDirectoryName(text3));
				File.Copy(text2, text3, overwrite: true);
			}
			File.Copy(item, text2, overwrite: true);
			num++;
		}
		return num;
	}

	private static int MergeLoose(string stage, string destMc, string hash)
	{
		int num = 0;
		string[] array = new string[2]
		{
			stage,
			Path.Combine(stage, ".minecraft")
		};
		string[] array2 = array;
		foreach (string path in array2)
		{
			if (!Directory.Exists(path))
			{
				continue;
			}
			foreach (string item in Directory.EnumerateFiles(path))
			{
				string fileName = Path.GetFileName(item);
				string text = Path.Combine(destMc, fileName);
				if (fileName.Equals("options.txt", StringComparison.OrdinalIgnoreCase) || fileName.Equals("optionsof.txt", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
				{
					if (File.Exists(text))
					{
						string text2 = Path.Combine(destMc, ".mhyunbox_respkg_bak_" + hash);
						Directory.CreateDirectory(text2);
						File.Copy(text, Path.Combine(text2, fileName), overwrite: true);
					}
					File.Copy(item, text, overwrite: true);
					num++;
				}
			}
		}
		return num;
	}

	private static void WriteSnapshot(string instanceMc, string packageName)
	{
		string directoryName = Path.GetDirectoryName(instanceMc);
		if (directoryName != null)
		{
			string text = Path.Combine(Path.GetTempPath(), "mhyunbox_respkg", Path.GetFileName(directoryName), packageName);
			if (File.Exists(text))
			{
				FileInfo fileInfo = new FileInfo(text);
				var value = new
				{
					md5 = Md5File(text),
					size = fileInfo.Length
				};
				File.WriteAllText(Path.Combine(directoryName, Path.GetFileName(directoryName) + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions
				{
					WriteIndented = true
				}));
			}
		}
	}

	private static string Md5File(string path)
	{
		using MD5 mD = MD5.Create();
		using FileStream inputStream = File.OpenRead(path);
		return Convert.ToHexString(mD.ComputeHash(inputStream)).ToLowerInvariant();
	}
}

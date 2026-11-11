using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MhyUnBoxSetup;

internal sealed class InstallOptions
{
	public string InstallDir = SetupInfo.GetRememberedInstallDir();

	public bool DesktopShortcut = true;

	public bool LaunchAfterInstall = true;
}

internal sealed class InstallResult
{
	public bool Success;

	public string Message = "";
}

internal static class SetupInfo
{
	public const string AppName = "MhyUnBox";

	public const string AppExe = "MhyunBox.exe";

	public const string Version = "8.3.0";

	public const string Publisher = "Notel";

	public const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MhyUnBox";

	/// <summary>.NET 8 桌面运行时（WPF/WinForms 依赖它，本程序本体是框架依赖的）。</summary>
	public const string RuntimeMetaUrl = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/8.0/releases.json";

	/// <summary>元数据拉不到时的兜底地址（8.0.34 为撰写时最新 8.0 LTS 补丁版）。</summary>
	public const string RuntimeFallbackUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.34/windowsdesktop-runtime-8.0.34-win-x64.exe";

	/// <summary>默认安装位置：用户目录，免 UAC；程序自身要把日志写在安装目录，放 Program Files 会没权限。</summary>
	public static string DefaultInstallDir => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

	/// <summary>记录上次所选安装位置的小文件（卸载重装也能记住）。</summary>
	public static string SetupStateFile => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MhyUnBoxSetup", "install.json");

	/// <summary>
	/// 取上次使用的安装位置：优先已安装的卸载项 InstallLocation，
	/// 其次安装器自己记下的 install.json，最后才落到默认位置。
	/// </summary>
	public static string GetRememberedInstallDir()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallSubKey);
			if (key?.GetValue("InstallLocation") is string fromReg && !string.IsNullOrWhiteSpace(fromReg))
			{
				return fromReg;
			}
		}
		catch
		{
		}
		try
		{
			string file = SetupStateFile;
			if (File.Exists(file))
			{
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
				if (doc.RootElement.TryGetProperty("InstallDir", out JsonElement el)
					&& el.GetString() is string fromFile && !string.IsNullOrWhiteSpace(fromFile))
				{
					return fromFile;
				}
			}
		}
		catch
		{
		}
		return DefaultInstallDir;
	}

	/// <summary>记住用户选择的安装位置，下次运行（升级）时作为默认值。</summary>
	public static void RememberInstallDir(string dir)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(dir))
			{
				return;
			}
			string file = SetupStateFile;
			string parent = Path.GetDirectoryName(file);
			if (!string.IsNullOrEmpty(parent))
			{
				Directory.CreateDirectory(parent);
			}
			string json = "{\n  \"InstallDir\": " + JsonSerializer.Serialize(dir) + ",\n  \"SavedAt\": " +
				JsonSerializer.Serialize(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) + "\n}\n";
			File.WriteAllText(file, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		catch
		{
		}
	}

	public static string UserDataDir => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MhyunBox");

	public static string StartMenuLnk => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppName + ".lnk");

	public static string DesktopLnk => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

	/// <summary>读取已安装版本（优先注册表卸载项，其次 exe 文件版本）。返回 null 表示未安装。</summary>
	public static System.Version GetInstalledVersion(string installDir)
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallSubKey);
			if (key?.GetValue("DisplayVersion") is string text && System.Version.TryParse(text, out System.Version parsed))
			{
				return parsed;
			}
		}
		catch
		{
		}
		try
		{
			string exe = Path.Combine(installDir, AppExe);
			if (File.Exists(exe))
			{
				string ver = FileVersionInfo.GetVersionInfo(exe).FileVersion;
				if (System.Version.TryParse(ver, out System.Version parsed2))
				{
					return parsed2;
				}
			}
		}
		catch
		{
		}
		return null;
	}
}

internal static class InstallerEngine
{
	/// <summary>查找已安装的 .NET 8 桌面运行时（Microsoft.WindowsDesktop.App 8.x），返回版本字符串。</summary>
	public static string FindDotNet8DesktopRuntime()
	{
		string[] roots =
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "shared", "Microsoft.WindowsDesktop.App")
		};
		foreach (string root in roots)
		{
			try
			{
				if (!Directory.Exists(root))
				{
					continue;
				}
				string best = null;
				foreach (string dir in Directory.GetDirectories(root))
				{
					string name = Path.GetFileName(dir);
					if (name.StartsWith("8.", StringComparison.Ordinal) && (best == null || string.CompareOrdinal(name, best) > 0))
					{
						best = name;
					}
				}
				if (best != null)
				{
					return best;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	/// <summary>向官方 release-metadata 询问当前 8.0 最新桌面运行时下载地址；失败则用兜底地址。</summary>
	public static async Task<string> ResolveRuntimeUrlAsync(HttpClient http, IProgress<string> log, CancellationToken ct)
	{
		try
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(TimeSpan.FromSeconds(20));
			string json = await http.GetStringAsync(SetupInfo.RuntimeMetaUrl, cts.Token);
			using JsonDocument doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("releases", out JsonElement releases))
			{
				foreach (JsonElement release in releases.EnumerateArray())
				{
					if (!release.TryGetProperty("windowsdesktop", out JsonElement wd)
						|| !wd.TryGetProperty("files", out JsonElement files))
					{
						continue;
					}
					foreach (JsonElement file in files.EnumerateArray())
					{
						if (file.TryGetProperty("rid", out JsonElement rid)
							&& string.Equals(rid.GetString(), "win-x64", StringComparison.OrdinalIgnoreCase)
							&& file.TryGetProperty("url", out JsonElement url))
						{
							string value = url.GetString();
							if (!string.IsNullOrWhiteSpace(value))
							{
								log?.Report("已从官方元数据解析到运行时地址：" + Path.GetFileName(new Uri(value).AbsolutePath));
								return value;
							}
						}
					}
					break;
				}
			}
			log?.Report("官方元数据格式不匹配，改用兜底下载地址。");
		}
		catch (Exception ex)
		{
			log?.Report("解析官方元数据失败（" + ex.Message + "），改用兜底下载地址。");
		}
		return SetupInfo.RuntimeFallbackUrl;
	}

	public static async Task DownloadAsync(HttpClient http, string url, string destPath, IProgress<string> log, IProgress<int> percent, CancellationToken ct)
	{
		using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
		resp.EnsureSuccessStatusCode();
		long total = resp.Content.Headers.ContentLength ?? -1L;
		log?.Report("开始下载：" + (total > 0 ? $"{total / 1024.0 / 1024.0:F1} MB" : "大小未知") + " → " + destPath);
		string dir = Path.GetDirectoryName(destPath);
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		using Stream source = await resp.Content.ReadAsStreamAsync(ct);
		using FileStream target = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
		byte[] buffer = new byte[81920];
		long read = 0L;
		int lastReported = -1;
		int n;
		while ((n = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
		{
			await target.WriteAsync(buffer, 0, n, ct);
			read += n;
			if (total > 0)
			{
				int pct = (int)(read * 100L / total);
				if (pct != lastReported)
				{
					lastReported = pct;
					percent?.Report(pct);
				}
			}
		}
		await target.FlushAsync(ct);
	}

	/// <summary>静默安装桌面运行时（需要管理员：走 runas，会弹一次 UAC）。</summary>
	public static (bool ok, int exitCode) InstallRuntimeSilently(string exePath, IProgress<string> log)
	{
		var psi = new ProcessStartInfo
		{
			FileName = exePath,
			Arguments = "/install /quiet /norestart",
			UseShellExecute = true,
			Verb = "runas"
		};
		try
		{
			using Process p = Process.Start(psi);
			if (p == null)
			{
				return (false, -1);
			}
			p.WaitForExit();
			return (p.ExitCode == 0 || p.ExitCode == 3010 || p.ExitCode == 1638, p.ExitCode);
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			log?.Report("用户取消了 UAC 授权，无法安装 .NET 8 运行时。");
			return (false, 1223);
		}
	}

	public static async Task<InstallResult> InstallAsync(InstallOptions opt, IProgress<string> log, IProgress<int> progress, CancellationToken ct)
	{
		var result = new InstallResult();
		HttpClient http = null;
		string tempRoot = Path.Combine(Path.GetTempPath(), "MhyUnBoxSetup");
		try
		{
			string dir = Path.GetFullPath(opt.InstallDir.Trim());
			progress?.Report(2);
			log?.Report("安装位置：" + dir);

			// 1. 关闭正在运行的程序（升级时文件会被占用）
			StopRunningApp(log);
			Directory.CreateDirectory(dir);

			// 2. .NET 8 桌面运行时
			string runtime = FindDotNet8DesktopRuntime();
			if (runtime != null)
			{
				log?.Report("已检测到 .NET 8 桌面运行时 " + runtime + "，跳过下载。");
				progress?.Report(55);
			}
			else
			{
				log?.Report("未检测到 .NET 8 桌面运行时，准备自动下载安装（约占 55 MB，需要一次管理员授权）。");
				http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
				http.DefaultRequestHeaders.UserAgent.ParseAdd("MhyUnBoxSetup/" + SetupInfo.Version);
				string url = await ResolveRuntimeUrlAsync(http, log, ct);
				Directory.CreateDirectory(tempRoot);
				string file = Path.Combine(tempRoot, Path.GetFileName(new Uri(url).AbsolutePath));
				var dlPercent = new Progress<int>(p => progress?.Report(5 + p * 45 / 100));
				await DownloadAsync(http, url, file, log, dlPercent, ct);
				log?.Report("下载完成，开始静默安装运行时…");
				var (ok, code) = InstallRuntimeSilently(file, log);
				log?.Report("运行时安装退出码：" + code);
				// 「清除」：安装包用完即删，不留 55MB 垃圾
				TryDeleteFile(file, log);
				if (!ok)
				{
					throw new InvalidOperationException("运行时安装未成功（退出码 " + code + "）。可手动安装后重试：" + url);
				}
				runtime = FindDotNet8DesktopRuntime();
				if (runtime == null)
				{
					throw new InvalidOperationException("运行时安装完成但未检测到 Microsoft.WindowsDesktop.App 8.x，请重启后再试。");
				}
				log?.Report("已安装 .NET 8 桌面运行时 " + runtime);
				progress?.Report(55);
			}

			// 3. 解压程序本体
			Assembly asm = Assembly.GetExecutingAssembly();
			using Stream payload = asm.GetManifestResourceStream("payload.zip")
				?? throw new InvalidOperationException("安装包内缺少程序数据（payload.zip），请重新获取完整安装包。");
			long written = ExtractPayload(payload, dir, log, p =>
			{
				progress?.Report(55 + p * 38 / 100);
			}, ct);
			progress?.Report(94);

			// 4. 释放卸载器
			string uninstaller = Path.Combine(dir, "uninstall.exe");
			using (Stream bin = asm.GetManifestResourceStream("uninstaller.bin"))
			{
				if (bin != null)
				{
					using FileStream fs = new FileStream(uninstaller, FileMode.Create, FileAccess.Write, FileShare.None);
					await bin.CopyToAsync(fs, 81920, ct);
					log?.Report("卸载器已释放：uninstall.exe");
				}
				else
				{
					log?.Report("警告：安装包内缺少卸载器资源，卸载项将指向安装包本身。");
				}
			}

			// 5. 快捷方式
			string exePath = Path.Combine(dir, SetupInfo.AppExe);
			CreateShortcut(SetupInfo.StartMenuLnk, exePath, dir, exePath, SetupInfo.AppName, log);
			if (opt.DesktopShortcut)
			{
				CreateShortcut(SetupInfo.DesktopLnk, exePath, dir, exePath, SetupInfo.AppName, log);
			}
			else
			{
				TryDeleteFile(SetupInfo.DesktopLnk, null);
			}

			// 6. 卸载注册项（HKCU，卸载器在「设置 → 应用」里可见）
			string uninstallCmd = File.Exists(uninstaller)
				? "\"" + uninstaller + "\" --uninstall"
				: "\"" + Application.ExecutablePath + "\" --uninstall";
			WriteUninstallEntry(dir, exePath, uninstallCmd, written, log);
			progress?.Report(98);

			// 7. 收尾清理临时目录
			TryDeleteDir(tempRoot, log);
			progress?.Report(100);
			result.Success = true;
			result.Message = "安装完成。";
			// 记住本次安装位置，下次升级默认就用它
			SetupInfo.RememberInstallDir(dir);
			log?.Report("安装完成：" + exePath);

			if (opt.LaunchAfterInstall)
			{
				try
				{
					Process.Start(new ProcessStartInfo { FileName = exePath, WorkingDirectory = dir, UseShellExecute = true });
					log?.Report("已启动 MhyUnBox。");
				}
				catch (Exception ex2)
				{
					log?.Report("启动程序失败（不影响安装）：" + ex2.Message);
				}
			}
		}
		catch (OperationCanceledException)
		{
			result.Message = "已取消。";
			log?.Report("操作已取消。");
		}
		catch (Exception ex)
		{
			result.Message = ex.Message;
			log?.Report("安装失败：" + ex.Message);
		}
		finally
		{
			http?.Dispose();
			try
			{
				if (Directory.Exists(tempRoot))
				{
					Directory.Delete(tempRoot, recursive: true);
				}
			}
			catch
			{
			}
		}
		return result;
	}

	public static void StopRunningApp(IProgress<string> log)
	{
		foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(SetupInfo.AppExe)))
		{
			try
			{
				log?.Report("检测到 MhyUnBox 正在运行（PID " + p.Id + "），已结束进程以便替换文件。");
				p.Kill();
				p.WaitForExit(5000);
			}
			catch
			{
			}
		}
		Thread.Sleep(500);
	}

	/// <summary>把内嵌 zip 解到目标目录，返回解压字节数。带路径穿越防护。</summary>
	private static long ExtractPayload(Stream zipStream, string targetDir, IProgress<string> log, Action<int> reportPct, CancellationToken ct)
	{
		string root = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
		int total = zip.Entries.Count;
		int done = 0;
		long bytes = 0L;
		log?.Report("开始释放程序文件（共 " + total + " 项）…");
		foreach (ZipArchiveEntry entry in zip.Entries)
		{
			ct.ThrowIfCancellationRequested();
			string rel = entry.FullName.Replace('\\', '/');
			if (string.IsNullOrEmpty(rel) || rel.EndsWith("/", StringComparison.Ordinal))
			{
				done++;
				continue;
			}
			string dest = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
			if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
			{
				log?.Report("跳过可疑条目：" + rel);
				done++;
				continue;
			}
			string parent = Path.GetDirectoryName(dest);
			if (!string.IsNullOrEmpty(parent))
			{
				Directory.CreateDirectory(parent);
			}
			if (File.Exists(dest))
			{
				try
				{
					File.SetAttributes(dest, FileAttributes.Normal);
				}
				catch
				{
				}
			}
			entry.ExtractToFile(dest, overwrite: true);
			bytes += entry.Length;
			done++;
			if (done % 10 == 0 || done == total)
			{
				reportPct?.Invoke(done * 100 / Math.Max(total, 1));
			}
		}
		log?.Report("程序文件释放完成：" + (bytes / 1024.0 / 1024.0).ToString("F1") + " MB");
		return bytes;
	}

	private static void CreateShortcut(string lnkPath, string target, string workDir, string iconPath, string description, IProgress<string> log)
	{
		try
		{
			string parent = Path.GetDirectoryName(lnkPath);
			if (!string.IsNullOrEmpty(parent))
			{
				Directory.CreateDirectory(parent);
			}
			Type shellType = Type.GetTypeFromProgID("WScript.Shell");
			if (shellType == null)
			{
				log?.Report("创建快捷方式失败：WScript.Shell 不可用。");
				return;
			}
			object shell = Activator.CreateInstance(shellType);
			object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
			Type t = shortcut.GetType();
			t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
			t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workDir });
			t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { iconPath + ",0" });
			t.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
			// 刻意不写 System.AppUserModel.ID：实测写了之后开始菜单 AppsFolder 会把条目剔除，
			// 程序自己启动时也会建/维护同名 .lnk，两者保持一致即可。
			t.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
			Marshal.FinalReleaseComObject(shortcut);
			Marshal.FinalReleaseComObject(shell);
			log?.Report("快捷方式已创建：" + lnkPath);
		}
		catch (Exception ex)
		{
			log?.Report("创建快捷方式失败（不影响安装）：" + ex.Message);
		}
	}

	private static void WriteUninstallEntry(string dir, string exePath, string uninstallCmd, long installedBytes, IProgress<string> log)
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(SetupInfo.UninstallSubKey, writable: true);
			key.SetValue("DisplayName", SetupInfo.AppName, RegistryValueKind.String);
			key.SetValue("DisplayVersion", SetupInfo.Version, RegistryValueKind.String);
			key.SetValue("Publisher", SetupInfo.Publisher, RegistryValueKind.String);
			key.SetValue("DisplayIcon", exePath + ",0", RegistryValueKind.String);
			key.SetValue("InstallLocation", dir, RegistryValueKind.String);
			key.SetValue("UninstallString", uninstallCmd, RegistryValueKind.String);
			key.SetValue("QuietUninstallString", Path.GetFileName(uninstallCmd).StartsWith("uninstall", StringComparison.OrdinalIgnoreCase)
				? uninstallCmd + " --silent"
				: uninstallCmd, RegistryValueKind.String);
			key.SetValue("NoModify", 1, RegistryValueKind.DWord);
			key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
			key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
			long kb = installedBytes / 1024L;
			if (kb > 0 && kb <= int.MaxValue)
			{
				key.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
			}
			log?.Report("已写入卸载项：HKCU\\" + SetupInfo.UninstallSubKey);
		}
		catch (Exception ex)
		{
			log?.Report("写卸载项失败（不影响使用）：" + ex.Message);
		}
	}

	private static void TryDeleteFile(string path, IProgress<string> log)
	{
		try
		{
			if (File.Exists(path))
			{
				File.SetAttributes(path, FileAttributes.Normal);
				File.Delete(path);
			}
		}
		catch (Exception ex)
		{
			log?.Report("清理临时文件失败：" + path + "（" + ex.Message + "）");
		}
	}

	private static void TryDeleteDir(string path, IProgress<string> log)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
				log?.Report("已清理临时目录：" + path);
			}
		}
		catch
		{
		}
	}

	public static string ReadUninstallCommand()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey(SetupInfo.UninstallSubKey);
			return key?.GetValue("UninstallString") as string;
		}
		catch
		{
			return null;
		}
	}
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>
/// 单个打包的开源小工具（对应 BundledTools\tools.json 中的一条）。
/// </summary>
public sealed class SuperTool
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Desc { get; set; } = "";

	public string Zip { get; set; } = "";

	public string Exe { get; set; } = "";

	public bool Admin { get; set; }

	public string License { get; set; } = "";

	public string Home { get; set; } = "";
}

/// <summary>
/// 超级工具箱：把随程序分发的「压缩版」开源小工具按需解压到本地缓存再启动。
/// 清单驱动——往 BundledTools 丢一个 zip、在 tools.json 加一条即接入新工具，无需改代码。
/// 压缩包随程序分发（体积小、许可证干净），用户用哪个才解压哪个，解压一次后可离线复用。
/// </summary>
public static class SuperToolkit
{
	private static readonly string BundleDir = Path.Combine(AppContext.BaseDirectory, "BundledTools");

	private static readonly string CacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MhyunBox", "SuperTools");

	public static IReadOnlyList<SuperTool> ListTools()
	{
		List<SuperTool> list = new List<SuperTool>();
		try
		{
			string manifest = Path.Combine(BundleDir, "tools.json");
			if (!File.Exists(manifest))
			{
				return list;
			}
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
			if (doc.RootElement.TryGetProperty("tools", out JsonElement tools) && tools.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement el in tools.EnumerateArray())
				{
					SuperTool tool = new SuperTool
					{
						Id = (el.TryGetProperty("id", out JsonElement id) ? id.GetString() : "") ?? "",
						Name = (el.TryGetProperty("name", out JsonElement n) ? n.GetString() : "") ?? "",
						Desc = (el.TryGetProperty("desc", out JsonElement d) ? d.GetString() : "") ?? "",
						Zip = (el.TryGetProperty("zip", out JsonElement z) ? z.GetString() : "") ?? "",
						Exe = (el.TryGetProperty("exe", out JsonElement e) ? e.GetString() : "") ?? "",
						Admin = el.TryGetProperty("admin", out JsonElement a) && a.ValueKind == JsonValueKind.True,
						License = (el.TryGetProperty("license", out JsonElement l) ? l.GetString() : "") ?? "",
						Home = (el.TryGetProperty("home", out JsonElement h) ? h.GetString() : "") ?? ""
					};
					if (!string.IsNullOrWhiteSpace(tool.Id) && !string.IsNullOrWhiteSpace(tool.Zip) && !string.IsNullOrWhiteSpace(tool.Exe))
					{
						list.Add(tool);
					}
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "读取超级工具箱清单失败");
		}
		return list;
	}

	/// <summary>解压（如需要）并启动工具。onStatus 回报“正在解压/正在启动/已启动”等进度。</summary>
	public static async Task RunToolAsync(SuperTool tool, Action<string>? onStatus = null)
	{
		if (tool == null || !IsSimpleName(tool.Id))
		{
			onStatus?.Invoke("工具标识无效，已拒绝启动。");
			return;
		}
		if (!TryResolveUnder(BundleDir, tool.Zip, out string zipPath))
		{
			onStatus?.Invoke("工具压缩包路径无效，已拒绝启动：" + tool.Zip);
			return;
		}
		if (!File.Exists(zipPath))
		{
			onStatus?.Invoke("未找到工具压缩包：" + tool.Zip);
			return;
		}
		string destDir = Path.Combine(CacheRoot, tool.Id);
		if (!TryResolveUnder(destDir, tool.Exe, out string exePath))
		{
			onStatus?.Invoke("工具主程序路径无效，已拒绝启动：" + tool.Exe);
			return;
		}
		try
		{
			if (!File.Exists(exePath))
			{
				onStatus?.Invoke("首次使用，正在解压 " + tool.Name + " …");
				await Task.Run(delegate
				{
					Directory.CreateDirectory(destDir);
					ArchiveSafety.ExtractZipToDirectory(zipPath, destDir, overwrite: true);
				});
			}
			if (!File.Exists(exePath))
			{
				onStatus?.Invoke("解压后未找到主程序：" + tool.Exe);
				return;
			}
			onStatus?.Invoke("正在启动 " + tool.Name + " …");
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = exePath,
				WorkingDirectory = destDir,
				UseShellExecute = true
			};
			if (tool.Admin)
			{
				psi.Verb = "runas";
			}
			await Task.Run(delegate
			{
				Process.Start(psi);
			});
			onStatus?.Invoke("已启动 " + tool.Name + "。");
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			onStatus?.Invoke("已取消管理员授权，" + tool.Name + " 未启动。");
		}
		catch (Exception ex2)
		{
			LogHelper.LogError(ex2, "启动超级工具箱工具失败: " + tool.Id);
			onStatus?.Invoke("启动失败：" + ex2.Message);
		}
	}

	/// <summary>清单里的 id 必须是单层名称（允许中文/空格），不能带路径分隔符或盘符。</summary>
	private static bool IsSimpleName(string? value)
	{
		if (string.IsNullOrWhiteSpace(value) || value!.Length > 96)
		{
			return false;
		}
		if (value == "." || value == ".." || value.IndexOf('\\') >= 0 || value.IndexOf('/') >= 0 || value.IndexOf(':') >= 0)
		{
			return false;
		}
		foreach (char c in value)
		{
			if (c < 32 || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>把相对路径解析到指定根目录内；越界/绝对路径返回 false。</summary>
	private static bool TryResolveUnder(string root, string relative, out string full)
	{
		full = "";
		if (!ArchiveSafety.IsSafeEntryPath(relative))
		{
			return false;
		}
		try
		{
			string rootFull = Path.GetFullPath(root);
			string candidate = Path.GetFullPath(Path.Combine(rootFull, relative));
			string prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			full = candidate;
			return true;
		}
		catch
		{
			return false;
		}
	}
}

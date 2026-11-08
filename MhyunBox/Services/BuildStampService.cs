using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Settings;

namespace MhyunBox.Services;

public enum BuildStampStatus
{
	/// <summary>本地与仓库基本一致。</summary>
	Current,
	/// <summary>本地比仓库旧（该更新了）。</summary>
	Behind,
	/// <summary>本地比仓库新（本地修改版 / 尚未推送）。</summary>
	Ahead,
	/// <summary>DLL 与主程序不是同一次构建（常见于只替换了 DLL）。</summary>
	Mixed,
	/// <summary>拿不到仓库时间或读不到本地文件。</summary>
	Unknown
}

public sealed class BuildStampInfo
{
	public string DllPath { get; init; } = "";

	public DateTime LocalDllUtc { get; init; }

	public DateTime LocalExeUtc { get; init; }

	public DateTime? RemoteCommitUtc { get; init; }

	/// <summary>本地 DLL 时间的来源：PE 时间戳 / 文件写入时间。</summary>
	public string LocalSource { get; init; } = "";

	public BuildStampStatus Status { get; init; }

	public string StatusText { get; init; } = "";

	public string? RemoteError { get; init; }

	public bool HasLocal => LocalDllUtc > DateTime.MinValue;
}

/// <summary>
/// 程序 DLL 构建时间校验：
/// 读取本机 MhyunBox.dll 的构建时间，与 GitHub 仓库最新提交时间对比，
/// 判断「本地落后 / 本地较新 / DLL 与主程序不是同一次构建」，并引导用户加群换 DLL。
///
/// 关于「PE 时间戳」：.NET SDK 默认开启确定性构建（Deterministic），
/// 编译进 PE 头的 TimeDateStamp 是内容哈希而不是真实时间（常见为 0x8xxxxxxx 的怪值）。
/// 因此这里先尝试 PE 时间戳，落在合理区间才采用；否则退回文件最后写入时间
/// （解压/复制会保留该时间，足以判断构建批次）。
/// </summary>
public static class BuildStampService
{
	public const string DllFileName = "MhyunBox.dll";

	private const string CommitsApi = "https://api.github.com/repos/" + GitHubService.RepoOwner + "/" + GitHubService.RepoName + "/commits?per_page=1";

	private static readonly HttpClient Http = CreateClient();

	private static readonly SemaphoreSlim RemoteLock = new SemaphoreSlim(1, 1);

	private static DateTime? _remoteCommitUtc;

	private static DateTime _remoteFetchedAtUtc = DateTime.MinValue;

	private static readonly TimeSpan RemoteCache = TimeSpan.FromMinutes(30);

	/// <summary>一致性容差：同一次构建内的文件时间差、时区/提交时间差都落在半天以内。</summary>
	private static readonly TimeSpan Tolerance = TimeSpan.FromHours(12);

	private static HttpClient CreateClient()
	{
		HttpClient client = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(12.0)
		};
		try
		{
			client.DefaultRequestHeaders.UserAgent.ParseAdd("MhyunBox/" + AppVersion.Display.TrimStart('v', 'V'));
		}
		catch
		{
		}
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		return client;
	}

	/// <summary>本地 DLL / EXE 的构建时间（不发网络请求）。</summary>
	public static BuildStampInfo CheckLocal()
	{
		string dir = AppContext.BaseDirectory;
		string dll = Path.Combine(dir, DllFileName);
		string exe = Environment.ProcessPath ?? Path.Combine(dir, "MhyunBox.exe");
		DateTime dllUtc = DateTime.MinValue;
		string source = "未知";
		try
		{
			if (File.Exists(dll))
			{
				DateTime? pe = TryReadPeTimestamp(dll);
				if (pe.HasValue)
				{
					dllUtc = pe.Value;
					source = "PE 时间戳";
				}
				else
				{
					dllUtc = File.GetLastWriteTimeUtc(dll);
					source = "文件写入时间";
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[构建校验] 读取本地 DLL 时间失败");
		}
		DateTime exeUtc = DateTime.MinValue;
		try
		{
			if (File.Exists(exe))
			{
				exeUtc = File.GetLastWriteTimeUtc(exe);
			}
		}
		catch
		{
		}
		BuildStampStatus localStatus = BuildStampStatus.Unknown;
		string localText = "未能读取本地 " + DllFileName;
		if (dllUtc > DateTime.MinValue)
		{
			localStatus = BuildStampStatus.Current;
			localText = "本地版本可用";
			if (exeUtc > DateTime.MinValue && (dllUtc - exeUtc).Duration() > Tolerance)
			{
				localStatus = BuildStampStatus.Mixed;
				localText = "DLL 与主程序不是同一次构建";
			}
		}
		return new BuildStampInfo
		{
			DllPath = dll,
			LocalDllUtc = dllUtc,
			LocalExeUtc = exeUtc,
			LocalSource = source,
			Status = localStatus,
			StatusText = localText
		};
	}

	/// <summary>本地 + 仓库对比。forceRefresh=true 时忽略 30 分钟缓存。</summary>
	public static async Task<BuildStampInfo> CheckAsync(bool forceRefresh = false)
	{
		BuildStampInfo local = CheckLocal();
		DateTime? remote = await GetRemoteCommitUtcAsync(forceRefresh).ConfigureAwait(false);
		string? error = remote.HasValue ? null : "未能获取仓库最新提交（网络或接口不可用）";
		BuildStampStatus status = local.Status;
		string text = local.StatusText;
		if (local.HasLocal && remote.HasValue)
		{
			TimeSpan gap = local.LocalDllUtc - remote.Value;
			if (status != BuildStampStatus.Mixed)
			{
				if (gap > Tolerance)
				{
					status = BuildStampStatus.Ahead;
					text = "本地比仓库新 " + Humanize(gap) + "（本地修改版或尚未推送）";
				}
				else if (-gap > Tolerance)
				{
					status = BuildStampStatus.Behind;
					text = "本地比仓库旧 " + Humanize(-gap) + "，建议更新 DLL";
				}
				else
				{
					status = BuildStampStatus.Current;
					text = "本地与仓库基本一致";
				}
			}
			else
			{
				text = "DLL 与主程序不是同一次构建（常见于只替换了 DLL），建议重新获取完整包";
			}
		}
		else if (status == BuildStampStatus.Current)
		{
			status = BuildStampStatus.Unknown;
		}
		return new BuildStampInfo
		{
			DllPath = local.DllPath,
			LocalDllUtc = local.LocalDllUtc,
			LocalExeUtc = local.LocalExeUtc,
			LocalSource = local.LocalSource,
			RemoteCommitUtc = remote,
			RemoteError = error,
			Status = status,
			StatusText = text
		};
	}

	/// <summary>启动后台校验：落后或 DLL 不匹配时提示加群（每天最多一次）。</summary>
	public static void StartStartupCheck()
	{
		Task.Run(async delegate
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(10.0)).ConfigureAwait(continueOnCapturedContext: false);
				BuildStampInfo info = await CheckAsync().ConfigureAwait(continueOnCapturedContext: false);
				LogHelper.Log("[构建校验] 本地 DLL=" + FormatUtc(info.LocalDllUtc) + "（" + info.LocalSource + "）"
					+ " EXE=" + FormatUtc(info.LocalExeUtc)
					+ " 仓库=" + (info.RemoteCommitUtc.HasValue ? FormatUtc(info.RemoteCommitUtc.Value) : "获取失败")
					+ " => " + info.Status + "：" + info.StatusText);
				if (info.Status != BuildStampStatus.Behind && info.Status != BuildStampStatus.Mixed)
				{
					return;
				}
				string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
				if (string.Equals(SettingsManager.Instance.BuildCheckPromptDate, today, StringComparison.Ordinal))
				{
					return;
				}
				try
				{
					AppSettings settings = SettingsManager.Instance;
					settings.BuildCheckPromptDate = today;
					SettingsManager.Save(settings);
				}
				catch
				{
				}
				string body = BuildPromptText(info);
				MainWindow window = MainWindow.Instance;
				if (window != null)
				{
					window.DispatcherQueue.TryEnqueue(async delegate
					{
						await CommunityChannel.ShowJoinPromptAsync("发现 DLL 版本不一致", body, "一键加群换 DLL");
					});
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[构建校验] 启动校验失败");
			}
		});
	}

	public static string BuildPromptText(BuildStampInfo info, bool preview = false)
	{
		string local = info.HasLocal ? FormatUtc(info.LocalDllUtc) : "读取失败";
		string remote = info.RemoteCommitUtc.HasValue ? FormatUtc(info.RemoteCommitUtc.Value) : "获取失败";
		string advice = info.Status switch
		{
			BuildStampStatus.Mixed => "检测到你本地的 MhyunBox.dll 与主程序不是同一次构建（常见于只替换了 DLL）。\n请加群下载完整包后整体替换，或从仓库拉取源码重新构建再合并。",
			BuildStampStatus.Behind => "你本地的 MhyunBox.dll 比仓库最新提交旧，请加群下载最新 DLL 替换，或从仓库拉取源码重新构建后合并。",
			BuildStampStatus.Ahead => "本地 DLL 比仓库提交更新（本地修改版或仓库尚未同步）。若你只是普通使用，建议以群内发布的完整包为准。",
			_ => "当前本地文件与仓库基本一致；这条提示用于预览提醒样式。"
		};
		string head = preview ? "（预览）真实使用时只会在 DLL 落后或与主程序不匹配时弹出。\n\n" : "";
		return head + "本地 DLL 构建时间：" + local + "\n仓库最新提交时间：" + remote + "\n\n" + advice
			+ "\n\nQQ 群：" + CommunityChannel.QqGroupNumber + "（更新 DLL / 反馈问题 / 崩溃日志）";
	}

	public static string FormatUtc(DateTime utc)
	{
		if (utc <= DateTime.MinValue)
		{
			return "—";
		}
		return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
	}

	private static string Humanize(TimeSpan span)
	{
		if (span.TotalDays >= 1.0)
		{
			return ((int)span.TotalDays) + " 天";
		}
		return Math.Max(1, (int)span.TotalHours) + " 小时";
	}

	/// <summary>读取 PE 头 TimeDateStamp；确定性构建下是哈希，判定为不可用时返回 null。</summary>
	private static DateTime? TryReadPeTimestamp(string path)
	{
		try
		{
			using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using BinaryReader reader = new BinaryReader(fs);
			if (fs.Length < 0x100)
			{
				return null;
			}
			fs.Position = 0x3C;
			int peOffset = reader.ReadInt32();
			if (peOffset <= 0 || peOffset + 12 > fs.Length)
			{
				return null;
			}
			fs.Position = peOffset;
			if (reader.ReadUInt32() != 0x00004550u)
			{
				return null;
			}
			uint stamp = reader.ReadUInt32();
			if (stamp == 0 || stamp == uint.MaxValue)
			{
				return null;
			}
			// 确定性构建会把高位做成哈希：超过 2038 年或早于 2000 年都视为不可用
			DateTime utc = DateTimeOffset.FromUnixTimeSeconds(stamp).UtcDateTime;
			if (utc.Year < 2000 || utc > DateTime.UtcNow.AddDays(2.0))
			{
				return null;
			}
			return utc;
		}
		catch
		{
			return null;
		}
	}

	private static async Task<DateTime?> GetRemoteCommitUtcAsync(bool forceRefresh)
	{
		if (!forceRefresh && _remoteCommitUtc.HasValue && DateTime.UtcNow - _remoteFetchedAtUtc < RemoteCache)
		{
			return _remoteCommitUtc;
		}
		if (!await RemoteLock.WaitAsync(TimeSpan.FromSeconds(5.0)).ConfigureAwait(continueOnCapturedContext: false))
		{
			return _remoteCommitUtc;
		}
		try
		{
			if (!forceRefresh && _remoteCommitUtc.HasValue && DateTime.UtcNow - _remoteFetchedAtUtc < RemoteCache)
			{
				return _remoteCommitUtc;
			}
			using JsonDocument doc = JsonDocument.Parse(await Http.GetStringAsync(CommitsApi).ConfigureAwait(continueOnCapturedContext: false));
			JsonElement root = doc.RootElement;
			if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
			{
				JsonElement commit = root[0];
				JsonElement node = default(JsonElement);
				if (commit.TryGetProperty("commit", out JsonElement commitNode)
					&& commitNode.TryGetProperty("committer", out node)
					&& node.TryGetProperty("date", out JsonElement dateNode)
					&& DateTime.TryParse(dateNode.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
				{
					_remoteCommitUtc = parsed;
					_remoteFetchedAtUtc = DateTime.UtcNow;
					return _remoteCommitUtc;
				}
			}
			LogHelper.Log("[构建校验] 仓库提交接口返回内容无法解析");
		}
		catch (Exception ex)
		{
			LogHelper.Log("[构建校验] 获取仓库最新提交失败：" + ex.Message);
		}
		finally
		{
			RemoteLock.Release();
		}
		return _remoteCommitUtc;
	}
}

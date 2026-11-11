using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MhyunBox.Settings;

namespace MhyunBox.Services;

public enum BuildStampStatus
{
	/// <summary>本地 DLL 与仓库清单的大小和 SHA256 完全一致。</summary>
	Current,
	/// <summary>与仓库 DLL 不一致（大小或 SHA256 有差异，需要换 DLL）。</summary>
	Behind,
	/// <summary>保留枚举值（大小+SHA 对比下无法判断先后）。</summary>
	Ahead,
	/// <summary>本地 DLL 读不到 / 损坏。</summary>
	Mixed,
	/// <summary>拿不到仓库校验清单。</summary>
	Unknown
}

public sealed class BuildStampInfo
{
	public string DllPath { get; init; } = "";

	/// <summary>本地 DLL 字节数；0 = 读取失败。</summary>
	public long LocalSize { get; init; }

	/// <summary>本地 DLL 的 SHA256（小写十六进制）；读取失败为空串。</summary>
	public string LocalSha256 { get; init; } = "";

	/// <summary>仓库清单里的 DLL 字节数。</summary>
	public long? RemoteSize { get; init; }

	/// <summary>仓库清单里的 SHA256。</summary>
	public string? RemoteSha256 { get; init; }

	public string? RemoteVersion { get; init; }

	public DateTime? RemoteUpdatedUtc { get; init; }

	public BuildStampStatus Status { get; init; }

	public string StatusText { get; init; } = "";

	public string? RemoteError { get; init; }

	public bool HasLocal => LocalSize > 0 && LocalSha256.Length == 64;
}

/// <summary>
/// DLL 一致性校验：对比本地 MhyunBox.dll 与仓库公布的官方 DLL 的
/// **文件大小 + SHA256**，不再用构建时间/提交时间。
///
/// 为什么改：时间戳会被解压、复制、不同机器时区、用户手动更新 DLL 打乱，
/// 对部分用户会误判「落后 / 较新」。大小与哈希是内容本身的指纹，结论唯一。
/// 仓库里不放 DLL 二进制（构建产物本来被 .gitignore 排除），只放一份
/// dll-manifest.json（size/sha256/version/updated），程序按 raw 地址拉取。
/// </summary>
public static class BuildStampService
{
	public const string DllFileName = "MhyunBox.dll";

	private const string ManifestUrl = "https://raw.githubusercontent.com/" + GitHubService.RepoOwner + "/" + GitHubService.RepoName + "/" + GitHubService.DefaultBranch + "/dll-manifest.json";

	private static readonly HttpClient Http = CreateClient();

	private static readonly SemaphoreSlim RemoteLock = new SemaphoreSlim(1, 1);

	private static BuildManifest? _remoteManifest;

	private static string? _remoteError;

	private static DateTime _remoteFetchedAtUtc = DateTime.MinValue;

	private static readonly TimeSpan RemoteCache = TimeSpan.FromMinutes(30);

	private static HttpClient CreateClient()
	{
		HttpClient client = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(12.0)
		};
		try
		{
			client.DefaultRequestHeaders.UserAgent.ParseAdd("MhyUnBox/" + AppVersion.Display.TrimStart('v', 'V'));
		}
		catch
		{
		}
		return client;
	}

	private sealed class BuildManifest
	{
		public long Size { get; init; }
		public string Sha256 { get; init; } = "";
		public string Version { get; init; } = "";
		public DateTime? UpdatedUtc { get; init; }
	}

	/// <summary>读取本地 DLL 的大小与 SHA256（不发网络请求）。</summary>
	public static BuildStampInfo CheckLocal()
	{
		string dll = Path.Combine(AppContext.BaseDirectory, DllFileName);
		long size = 0L;
		string sha = "";
		try
		{
			if (File.Exists(dll))
			{
				using FileStream fs = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				size = fs.Length;
				sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[DLL校验] 读取本地 DLL 失败");
		}
		bool hasLocal = size > 0L && sha.Length == 64;
		return new BuildStampInfo
		{
			DllPath = dll,
			LocalSize = size,
			LocalSha256 = sha,
			Status = hasLocal ? BuildStampStatus.Current : BuildStampStatus.Mixed,
			StatusText = hasLocal ? "本地 DLL 已读取" : "本地 " + DllFileName + " 读取失败（文件缺失或被占用）"
		};
	}

	/// <summary>本地 + 仓库清单对比。forceRefresh=true 时忽略 30 分钟缓存。</summary>
	public static async Task<BuildStampInfo> CheckAsync(bool forceRefresh = false)
	{
		BuildStampInfo local = CheckLocal();
		BuildManifest? remote = await GetRemoteManifestAsync(forceRefresh).ConfigureAwait(false);
		BuildStampStatus status;
		string text;
		if (!local.HasLocal)
		{
			status = BuildStampStatus.Mixed;
			text = "无法读取本地 DLL，建议重新获取完整包";
		}
		else if (remote == null)
		{
			status = BuildStampStatus.Unknown;
			text = "无法获取仓库 DLL 校验清单（网络或文件不可用）";
		}
		else if (local.LocalSize != remote.Size)
		{
			status = BuildStampStatus.Behind;
			text = "DLL 大小与仓库不一致：本地 " + FormatSize(local.LocalSize) + " / 仓库 " + FormatSize(remote.Size);
		}
		else if (!string.Equals(local.LocalSha256, remote.Sha256, StringComparison.OrdinalIgnoreCase))
		{
			status = BuildStampStatus.Behind;
			text = "DLL 内容与仓库不一致（SHA256 不同），建议更新 DLL";
		}
		else
		{
			status = BuildStampStatus.Current;
			text = "与仓库 DLL 完全一致" + (string.IsNullOrWhiteSpace(remote.Version) ? "" : "（v" + remote.Version + "）");
		}
		return new BuildStampInfo
		{
			DllPath = local.DllPath,
			LocalSize = local.LocalSize,
			LocalSha256 = local.LocalSha256,
			RemoteSize = remote?.Size,
			RemoteSha256 = remote?.Sha256,
			RemoteVersion = remote?.Version,
			RemoteUpdatedUtc = remote?.UpdatedUtc,
			RemoteError = remote == null ? (_remoteError ?? "未能获取仓库校验清单") : null,
			Status = status,
			StatusText = text
		};
	}

	/// <summary>启动后台校验：与仓库不一致或本地 DLL 读不到时提示加群（每天最多一次）。</summary>
	public static void StartStartupCheck()
	{
		Task.Run(async delegate
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(10.0)).ConfigureAwait(continueOnCapturedContext: false);
				BuildStampInfo info = await CheckAsync().ConfigureAwait(continueOnCapturedContext: false);
				LogHelper.Log("[DLL校验] 本地 size=" + FormatSize(info.LocalSize) + " sha=" + ShortHash(info.LocalSha256)
					+ " 仓库 size=" + (info.RemoteSize.HasValue ? FormatSize(info.RemoteSize.Value) : "获取失败")
					+ " sha=" + (info.RemoteSha256 ?? "获取失败")
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
				LogHelper.LogError(ex, "[DLL校验] 启动校验失败");
			}
		});
	}

	public static string BuildPromptText(BuildStampInfo info, bool preview = false)
	{
		string local = info.HasLocal ? FormatSize(info.LocalSize) + " / " + ShortHash(info.LocalSha256) : "读取失败";
		string remote = info.RemoteSize.HasValue
			? FormatSize(info.RemoteSize.Value) + " / " + ShortHash(info.RemoteSha256 ?? "")
			: (info.RemoteError ?? "获取失败");
		string advice = info.Status switch
		{
			BuildStampStatus.Mixed => "本地 MhyunBox.dll 读不到（文件缺失、被占用或损坏），请加群下载完整包后整体替换。",
			BuildStampStatus.Behind => "你本地的 MhyunBox.dll 与仓库发布的 DLL 不一致（大小或 SHA256 不同），请加群下载最新 DLL 替换，或从仓库拉取源码重新构建后合并。",
			BuildStampStatus.Unknown => "暂时拿不到仓库的 DLL 校验清单，无法判断一致性；可稍后点「重新检测」。",
			_ => "当前本地 DLL 与仓库清单完全一致；这条提示用于预览提醒样式。"
		};
		string head = preview ? "（预览）真实使用时只会在 DLL 与仓库不一致或读取失败时弹出。\n\n" : "";
		return head + "本地 DLL：" + local + "\n仓库 DLL：" + remote + "\n\n" + advice
			+ "\n\nQQ 群：" + CommunityChannel.QqGroupNumber + "（更新 DLL / 反馈问题 / 崩溃日志）";
	}

	public static string FormatSize(long bytes)
	{
		return bytes.ToString("N0", CultureInfo.InvariantCulture) + " 字节";
	}

	public static string ShortHash(string? sha)
	{
		if (string.IsNullOrWhiteSpace(sha))
		{
			return "—";
		}
		return sha.Length <= 12 ? sha : sha.Substring(0, 12) + "…";
	}

	private static async Task<BuildManifest?> GetRemoteManifestAsync(bool forceRefresh)
	{
		if (!forceRefresh && _remoteManifest != null && DateTime.UtcNow - _remoteFetchedAtUtc < RemoteCache)
		{
			return _remoteManifest;
		}
		if (!await RemoteLock.WaitAsync(TimeSpan.FromSeconds(5.0)).ConfigureAwait(continueOnCapturedContext: false))
		{
			return _remoteManifest;
		}
		try
		{
			if (!forceRefresh && _remoteManifest != null && DateTime.UtcNow - _remoteFetchedAtUtc < RemoteCache)
			{
				return _remoteManifest;
			}
			string url = ManifestUrl + (forceRefresh ? "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) : "");
			string json = await Http.GetStringAsync(url).ConfigureAwait(continueOnCapturedContext: false);
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;
			long size = root.TryGetProperty("size", out JsonElement sizeNode) && sizeNode.TryGetInt64(out long parsedSize) ? parsedSize : 0L;
			string sha = root.TryGetProperty("sha256", out JsonElement shaNode) ? (shaNode.GetString() ?? "").Trim().ToLowerInvariant() : "";
			string version = root.TryGetProperty("version", out JsonElement versionNode) ? (versionNode.GetString() ?? "") : "";
			DateTime? updated = null;
			if (root.TryGetProperty("updated", out JsonElement dateNode)
				&& DateTime.TryParse(dateNode.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsedDate))
			{
				updated = parsedDate;
			}
			if (size <= 0L || sha.Length != 64)
			{
				throw new FormatException("清单缺少有效的 size / sha256");
			}
			_remoteManifest = new BuildManifest
			{
				Size = size,
				Sha256 = sha,
				Version = version,
				UpdatedUtc = updated
			};
			_remoteError = null;
			_remoteFetchedAtUtc = DateTime.UtcNow;
			return _remoteManifest;
		}
		catch (Exception ex)
		{
			_remoteError = "清单获取失败：" + ex.Message;
			LogHelper.Log("[DLL校验] " + _remoteError);
		}
		finally
		{
			RemoteLock.Release();
		}
		return _remoteManifest;
	}
}

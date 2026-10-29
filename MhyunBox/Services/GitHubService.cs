using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>
/// GitHub 仓库联动：更新检查 + 合并请求（Pull Request）提交。
///
/// 设计取舍：
/// 1) 更新检查走公开 API，不需要任何凭据（未认证 60 次/小时的配额对本场景足够）。
/// 2) 合并请求分两级：
///    - 配置了 Personal Access Token（PAT）时，直接走 API 建分支 → 提交文件 → 开 PR，全程在程序内完成；
///    - 没有 Token 或 API 失败时，回退为「打开 GitHub 预填好的 compare 页面」，用户点一下即可提交。
///    这样即使没有凭据也一定有可用的提交路径，不会出现「点了没反应」。
/// 3) Token 用 SecureStore（DPAPI，当前用户维度加密）保存，不进设置 json 明文。
/// </summary>
public static class GitHubService
{
	public const string RepoOwner = "Notel8364";

	public const string RepoName = "MhyUnBox";

	public const string DefaultBranch = "main";

	private const string TokenStoreName = "github_pat";

	private const string ApiBase = "https://api.github.com";

	private static readonly HttpClient Http = CreateClient();

	public static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";

	public static string ReleasesUrl => RepoUrl + "/releases";

	public static string IssuesUrl => RepoUrl + "/issues";

	private static HttpClient CreateClient()
	{
		HttpClient client = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(20.0)
		};
		client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MhyunBox", TrimVersion(AppVersion.Display)));
		client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
		return client;
	}

	// ---------- Token（可选，用于程序内直接开 PR） ----------

	public static bool HasToken => !string.IsNullOrWhiteSpace(LoadToken());

	public static string? LoadToken()
	{
		try
		{
			string? text = SecureStore.Load(TokenStoreName);
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}
			return text.Trim();
		}
		catch
		{
			return null;
		}
	}

	public static void SaveToken(string token)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			SecureStore.Delete(TokenStoreName);
			return;
		}
		SecureStore.Save(TokenStoreName, token.Trim());
	}

	public static void ClearToken()
	{
		SecureStore.Delete(TokenStoreName);
	}

	// ---------- 更新检查 ----------

	public sealed class UpdateInfo
	{
		public bool Ok;

		public bool HasUpdate;

		public string CurrentVersion = "";

		public string LatestTag = "";

		public string LatestName = "";

		public string PublishedAt = "";

		public string HtmlUrl = "";

		public string DownloadUrl = "";

		public string Notes = "";

		public string Message = "";
	}

	/// <summary>
	/// 检查上游是否有新版本。优先取 latest release；仓库没有 release 时回退到最新提交（按时间提示）。
	/// 任何网络/解析失败都返回 Ok=false 并带上原因，不抛异常给 UI。
	/// </summary>
	public static async Task<UpdateInfo> CheckUpdateAsync(CancellationToken ct = default(CancellationToken))
	{
		UpdateInfo info = new UpdateInfo
		{
			CurrentVersion = AppVersion.Display
		};
		try
		{
			using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/repos/{RepoOwner}/{RepoName}/releases/latest");
			AttachAuth(req);
			using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
			if (resp.StatusCode == HttpStatusCode.NotFound)
			{
				return await CheckByLatestCommitAsync(info, ct).ConfigureAwait(false);
			}
			string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
			{
				info.Message = "GitHub 返回 " + (int)resp.StatusCode + "：" + Shorten(ExtractApiMessage(json) ?? json);
				return info;
			}
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;
			info.LatestTag = GetString(root, "tag_name") ?? "";
			info.LatestName = GetString(root, "name") ?? "";
			info.HtmlUrl = GetString(root, "html_url") ?? ReleasesUrl;
			info.PublishedAt = GetString(root, "published_at") ?? "";
			info.Notes = TrimNotes(GetString(root, "body"));
			if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement a in assets.EnumerateArray())
				{
					string? url = GetString(a, "browser_download_url");
					if (!string.IsNullOrWhiteSpace(url))
					{
						info.DownloadUrl = url;
						break;
					}
				}
			}
			info.HasUpdate = IsNewer(info.LatestTag, info.CurrentVersion);
			info.Ok = true;
			if (string.IsNullOrWhiteSpace(info.Message))
			{
				info.Message = info.HasUpdate ? "发现新版本" : "当前已是最新版本";
			}
			return info;
		}
		catch (OperationCanceledException)
		{
			info.Message = "检查超时，请稍后重试";
			return info;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[GitHub] 更新检查失败");
			info.Message = "检查失败：" + ex.Message;
			return info;
		}
	}

	/// <summary>没有 release 的仓库，退化为比较「最新提交时间」并给出仓库地址。</summary>
	private static async Task<UpdateInfo> CheckByLatestCommitAsync(UpdateInfo info, CancellationToken ct)
	{
		try
		{
			using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/repos/{RepoOwner}/{RepoName}/commits/{DefaultBranch}");
			AttachAuth(req);
			using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
			string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
			{
				info.Message = "仓库暂无 Release，且无法读取最新提交（" + (int)resp.StatusCode + "）";
				return info;
			}
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;
			info.LatestTag = (GetString(root, "sha") ?? "").Length > 7 ? (GetString(root, "sha") ?? "").Substring(0, 7) : "";
			if (root.TryGetProperty("commit", out JsonElement commit) && commit.TryGetProperty("committer", out JsonElement committer))
			{
				info.PublishedAt = GetString(committer, "date") ?? "";
			}
			info.Notes = (commit.TryGetProperty("message", out JsonElement msg) ? msg.GetString() : null) ?? "";
			info.HtmlUrl = RepoUrl;
			info.HasUpdate = false;
			info.Ok = true;
			info.Message = "仓库还没有发布 Release，无法自动判定新旧；已显示 main 分支最新提交。";
			return info;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[GitHub] 提交信息读取失败");
			info.Message = "读取仓库信息失败：" + ex.Message;
			return info;
		}
	}

	// ---------- 合并请求（Pull Request） ----------

	public sealed class MergeRequestResult
	{
		public bool Ok;

		public string Message = "";

		public string? PrUrl;

		/// <summary>是否已改为「打开浏览器预填页」让用户手动完成。</summary>
		public bool OpenedInBrowser;
	}

	/// <summary>
	/// 提交合并请求。
	/// 有 PAT：建分支 → 提交 <c>PR/xxx.md</c> → 开 PR，返回 PR 地址。
	/// 无 PAT / 失败：打开预填的 compare 页面（quick_pull=1），由用户在网页上点一下完成。
	/// </summary>
	public static async Task<MergeRequestResult> SubmitMergeRequestAsync(string title, string body, CancellationToken ct = default(CancellationToken))
	{
		string safeTitle = (title ?? "").Trim();
		if (string.IsNullOrWhiteSpace(safeTitle))
		{
			return new MergeRequestResult { Message = "标题不能为空" };
		}
		string? token = LoadToken();
		if (!string.IsNullOrWhiteSpace(token))
		{
			try
			{
				return await SubmitViaApiAsync(safeTitle, body ?? "", token, ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return new MergeRequestResult { Message = "提交超时，已改为打开网页提交" , OpenedInBrowser = OpenCompareInBrowser(safeTitle, body ?? "", "") };
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[GitHub] API 提交 PR 失败，回退网页提交");
				MergeRequestResult fallback = new MergeRequestResult
				{
					Message = "程序内提交失败（" + ex.Message + "），已打开网页预填页，请点一下即可完成提交。"
				};
				fallback.OpenedInBrowser = OpenCompareInBrowser(safeTitle, body ?? "", "");
				return fallback;
			}
		}
		bool opened = OpenCompareInBrowser(safeTitle, body ?? "", "");
		return new MergeRequestResult
		{
			Ok = opened,
			OpenedInBrowser = opened,
			Message = opened ? "未配置 Token，已打开网页预填页，点「Create pull request」即可提交合并请求。" : "打开浏览器失败，请手动访问 " + RepoUrl
		};
	}

	/// <summary>用 PAT 走 API：创建分支 → 提交一个说明文件 → 开 PR。</summary>
	private static async Task<MergeRequestResult> SubmitViaApiAsync(string title, string body, string token, CancellationToken ct)
	{
		string baseSha = await GetBranchShaAsync(DefaultBranch, token, ct).ConfigureAwait(false);
		if (string.IsNullOrEmpty(baseSha))
		{
			throw new InvalidOperationException("无法获取 " + DefaultBranch + " 分支的最新提交（仓库不存在或 Token 无权限）");
		}
		string slug = Slugify(title);
		string branch = "pr/" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + slug;
		if (!await CreateBranchAsync(branch, baseSha, token, ct).ConfigureAwait(false))
		{
			throw new InvalidOperationException("创建分支失败（Token 可能缺少写权限）");
		}
		string path = "PR/" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + slug + ".md";
		if (!await CommitFileAsync(path, BuildRequestBody(title, body), branch, token, ct).ConfigureAwait(false))
		{
			throw new InvalidOperationException("提交文件失败");
		}
		string? prUrl = await CreatePullRequestAsync(title, BuildRequestBody(title, body), branch, token, ct).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(prUrl))
		{
			throw new InvalidOperationException("创建 Pull Request 失败");
		}
		return new MergeRequestResult
		{
			Ok = true,
			PrUrl = prUrl,
			Message = "合并请求已提交：" + prUrl
		};
	}

	private static async Task<string?> GetBranchShaAsync(string branch, string token, CancellationToken ct)
	{
		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/repos/{RepoOwner}/{RepoName}/git/ref/heads/{branch}");
		AttachAuth(req, token);
		using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			return null;
		}
		using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
		if (doc.RootElement.TryGetProperty("object", out JsonElement obj) && obj.TryGetProperty("sha", out JsonElement sha))
		{
			return sha.GetString();
		}
		return null;
	}

	private static async Task<bool> CreateBranchAsync(string branch, string sha, string token, CancellationToken ct)
	{
		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/repos/{RepoOwner}/{RepoName}/git/refs")
		{
			Content = new StringContent(JsonSerializer.Serialize(new { @ref = "refs/heads/" + branch, sha }), Encoding.UTF8, "application/json")
		};
		AttachAuth(req, token);
		using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
		return resp.IsSuccessStatusCode;
	}

	private static async Task<bool> CommitFileAsync(string path, string content, string branch, string token, CancellationToken ct)
	{
		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/repos/{RepoOwner}/{RepoName}/contents/{Uri.EscapeDataString(path)}")
		{
			Content = new StringContent(JsonSerializer.Serialize(new
			{
				message = "chore: 提交合并请求（来自 MhyunBox）",
				content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
				branch
			}), Encoding.UTF8, "application/json")
		};
		AttachAuth(req, token);
		using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
		return resp.IsSuccessStatusCode;
	}

	private static async Task<string?> CreatePullRequestAsync(string title, string body, string head, string token, CancellationToken ct)
	{
		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/repos/{RepoOwner}/{RepoName}/pulls")
		{
			Content = new StringContent(JsonSerializer.Serialize(new
			{
				title,
				body,
				head,
				@base = DefaultBranch
			}), Encoding.UTF8, "application/json")
		};
		AttachAuth(req, token);
		using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
		string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			throw new InvalidOperationException(Shorten(ExtractApiMessage(json) ?? json));
		}
		using JsonDocument doc = JsonDocument.Parse(json);
		if (doc.RootElement.TryGetProperty("html_url", out JsonElement url))
		{
			return url.GetString();
		}
		return null;
	}

	/// <summary>打开 GitHub 的 compare 页并预填标题/正文（无 Token 时的兜底路径）。</summary>
	public static bool OpenCompareInBrowser(string title, string body, string head)
	{
		try
		{
			string headPart = string.IsNullOrWhiteSpace(head) ? DefaultBranch : head;
			string url = $"{RepoUrl}/compare/{DefaultBranch}...{headPart}?quick_pull=1&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body ?? "")}";
			return OpenUrl(url);
		}
		catch
		{
			return false;
		}
	}

	public static bool OpenUrl(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo(url)
			{
				UseShellExecute = true
			});
			return true;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[GitHub] 打开链接失败");
			return false;
		}
	}

	/// <summary>构造合并请求正文：用户填写内容 + 环境与版本信息（便于作者复现）。</summary>
	public static string BuildRequestBody(string title, string body)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("## 合并请求");
		sb.AppendLine();
		sb.AppendLine("**标题**：" + (title ?? "").Trim());
		sb.AppendLine();
		sb.AppendLine("### 说明");
		sb.AppendLine(string.IsNullOrWhiteSpace(body) ? "（未填写）" : body.Trim());
		sb.AppendLine();
		sb.AppendLine("---");
		sb.AppendLine();
		sb.AppendLine("> 本请求由 MhyunBox " + AppVersion.Display + " 提交");
		sb.AppendLine("> ");
		sb.AppendLine("> - 系统：" + Environment.OSVersion.VersionString);
		sb.AppendLine("> - 运行时：.NET " + Environment.Version);
		sb.AppendLine("> - 提交时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
		return sb.ToString();
	}

	// ---------- 版本比较 ----------

	/// <summary>
	/// 判断 latest 是否比 current 新。两边都按「点分数字」逐段比较，
	/// 任一侧不是标准版本号时退化为「不相等即视为有更新」并交由用户判断。
	/// </summary>
	public static bool IsNewer(string? latest, string? current)
	{
		int[] a = ParseVersion(latest);
		int[] b = ParseVersion(current);
		if (a.Length == 0 || b.Length == 0)
		{
			return false;
		}
		int n = Math.Max(a.Length, b.Length);
		for (int i = 0; i < n; i++)
		{
			int x = (i < a.Length) ? a[i] : 0;
			int y = (i < b.Length) ? b[i] : 0;
			if (x > y)
			{
				return true;
			}
			if (x < y)
			{
				return false;
			}
		}
		return false;
	}

	private static int[] ParseVersion(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return Array.Empty<int>();
		}
		string s = text.Trim();
		if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
		{
			s = s.Substring(1);
		}
		string[] parts = s.Split(new char[3] { '.', '-', '+' }, StringSplitOptions.RemoveEmptyEntries);
		List<int> list = new List<int>();
		string[] array = parts;
		foreach (string p in array)
		{
			if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
			{
				break;
			}
			list.Add(value);
		}
		return list.ToArray();
	}

	// ---------- 小工具 ----------

	private static void AttachAuth(HttpRequestMessage req, string? token = null)
	{
		string? t = token ?? LoadToken();
		if (!string.IsNullOrWhiteSpace(t))
		{
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
		}
	}

	private static string? GetString(JsonElement obj, string name)
	{
		if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String)
		{
			return v.GetString();
		}
		return null;
	}

	private static string? ExtractApiMessage(string json)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("message", out JsonElement m))
			{
				return m.GetString();
			}
		}
		catch
		{
		}
		return null;
	}

	private static string TrimNotes(string? body)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return "";
		}
		string s = body.Trim();
		return (s.Length > 600) ? (s.Substring(0, 600) + "…") : s;
	}

	private static string Shorten(string? s)
	{
		if (string.IsNullOrWhiteSpace(s))
		{
			return "";
		}
		string t = s.Trim();
		return (t.Length > 200) ? (t.Substring(0, 200) + "…") : t;
	}

	private static string Slugify(string title)
	{
		StringBuilder sb = new StringBuilder();
		foreach (char c in title.Trim().ToLowerInvariant())
		{
			if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
			{
				sb.Append(c);
			}
			else if (c == ' ' || c == '-' || c == '_' || c == '/' || c == '.' || c == ':')
			{
				sb.Append('-');
			}
			if (sb.Length >= 28)
			{
				break;
			}
		}
		string s = sb.ToString().Trim('-');
		return string.IsNullOrWhiteSpace(s) ? "request" : s;
	}

	private static string TrimVersion(string v)
	{
		string s = (v ?? "").Trim();
		if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
		{
			s = s.Substring(1);
		}
		return string.IsNullOrWhiteSpace(s) ? "1.0" : s;
	}
}

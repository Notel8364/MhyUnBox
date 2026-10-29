using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>
/// 网易网络游戏 / 服务器资源包（item）下载地址自动获取。
/// 协议逆向自 EchooQt 1.1.2 的 EchooCore.exe（Go 编写，UPX 加壳，已剥壳分析）：
///   1) API 基址取自 https://x19.update.netease.com/serverlist/release.json 的 ApiGatewayUrl
///      （即 https://x19apigatewayobt.nie.netease.com，与 NeteaseRoleService 同址）。
///   2) POST /user-item-download-v2
///      headers: user-id / user-token（NeteaseCipher.ComputeHttpRequestToken 同款 md5 签名算法）、
///               user-agent: WPFLauncher/0.0.0.0、content-type: application/json
///      body:    {"offset":0,"length":0,"item_id":"&lt;gameId&gt;"}
///      响应 entity 内含 url / res_url，指向 CDN 上的 item_&lt;gameid&gt;_&lt;m&gt;_v&lt;ver&gt;_&lt;hash&gt;.7z。
///   3) 若直接按 gameId 取不到，则退而查询 /item/query/available 列出该账号可用的 item，
///      按 entity_id 匹配服务器并取其 iid 再次请求下载地址。
/// 拿到 URL 后交回 ClientUpdater 既有的「下载 → MD5 校验 → 7z 解压 → 合并进 .minecraft」流程。
/// </summary>
public static class NeteaseItemService
{
	private const string ApiBase = "https://x19apigatewayobt.nie.netease.com";

	private const string DownloadPath = "/user-item-download-v2";

	private const string QueryPath = "/item/query/available";

	private const string UserAgent = "WPFLauncher/0.0.0.0";

	/// <summary>
	/// 获取指定服务器（gameId）当前最新的 item 资源包下载地址。
	/// 成功返回完整 URL（形如 https://x19.gph.netease.com/item_&lt;gameid&gt;_&lt;m&gt;_v&lt;ver&gt;_&lt;hash&gt;.7z）；
	/// 失败返回 null，具体原因写入 log。
	/// </summary>
	public static async Task<string?> FetchLatestItemUrlAsync(long gameId, string userId, string userToken, Action<string>? log, CancellationToken ct)
	{
		if (gameId <= 0)
		{
			log?.Invoke("[item] gameId 无效");
			return null;
		}
		if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userToken))
		{
			log?.Invoke("[item] 缺少网易游戏会话（EntityId/Token），请先到账号绑定页绑定并验证账号");
			return null;
		}
		try
		{
			// 首选：item_id 直接用 gameId（网易物品体系里服务器实体的 entity_id 即 gameId）
			string body = "{\"offset\":0,\"length\":0,\"item_id\":\"" + gameId.ToString() + "\"}";
			var (status, text) = await PostAsync(DownloadPath, body, userId, userToken, ct);
			log?.Invoke($"[item] POST {DownloadPath} item_id={gameId} => HTTP {status} {Truncate(text)}");
			string? url = ExtractItemUrl(text, out string? failReason);
			if (url != null)
			{
				log?.Invoke("[item] 已获取下载地址: " + url);
				return url;
			}
			log?.Invoke("[item] 直接按 gameId 获取失败：" + (failReason ?? "响应中未找到下载地址"));
			// 回退：查可用 item 列表，按 entity_id 找该服实体，取其 iid 再试
			string? iid = await QueryItemIdForGameAsync(gameId, userId, userToken, log, ct);
			if (!string.IsNullOrWhiteSpace(iid) && iid != gameId.ToString())
			{
				string body2 = "{\"offset\":0,\"length\":0,\"item_id\":\"" + iid + "\"}";
				var (status2, text2) = await PostAsync(DownloadPath, body2, userId, userToken, ct);
				log?.Invoke($"[item] POST {DownloadPath} item_id={iid} => HTTP {status2} {Truncate(text2)}");
				string? url2 = ExtractItemUrl(text2, out string? failReason2);
				if (url2 != null)
				{
					log?.Invoke("[item] 已获取下载地址(iid): " + url2);
					return url2;
				}
				log?.Invoke("[item] 按 iid 获取失败：" + (failReason2 ?? "响应中未找到下载地址"));
			}
			else
			{
				log?.Invoke("[item] 可用 item 列表中未找到该服务器的 iid（可能账号未包含该服或接口有变）");
			}
			if (!string.IsNullOrWhiteSpace(failReason))
			{
				log?.Invoke("[item] 失败原因：" + failReason);
			}
			return null;
		}
		catch (Exception ex)
		{
			log?.Invoke("[item] 异常: " + ex.Message);
			return null;
		}
	}

	/// <summary>
	/// 查询账号下可用的 item（网络游戏）列表，返回 (entityId, iid, name) 元组，供诊断/展示。
	/// </summary>
	public static async Task<List<(string EntityId, string Iid, string Name)>> ListAvailableItemsAsync(string userId, string userToken, Action<string>? log, CancellationToken ct)
	{
		List<(string, string, string)> list = new List<(string, string, string)>();
		if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userToken))
		{
			return list;
		}
		try
		{
			// 与 Echoo 脱盒一致的查询体；带分页遍历
			for (int offset = 0; offset < 500; offset += 50)
			{
				string body = "{\"item_type\":1,\"sort_type\":8,\"offset\":" + offset + ",\"length\":50,\"with_title_image\":1,\"with_mc_versions\":1}";
				var (status, text) = await PostAsync(QueryPath, body, userId, userToken, ct);
				log?.Invoke($"[item] POST {QueryPath} offset={offset} => HTTP {status} {Truncate(text)}");
				int added = 0;
				using (JsonDocument doc = JsonDocument.Parse(text))
				{
					JsonElement root = doc.RootElement;
					int code = TryGetInt(root, "code");
					if (code != 0)
					{
						log?.Invoke("[item] 列表查询返回 code=" + code + " " + (TryGetString(root, "message") ?? ""));
						break;
					}
					if (!root.TryGetProperty("entity", out JsonElement entity))
					{
						break;
					}
					foreach (JsonElement item in EnumerateEntities(entity))
					{
						string eid = TryGetString(item, "entity_id") ?? TryGetString(item, "game_id") ?? "";
						string iid = TryGetString(item, "iid") ?? TryGetString(item, "item_id") ?? "";
						string name = TryGetString(item, "name") ?? "";
						if (!string.IsNullOrWhiteSpace(eid) || !string.IsNullOrWhiteSpace(name))
						{
							list.Add((eid, iid, name));
							added++;
						}
					}
				}
				if (added < 50)
				{
					break;
				}
			}
		}
		catch (Exception ex)
		{
			log?.Invoke("[item] 列表查询异常: " + ex.Message);
		}
		return list;
	}

	/// <summary>在可用 item 列表里按 entity_id 匹配 gameId，返回其 iid（找不到返回 null）。</summary>
	private static async Task<string?> QueryItemIdForGameAsync(long gameId, string userId, string userToken, Action<string>? log, CancellationToken ct)
	{
		List<(string EntityId, string Iid, string Name)> list = await ListAvailableItemsAsync(userId, userToken, null, ct);
		foreach (var (entityId, iid, name) in list)
		{
			if (entityId == gameId.ToString())
			{
				log?.Invoke($"[item] 列表命中: {name} entity_id={entityId} iid={iid}");
				return iid;
			}
		}
		if (list.Count > 0)
		{
			log?.Invoke("[item] 可用 item 共 " + list.Count + " 个，未包含 gameId=" + gameId);
		}
		return null;
	}

	private static async Task<(int Status, string Body)> PostAsync(string path, string body, string userId, string userToken, CancellationToken ct)
	{
		using HttpClient http = new HttpClient();
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, ApiBase + path);
		request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
		request.Content = new StringContent(body, Encoding.UTF8, "application/json");
		foreach (KeyValuePair<string, string> item in NeteaseCipher.ComputeHttpRequestToken(path, body, userId, userToken))
		{
			request.Headers.TryAddWithoutValidation(item.Key, item.Value);
		}
		using HttpResponseMessage response = await http.SendAsync(request, ct);
		return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
	}

	/// <summary>从响应 JSON 中提取 item 包下载地址。优先匹配 item_*.7z 形态，其次任意 http(s) 链接。</summary>
	private static string? ExtractItemUrl(string json, out string? failReason)
	{
		failReason = null;
		if (string.IsNullOrWhiteSpace(json))
		{
			failReason = "空响应";
			return null;
		}
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;
			int code = TryGetInt(root, "code");
			if (code != 0)
			{
				failReason = "code=" + code + " " + (TryGetString(root, "message") ?? TryGetString(root, "details") ?? "");
				if (string.IsNullOrWhiteSpace(failReason) || failReason == "code=" + code)
				{
					failReason = "接口返回 code=" + code;
				}
			}
			// 先找标准 item 包名
			string? byName = FindString(root, (s) => s.Contains("item_") && s.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));
			if (byName != null)
			{
				return NormalizeUrl(byName);
			}
			// 再找 url / res_url 字段
			foreach (string key in new string[3] { "res_url", "url", "download_url" })
			{
				string? v = FindByKey(root, key);
				if (!string.IsNullOrWhiteSpace(v) && v.StartsWith("http", StringComparison.OrdinalIgnoreCase))
				{
					return NormalizeUrl(v);
				}
			}
			// 最后找任意 http(s) 链接
			string? anyHttp = FindString(root, (s) => s.StartsWith("http", StringComparison.OrdinalIgnoreCase));
			if (anyHttp != null)
			{
				return NormalizeUrl(anyHttp);
			}
			if (failReason == null)
			{
				failReason = "响应中未找到下载地址字段";
			}
			return null;
		}
		catch (Exception ex)
		{
			failReason = "响应解析失败: " + ex.Message;
			return null;
		}
	}

	/// <summary>CDN 地址可能是相对路径，补齐为绝对 URL。
	/// 实测 item 资源包落在 x19.gph.netease.com（gdl 是 component 组件包主机，item 包在 gdl 上 404）。</summary>
	private static string NormalizeUrl(string url)
	{
		if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			return url;
		}
		if (url.StartsWith("/"))
		{
			return "https://x19.gph.netease.com" + url;
		}
		return "https://x19.gph.netease.com/" + url;
	}

	private static IEnumerable<JsonElement> EnumerateEntities(JsonElement entity)
	{
		if (entity.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in entity.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object)
				{
					yield return item;
				}
			}
		}
		else if (entity.ValueKind == JsonValueKind.Object)
		{
			// entity 可能是 { "entities": [...] } 或 { "list": [...] } 包裹
			foreach (string key in new string[3] { "entities", "list", "items" })
			{
				if (entity.TryGetProperty(key, out JsonElement sub) && sub.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement item in sub.EnumerateArray())
					{
						if (item.ValueKind == JsonValueKind.Object)
						{
							yield return item;
						}
					}
				}
			}
		}
	}

	private static string? FindByKey(JsonElement root, string key)
	{
		string? found = null;
		Visit(root, delegate (JsonElement el)
		{
			if (found != null || el.ValueKind != JsonValueKind.Object)
			{
				return;
			}
			if (el.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String)
			{
				string s = v.GetString() ?? "";
				if (!string.IsNullOrWhiteSpace(s))
				{
					found = s;
				}
			}
		});
		return found;
	}

	private static string? FindString(JsonElement root, Func<string, bool> predicate)
	{
		string? found = null;
		Visit(root, delegate (JsonElement el)
		{
			if (found != null || el.ValueKind != JsonValueKind.String)
			{
				return;
			}
			string s = el.GetString() ?? "";
			if (!string.IsNullOrWhiteSpace(s) && predicate(s))
			{
				found = s;
			}
		});
		return found;
	}

	private static void Visit(JsonElement el, Action<JsonElement> action)
	{
		action(el);
		switch (el.ValueKind)
		{
		case JsonValueKind.Object:
			foreach (JsonProperty item in el.EnumerateObject())
			{
				Visit(item.Value, action);
			}
			break;
		case JsonValueKind.Array:
			foreach (JsonElement item2 in el.EnumerateArray())
			{
				Visit(item2, action);
			}
			break;
		}
	}

	private static int TryGetInt(JsonElement el, string key)
	{
		try
		{
			if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out JsonElement v))
			{
				if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
				{
					return n;
				}
				if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int n2))
				{
					return n2;
				}
			}
		}
		catch
		{
		}
		return -1;
	}

	private static string? TryGetString(JsonElement el, string key)
	{
		try
		{
			if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String)
			{
				return v.GetString();
			}
		}
		catch
		{
		}
		return null;
	}

	private static string Truncate(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		return (s.Length > 300) ? (s.Substring(0, 300) + "…") : s;
	}
}

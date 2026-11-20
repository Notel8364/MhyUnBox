using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

public static class NeteaseRoleService
{
	private sealed class EntityNetGameRequest
	{
		[JsonPropertyName("available_mc_versions")]
		public string[]? AvailableMcVersions { get; set; }

		[JsonPropertyName("item_type")]
		public int ItemType { get; set; }

		[JsonPropertyName("length")]
		public int Length { get; set; }

		[JsonPropertyName("offset")]
		public int Offset { get; set; }

		[JsonPropertyName("master_type_id")]
		public string MasterTypeId { get; set; } = "";

		[JsonPropertyName("secondary_type_id")]
		public string SecondaryTypeId { get; set; } = "";
	}

	private sealed class EntityQueryGameCharacters
	{
		[JsonPropertyName("offset")]
		public int Offset { get; set; }

		[JsonPropertyName("length")]
		public int Length { get; set; } = 10;

		[JsonPropertyName("user_id")]
		public string UserId { get; set; } = "";

		[JsonPropertyName("game_id")]
		public string GameId { get; set; } = "";

		[JsonPropertyName("game_type")]
		public string GameType { get; set; } = "2";
	}

	private sealed class NetGameItem
	{
		[JsonPropertyName("entity_id")]
		public string EntityId { get; set; } = "";

		[JsonPropertyName("name")]
		public string Name { get; set; } = "";
	}

	private sealed class EntityGameCharacter
	{
		[JsonPropertyName("entity_id")]
		public string EntityId { get; set; } = "";

		[JsonPropertyName("name")]
		public string Name { get; set; } = "";

		[JsonPropertyName("expire_time")]
		public long ExpireTime { get; set; }
	}

	/// <summary>角色条目（含删除冷静期信息，供角色管理弹窗使用）。</summary>
	public sealed class RoleInfo
	{
		public string EntityId { get; set; } = "";

		public string Name { get; set; } = "";

		public long ExpireTime { get; set; }

		/// <summary>pre-delete 是 30 天软删除：expire_time 大于当前时间即处于删除冷静期，可撤回。</summary>
		public bool IsPendingDelete => ExpireTime > 0;

		public string DisplayName => IsPendingDelete ? Name + "（删除中·可撤回）" : Name;
	}

	// 抓包确认：POST /game-character 请求体（创建角色），game_type 为数字、entity_id 固定 "0"、create_time 为哨兵值 555555
	private sealed class EntityCreateGameCharacter
	{
		[JsonPropertyName("entity_id")]
		public string EntityId { get; set; } = "0";

		[JsonPropertyName("game_id")]
		public string GameId { get; set; } = "";

		[JsonPropertyName("game_type")]
		public int GameType { get; set; } = 2;

		[JsonPropertyName("user_id")]
		public string UserId { get; set; } = "";

		[JsonPropertyName("name")]
		public string Name { get; set; } = "";

		[JsonPropertyName("create_time")]
		public long CreateTime { get; set; } = 555555L;
	}

	// 抓包确认：POST /game-character/pre-delete 请求体（删除角色），仅需角色 entity_id
	private sealed class EntityDeleteGameCharacter
	{
		[JsonPropertyName("entity_id")]
		public string EntityId { get; set; } = "";
	}

	private sealed class Entities<T>
	{
		[JsonPropertyName("code")]
		public int Code { get; set; }

		[JsonPropertyName("entities")]
		public T[] Data { get; set; } = Array.Empty<T>();
	}

	private const string ApiBase = "https://x19apigatewayobt.nie.netease.com";

	private static readonly string[] MasterTypeCandidates = new string[2] { "2", "11" };

	private static readonly string[] GameTypeCandidates = new string[4] { "2", "10", "8", "7" };

	private static readonly JsonSerializerOptions Relaxed = new JsonSerializerOptions
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public static async Task<(bool Ok, Dictionary<string, List<string>> ServerRoles, string Message)> CaptureRolesAsync(string userId, string userToken, IEnumerable<string> serverNames, CancellationToken ct = default(CancellationToken))
	{
		Dictionary<string, List<string>> serverRoles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		int total = 0;
		if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userToken))
		{
			return (Ok: false, ServerRoles: serverRoles, Message: "缺少游戏会话（EntityId/Token），无法查询角色");
		}
		try
		{
			List<string> diag = new List<string>();
			foreach (string serverName in serverNames)
			{
				if (total >= 60)
				{
					break;
				}
				ServerPreset serverPreset = ServerPresets.Find(serverName);
				string gameId;
				if (serverPreset != null && !serverPreset.IsPending && serverPreset.GameId > 0)
				{
					gameId = serverPreset.GameId.ToString();
					LogHelper.Log("[角色查询] 服务器「" + serverName + "」命中预设 GameId=" + gameId);
				}
				else
				{
					gameId = await ResolveServerIdAsync(userId, userToken, serverName, ct);
					LogHelper.Log("[角色查询] 服务器「" + serverName + "」解析结果: " + (string.IsNullOrWhiteSpace(gameId) ? "未找到" : gameId));
				}
				diag.Add(serverName + "=" + (string.IsNullOrWhiteSpace(gameId) ? "未找到" : gameId));
				if (string.IsNullOrWhiteSpace(gameId))
				{
					continue;
				}
				List<string> names = new List<string>();
				HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
				string[] gameTypeCandidates = GameTypeCandidates;
				string[] array = gameTypeCandidates;
				string[] array2 = array;
				foreach (string gameType in array2)
				{
					if (names.Count >= 20)
					{
						break;
					}
					List<string> list = await QueryCharactersAsync(userId, userToken, gameId, gameType, ct);
					LogHelper.Log($"[角色查询] 服务器「{serverName}」game_id={gameId} game_type={gameType} => {((list.Count == 0) ? "空" : string.Join(" / ", list))}");
					foreach (string item in list)
					{
						if (!string.IsNullOrWhiteSpace(item) && seen.Add(item))
						{
							names.Add(item);
							total++;
						}
					}
				}
				if (names.Count > 0)
				{
					names.Sort(StringComparer.Ordinal);
					serverRoles[serverName] = names.Take(20).ToList();
				}
			}
			if (serverRoles.Values.Sum((List<string> v) => v.Count) > 0)
			{
				string text = string.Join("；", serverRoles.Select((KeyValuePair<string, List<string>> kv) => kv.Key + ": " + string.Join(" / ", kv.Value)));
				return (Ok: true, ServerRoles: serverRoles, Message: "已抓取角色名：" + text);
			}
			return (Ok: false, ServerRoles: serverRoles, Message: "未找到该账号的角色（" + string.Join("；", diag) + "）");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[角色查询] 抓取失败");
			return (Ok: false, ServerRoles: serverRoles, Message: "角色查询异常：" + ex.Message);
		}
	}

	public static async Task<string?> ResolveServerIdAsync(string userId, string userToken, string serverName, CancellationToken ct)
	{
		string[] masterTypeCandidates = MasterTypeCandidates;
		string[] array = masterTypeCandidates;
		string[] array2 = array;
		foreach (string masterType in array2)
		{
			for (int offset = 0; offset < 1000; offset += 100)
			{
				List<NetGameItem> list = await QueryAvailableNetGamesAsync(userId, userToken, offset, 100, masterType, ct);
				if (list.Count == 0)
				{
					break;
				}
				foreach (NetGameItem item in list)
				{
					if (IsNameMatch(item.Name, serverName))
					{
						return item.EntityId;
					}
				}
				if (list.Count < 100)
				{
					break;
				}
			}
		}
		return null;
	}

	private static bool IsNameMatch(string? itemName, string serverName)
	{
		if (string.IsNullOrWhiteSpace(itemName))
		{
			return false;
		}
		string text = itemName.Trim();
		string text2 = serverName.Trim();
		if (string.IsNullOrEmpty(text2))
		{
			return false;
		}
		if (!string.Equals(text, text2, StringComparison.OrdinalIgnoreCase))
		{
			return text.Contains(text2, StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static async Task<List<NetGameItem>> QueryAvailableNetGamesAsync(string userId, string userToken, int offset, int length, string masterTypeId, CancellationToken ct)
	{
		string body = JsonSerializer.Serialize(new EntityNetGameRequest
		{
			ItemType = 1,
			Length = length,
			Offset = offset,
			MasterTypeId = masterTypeId,
			SecondaryTypeId = ""
		}, Relaxed);
		var (value, text) = await PostAsync("/item/query/available", body, userId, userToken, ct);
		var (num, value2) = ParseCode(text);
		LogHelper.Log($"[角色查询] POST /item/query/available master_type={masterTypeId} offset={offset} => HTTP {value} code={num} msg={value2} body={Truncate(text)}");
		if (num != 0)
		{
			return new List<NetGameItem>();
		}
		try
		{
			return JsonSerializer.Deserialize<Entities<NetGameItem>>(text)?.Data?.ToList() ?? new List<NetGameItem>();
		}
		catch
		{
			return new List<NetGameItem>();
		}
	}

	private static async Task<List<string>> QueryCharactersAsync(string userId, string userToken, string gameId, string gameType, CancellationToken ct)
	{
		string body = JsonSerializer.Serialize(new EntityQueryGameCharacters
		{
			Offset = 0,
			Length = 10,
			UserId = userId,
			GameId = gameId,
			GameType = gameType
		}, Relaxed);
		var (value, text) = await PostAsync("/game-character/query/user-game-characters", body, userId, userToken, ct);
		var (num, value2) = ParseCode(text);
		LogHelper.Log($"[角色查询] POST /game-character/query/user-game-characters game_id={gameId} game_type={gameType} => HTTP {value} code={num} msg={value2} body={Truncate(text)}");
		if (num != 0)
		{
			return new List<string>();
		}
		try
		{
			return JsonSerializer.Deserialize<Entities<EntityGameCharacter>>(text)?.Data?.Select((EntityGameCharacter c) => c.Name).ToList() ?? new List<string>();
		}
		catch
		{
			return new List<string>();
		}
	}

	/// <summary>会话被顶（code=22「您的帐号在另一处登录」）：同账号单点登录，官方启动器在线/重新登录会把本应用的 token 顶掉。
	/// 页面层捕获后可走 AccountAutoLoginService.ReloginAsync 自动换新会话并重试一次。</summary>
	public sealed class NeteaseSessionKickedException : Exception
	{
		public int Code { get; }

		public NeteaseSessionKickedException(string message, int code = 22) : base(message)
		{
			Code = code;
		}
	}

	private static void ThrowIfSessionKicked(int code, string message)
	{
		if (code == 22)
		{
			throw new NeteaseSessionKickedException(string.IsNullOrWhiteSpace(message) ? "您的帐号在另一处登录" : message);
		}
	}

	/// <summary>创建角色：POST /game-character（抓包逆向自官方启动器 1.15.34）</summary>
	public static async Task<(bool Ok, string Message, string? EntityId)> CreateRoleAsync(string userId, string userToken, string gameId, string name, int gameType = 2, CancellationToken ct = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userToken))
		{
			return (Ok: false, Message: "缺少游戏会话（EntityId/Token），无法创建角色", EntityId: null);
		}
		if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(name))
		{
			return (Ok: false, Message: "服务器 ID 或角色名为空", EntityId: null);
		}
		try
		{
			string body = JsonSerializer.Serialize(new EntityCreateGameCharacter
			{
				GameId = gameId,
				GameType = gameType,
				UserId = userId,
				Name = name
			}, Relaxed);
			var (status, text) = await PostAsync("/game-character", body, userId, userToken, ct);
			var (code, message) = ParseCode(text);
			LogHelper.Log($"[角色创建] POST /game-character game_id={gameId} name={name} => HTTP {status} code={code} msg={message} body={Truncate(text)}");
			string? entityId = null;
			try
			{
				using JsonDocument doc = JsonDocument.Parse(text);
				if (doc.RootElement.TryGetProperty("entity", out var ent) && ent.TryGetProperty("entity_id", out var eid))
				{
					entityId = eid.ValueKind == JsonValueKind.String ? eid.GetString() : eid.ToString();
				}
			}
			catch
			{
			}
			if (code == 0)
			{
				return (Ok: true, Message: "角色「" + name + "」创建成功" + (entityId != null ? "（entity_id=" + entityId + "）" : ""), EntityId: entityId);
			}
			ThrowIfSessionKicked(code, message);
			return (Ok: false, Message: "创建角色失败 code=" + code + (string.IsNullOrEmpty(message) ? "" : "：" + message), EntityId: null);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[角色创建] 创建失败");
			return (Ok: false, Message: "创建角色异常：" + ex.Message, EntityId: null);
		}
	}

	/// <summary>删除角色：POST /game-character/pre-delete（抓包逆向自官方启动器 1.15.34）</summary>
	public static async Task<(bool Ok, string Message)> DeleteRoleAsync(string userId, string userToken, string characterEntityId, CancellationToken ct = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userToken))
		{
			return (Ok: false, Message: "缺少游戏会话（EntityId/Token），无法删除角色");
		}
		if (string.IsNullOrWhiteSpace(characterEntityId))
		{
			return (Ok: false, Message: "角色 entity_id 为空");
		}
		try
		{
			string body = JsonSerializer.Serialize(new EntityDeleteGameCharacter
			{
				EntityId = characterEntityId
			}, Relaxed);
			var (status, text) = await PostAsync("/game-character/pre-delete", body, userId, userToken, ct);
			var (code, message) = ParseCode(text);
			LogHelper.Log($"[角色删除] POST /game-character/pre-delete entity_id={characterEntityId} => HTTP {status} code={code} msg={message} body={Truncate(text)}");
			if (code == 0)
			{
				return (Ok: true, Message: "角色删除成功");
			}
			ThrowIfSessionKicked(code, message);
			return (Ok: false, Message: "删除角色失败 code=" + code + (string.IsNullOrEmpty(message) ? "" : "：" + message));
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[角色删除] 删除失败");
			return (Ok: false, Message: "删除角色异常：" + ex.Message);
		}
	}

	/// <summary>取消删除角色：POST /game-character/cancel-pre-delete（抓包逆向自官方启动器 1.15.34）。
	/// pre-delete 是 30 天冷静期软删除，cancel-pre-delete 将 expire_time 归零恢复角色。</summary>
	public static async Task<(bool Ok, string Message)> CancelDeleteRoleAsync(string userId, string userToken, string characterEntityId, CancellationToken ct = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userToken))
		{
			return (Ok: false, Message: "缺少游戏会话（EntityId/Token），无法取消删除");
		}
		if (string.IsNullOrWhiteSpace(characterEntityId))
		{
			return (Ok: false, Message: "角色 entity_id 为空");
		}
		try
		{
			string body = JsonSerializer.Serialize(new EntityDeleteGameCharacter
			{
				EntityId = characterEntityId
			}, Relaxed);
			var (status, text) = await PostAsync("/game-character/cancel-pre-delete", body, userId, userToken, ct);
			var (code, message) = ParseCode(text);
			LogHelper.Log($"[角色删除] POST /game-character/cancel-pre-delete entity_id={characterEntityId} => HTTP {status} code={code} msg={message} body={Truncate(text)}");
			if (code == 0)
			{
				return (Ok: true, Message: "已取消删除，角色恢复");
			}
			ThrowIfSessionKicked(code, message);
			return (Ok: false, Message: "取消删除失败 code=" + code + (string.IsNullOrEmpty(message) ? "" : "：" + message));
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[角色删除] 取消删除失败");
			return (Ok: false, Message: "取消删除异常：" + ex.Message);
		}
	}

	/// <summary>查询服务器下全部角色（含 entity_id 与删除冷静期状态，供角色管理弹窗使用）。</summary>
	public static async Task<List<RoleInfo>> QueryRolesAsync(string userId, string userToken, string gameId, CancellationToken ct = default(CancellationToken))
	{
		List<RoleInfo> roles = new List<RoleInfo>();
		if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userToken) || string.IsNullOrWhiteSpace(gameId))
		{
			return roles;
		}
		// 与 CaptureRolesAsync 同款：game_type 依次尝试已知取值，取到非空即止（正常联机服务器为 "2"）
		int lastCode = 0;
		string? lastMessage = null;
		bool anySucceeded = false;
		foreach (string gameType in GameTypeCandidates)
		{
			string body = JsonSerializer.Serialize(new EntityQueryGameCharacters
			{
				Offset = 0,
				Length = 30,
				UserId = userId,
				GameId = gameId,
				GameType = gameType
			}, Relaxed);
			var (status, text) = await PostAsync("/game-character/query/user-game-characters", body, userId, userToken, ct);
			var (code, message) = ParseCode(text);
			LogHelper.Log($"[角色管理] POST /game-character/query/user-game-characters game_id={gameId} game_type={gameType} => HTTP {status} code={code} msg={message} body={Truncate(text)}");
			if (code == 22)
			{
				// 会话被顶：继续换 game_type 试没有意义，直接抛出让页面层走自动重登
				ThrowIfSessionKicked(code, message);
			}
			if (code != 0)
			{
				lastCode = code;
				lastMessage = message;
				continue;
			}
			anySucceeded = true;
			try
			{
				List<RoleInfo> page = JsonSerializer.Deserialize<Entities<EntityGameCharacter>>(text)?.Data?
					.Where((EntityGameCharacter c) => !string.IsNullOrWhiteSpace(c.Name))
					.Select((EntityGameCharacter c) => new RoleInfo
					{
						EntityId = c.EntityId,
						Name = c.Name,
						ExpireTime = c.ExpireTime
					}).ToList() ?? new List<RoleInfo>();
				if (page.Count > 0)
				{
					roles.AddRange(page);
					break;
				}
			}
			catch
			{
			}
		}
		// 2026-11-20 修复：所有 game_type 都失败时不能再静默返回空列表（弹窗会误报「还没有角色」）
		if (!anySucceeded)
		{
			throw new InvalidOperationException("角色查询失败 code=" + lastCode + (string.IsNullOrEmpty(lastMessage) ? "" : "：" + lastMessage));
		}
		return roles;
	}

	/// <summary>查询服务器下角色（含 entity_id，用于删除）</summary>
	public static async Task<List<(string EntityId, string Name)>> QueryCharacterEntitiesAsync(string userId, string userToken, string gameId, string gameType, CancellationToken ct)
	{
		string body = JsonSerializer.Serialize(new EntityQueryGameCharacters
		{
			Offset = 0,
			Length = 10,
			UserId = userId,
			GameId = gameId,
			GameType = gameType
		}, Relaxed);
		var (value, text) = await PostAsync("/game-character/query/user-game-characters", body, userId, userToken, ct);
		var (num, value2) = ParseCode(text);
		LogHelper.Log($"[角色查询] POST /game-character/query/user-game-characters game_id={gameId} game_type={gameType} => HTTP {value} code={num} msg={value2} body={Truncate(text)}");
		if (num != 0)
		{
			return new List<(string, string)>();
		}
		try
		{
			return JsonSerializer.Deserialize<Entities<EntityGameCharacter>>(text)?.Data?
				.Select((EntityGameCharacter c) => (c.EntityId, c.Name))
				.ToList() ?? new List<(string, string)>();
		}
		catch
		{
			return new List<(string, string)>();
		}
	}

	private static async Task<(int Status, string Body)> PostAsync(string path, string body, string userId, string userToken, CancellationToken ct)
	{
		// 角色拉取是登录成功后的第一步，必须直连（用户挂加速器时系统代理会劫持网易 API）
		using HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false });
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://x19apigatewayobt.nie.netease.com" + path);
		request.Content = new StringContent(body, Encoding.UTF8, "application/json");
		foreach (KeyValuePair<string, string> item2 in NeteaseCipher.ComputeHttpRequestToken(path, body, userId, userToken))
		{
			request.Headers.TryAddWithoutValidation(item2.Key, item2.Value);
		}
		using HttpResponseMessage response = await http.SendAsync(request, ct);
		return new ValueTuple<int, string>(item2: await response.Content.ReadAsStringAsync(ct), item1: (int)response.StatusCode);
	}

	private static (int Code, string? Message) ParseCode(string text)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(text);
			if (jsonDocument.RootElement.TryGetProperty("code", out var value))
			{
				int value2 = 0;
				if (value.ValueKind == JsonValueKind.Number)
				{
					value.TryGetInt32(out value2);
				}
				else if (value.ValueKind == JsonValueKind.String)
				{
					int.TryParse(value.GetString(), out value2);
				}
				string item = null;
				if (jsonDocument.RootElement.TryGetProperty("message", out var value3) && value3.ValueKind == JsonValueKind.String)
				{
					item = value3.GetString();
				}
				return (Code: value2, Message: item);
			}
		}
		catch
		{
		}
		return (Code: -1, Message: null);
	}

	private static string Truncate(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}
		if (text.Length > 2000)
		{
			return text.Substring(0, 2000) + "...(truncated)";
		}
		return text;
	}
}

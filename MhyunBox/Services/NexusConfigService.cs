using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MhyunBox.Services;

/// <summary>
/// MythosNexus 配置（config/mythosnexus.json）读写：账号花名册、分组/备注/自动重连/代理绑定、
/// 代理池与 Tick 调度参数。只改动对应键，其余键（主题、按键绑定、同步组等）原样保留。
/// 模组在运行期把配置读进内存并整体回写，因此修改应在本局游戏未运行时进行。
/// </summary>
public static class NexusConfigService
{
	public sealed class Account
	{
		public string User = "";
		public string Host = "";
		public int Port = 25565;
		public string Group = "默认";
		public int Proxy = -1;
		public string Note = "";
		public bool AutoReconnect;
		public bool LastOnline;

		public string Key => User + "@" + Host + ":" + Port;

		public string Display
		{
			get
			{
				var sb = new StringBuilder();
				sb.Append(User).Append(" @ ").Append(Host).Append(':').Append(Port);
				sb.Append("｜分组：").Append(string.IsNullOrWhiteSpace(Group) ? "默认" : Group);
				sb.Append(Proxy >= 0 ? "｜代理 #" + (Proxy + 1) : "｜直连");
				sb.Append(AutoReconnect ? "｜自动重连" : "｜不自动重连");
				if (LastOnline)
				{
					sb.Append("｜上次在线");
				}
				if (!string.IsNullOrWhiteSpace(Note))
				{
					sb.Append("｜").Append(Note);
				}
				return sb.ToString();
			}
		}
	}

	public sealed class Proxy
	{
		public string Type = "socks5";
		public string Host = "";
		public int Port = 1080;
		public string User = "";
		public string Pass = "";

		public string Display => Type + "://" + Host + ":" + Port + (string.IsNullOrWhiteSpace(User) ? "" : "（带认证）");
	}

	public sealed class Snapshot
	{
		public bool Exists;
		public List<Account> Accounts = new List<Account>();
		public List<Proxy> Proxies = new List<Proxy>();
		public bool AdaptiveTick = true;
		public int MaxCpuPct = 40;
		public int MinBandPct = 20;
		public int TickRatePathing = 100;
		public int TickRateIdle = 20;
		public int RejoinMinMs = 3000;
		public int RejoinMaxMs = 8000;
		public int ConnectDelayMs = 500;
		public float[] PriorityCurve = { 100f, 60f, 35f, 20f, 12f, 8f };
		public string CardMode = "WATERFALL";
		public string CardSort = "DEFAULT";
		public string CardDensity = "NORMAL";
		public string LiveViewSize = "320x180";
		public bool HdFont = true;
		/// <summary>程序写入：游戏画面自动挂载并显示这个账号的视角（空 = 不自动切换）。</summary>
		public string ViewAccount = "";
	}

	public static string ConfigPath(string gameDir)
	{
		return Path.Combine(gameDir ?? "", "config", "mythosnexus.json");
	}

	public static Snapshot Load(string gameDir)
	{
		var snapshot = new Snapshot();
		string path = ConfigPath(gameDir);
		if (string.IsNullOrWhiteSpace(gameDir) || !File.Exists(path))
		{
			return snapshot;
		}
		try
		{
			JsonObject root = ParseRoot(File.ReadAllText(path));
			snapshot.Exists = true;

			if (root["proxies"] is JsonArray proxies)
			{
				foreach (JsonNode? node in proxies)
				{
					if (node is not JsonObject p)
					{
						continue;
					}
					snapshot.Proxies.Add(new Proxy
					{
						Type = Str(p, "type", "socks5"),
						Host = Str(p, "host"),
						Port = Int(p, "port", 1080),
						User = Str(p, "user"),
						Pass = Str(p, "pass")
					});
				}
			}

			var lastOnline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (root["lastOnline"] is JsonArray online)
			{
				foreach (JsonNode? node in online)
				{
					if (node is JsonValue v && v.TryGetValue<string>(out string s) && !string.IsNullOrWhiteSpace(s))
					{
						lastOnline.Add(s);
					}
				}
			}

			if (root["accounts"] is JsonArray accounts)
			{
				foreach (JsonNode? node in accounts)
				{
					if (node is not JsonObject a)
					{
						continue;
					}
					string user = Str(a, "username");
					string host = Str(a, "host");
					if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(host))
					{
						continue;
					}
					// "null"/空地址是早期绑定写坏留下的占位账号，不参与花名册
					if (string.Equals(host.Trim(), "null", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					var account = new Account
					{
						User = user,
						Host = host,
						Port = Int(a, "port", 25565)
					};
					account.LastOnline = lastOnline.Contains(account.Key);
					snapshot.Accounts.Add(account);
				}
			}

			if (root["bindings"] is JsonObject bindings)
			{
				foreach (Account account in snapshot.Accounts)
				{
					string? key = FindProperty(bindings, account.Key);
					if (key == null || bindings[key] is not JsonObject b)
					{
						continue;
					}
					account.Group = Str(b, "group", "默认");
					account.Proxy = Int(b, "proxy", -1);
					account.Note = Str(b, "note");
					account.AutoReconnect = Bool(b, "autoReconnect", false);
				}
			}

			snapshot.AdaptiveTick = Bool(root, "adaptiveTick", true);
			snapshot.MaxCpuPct = Int(root, "maxCpuPct", 40);
			snapshot.MinBandPct = Int(root, "minBandPct", 20);
			snapshot.TickRatePathing = Int(root, "tickRatePathing", 100);
			snapshot.TickRateIdle = Int(root, "tickRateIdle", 20);
			snapshot.RejoinMinMs = Int(root, "rejoinMinMs", 3000);
			snapshot.RejoinMaxMs = Int(root, "rejoinMaxMs", 8000);
			snapshot.ConnectDelayMs = Int(root, "connectDelayMs", 500);
			snapshot.CardMode = Str(root, "cardMode", "WATERFALL");
			snapshot.CardSort = Str(root, "cardSort", "DEFAULT");
			snapshot.CardDensity = Str(root, "cardDensity", "NORMAL");
			snapshot.LiveViewSize = Str(root, "liveViewSize", "320x180");
			snapshot.HdFont = Bool(root, "hdFont", true);
			snapshot.ViewAccount = Str(root, "viewAccount");

			if (root["priorityCurve"] is JsonArray curve && curve.Count >= 6)
			{
				var values = new List<float>();
				foreach (JsonNode? node in curve)
				{
					if (node is JsonValue v && v.TryGetValue<float>(out float f))
					{
						values.Add(f);
					}
				}
				if (values.Count >= 6)
				{
					snapshot.PriorityCurve = values.Take(6).ToArray();
				}
			}
		}
		catch
		{
			// 配置损坏时按空配置展示，不覆盖原文件
		}
		return snapshot;
	}

	/// <summary>
	/// 清理「孤儿机器人绑定」：note = MhyunBox 机器人号代理、host 为本机回环、
	/// 端口不在 allowedPorts 内、且 accounts 里已没有对应账号条目的 binding。
	/// 这种条目不会直接挂机器人（没有账号），但会残留并在同键账号被重新创建时
	/// 把 autoReconnect=true 一起带回来；allowedPorts 为空 = 全部清理。
	/// 返回清理的条目数。
	/// </summary>
	public static int PruneOrphanMarkerBindings(string gameDir, IReadOnlySet<int>? allowedPorts)
	{
		if (string.IsNullOrWhiteSpace(gameDir) || !File.Exists(ConfigPath(gameDir)))
		{
			return 0;
		}
		try
		{
			JsonObject root = LoadRoot(gameDir, out bool existed);
			if (!existed)
			{
				return 0;
			}
			var accountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (root["accounts"] is JsonArray accounts)
			{
				foreach (JsonNode? node in accounts)
				{
					if (node is JsonObject a)
					{
						accountKeys.Add(KeyOf(a));
					}
				}
			}
			int removed = 0;
			if (root["bindings"] is JsonObject bindings)
			{
				foreach (KeyValuePair<string, JsonNode?> kv in bindings.ToList())
				{
					if (kv.Value is not JsonObject b
						|| !string.Equals(Str(b, "note"), "MhyunBox 机器人号代理", StringComparison.Ordinal))
					{
						continue;
					}
					string key = kv.Key;
					int at = key.IndexOf('@');
					int colon = key.LastIndexOf(':');
					if (at <= 0 || colon <= at || !int.TryParse(key.Substring(colon + 1), out int port))
					{
						continue;
					}
					string host = key.Substring(at + 1, colon - at - 1);
					bool loopback = string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
					if (!loopback || (allowedPorts != null && allowedPorts.Contains(port)) || accountKeys.Contains(key))
					{
						continue;
					}
					bindings.Remove(key);
					removed++;
				}
			}
			if (removed > 0)
			{
				SaveRoot(gameDir, root);
			}
			return removed;
		}
		catch
		{
			return 0;
		}
	}

	/// <summary>
	/// 一次写清机器人账号：清理同账号旧端口条目 + 写入账号/绑定 + 标记 lastOnline，单次读改写落盘。
	/// 之前拆成三次独立写，每次都 bump 文件 mtime；游戏进程在窗口内保存配置时（500ms 合并写）
	/// 可能读不到最后一次写入，把刚写的账号覆盖掉 —— 表现为「启动机器人代理后客户端挂不上，重启游戏才好」。
	/// </summary>
	public static void WriteBotAccount(string gameDir, string user, int port, string group, string note)
	{
		string u = (user ?? "").Trim();
		if (string.IsNullOrWhiteSpace(gameDir) || u.Length == 0 || port <= 0)
		{
			return;
		}
		string g = string.IsNullOrWhiteSpace(group) ? "默认" : group.Trim();
		port = Math.Max(1, Math.Min(65535, port));
		JsonObject root = LoadRoot(gameDir, out _);
		string loopHost = "127.0.0.1";
		string key = u + "@" + loopHost + ":" + port;

		// 1) 清理同账号其它本机端口的旧账号 / 绑定 / 在线记录（端口不是写死的，旧端口必须清掉）。
		if (root["accounts"] is JsonArray accounts)
		{
			for (int i = accounts.Count - 1; i >= 0; i--)
			{
				if (accounts[i] is not JsonObject a)
				{
					continue;
				}
				string host = Str(a, "host");
				bool loopback = string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
				if (loopback && Int(a, "port", 25565) != port
					&& string.Equals(Str(a, "username"), u, StringComparison.OrdinalIgnoreCase))
				{
					accounts.RemoveAt(i);
				}
			}
		}
		if (root["bindings"] is JsonObject oldBindings)
		{
			var stale = new List<string>();
			foreach (KeyValuePair<string, JsonNode?> kv in oldBindings)
			{
				string k = kv.Key;
				int at = k.IndexOf('@');
				int colon = k.LastIndexOf(':');
				if (at <= 0 || colon <= at || !int.TryParse(k.Substring(colon + 1), out int p) || p == port)
				{
					continue;
				}
				string who = k.Substring(0, at);
				string h = k.Substring(at + 1, colon - at - 1);
				bool loopback = string.Equals(h, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase);
				if (loopback && string.Equals(who, u, StringComparison.OrdinalIgnoreCase))
				{
					stale.Add(k);
				}
			}
			foreach (string k in stale)
			{
				oldBindings.Remove(k);
			}
		}
		if (root["lastOnline"] is JsonArray online)
		{
			for (int i = online.Count - 1; i >= 0; i--)
			{
				if (online[i] is JsonValue v && v.TryGetValue<string>(out string s)
					&& s.StartsWith(u + "@", StringComparison.OrdinalIgnoreCase))
				{
					int colon = s.LastIndexOf(':');
					if (colon > 0 && int.TryParse(s.Substring(colon + 1), out int p) && p != port
						&& (s.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) > 0
							|| s.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) > 0))
					{
						online.RemoveAt(i);
					}
				}
			}
		}

		// 2) 写入账号 + 绑定（保留已有绑定的其它字段，如重连间隔 / LiveView 尺寸）。
		JsonArray accountList = EnsureArray(root, "accounts");
		bool exists = accountList.OfType<JsonObject>().Any(a =>
			string.Equals(Str(a, "username"), u, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(Str(a, "host"), loopHost, StringComparison.OrdinalIgnoreCase)
			&& Int(a, "port", 25565) == port);
		if (!exists)
		{
			accountList.Add(new JsonObject
			{
				["username"] = u,
				["host"] = loopHost,
				["port"] = port
			});
		}
		JsonObject bindings = EnsureObject(root, "bindings");
		string? existingKey = FindProperty(bindings, key);
		JsonObject binding = new JsonObject();
		if (existingKey != null && bindings[existingKey] is JsonObject old)
		{
			foreach (KeyValuePair<string, JsonNode?> kv in old.ToList())
			{
				binding[kv.Key] = kv.Value?.DeepClone();
			}
			bindings.Remove(existingKey);
		}
		binding["group"] = g;
		binding["proxy"] = -1;
		binding["note"] = note ?? "";
		binding["autoReconnect"] = true;
		bindings[key] = binding;

		// 3) lastOnline 加上本账号，游戏启动即自动进服。
		JsonArray marks = EnsureArray(root, "lastOnline");
		if (!marks.Any(n => n is JsonValue v && v.TryGetValue<string>(out string s)
			&& string.Equals(s, key, StringComparison.OrdinalIgnoreCase)))
		{
			marks.Add(key);
		}
		SaveRoot(gameDir, root);
	}

	/// <summary>新增或更新一个账号（同名同服同端口视为同一条），同时写入分组/代理/自动重连/备注。</summary>
	public static void SaveAccount(string gameDir, string user, string host, int port, string group, int proxy, bool autoReconnect, string note)
	{
		string u = (user ?? "").Trim();
		string h = (host ?? "").Trim();
		string g = string.IsNullOrWhiteSpace(group) ? "默认" : group.Trim();
		if (u.Length == 0 || h.Length == 0)
		{
			throw new ArgumentException("账号名与服务器地址不能为空");
		}
		port = Math.Max(1, Math.Min(65535, port));

		JsonObject root = LoadRoot(gameDir, out _);
		JsonArray accounts = EnsureArray(root, "accounts");
		bool exists = accounts.OfType<JsonObject>().Any(a =>
			string.Equals(Str(a, "username"), u, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(Str(a, "host"), h, StringComparison.OrdinalIgnoreCase)
			&& Int(a, "port", 25565) == port);
		if (!exists)
		{
			accounts.Add(new JsonObject
			{
				["username"] = u,
				["host"] = h,
				["port"] = port
			});
		}

		string key = u + "@" + h + ":" + port;
		JsonObject bindings = EnsureObject(root, "bindings");
		string? existingKey = FindProperty(bindings, key);
		JsonObject binding = new JsonObject();
		// 保留该账号已有的其它绑定字段（重连间隔、LiveView 尺寸、单账号 tick 速率等）
		if (existingKey != null && bindings[existingKey] is JsonObject old)
		{
			foreach (KeyValuePair<string, JsonNode?> kv in old.ToList())
			{
				binding[kv.Key] = kv.Value?.DeepClone();
			}
			bindings.Remove(existingKey);
		}
		binding["group"] = g;
		binding["proxy"] = Math.Max(-1, proxy);
		binding["note"] = note ?? "";
		binding["autoReconnect"] = autoReconnect;
		bindings[key] = binding;
		SaveRoot(gameDir, root);
	}

	/// <summary>
	/// 把本机该角色的自动进区开关写成固定值（0 = 不自动进区）。
	/// 按「角色名 + 本机地址」匹配，不限定端口：客户端实际连的端口可能和旧配置里的端口不同，
	/// 而 AutoJoin 的 tickClient 在精确键查不到时会「按角色名回退扫描 accounts」，
	/// 只写当前端口会把同名的旧端口账号再次当成进区配置。binding 与 accounts 都要写。
	/// 只改已存在的条目，不新增账号/绑定。
	/// </summary>
	public static bool SetLocalAutoJoinZone(string gameDir, string userName, int port, int zone)
	{
		string name = (userName ?? "").Trim();
		if (string.IsNullOrWhiteSpace(gameDir) || name.Length == 0 || port <= 0)
		{
			return false;
		}
		int value = Math.Max(0, Math.Min(88, zone));
		JsonObject root = LoadRoot(gameDir, out _);
		bool changed = false;
		if (root["accounts"] is JsonArray accounts)
		{
			foreach (JsonNode? node in accounts)
			{
				if (node is not JsonObject a
					|| !string.Equals(Str(a, "username"), name, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				string host = Str(a, "host");
				if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				string? key = FindProperty(a, "autoJoinZone") ?? "autoJoinZone";
				if (Int(a, "autoJoinZone", int.MinValue) != value)
				{
					a[key] = value;
					changed = true;
				}
			}
		}
		if (root["bindings"] is JsonObject bindings)
		{
			foreach (KeyValuePair<string, JsonNode?> kv in bindings.ToList())
			{
				if (kv.Value is not JsonObject b)
				{
					continue;
				}
				int at = kv.Key.IndexOf('@');
				int colon = kv.Key.LastIndexOf(':');
				if (at <= 0 || colon <= at)
				{
					continue;
				}
				if (!string.Equals(kv.Key.Substring(0, at), name, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				string host = kv.Key.Substring(at + 1, colon - at - 1);
				if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				string? field = FindProperty(b, "autoJoinZone") ?? "autoJoinZone";
				if (Int(b, "autoJoinZone", int.MinValue) != value)
				{
					b[field] = value;
					changed = true;
				}
			}
		}
		if (changed)
		{
			SaveRoot(gameDir, root);
		}
		return changed;
	}

	/// <summary>
	/// 把账号键写入 / 移出 lastOnline。MythosNexus 启动时按 lastOnline + autoReconnect 决定哪些角色自动进服。
	/// </summary>
	public static void MarkOnline(string gameDir, string user, string host, int port, bool online)
	{
		string u = (user ?? "").Trim();
		string h = (host ?? "").Trim();
		if (u.Length == 0 || h.Length == 0)
		{
			return;
		}
		string key = u + "@" + h + ":" + port;
		JsonObject root = LoadRoot(gameDir, out _);
		JsonArray arr = EnsureArray(root, "lastOnline");
		int index = -1;
		for (int i = 0; i < arr.Count; i++)
		{
			if (arr[i] is JsonValue v && v.TryGetValue<string>(out string s)
				&& string.Equals(s, key, StringComparison.OrdinalIgnoreCase))
			{
				index = i;
				break;
			}
		}
		if (online && index < 0)
		{
			arr.Add(key);
		}
		else if (!online && index >= 0)
		{
			arr.RemoveAt(index);
		}
		else
		{
			return;
		}
		SaveRoot(gameDir, root);
	}

	/// <summary>删除账号及其绑定、上次在线记录、人物同步组成员。</summary>
	public static void RemoveAccount(string gameDir, string key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return;
		}
		JsonObject root = LoadRoot(gameDir, out _);

		if (root["accounts"] is JsonArray accounts)
		{
			for (int i = accounts.Count - 1; i >= 0; i--)
			{
				if (accounts[i] is JsonObject a && string.Equals(KeyOf(a), key, StringComparison.OrdinalIgnoreCase))
				{
					accounts.RemoveAt(i);
				}
			}
		}
		if (root["bindings"] is JsonObject bindings)
		{
			string? existingKey = FindProperty(bindings, key);
			if (existingKey != null)
			{
				bindings.Remove(existingKey);
			}
		}
		if (root["lastOnline"] is JsonArray online)
		{
			for (int i = online.Count - 1; i >= 0; i--)
			{
				if (online[i] is JsonValue v && v.TryGetValue<string>(out string s) && string.Equals(s, key, StringComparison.OrdinalIgnoreCase))
				{
					online.RemoveAt(i);
				}
			}
		}
		if (root["syncGroups"] is JsonArray groups)
		{
			foreach (JsonNode? node in groups)
			{
				if (node is JsonObject grp && grp["members"] is JsonArray members)
				{
					for (int i = members.Count - 1; i >= 0; i--)
					{
						if (members[i] is JsonValue v && v.TryGetValue<string>(out string s) && string.Equals(s, key, StringComparison.OrdinalIgnoreCase))
						{
							members.RemoveAt(i);
						}
					}
				}
			}
		}
		SaveRoot(gameDir, root);
	}

	/// <summary>
	/// 移除同一账号在其它本机端口上的旧账号 / 绑定 / 在线记录。
	/// 端口并非写死（程序会按占用情况自动改绑），客户端里残留旧端口账号时
	/// 会一直重连一个没人监听的端口，表现为永远“连接被拒绝”。
	/// </summary>
	public static void RemoveLocalAccountVariants(string gameDir, string user, int keepPort)
	{
		string name = (user ?? "").Trim();
		if (string.IsNullOrWhiteSpace(gameDir) || name.Length == 0 || keepPort <= 0)
		{
			return;
		}
		JsonObject root = LoadRoot(gameDir, out _);
		bool changed = false;
		if (root["accounts"] is JsonArray accounts)
		{
			for (int i = accounts.Count - 1; i >= 0; i--)
			{
				if (accounts[i] is not JsonObject a)
				{
					continue;
				}
				string host = Str(a, "host");
				bool loopback = string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
				if (loopback && Int(a, "port", 25565) != keepPort
					&& string.Equals(Str(a, "username"), name, StringComparison.OrdinalIgnoreCase))
				{
					accounts.RemoveAt(i);
					changed = true;
				}
			}
		}
		if (root["bindings"] is JsonObject bindings)
		{
			var stale = new List<string>();
			foreach (KeyValuePair<string, JsonNode?> kv in bindings)
			{
				string key = kv.Key;
				int at = key.IndexOf('@');
				int colon = key.LastIndexOf(':');
				if (at <= 0 || colon <= at || !int.TryParse(key.Substring(colon + 1), out int port) || port == keepPort)
				{
					continue;
				}
				string who = key.Substring(0, at);
				string host = key.Substring(at + 1, colon - at - 1);
				bool loopback = string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
				if (loopback && string.Equals(who, name, StringComparison.OrdinalIgnoreCase))
				{
					stale.Add(key);
				}
			}
			foreach (string key in stale)
			{
				bindings.Remove(key);
				changed = true;
			}
		}
		if (root["lastOnline"] is JsonArray online)
		{
			for (int i = online.Count - 1; i >= 0; i--)
			{
				if (online[i] is JsonValue v && v.TryGetValue<string>(out string s) && s.StartsWith(name + "@", StringComparison.OrdinalIgnoreCase))
				{
					int colon = s.LastIndexOf(':');
					if (colon > 0 && int.TryParse(s.Substring(colon + 1), out int port) && port != keepPort
						&& (s.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) > 0
							|| s.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) > 0))
					{
						online.RemoveAt(i);
						changed = true;
					}
				}
			}
		}
		if (changed)
		{
			SaveRoot(gameDir, root);
		}
	}

	/// <summary>
	/// 把客户端里某个账号的本机端口改成新端口（账号 / 绑定 / 在线记录一起迁移）。
	/// 只在客户端里已经存在该账号时修改，不新增；<paramref name="protectedPorts"/>
	/// 里的端口属于其它角色，保持不动，避免同账号多角色时误改。
	/// 返回是否发生了修改。
	/// </summary>
	public static bool UpdateLocalAccountPort(string gameDir, string user, int newPort, IReadOnlySet<int>? protectedPorts)
	{
		string name = (user ?? "").Trim();
		if (string.IsNullOrWhiteSpace(gameDir) || name.Length == 0 || newPort <= 0)
		{
			return false;
		}
		JsonObject root = LoadRoot(gameDir, out _);
		bool changed = false;
		if (root["accounts"] is JsonArray accounts)
		{
			foreach (JsonNode? node in accounts)
			{
				if (node is not JsonObject a || !string.Equals(Str(a, "username"), name, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				string host = Str(a, "host");
				bool loopback = string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
				int port = Int(a, "port", 25565);
				if (!loopback || port == newPort || (protectedPorts != null && protectedPorts.Contains(port)))
				{
					continue;
				}
				a["port"] = newPort;
				changed = true;
			}
		}
		if (root["bindings"] is JsonObject bindings)
		{
			var moves = new List<(string Old, string New, JsonNode? Value)>();
			foreach (KeyValuePair<string, JsonNode?> kv in bindings)
			{
				string key = kv.Key;
				int at = key.IndexOf('@');
				int colon = key.LastIndexOf(':');
				if (at <= 0 || colon <= at || !int.TryParse(key.Substring(colon + 1), out int port) || port == newPort)
				{
					continue;
				}
				if (!string.Equals(key.Substring(0, at), name, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				string host = key.Substring(at + 1, colon - at - 1);
				bool loopback = string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
				if (!loopback || (protectedPorts != null && protectedPorts.Contains(port)))
				{
					continue;
				}
				moves.Add((key, key.Substring(0, at + 1) + host + ":" + newPort, kv.Value));
			}
			foreach ((string oldKey, string newKey, JsonNode? value) in moves)
			{
				bindings.Remove(oldKey);
				if (!bindings.ContainsKey(newKey))
				{
					bindings[newKey] = value?.DeepClone();
				}
				changed = true;
			}
		}
		if (root["lastOnline"] is JsonArray online)
		{
			for (int i = 0; i < online.Count; i++)
			{
				if (online[i] is not JsonValue v || !v.TryGetValue<string>(out string s)
					|| !s.StartsWith(name + "@", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				int colon = s.LastIndexOf(':');
				if (colon <= 0 || !int.TryParse(s.Substring(colon + 1), out int port) || port == newPort)
				{
					continue;
				}
				if (s.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) <= 0
					&& s.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) <= 0)
				{
					continue;
				}
				if (protectedPorts != null && protectedPorts.Contains(port))
				{
					continue;
				}
				online[i] = s.Substring(0, colon + 1) + newPort;
				changed = true;
			}
		}
		if (changed)
		{
			SaveRoot(gameDir, root);
		}
		return changed;
	}

	/// <summary>追加一个代理到代理池尾部（Nexus 按数组下标绑定账号）。</summary>
	public static void AddProxy(string gameDir, string type, string host, int port, string user, string pass)
	{
		string h = (host ?? "").Trim();
		if (h.Length == 0)
		{
			throw new ArgumentException("代理地址不能为空");
		}
		JsonObject root = LoadRoot(gameDir, out _);
		JsonArray proxies = EnsureArray(root, "proxies");
		proxies.Add(new JsonObject
		{
			["type"] = string.Equals(type, "http", StringComparison.OrdinalIgnoreCase) ? "http" : "socks5",
			["host"] = h,
			["port"] = Math.Max(1, Math.Min(65535, port)),
			["user"] = user ?? "",
			["pass"] = pass ?? ""
		});
		SaveRoot(gameDir, root);
	}

	/// <summary>删除代理并同步修正所有账号的代理下标（与 NexusConfig.removeProxy 行为一致）。</summary>
	public static void RemoveProxy(string gameDir, int index)
	{
		JsonObject root = LoadRoot(gameDir, out _);
		if (root["proxies"] is not JsonArray proxies || index < 0 || index >= proxies.Count)
		{
			return;
		}
		proxies.RemoveAt(index);
		if (root["bindings"] is JsonObject bindings)
		{
			foreach (KeyValuePair<string, JsonNode?> kv in bindings)
			{
				if (kv.Value is not JsonObject b)
				{
					continue;
				}
				int p = Int(b, "proxy", -1);
				if (p == index)
				{
					b["proxy"] = -1;
				}
				else if (p > index)
				{
					b["proxy"] = p - 1;
				}
			}
		}
		SaveRoot(gameDir, root);
	}

	/// <summary>写回 Tick 调度参数（对应游戏内 Tick 面板）。</summary>
	public static void SaveTickSettings(string gameDir, bool adaptiveTick, int maxCpuPct, int minBandPct,
		int tickRatePathing, int tickRateIdle, int rejoinMinMs, int rejoinMaxMs, int connectDelayMs, float[] curve)
	{
		JsonObject root = LoadRoot(gameDir, out _);
		root["adaptiveTick"] = adaptiveTick;
		root["maxCpuPct"] = Math.Max(5, Math.Min(95, maxCpuPct));
		root["minBandPct"] = Math.Max(0, Math.Min(100, minBandPct));
		root["tickRatePathing"] = Math.Max(0, Math.Min(100, tickRatePathing));
		root["tickRateIdle"] = Math.Max(0, Math.Min(100, tickRateIdle));
		root["rejoinMinMs"] = Math.Max(0, Math.Min(600000, rejoinMinMs));
		root["rejoinMaxMs"] = Math.Max(Math.Max(0, Math.Min(600000, rejoinMinMs)), Math.Min(600000, rejoinMaxMs));
		root["connectDelayMs"] = Math.Max(0, Math.Min(600000, connectDelayMs));
		if (curve != null && curve.Length >= 6)
		{
			var arr = new JsonArray();
			for (int i = 0; i < 6; i++)
			{
				arr.Add((JsonNode)(float)Math.Max(0f, Math.Min(100f, curve[i])));
			}
			root["priorityCurve"] = arr;
		}
		SaveRoot(gameDir, root);
	}

	/// <summary>写回 Nexus 面板外观：卡片模式 / 排序 / 密度 / LiveViewport 尺寸 / 高清字体。</summary>
	public static void SavePanelSettings(string gameDir, string cardMode, string cardSort, string cardDensity, string liveViewSize, bool hdFont)
	{
		JsonObject root = LoadRoot(gameDir, out _);
		root["cardMode"] = NormalizeChoice(cardMode, "WATERFALL", "WATERFALL", "PAGE", "LIVE");
		root["cardSort"] = NormalizeChoice(cardSort, "DEFAULT", "DEFAULT", "STATE", "NAME", "UPTIME");
		root["cardDensity"] = NormalizeChoice(cardDensity, "NORMAL", "NORMAL", "COMPACT", "DETAILED");
		root["liveViewSize"] = string.IsNullOrWhiteSpace(liveViewSize) ? "320x180" : liveViewSize.Trim();
		root["hdFont"] = hdFont;
		SaveRoot(gameDir, root);
	}

	/// <summary>
	/// 写回「画面显示账号」：MythosNexus 读到非空值时会把该账号的机器人画面直接挂到
	/// 游戏主窗口（用于程序反向指定某个实例显示哪个账号的游戏画面）。
	/// </summary>
	public static void SaveViewAccount(string gameDir, string account)
	{
		if (string.IsNullOrWhiteSpace(gameDir))
		{
			return;
		}
		JsonObject root = LoadRoot(gameDir, out _);
		root["viewAccount"] = (account ?? "").Trim();
		SaveRoot(gameDir, root);
	}

	private static string NormalizeChoice(string value, string fallback, params string[] allowed)
	{
		foreach (string item in allowed)
		{
			if (string.Equals(value, item, StringComparison.OrdinalIgnoreCase))
			{
				return item;
			}
		}
		return fallback;
	}

	// ---- JSON 辅助 ----

	private static JsonObject LoadRoot(string gameDir, out bool existed)
	{
		string path = ConfigPath(gameDir);
		existed = false;
		if (string.IsNullOrWhiteSpace(gameDir) || !File.Exists(path))
		{
			return new JsonObject();
		}
		try
		{
			existed = true;
			return ParseRoot(File.ReadAllText(path));
		}
		catch
		{
			return new JsonObject();
		}
	}

	private static JsonObject ParseRoot(string text)
	{
		JsonNode? node = JsonNode.Parse(text);
		return node as JsonObject ?? new JsonObject();
	}

	private static void SaveRoot(string gameDir, JsonObject root)
	{
		string path = ConfigPath(gameDir);
		string dir = Path.GetDirectoryName(path) ?? "";
		Directory.CreateDirectory(dir);
		// .NET 8 的 JsonNode.ToJsonString(options) 在 options 被标记只读后要求显式
		// TypeInfoResolver，否则抛「must specify a TypeInfoResolver」。这里显式指定。
		string json = root.ToJsonString(WriteOptions);
		string tmp = path + ".tmp";
		File.WriteAllText(tmp, json, new UTF8Encoding(false));
		File.Move(tmp, path, true);
	}

	private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
	};

	private static JsonArray EnsureArray(JsonObject root, string key)
	{
		if (root[key] is JsonArray array)
		{
			return array;
		}
		var created = new JsonArray();
		root[key] = created;
		return created;
	}

	private static JsonObject EnsureObject(JsonObject root, string key)
	{
		if (root[key] is JsonObject obj)
		{
			return obj;
		}
		var created = new JsonObject();
		root[key] = created;
		return created;
	}

	private static string? FindProperty(JsonObject obj, string key)
	{
		foreach (KeyValuePair<string, JsonNode?> kv in obj)
		{
			if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
			{
				return kv.Key;
			}
		}
		return null;
	}

	private static string KeyOf(JsonObject account)
	{
		return Str(account, "username") + "@" + Str(account, "host") + ":" + Int(account, "port", 25565);
	}

	private static string Str(JsonObject obj, string key, string fallback = "")
	{
		try
		{
			if (obj[key] is JsonValue v && v.TryGetValue<string>(out string s))
			{
				return s ?? fallback;
			}
		}
		catch
		{
		}
		return fallback;
	}

	private static int Int(JsonObject obj, string key, int fallback)
	{
		try
		{
			if (obj[key] is JsonValue v)
			{
				if (v.TryGetValue<int>(out int i))
				{
					return i;
				}
				if (v.TryGetValue<double>(out double d))
				{
					return (int)d;
				}
				if (v.TryGetValue<string>(out string s) && int.TryParse(s, out int parsed))
				{
					return parsed;
				}
			}
		}
		catch
		{
		}
		return fallback;
	}

	private static bool Bool(JsonObject obj, string key, bool fallback)
	{
		try
		{
			if (obj[key] is JsonValue v && v.TryGetValue<bool>(out bool b))
			{
				return b;
			}
		}
		catch
		{
		}
		return fallback;
	}
}

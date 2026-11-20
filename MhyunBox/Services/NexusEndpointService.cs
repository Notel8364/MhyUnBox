using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MhyunBox.Settings;

namespace MhyunBox.Services;

/// <summary>
/// 实例多开的连接模型（简化版，2026-11-15）：
/// 不再使用外部 SOCKS/HTTP 代理池，每个账号（角色）在花名册里分到一个
/// 127.0.0.1:本地端口，游戏连这个本地端口 → 启动器被动中继到该账号所在服务器。
/// 只有「角色 + 本地端口 + 服务器」都分配了、且勾选启用的账号才参与连接与自动重连。
/// </summary>
public static class NexusEndpointService
{
	public sealed class Endpoint
	{
		public string User = "";
		public string Host = "";
		public int Port = 25565;
		public int ListenPort;
		public bool Enabled = true;
		public bool AutoReconnect;
		/// <summary>该角色是否勾选「程序启动时自动启动机器人代理」（每卡片独立开关）。</summary>
		public bool AutoStart;
		public string Group = "默认";
		public string Note = "";
		public bool LastOnline;

		/// <summary>角色在游戏内的名称（来自快速启动分组的角色），卡片标题优先显示它而不是账号。</summary>
		public string RoleName = "";

		/// <summary>角色来源客户端版本对应的服务器名（用于把 127.0.0.1/null 这类占位目标替换成真实预设地址）。</summary>
		public string ServerHint = "";

		/// <summary>
		/// 该卡片的机器人账号写进哪个客户端目录（空 = 按 ServerHint 自动解析）。
		/// 单客户端 10 个会话是内存上限，超出部分会自动过渡到第二个同服客户端，
		/// 过渡结果就记在这里，避免每次 Load 又算回第一个客户端。
		/// </summary>
		public string ClientDir = "";

		public string Key => User + "@" + Host + ":" + Port;
		public string ServerKey => Host + ":" + Port;
		public string ListenAddress => "127.0.0.1:" + ListenPort;

		/// <summary>
		/// 卡片唯一键 = baseKey + 角色名。同一账号同服务器的多个角色各有一张卡、各存一份配置，
		/// 删除 / 启用 / 隐藏都必须按这个键精确匹配；Key 只能用于日志与目标地址解析。
		/// </summary>
		public string CardKey => string.IsNullOrWhiteSpace(RoleName) ? Key : Key + "#" + RoleName;
	}

	/// <summary>设置条目的卡片键（与 Endpoint.CardKey 同一规则），删除/启用/隐藏按它精确匹配。</summary>
	public static string CardKeyOf(NexusEndpointSetting s)
	{
		return s == null || string.IsNullOrWhiteSpace(s.RoleName) ? (s?.Key ?? "") : s.Key + "#" + s.RoleName;
	}

	/// <summary>单客户端机器人会话上限（10 个；第 11 个开始游戏会爆内存并崩溃）。</summary>
	public const int SessionLimitPerClient = 10;

	/// <summary>该卡片的机器人账号实际写进哪个客户端：优先卡片自身的过渡结果，其次按服务器解析。</summary>
	public static string ClientDirOf(Endpoint e)
	{
		if (e == null)
		{
			return "";
		}
		if (!string.IsNullOrWhiteSpace(e.ClientDir) && Directory.Exists(e.ClientDir))
		{
			return e.ClientDir;
		}
		return ResolveClientDir(e.ServerHint);
	}

	// ---- 机器人卡片墓碑：删除过的卡片不再被任何自动流程写回来 ----

	/// <summary>卡片级墓碑键。</summary>
	private static string TombstoneOfCard(string cardKey)
	{
		return "card#" + (cardKey ?? "").Trim().ToLowerInvariant();
	}

	/// <summary>账号级墓碑键（用户直接在游戏模组界面删掉账号时用）。</summary>
	private static string TombstoneOfAccount(string clientDir, string user, int listenPort)
	{
		return "acct#" + (Path.GetFileName(clientDir) ?? "").Trim().ToLowerInvariant()
			+ "|" + (user ?? "").Trim().ToLowerInvariant() + "@" + listenPort;
	}

	private static bool HasTombstone(string key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return false;
		}
		List<string> list = SettingsManager.Instance.NexusBotTombstones ?? new List<string>();
		return list.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>该卡片 / 该账号是否已被用户删除并打了墓碑。</summary>
	public static bool IsBotTombstoned(string cardKey, string clientDir = "", string user = "", int listenPort = 0)
	{
		if (!string.IsNullOrWhiteSpace(cardKey) && HasTombstone(TombstoneOfCard(cardKey)))
		{
			return true;
		}
		return listenPort > 0 && !string.IsNullOrWhiteSpace(clientDir)
			&& HasTombstone(TombstoneOfAccount(clientDir, user, listenPort));
	}

	private static void AddTombstone(string key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return;
		}
		AppSettings cfg = SettingsManager.Instance;
		cfg.NexusBotTombstones ??= new List<string>();
		if (!cfg.NexusBotTombstones.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase)))
		{
			cfg.NexusBotTombstones.Add(key);
			SettingsManager.Save(cfg);
		}
	}

	/// <summary>把一张卡片记成「已删除」，之后所有自动回写都会跳过它。</summary>
	public static void AddCardTombstone(Endpoint e)
	{
		if (e == null)
		{
			return;
		}
		AddTombstone(TombstoneOfCard(e.CardKey));
		string dir = ClientDirOf(e);
		string user = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
		if (!string.IsNullOrWhiteSpace(dir) && e.ListenPort > 0)
		{
			AddTombstone(TombstoneOfAccount(dir, user, e.ListenPort));
		}
	}

	/// <summary>用户在这张卡上显式点了「启动」：解除墓碑（否则启动也不会写账号）。</summary>
	public static void ClearCardTombstone(Endpoint e, string clientDir = "")
	{
		if (e == null)
		{
			return;
		}
		AppSettings cfg = SettingsManager.Instance;
		List<string>? list = cfg.NexusBotTombstones;
		if (list == null || list.Count == 0)
		{
			return;
		}
		string card = TombstoneOfCard(e.CardKey);
		string dir = string.IsNullOrWhiteSpace(clientDir) ? ClientDirOf(e) : clientDir;
		string user = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
		string acct = e.ListenPort > 0 ? TombstoneOfAccount(dir, user, e.ListenPort) : "";
		int removed = list.RemoveAll(x => string.Equals(x, card, StringComparison.OrdinalIgnoreCase)
			|| (!string.IsNullOrEmpty(acct) && x.StartsWith("acct#", StringComparison.OrdinalIgnoreCase)
				&& string.Equals(x, acct, StringComparison.OrdinalIgnoreCase)));
		if (removed > 0)
		{
			SettingsManager.Save(cfg);
			LogHelper.Log("[实例多开] 已清除卡片墓碑：" + e.CardKey + "（用户显式启动）");
		}
	}

	/// <summary>
	/// 枚举 PCL versions 下所有客户端目录：支持两级结构
	/// （versions\元素芝士？启动！挂号版 与 versions\新建文件夹\元素芝士？启动！挂号版 都算）。
	/// </summary>
	public static List<string> EnumerateClientDirs()
	{
		var list = new List<string>();
		try
		{
			string pcl = (SettingsManager.Instance.PclPath ?? "").Trim();
			if (string.IsNullOrWhiteSpace(pcl))
			{
				return list;
			}
			string versions = Path.Combine(Path.GetDirectoryName(pcl) ?? "", ".minecraft", "versions");
			if (!Directory.Exists(versions))
			{
				return list;
			}
			foreach (string dir in Directory.EnumerateDirectories(versions))
			{
				if (IsClientDir(dir))
				{
					list.Add(dir);
					continue;
				}
				foreach (string sub in Directory.EnumerateDirectories(dir))
				{
					if (IsClientDir(sub))
					{
						list.Add(sub);
					}
				}
			}
		}
		catch
		{
		}
		return list;
	}

	private static bool IsClientDir(string dir)
	{
		try
		{
			// 备份副本（.bak-2026… / - 副本 / backup）也是完整客户端，会被当成
			// 独立客户端去读写账号：批量重启时每个端口变更都往备份里写一遍，
			// 既拖慢操作也会污染备份。这里直接跳过。
			if (IsBackupDir(dir))
			{
				return false;
			}
			return Directory.Exists(Path.Combine(dir, "mods"))
				|| File.Exists(Path.Combine(dir, "config", "mythosnexus.json"));
		}
		catch
		{
			return false;
		}
	}

	/// <summary>版本目录是否是备份/副本（不参与账号写入与花名册读取）。</summary>
	private static bool IsBackupDir(string dir)
	{
		string name = Path.GetFileName(dir) ?? "";
		if (name.Length == 0)
		{
			return false;
		}
		return name.Contains(".bak", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith("-bak", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("备份", StringComparison.Ordinal)
			|| name.Contains("副本", StringComparison.Ordinal)
			|| name.Contains("backup", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>定位实例多开使用的客户端目录：优先「挂号版」，其次带 Nexus 配置/模组的版本。</summary>
	public static string ResolveGameDir(string? preferredVersion = null)
	{
		try
		{
			List<string> dirs = EnumerateClientDirs();
			string? pick = null;
			if (!string.IsNullOrWhiteSpace(preferredVersion))
			{
				pick = dirs.FirstOrDefault(v => Path.GetFileName(v).Contains(preferredVersion, StringComparison.OrdinalIgnoreCase));
			}
			pick ??= dirs.FirstOrDefault(v => Path.GetFileName(v).Contains("挂号版", StringComparison.OrdinalIgnoreCase))
				?? dirs.FirstOrDefault(v => File.Exists(Path.Combine(v, "config", "mythosnexus.json")))
				?? dirs.FirstOrDefault(v =>
				{
					string mods = Path.Combine(v, "mods");
					return Directory.Exists(mods) && Directory.GetFiles(mods, "Mythos-Nexus-*.jar").Length > 0;
				})
				?? dirs.FirstOrDefault(v => Path.GetFileName(v).Contains("失落世界", StringComparison.OrdinalIgnoreCase));
			return pick ?? "";
		}
		catch
		{
			return "";
		}
	}

	/// <summary>
	/// 找到某个服务器对应的「挂号版」客户端目录（机器人账号必须写进玩家实际启动的那个客户端）。
	/// </summary>
	public static string ResolveClientDir(string serverHint)
	{
		try
		{
			List<string> dirs = EnumerateClientDirs();
			string? hint = string.IsNullOrWhiteSpace(serverHint) ? null : InferServerHint(serverHint);
			// 目录名可能是「元素芝士？启动！挂号版」，所以只取服务器关键字匹配。
			string? keyword = hint == null ? null
				: hint.Contains("元素") ? "元素"
				: hint.Contains("失落") ? "失落"
				: hint;
			List<string> guahao = dirs
				.Where(v => Path.GetFileName(v).Contains("挂号版", StringComparison.OrdinalIgnoreCase))
				.ToList();
			string? pick = null;
			if (!string.IsNullOrWhiteSpace(keyword))
			{
				pick = guahao.FirstOrDefault(v =>
					Path.GetFileName(v).Contains(keyword, StringComparison.OrdinalIgnoreCase));
			}
			pick ??= guahao.FirstOrDefault();
			if (pick == null)
			{
				return "";
			}
			Directory.CreateDirectory(Path.Combine(pick, "config"));
			return pick;
		}
		catch
		{
			return "";
		}
	}

	/// <summary>枚举所有安装了交易行行情模组的客户端目录（market-prices 目录或 *交易行行情*.jar）。</summary>
	private static List<string> EnumerateMarketClientDirs()
	{
		var list = new List<string>();
		try
		{
			foreach (string dir in EnumerateClientDirs())
			{
				// 只处理 versions 根目录下的挂号版客户端（用户现场只维护这两个挂号版，合并版不再参与）。
				if (!string.Equals(new DirectoryInfo(dir).Parent?.Name, "versions", StringComparison.OrdinalIgnoreCase)
					|| Path.GetFileName(dir).IndexOf("挂号版", StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}
				string mods = Path.Combine(dir, "mods");
				bool has = Directory.Exists(Path.Combine(dir, "market-prices"))
					|| (Directory.Exists(mods) && Directory.GetFiles(mods, "*交易行行情*.jar").Length > 0);
				if (has && !list.Contains(dir, StringComparer.OrdinalIgnoreCase))
				{
					list.Add(dir);
				}
			}
		}
		catch
		{
		}
		return list;
	}

	/// <summary>
	/// 找出行情角色真正所在的客户端：按游戏内角色名/账号名或本地端口匹配 mythosnexus.json 的账号；
	/// 匹配不到时回退到 ResolveClientDir（挂号版）。
	/// </summary>
	public static List<string> ResolveMarketClientDirs(Endpoint market)
	{
		var hits = new List<string>();
		if (market == null)
		{
			return hits;
		}
		try
		{
			string owner = string.IsNullOrWhiteSpace(market.RoleName) ? market.User : market.RoleName;
			foreach (string dir in EnumerateMarketClientDirs())
			{
				NexusConfigService.Snapshot snap = NexusConfigService.Load(dir);
				bool match = snap.Accounts.Any(a =>
					string.Equals(a.User, owner, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(a.User, market.User, StringComparison.OrdinalIgnoreCase)
					|| (market.ListenPort > 0 && a.Port == market.ListenPort
						&& string.Equals(a.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)));
				if (match)
				{
					hits.Add(dir);
				}
			}
			if (hits.Count == 0)
			{
				string fallback = ResolveClientDir(market.ServerHint);
				if (!string.IsNullOrWhiteSpace(fallback))
				{
					hits.Add(fallback);
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[行情角色] 定位行情客户端失败");
		}
		return hits;
	}

	/// <summary>
	/// 把机器人角色写进对应挂号版客户端的 mythosnexus.json：
	/// 账号 host 指向本机监听端口，游戏内 MythosNexus 启动时就会连 127.0.0.1:端口 → 本程序 → 真实服务器。
	/// 同时写入 lastOnline + autoReconnect，保证进游戏后自动挂机。
	/// </summary>
	public static bool EnsureBotAccount(string serverHint, string user, int listenPort, string group = "机器人代理",
		string? cardKey = null, string? clientDir = null)
	{
		string dir = string.IsNullOrWhiteSpace(clientDir) ? ResolveClientDir(serverHint) : clientDir;
		if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(user) || listenPort <= 0)
		{
			return false;
		}
		// 用户删掉的卡片永远不再写回来：游戏每次启动都会走这里重放账号，
		// 没有这道闸门就会出现「删了又出现」。
		if (IsBotTombstoned(cardKey ?? "", dir, user, listenPort))
		{
			LogHelper.Log("[实例多开] 跳过已删除的机器人卡片（墓碑）：" + user + "@127.0.0.1:" + listenPort
				+ (string.IsNullOrWhiteSpace(cardKey) ? "" : " 卡片=" + cardKey));
			return false;
		}
		try
		{
			// 单次读改写完成「清旧端口 + 写账号/绑定 + 标记在线」，缩短与游戏端配置保存的竞态窗口。
			NexusConfigService.WriteBotAccount(dir, user, listenPort, group, "MhyunBox 机器人号代理");
			MarkBotAccountWritten(dir, user, listenPort);
			// 其它客户端（例如合并版）里如果已经存在这个账号，也一起改成实际端口：
			// 用户可能在别的客户端里开同一个角色，端口一变它就会一直连旧端口。
			HashSet<int> keep = CollectKnownPorts(listenPort);
			foreach (string otherDir in EnumerateClientDirs())
			{
				if (string.Equals(otherDir, dir, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (NexusConfigService.UpdateLocalAccountPort(otherDir, user, listenPort, keep))
				{
					LogHelper.Log("[端口自适配] 客户端账号端口已更新：" + Path.GetFileName(otherDir)
						+ " " + user + " → 127.0.0.1:" + listenPort);
				}
			}
			LogHelper.Log("[实例多开] 机器人账号已写入客户端：" + user + "@127.0.0.1:" + listenPort
				+ " → " + Path.GetFileName(dir) + "\\config\\mythosnexus.json（启动游戏后自动进服）");
			return true;
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 写入机器人账号失败（" + user + "）：" + ex.Message);
			return false;
		}
	}

	/// <summary>本进程最近写过哪些机器人账号（客户端目录|账号@端口 → 写入时刻）。</summary>
	private static readonly Dictionary<string, DateTime> WrittenBotAccounts = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>「账号消失了」的连续命中次数：连续两次才算用户删除，避免读到半写文件时误判。</summary>
	private static readonly Dictionary<string, int> MissingBotStrikes = new(StringComparer.OrdinalIgnoreCase);

	private static string BotAccountSlot(string clientDir, string user, int listenPort)
	{
		return (Path.GetFileName(clientDir) ?? "").Trim().ToLowerInvariant()
			+ "|" + (user ?? "").Trim().ToLowerInvariant() + "@" + listenPort;
	}

	private static void MarkBotAccountWritten(string clientDir, string user, int listenPort)
	{
		lock (WrittenBotAccounts)
		{
			WrittenBotAccounts[BotAccountSlot(clientDir, user, listenPort)] = DateTime.Now;
			MissingBotStrikes.Remove(BotAccountSlot(clientDir, user, listenPort));
		}
	}


	/// <summary>
	/// 只补「客户端里还没有」的机器人账号，已有的不动（避免每次启动把整份配置重写几十遍）。
	/// 卡片端口变了但客户端仍是旧端口的情况由 <see cref="WriteBotAccount"/> 的清理逻辑 + 端口冲突自愈处理。
	/// </summary>
	private static void EnsureMissingBotAccounts(List<Endpoint> all)
	{
		try
		{
			int added = 0;
			foreach (IGrouping<string, Endpoint> group in all
				.Where(e => e.Enabled && IsBotGroup(e) && e.ListenPort > 0 && InDisplayScope(e))
				.GroupBy(e => ClientDirOf(e)))
			{
				string dir = group.Key;
				if (string.IsNullOrWhiteSpace(dir))
				{
					continue;
				}
				var have = new HashSet<string>(
					NexusConfigService.Load(dir).Accounts.Select(a => a.Key),
					StringComparer.OrdinalIgnoreCase);
				foreach (Endpoint e in group)
				{
					string user = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
					if (string.IsNullOrWhiteSpace(user))
					{
						continue;
					}
					if (have.Contains(user + "@127.0.0.1:" + e.ListenPort))
					{
						continue;
					}
					if (EnsureBotAccount(e.ServerHint, user, e.ListenPort, "机器人代理", e.CardKey, ClientDirOf(e)))
					{
						added++;
					}
				}
			}
			if (added > 0)
			{
				LogHelper.Log("[实例多开] 启动补齐机器人账号：" + added + " 个（卡片有、客户端里缺的）");
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 补齐机器人账号失败：" + ex.Message);
		}
	}

	/// <summary>当前所有已知的本地端口（配置里的角色 + 实际监听），同步账号时不能误改这些端口上的其它角色。</summary>
	private static HashSet<int> CollectKnownPorts(int extra)
	{
		var set = new HashSet<int> { extra };
		try
		{
			// 只信任分组里的角色端口和实际监听：保存的 NexusEndpoints 可能残留旧端口，
			// 用它做保护会导致客户端账号一直被当成「别的角色」而不更新。
			foreach (ProxyGroup group in SettingsManager.Instance.ProxyGroups ?? new List<ProxyGroup>())
			{
				foreach (ProxyGroupRole role in group.Roles ?? new List<ProxyGroupRole>())
				{
					if (role.ListenPort > 0)
					{
						set.Add(role.ListenPort);
					}
				}
			}
			foreach (ProxyCaptureService.ActiveBindingInfo b in ProxyCaptureService.ActiveBindingsSnapshot())
			{
				if (b.ListenPort > 0)
				{
					set.Add(b.ListenPort);
				}
			}
		}
		catch
		{
		}
		return set;
	}

	/// <summary>停止机器人代理时移除写进客户端的机器人账号，避免游戏启动后又自动连回来。</summary>
	public static void RemoveBotAccount(string serverHint, string user, int listenPort, string? clientDir = null)
	{
		string dir = string.IsNullOrWhiteSpace(clientDir) ? ResolveClientDir(serverHint) : clientDir;
		if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(user) || listenPort <= 0)
		{
			return;
		}
		try
		{
			NexusConfigService.RemoveAccount(dir, user + "@127.0.0.1:" + listenPort);
			// 这是程序主动删的，不能算成「用户在游戏里删了」。
			lock (WrittenBotAccounts)
			{
				WrittenBotAccounts.Remove(BotAccountSlot(dir, user, listenPort));
				MissingBotStrikes.Remove(BotAccountSlot(dir, user, listenPort));
			}
			LogHelper.Log("[实例多开] 已移除客户端机器人账号：" + user + "@127.0.0.1:" + listenPort
				+ " → " + Path.GetFileName(dir));
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 移除机器人账号失败（" + user + "）：" + ex.Message);
		}
	}

	/// <summary>
	/// 检测「用户在游戏里的模组界面把机器人账号删掉了」。
	///
	/// 只看本进程写过、且写入之后客户端配置又被别人（游戏）改写过、账号却不见了的情况：
	/// 连续两次轮询都缺这个账号才确认，避免读到写一半的文件误杀卡片。
	/// 确认后：打墓碑 → 停监听 → 删快速启动角色 → 删卡片，之后任何自动流程都不会再写回来。
	/// </summary>
	public static int DetectUserDeletedBots()
	{
		List<KeyValuePair<string, DateTime>> snapshot;
		lock (WrittenBotAccounts)
		{
			if (WrittenBotAccounts.Count == 0)
			{
				return 0;
			}
			snapshot = WrittenBotAccounts.ToList();
		}
		int confirmed = 0;
		foreach (IGrouping<string, KeyValuePair<string, DateTime>> group in snapshot.GroupBy(p => p.Key.Split('|')[0]))
		{
			string dirName = group.Key;
			string dir = EnumerateClientDirs()
				.FirstOrDefault(d => string.Equals((Path.GetFileName(d) ?? "").ToLowerInvariant(), dirName, StringComparison.OrdinalIgnoreCase)) ?? "";
			if (string.IsNullOrWhiteSpace(dir))
			{
				continue;
			}
			string cfgPath = Path.Combine(dir, "config", "mythosnexus.json");
			if (!File.Exists(cfgPath))
			{
				continue;
			}
			DateTime writeTime;
			try
			{
				writeTime = File.GetLastWriteTime(cfgPath);
			}
			catch
			{
				continue;
			}
			HashSet<string> have;
			try
			{
				have = new HashSet<string>(NexusConfigService.Load(dir).Accounts.Select(a => a.Key), StringComparer.OrdinalIgnoreCase);
			}
			catch
			{
				continue;
			}
			foreach (KeyValuePair<string, DateTime> pair in group)
			{
				string[] parts = pair.Key.Split('|');
				if (parts.Length < 2)
				{
					continue;
				}
				string account = parts[1];
				if (have.Contains(account))
				{
					lock (WrittenBotAccounts)
					{
						MissingBotStrikes.Remove(pair.Key);
					}
					continue;
				}
				// 游戏必须在我们写完之后改过这个文件，才可能是「用户删的」。
				if (writeTime <= pair.Value.AddSeconds(1))
				{
					continue;
				}
				int strikes;
				lock (WrittenBotAccounts)
				{
					MissingBotStrikes.TryGetValue(pair.Key, out strikes);
					strikes++;
					MissingBotStrikes[pair.Key] = strikes;
				}
				if (strikes < 2)
				{
					continue;
				}
				int at = account.LastIndexOf('@');
				int colon = account.LastIndexOf(':');
				if (at <= 0 || colon <= at || !int.TryParse(account.Substring(colon + 1), out int port))
				{
					continue;
				}
				string user = account.Substring(0, at);
				ForgetDeletedBot(dir, user, port);
				confirmed++;
			}
		}
		if (confirmed > 0)
		{
			LogHelper.Log("[实例多开] 检测到用户在游戏内删除了 " + confirmed + " 个机器人账号，已同步删除对应卡片并打墓碑");
		}
		return confirmed;
	}

	/// <summary>用户在游戏内删掉账号：同步删掉脱盒这边的卡片，并打墓碑防止再写回。</summary>
	private static void ForgetDeletedBot(string clientDir, string user, int listenPort)
	{
		try
		{
			ProxyCaptureService.StopNexusBinding(listenPort);
			lock (WrittenBotAccounts)
			{
				WrittenBotAccounts.Remove(BotAccountSlot(clientDir, user, listenPort));
				MissingBotStrikes.Remove(BotAccountSlot(clientDir, user, listenPort));
			}
			AddTombstone(TombstoneOfAccount(clientDir, user, listenPort));
			string gameDir = ResolveGameDir();
			List<Endpoint> all = string.IsNullOrWhiteSpace(gameDir) ? new List<Endpoint>() : Load(gameDir);
			Endpoint? target = all.FirstOrDefault(e => e.ListenPort == listenPort
				&& string.Equals(ClientDirOf(e), clientDir, StringComparison.OrdinalIgnoreCase));
			AppSettings cfg = SettingsManager.Instance;
			if (target != null)
			{
				AddTombstone(TombstoneOfCard(target.CardKey));
				cfg.NexusHiddenCards ??= new List<string>();
				if (!cfg.NexusHiddenCards.Any(x => string.Equals(x, target.CardKey, StringComparison.OrdinalIgnoreCase)))
				{
					cfg.NexusHiddenCards.Add(target.CardKey);
				}
				cfg.NexusEndpoints.RemoveAll(x => string.Equals(CardKeyOf(x), target.CardKey, StringComparison.OrdinalIgnoreCase)
					|| (string.IsNullOrWhiteSpace(target.RoleName) && string.Equals(x.Key, target.Key, StringComparison.OrdinalIgnoreCase)
						&& (x.ListenPort == target.ListenPort || x.ListenPort <= 0)));
				RemoveQuickStartRole(target);
				SettingsManager.Save(cfg);
				LogHelper.Log("[实例多开] 游戏内已删除机器人：" + target.CardKey + "（客户端 "
					+ Path.GetFileName(clientDir) + "）→ 卡片同步删除");
			}
			else
			{
				cfg.NexusEndpoints.RemoveAll(x => x.ListenPort == listenPort && string.Equals(x.Key.Split('@')[0], user, StringComparison.OrdinalIgnoreCase));
				SettingsManager.Save(cfg);
				LogHelper.Log("[实例多开] 游戏内已删除机器人账号：" + user + "@127.0.0.1:" + listenPort + "，脱盒侧无对应卡片");
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 同步删除游戏内已删机器人失败：" + ex.Message);
		}
	}

	/// <summary>把某张卡的启用状态落盘：停止 = 停用，下次启动程序 / 下次游戏启动都不会再自动写回。</summary>
	public static void SetCardEnabled(Endpoint e, bool enabled)
	{
		if (e == null || string.IsNullOrWhiteSpace(e.Key))
		{
			return;
		}
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			NexusEndpointSetting? item = cfg.NexusEndpoints
				.FirstOrDefault(x => string.Equals(CardKeyOf(x), e.CardKey, StringComparison.OrdinalIgnoreCase));
			if (item == null)
			{
				item = new NexusEndpointSetting
				{
					Key = e.Key,
					RoleName = e.RoleName ?? "",
					ListenPort = e.ListenPort,
					AutoStart = e.AutoStart
				};
				cfg.NexusEndpoints.Add(item);
			}
			item.Enabled = enabled;
			if (e.ListenPort > 0)
			{
				item.ListenPort = e.ListenPort;
			}
			e.Enabled = enabled;
			SettingsManager.Save(cfg);
			LogHelper.Log("[实例多开] 卡片" + (enabled ? "启用" : "停用") + "：" + e.CardKey);
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 写卡片启用状态失败：" + ex.Message);
		}
	}

	/// <summary>
	/// 「显示组」的作用域：会话多开页选了某个组时，客户端里只挂这个组的机器人；
	/// 空（全部组）= 不过滤。返回 null 表示不过滤，否则是组内角色键集合（账号@服务器:端口）。
	/// </summary>
	public static HashSet<string>? DisplayScopeCardKeys()
	{
		string groupName = SettingsManager.Instance.DisplayGroupName ?? "";
		if (string.IsNullOrWhiteSpace(groupName))
		{
			return null;
		}
		ProxyGroup? group = (SettingsManager.Instance.ProxyGroups ?? new List<ProxyGroup>())
			.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
		if (group == null)
		{
			return null;
		}
		var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ProxyGroupRole role in group.Roles ?? new List<ProxyGroupRole>())
		{
			if (role == null || string.IsNullOrWhiteSpace(role.Account))
			{
				continue;
			}
			keys.Add(KeyOf(role, group));
		}
		return keys;
	}

	/// <summary>该卡片是否在当前显示组的作用域内（组外机器人不写客户端、不自启）。</summary>
	public static bool InDisplayScope(Endpoint e)
	{
		HashSet<string>? scope = DisplayScopeCardKeys();
		return scope == null || (e != null && scope.Contains(e.Key));
	}

	/// <summary>
	/// 显示组切换 / 启动后对齐客户端账号：
	///  - 组内、客户端缺的账号 → 补写；
	///  - 组外的账号 → 从客户端撤掉（模组 3 秒热加载后对应机器人会话自动下线）。
	/// 只动「已启用的机器人代理卡」，删除墓碑照常生效；空组名 = 全部组，即全部补齐、不撤。
	/// </summary>
	public static void SyncClientAccountsToDisplayGroup()
	{
		try
		{
			string gameDir = ResolveGameDir();
			if (string.IsNullOrWhiteSpace(gameDir))
			{
				return;
			}
			List<Endpoint> all = Load(gameDir);
			HashSet<string>? scope = DisplayScopeCardKeys();
			int added = 0, removed = 0;
			foreach (IGrouping<string, Endpoint> grp in all
				.Where(e => e.Enabled && IsBotGroup(e) && e.ListenPort > 0)
				.GroupBy(e => ClientDirOf(e), StringComparer.OrdinalIgnoreCase))
			{
				string dir = grp.Key;
				if (string.IsNullOrWhiteSpace(dir))
				{
					continue;
				}
				HashSet<string> have;
				try
				{
					have = new HashSet<string>(
						NexusConfigService.Load(dir).Accounts.Select(a => a.Key),
						StringComparer.OrdinalIgnoreCase);
				}
				catch
				{
					continue;
				}
				foreach (Endpoint e in grp)
				{
					string user = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
					string account = user + "@127.0.0.1:" + e.ListenPort;
					bool inScope = scope == null || scope.Contains(e.Key);
					if (inScope)
					{
						if (!have.Contains(account) && EnsureBotAccount(e.ServerHint, user, e.ListenPort, "机器人代理", e.CardKey, dir))
						{
							added++;
						}
					}
					else if (have.Contains(account))
					{
						NexusConfigService.RemoveAccount(dir, account);
						lock (WrittenBotAccounts)
						{
							WrittenBotAccounts.Remove(BotAccountSlot(dir, user, e.ListenPort));
							MissingBotStrikes.Remove(BotAccountSlot(dir, user, e.ListenPort));
						}
						removed++;
						LogHelper.Log("[实例多开] 显示组外机器人已撤下客户端：" + user + "@127.0.0.1:" + e.ListenPort
							+ " → " + Path.GetFileName(dir));
					}
				}
			}
			if (added > 0 || removed > 0)
			{
				LogHelper.Log("[实例多开] 按显示组对齐客户端机器人账号：补写 " + added + "，撤下 " + removed);
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 按显示组对齐客户端账号失败：" + ex.Message);
		}
	}

	/// <summary>该客户端目录上已启用的机器人会话数（一张卡片 = 一个会话）。</summary>
	public static int CountBotSessions(List<Endpoint> all, string clientDir)
	{
		if (all == null || string.IsNullOrWhiteSpace(clientDir))
		{
			return 0;
		}
		return all.Count(e => e.Enabled && IsBotGroup(e) && e.ListenPort > 0
			&& string.Equals(ClientDirOf(e), clientDir, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// 单客户端 10 个会话是内存上限（第 11 个起 ChunkRenderDispatcher 的直存池会把游戏撑爆）。
	/// 按要求不加锁：只提醒，并把超出上限的卡片自动过渡到第二个同服客户端继续增加会话数。
	/// 返回本次实际过渡的卡片数。
	/// </summary>
	public static int ApplySessionOverflow(List<Endpoint> all)
	{
		try
		{
			if (all == null || all.Count == 0)
			{
				return 0;
			}
			int moved = 0;
			List<IGrouping<string, Endpoint>> groups = all
				.Where(e => e.Enabled && IsBotGroup(e) && e.ListenPort > 0 && InDisplayScope(e))
				.GroupBy(e => ClientDirOf(e), StringComparer.OrdinalIgnoreCase)
				.ToList();
			foreach (IGrouping<string, Endpoint> group in groups)
			{
				string dir = group.Key;
				if (string.IsNullOrWhiteSpace(dir) || group.Count() <= SessionLimitPerClient)
				{
					continue;
				}
				List<Endpoint> overflow = group.OrderBy(e => e.ListenPort).Skip(SessionLimitPerClient).ToList();
				LogHelper.Log("[会话上限] " + Path.GetFileName(dir) + " 已有 " + group.Count()
					+ " 个会话（上限 " + SessionLimitPerClient + "），多余 " + overflow.Count + " 个准备过渡");
				bool overflowed = false;
				foreach (Endpoint e in overflow)
				{
					string? alt = FindOverflowClientDir(all, e, dir);
					if (string.IsNullOrWhiteSpace(alt))
					{
						continue;
					}
					string user = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
					string account = user + "@127.0.0.1:" + e.ListenPort;
					// 原客户端要先把账号撤掉，否则两个客户端会同时挂这个会话（等于没搬家）。
					NexusConfigService.RemoveAccount(dir, account);
					PersistEndpointClientDir(e, alt!);
					e.ClientDir = alt!;
					EnsureBotAccount(e.ServerHint, user, e.ListenPort, "机器人代理", e.CardKey, alt!);
					moved++;
					overflowed = true;
					LogHelper.Log("[会话上限] 已过渡「" + user + "」127.0.0.1:" + e.ListenPort + "："
						+ Path.GetFileName(dir) + " → " + Path.GetFileName(alt!));
				}
				WarnSessionLimit(dir, group.Count(), overflowed);
				if (!overflowed)
				{
					LogHelper.Log("[会话上限] " + Path.GetFileName(dir) + " 没有第二个同服客户端可过渡；"
						+ "请在 PCL 里再复制一份同名挂号版实例，程序会自动把超出的会话分过去");
				}
			}
			return moved;
		}
		catch (Exception ex)
		{
			LogHelper.Log("[会话上限] 过渡失败：" + ex.Message);
			return 0;
		}
	}

	/// <summary>找一个同服、还有空位的第二个客户端目录（单客户端不超过 10 个会话）。</summary>
	private static string? FindOverflowClientDir(List<Endpoint> all, Endpoint e, string excludeDir)
	{
		string name = Path.GetFileName(excludeDir) ?? "";
		string keyword = name.Contains("元素", StringComparison.Ordinal) ? "元素"
			: name.Contains("失落", StringComparison.Ordinal) ? "失落" : "";
		if (keyword.Length == 0)
		{
			return null;
		}
		foreach (string dir in EnumerateClientDirs())
		{
			if (string.Equals(dir, excludeDir, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (Path.GetFileName(dir).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			if (CountBotSessions(all, dir) >= SessionLimitPerClient)
			{
				continue;
			}
			return dir;
		}
		return null;
	}

	/// <summary>把卡片的「账号写到哪个客户端」落盘（自动过渡结果）。</summary>
	private static void PersistEndpointClientDir(Endpoint e, string clientDir)
	{
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			NexusEndpointSetting? item = cfg.NexusEndpoints
				.FirstOrDefault(x => string.Equals(CardKeyOf(x), e.CardKey, StringComparison.OrdinalIgnoreCase));
			if (item == null)
			{
				item = new NexusEndpointSetting
				{
					Key = e.Key,
					RoleName = e.RoleName ?? "",
					ListenPort = e.ListenPort,
					Enabled = e.Enabled,
					AutoStart = e.AutoStart
				};
				cfg.NexusEndpoints.Add(item);
			}
			if (string.Equals(item.ClientDir ?? "", clientDir, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			item.ClientDir = clientDir;
			SettingsManager.Save(cfg);
		}
		catch (Exception ex)
		{
			LogHelper.Log("[会话上限] 记录过渡客户端失败：" + ex.Message);
		}
	}

	/// <summary>卡片启动时调用：该客户端已到 / 超过 10 个会话就提醒一次（不拦截）。</summary>
	public static void WarnSessionLimitIfNeeded(string clientDir, int count)
	{
		if (count > SessionLimitPerClient)
		{
			WarnSessionLimit(clientDir, count, false);
		}
	}

	/// <summary>单客户端会话达到上限的提醒：只提醒不拦截，每个客户端最多 3 次。</summary>
	private static void WarnSessionLimit(string clientDir, int count, bool overflowed)	{
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			cfg.SessionLimitWarnCount ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			string key = Path.GetFileName(clientDir) ?? clientDir;
			cfg.SessionLimitWarnCount.TryGetValue(key, out int shown);
			if (shown >= 3)
			{
				return;
			}
			cfg.SessionLimitWarnCount[key] = shown + 1;
			SettingsManager.Save(cfg);
			ShowSessionLimitWarn(key, count, overflowed);
		}
		catch (Exception ex)
		{
			LogHelper.Log("[会话上限] 提醒失败：" + ex.Message);
		}
	}

	private static void ShowSessionLimitWarn(string clientName, int count, bool overflowed)
	{
		try
		{
			MainWindow? win = MainWindow.Instance;
			if (win == null)
			{
				return;
			}
			win.DispatcherQueue.TryEnqueue(async () =>
			{
				try
				{
					if (win.Content?.XamlRoot == null)
					{
						return;
					}
					var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
					{
						Title = "单客户端会话已达上限",
						Content = MhyunBox.Pages.NexusNotice.BuildSessionLimitContent(clientName, count, overflowed),
						PrimaryButtonText = "知道了",
						DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
						XamlRoot = win.Content.XamlRoot
					};
					await DialogGate.ShowAsync(dialog);
				}
				catch (Exception ex)
				{
					LogHelper.Log("[会话上限] 弹窗失败：" + ex.Message);
				}
			});
		}
		catch (Exception ex)
		{
			LogHelper.Log("[会话上限] 弹窗失败：" + ex.Message);
		}
	}


	/// <summary>
	/// 读取 Nexus 花名册并与本地端口分配合并。
	/// 会同时读取 .minecraft\versions 下所有「挂号版」客户端（元素之诗 / 失落世界各一份），
	/// 这样两套客户端里的角色都能出现在同一张花名册里。
	/// </summary>
	public static List<Endpoint> Load(string gameDir)
	{
		var list = new List<Endpoint>();
		// 自愈：早期版本会把「角色@null:25565」这种空地址绑定写进设置，
		// 它会在卡片列表里留一个假角色并挡住同名卡片创建，这里启动时直接清掉。
		SanitizeSavedEndpoints();
		// 按卡片键（Key + 角色名）建索引：同一账号多个角色的配置各自独立，
		// 旧数据（没有角色名的条目）以裸 Key 作为键，仍然兼容。
		Dictionary<string, NexusEndpointSetting> saved = new(StringComparer.OrdinalIgnoreCase);
		foreach (NexusEndpointSetting item in SettingsManager.Instance.NexusEndpoints ?? new List<NexusEndpointSetting>())
		{
			if (item == null || string.IsNullOrWhiteSpace(item.Key))
			{
				continue;
			}
			string ck = CardKeyOf(item);
			if (!saved.ContainsKey(ck))
			{
				saved[ck] = item;
			}
		}
		int basePort = SettingsManager.Instance.GroupPortBase > 0 ? SettingsManager.Instance.GroupPortBase : 20000;
		int next = basePort;
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach ((NexusConfigService.Account a, string hint) in LoadAllAccounts(gameDir))
		{
			// 程序自己写入客户端的机器人账号（host=127.0.0.1）不重复出卡片，
			// 卡片以「快速启动分组」里那条带角色名的记录为准。
			if (IsPlaceholderHost(a.Host))
			{
				continue;
			}
			var e = new Endpoint
			{
				User = a.User,
				Host = a.Host,
				Port = a.Port,
				AutoReconnect = a.AutoReconnect,
				Group = string.IsNullOrWhiteSpace(a.Group) ? "默认" : a.Group,
				Note = a.Note,
				LastOnline = a.LastOnline,
				ServerHint = hint
			};
			if (!seen.Add(e.Key))
			{
				continue;
			}
			// 客户端来源的卡片没有角色名，saved 字典按卡片键（Key#角色名）索引，裸 Key 直接查
			// 会在「保存条目带角色名」时落空 → 端口/启用状态丢失。回退到同 Key 的第一条条目。
			if (!saved.TryGetValue(e.Key, out NexusEndpointSetting? s))
			{
				s = saved.Values.FirstOrDefault(v => string.Equals(v.Key, e.Key, StringComparison.OrdinalIgnoreCase));
			}
			if (s != null)
			{
				e.ListenPort = s.ListenPort;
				e.Enabled = s.Enabled;
			}
			else
			{
				// 没有显式配置过的账号不参与连接/重连，避免误连
				e.Enabled = false;
			}
			if (e.ListenPort <= 0)
			{
				// 未分配端口时给一个 20000 起的默认值，用户可改；只有确认启用的账号才真正监听
				while (list.Any(x => x.ListenPort == next))
				{
					next++;
				}
				e.ListenPort = next++;
			}
			list.Add(e);
		}
		// 快速启动分组里勾选的「机器人号代理」也会写进 NexusEndpoints：
		// 这些角色不一定在 Nexus 配置里，这里从保存的角色键反解出账号与服务器补进花名册。
		foreach (NexusEndpointSetting savedItem in saved.Values)
		{
			// 同一卡片键（含角色名）已有卡片就跳过；没有角色名的旧条目仍按裸 Key 对齐。
			string itemCardKey = CardKeyOf(savedItem);
			// 与分组循环同一套合并规则：保存条目带角色名时，允许并入「同 Key 且还没角色名」的
			// 客户端来源卡片——否则这里会新增一张卡，随后 ApplyRoleNames 把两张卡改成同一个名字，
			// 出现「启动机器人后多出一张一模一样的卡片」。
			Endpoint? sameBase = list.FirstOrDefault(x => string.Equals(x.Key, savedItem.Key, StringComparison.OrdinalIgnoreCase)
				&& (string.IsNullOrWhiteSpace(x.RoleName)
					|| string.Equals(x.RoleName, savedItem.RoleName, StringComparison.OrdinalIgnoreCase)));
			if (sameBase != null)
			{
				if (string.IsNullOrWhiteSpace(sameBase.RoleName) && !string.IsNullOrWhiteSpace(savedItem.RoleName))
				{
					sameBase.RoleName = savedItem.RoleName;
				}
				if (sameBase.ListenPort <= 0 && savedItem.ListenPort > 0)
				{
					sameBase.ListenPort = savedItem.ListenPort;
				}
				sameBase.Enabled = savedItem.Enabled;
				sameBase.AutoStart = savedItem.AutoStart;
				if (string.IsNullOrWhiteSpace(sameBase.ClientDir))
				{
					sameBase.ClientDir = savedItem.ClientDir ?? "";
				}
				continue;
			}
			if (list.Any(x => string.Equals(x.CardKey, itemCardKey, StringComparison.OrdinalIgnoreCase)
				|| (string.IsNullOrWhiteSpace(savedItem.RoleName)
					&& string.Equals(x.Key, savedItem.Key, StringComparison.OrdinalIgnoreCase))))
			{
				continue;
			}
			if (!TryParseKey(savedItem.Key, out string user, out string host, out int port))
			{
				continue;
			}
			if (IsInvalidHost(host) || port <= 0)
			{
				LogHelper.Log("[实例多开] 忽略空地址角色绑定：" + savedItem.Key);
				continue;
			}
			list.Add(new Endpoint
			{
				User = user,
				Host = host,
				Port = port,
				ListenPort = savedItem.ListenPort,
				Enabled = savedItem.Enabled,
				Group = "机器人代理",
				RoleName = savedItem.RoleName ?? "",
				ServerHint = InferServerHint(host),
				ClientDir = savedItem.ClientDir ?? ""
			});
		}
		// 快速启动分组里的角色即使还没启用 / 还没启动，也要先出现在实例多开卡片里；
		// 端口以分组里角色的设置为准，启用状态仍以已保存的设置为准，分组名决定它是机器人还是脱盒代理。
		// 旧数据（裸 Key 条目）继承记录：每次 Load 独立，第一个匹配到的角色继承旧配置，其余角色走自己的端口/默认状态。
		var claimedLegacyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ProxyGroup group in SettingsManager.Instance.ProxyGroups ?? new List<ProxyGroup>())
		{
			string groupTag = (group.Name ?? "").Contains("脱盒", StringComparison.OrdinalIgnoreCase) ? "脱盒代理" : "机器人代理";
			foreach (ProxyGroupRole role in group.Roles ?? new List<ProxyGroupRole>())
			{
				string key = KeyOf(role, group);
				if (string.IsNullOrWhiteSpace(role.Account) || !TryParseKey(key, out string user, out string host, out int port))
				{
					continue;
				}
				if (IsInvalidHost(host) || port <= 0)
				{
					continue;
				}
				// 同一账号 + 同一服务器可以有多个角色（不同本地端口）：
				// 不能只用 baseKey 去重，否则快速启动分组里的同名账号只会显示一张卡。
				// 规则：
				//   1) baseKey + 角色名完全一致 → 合并；
				//   2) 已有 baseKey 卡片还没角色名 → 先并入这条角色，避免重复卡；
				//   3) baseKey 相同但角色名不同 → 新增一张卡，这是真正的多会话。
				string roleName = role.RoleName ?? "";
				Endpoint? existing = null;
				if (!string.IsNullOrWhiteSpace(roleName))
				{
					existing = list.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)
						&& string.Equals(x.RoleName ?? "", roleName, StringComparison.OrdinalIgnoreCase))
						?? list.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)
							&& string.IsNullOrWhiteSpace(x.RoleName));
				}
				else
				{
					existing = list.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
				}
				if (existing != null)
				{
					// 分组归属优先：避免脱盒角色被当成机器人写账号 / 挂会话。
					existing.Group = groupTag;
					if (string.IsNullOrWhiteSpace(existing.RoleName) && !string.IsNullOrWhiteSpace(roleName))
					{
						existing.RoleName = roleName;
					}
					if (string.IsNullOrWhiteSpace(existing.ServerHint))
					{
						existing.ServerHint = InferServerHint(host);
					}
					// 快速启动里改过的角色端口优先：端口不是写死的，用户调整后必须立即生效；
					// 端口被占用时 Start 会自动改绑空闲端口并写回这里。
					if (role.ListenPort >= 1 && role.ListenPort <= 65535)
					{
						existing.ListenPort = role.ListenPort;
					}
					continue;
				}
				// 每个角色先按自己的卡片键找配置；找不到才回退到旧数据（没有角色名的裸 Key 条目），
				// 且同一旧条目只允许被一个角色继承，避免多角色共用同一份端口/启用状态。
				NexusEndpointSetting? roleSaved = null;
				string roleCardKey = string.IsNullOrWhiteSpace(roleName) ? key : key + "#" + roleName;
				if (!saved.TryGetValue(roleCardKey, out roleSaved) && !string.IsNullOrWhiteSpace(roleName))
				{
					if (saved.TryGetValue(key, out NexusEndpointSetting? legacy) && claimedLegacyKeys.Add(key))
					{
						roleSaved = legacy;
					}
				}
				int listenPort = role.ListenPort >= 1 && role.ListenPort <= 65535 ? role.ListenPort : (roleSaved?.ListenPort ?? 0);
				if (listenPort <= 0)
				{
					while (list.Any(x => x.ListenPort == next))
					{
						next++;
					}
					listenPort = next++;
				}
			list.Add(new Endpoint
			{
				User = user,
				Host = host,
				Port = port,
				ListenPort = listenPort,
				Enabled = roleSaved?.Enabled ?? false,
				AutoStart = roleSaved?.AutoStart ?? false,
				Group = groupTag,
				RoleName = roleName,
				ServerHint = InferServerHint(host),
				ClientDir = roleSaved?.ClientDir ?? ""
			});
			}
		}
		ApplyRoleNames(list);
		// 「自启动」必须以保存值为准，且必须放在 ApplyRoleNames 之后（角色名是卡片键的一部分）。
		// 旧实现只在“新建卡片”的分支里读 saved.AutoStart：客户端花名册来源的卡片、
		// 以及被分组角色合并过的卡片，每次 Load 都会把 AutoStart 重置成 false。
		// 而会话多开页每 2 秒按 signature 重建卡片 —— 只要任何一个卡片的状态变了，
		// 所有卡片一起重新取值，用户勾过的「自启动」就会连同别的卡片一起被弹掉。
		foreach (Endpoint e in list)
		{
			if (saved.TryGetValue(e.CardKey, out NexusEndpointSetting? cardSaved))
			{
				e.AutoStart = cardSaved.AutoStart;
			}
		}
		// 旧数据（没有角色名的裸 Key 条目）只在「该账号/服务器下只有这一张卡」时继承，
		// 否则一张卡勾上会把同账号的兄弟角色一起带勾（多角色共用一条裸 Key 条目）。
		Dictionary<string, int> cardCountByKey = new(StringComparer.OrdinalIgnoreCase);
		foreach (Endpoint e in list)
		{
			cardCountByKey.TryGetValue(e.Key, out int n);
			cardCountByKey[e.Key] = n + 1;
		}
		foreach (Endpoint e in list)
		{
			if (saved.ContainsKey(e.CardKey))
			{
				continue;
			}
			if (cardCountByKey.TryGetValue(e.Key, out int cards) && cards == 1
				&& saved.TryGetValue(e.Key, out NexusEndpointSetting? baseSaved))
			{
				e.AutoStart = baseSaved.AutoStart;
			}
		}
		// 兜底去重：ApplyRoleNames 可能给两张同 Key 的卡盖上同一个角色名（历史脏数据），
		// 按卡片键合并成一张，保留端口/启用/自启动里更有效的那个值。
		for (int i = 0; i < list.Count; i++)
		{
			for (int j = list.Count - 1; j > i; j--)
			{
				if (!string.Equals(list[i].CardKey, list[j].CardKey, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				Endpoint keep = list[i], drop = list[j];
				if (keep.ListenPort <= 0 && drop.ListenPort > 0)
				{
					keep.ListenPort = drop.ListenPort;
				}
				keep.Enabled = keep.Enabled || drop.Enabled;
				keep.AutoStart = keep.AutoStart || drop.AutoStart;
				if (string.IsNullOrWhiteSpace(keep.Group) || keep.Group == "默认")
				{
					keep.Group = drop.Group;
				}
				list.RemoveAt(j);
				LogHelper.Log("[实例多开] 合并重复卡片：" + keep.CardKey);
			}
		}
		// 主页「删除卡片」按卡片键（Key + 角色名）隐藏该角色，同账号的其它角色卡不受影响；
		// 旧数据（裸 Key）只对没有角色名的卡片生效，避免删一张卡把同账号的兄弟角色全藏掉。
		PurgeStaleHiddenCards();
		var hidden = new HashSet<string>(SettingsManager.Instance.NexusHiddenCards ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
		if (hidden.Count > 0)
		{
			int removed = list.RemoveAll(e => hidden.Contains(e.CardKey)
				|| (string.IsNullOrWhiteSpace(e.RoleName) && hidden.Contains(e.Key)));
			if (removed > 0)
			{
				LogHelper.Log("[实例多开] 已隐藏 " + removed + " 个被删除卡片的角色");
			}
		}
		ResolvePortConflicts(list);
		// 单客户端 10 个会话到顶：不锁功能，只提醒 + 把多出来的卡片过渡到第二个同服客户端。
		ApplySessionOverflow(list);
		LogAlignment(list);
		return list;
	}

	/// <summary>
	/// 本地端口冲突自愈：127.0.0.1 的端口是全局唯一的，两张卡被分到同一个端口时，
	/// 后一张永远绑不上（监听漂移到别的端口 → 卡片上「已启动」和实际端口对不上，
	/// 看起来就是「机器人起不来 / 卡片接不上信息」）。
	/// 按 list 顺序保留先出现的卡（分组角色排在前面，优先保留显式分配的端口），
	/// 后面的挪到同区间空闲端口并立刻写回设置，避免每 2 秒的 Load 反复改。
	/// </summary>
	private static void ResolvePortConflicts(List<Endpoint> list)
	{
		try
		{
			var used = new HashSet<int>();
			int reserved = SettingsManager.Instance.ProxyListenPort;
			if (reserved > 0)
			{
				used.Add(reserved);
			}
			int moved = 0;
			foreach (Endpoint e in list)
			{
				if (e.ListenPort <= 0)
				{
					continue;
				}
				if (used.Add(e.ListenPort))
				{
					continue;
				}
				int next = Math.Max(20000, e.ListenPort + 1);
				while (used.Contains(next) || next > 65535)
				{
					next++;
					if (next > 65535)
					{
						next = 20001;
						break;
					}
				}
				int old = e.ListenPort;
				e.ListenPort = next;
				used.Add(next);
				moved++;
				PersistEndpointPort(e, next);
				LogHelper.Log("[实例多开] 端口冲突：角色「" + (string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName)
					+ "」原端口 " + old + " 已被占用，改用 " + next + "（目标 " + e.ServerKey + "）");
				// 客户端里的账号必须跟着改：否则游戏仍旧连旧端口，而旧端口现在属于另一台服务器的角色，
				// 角色会「莫名跑到别的服务器去」（这正是元素之诗里出现失落世界角色的原因之一）。
				if (IsBotGroup(e) && e.Enabled)
				{
					string botUser = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
					EnsureBotAccount(e.ServerHint, botUser, next, "机器人代理", e.CardKey, ClientDirOf(e));
				}
			}
			if (moved > 0)
			{
				LogHelper.Log("[实例多开] 端口冲突自愈：本次调整 " + moved + " 张卡片的本地端口");
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 端口冲突自愈失败：" + ex.Message);
		}
	}

	/// <summary>上一次「端口对准」日志的内容签名：Load 每 2 秒调用一次，内容没变不重复打。</summary>
	private static string _lastAlignmentSignature = "";

	/// <summary>
	/// 自愈：恢复被误隐藏的卡片。
	/// 「删除卡片」是两步操作 —— 先从快速启动组里删掉角色，再把卡片键写进 NexusHiddenCards。
	/// 所以只要隐藏条目对应的角色仍然留在某个快速启动组里，这条隐藏就一定不是用户想要的结果
	/// （批量重启卡死时连点删除、或旧版本遗留），直接放出来。
	/// </summary>
	private static void PurgeStaleHiddenCards()
	{
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			List<string>? hidden = cfg.NexusHiddenCards;
			if (hidden == null || hidden.Count == 0)
			{
				return;
			}
			var stillInGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (ProxyGroup group in cfg.ProxyGroups ?? new List<ProxyGroup>())
			{
				foreach (ProxyGroupRole role in group.Roles ?? new List<ProxyGroupRole>())
				{
					if (role == null || string.IsNullOrWhiteSpace(role.Account))
					{
						continue;
					}
					string baseKey = KeyOf(role, group);
					stillInGroups.Add(baseKey);
					if (!string.IsNullOrWhiteSpace(role.RoleName))
					{
						stillInGroups.Add(baseKey + "#" + role.RoleName);
					}
				}
			}
			if (stillInGroups.Count == 0)
			{
				return;
			}
			// 墓碑优先：用户明确删掉的卡片就算角色还留在分组里也不能放出来，
			// 否则「删了又出现」永远停不下来。
			int restored = hidden.RemoveAll(k => stillInGroups.Contains(k) && !IsBotTombstoned(k));
			if (restored > 0)
			{
				SettingsManager.Save(cfg);
				LogHelper.Log("[实例多开] 自愈：已恢复 " + restored + " 个被隐藏的卡片（对应角色仍在快速启动分组里）");
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 恢复隐藏卡片失败：" + ex.Message);
		}
	}

	/// <summary>
	/// 把每个角色的「本地监听端口 → 实际中继目标」打一次日志，
	/// 用来核对不同服务器下机器人代理端口有没有对准；顺带提示端口复用。
	/// </summary>
	private static void LogAlignment(List<Endpoint> list)
	{
		try
		{
			var sb = new System.Text.StringBuilder();
			foreach (Endpoint e in list.OrderBy(x => x.ListenPort))
			{
				(string host, int port) = ResolveTarget(e);
				sb.Append(e.Key).Append('|').Append(e.ListenPort).Append('>')
					.Append(host).Append(':').Append(port).Append('|')
					.Append(e.Enabled).Append('|').Append(e.Group).Append('\n');
			}
			string signature = sb.ToString();
			if (signature == _lastAlignmentSignature)
			{
				return;
			}
			_lastAlignmentSignature = signature;
			foreach (Endpoint e in list.OrderBy(x => x.ListenPort))
			{
				(string host, int port) = ResolveTarget(e);
				string name = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
				LogHelper.Log("[端口对准] " + name + " 本地 127.0.0.1:" + e.ListenPort
					+ " → " + host + ":" + port + "（" + e.Group + (e.Enabled ? "，已启用" : "，未启用") + "）");
			}
			foreach (IGrouping<int, Endpoint> reused in list.Where(x => x.ListenPort > 0)
				.GroupBy(x => x.ListenPort).Where(g => g.Count() > 1))
			{
				LogHelper.Log("[端口对准] 注意：本地端口 " + reused.Key + " 被多个角色共用："
					+ string.Join("、", reused.Select(x => string.IsNullOrWhiteSpace(x.RoleName) ? x.User : x.RoleName)));
			}
		}
		catch
		{
		}
	}

	/// <summary>把快速启动分组里的角色换算成花名册键：账号@服务器host:port（默认 25565）。</summary>
	public static string KeyOf(ProxyGroupRole role, ProxyGroup group)
	{
		string server = string.IsNullOrWhiteSpace(role.ServerName) ? (group?.ServerName ?? "") : role.ServerName;
		string host = server ?? "";
		int port = 25565;
		int colon = host.LastIndexOf(':');
		if (colon > 0 && int.TryParse(host.Substring(colon + 1), out int parsed))
		{
			port = parsed;
			host = host.Substring(0, colon);
		}
		return (role.Account ?? "") + "@" + host + ":" + port;
	}

	/// <summary>把快速启动分组里的角色名映射到花名册条目，卡片标题用它代替账号显示。</summary>
	private static void ApplyRoleNames(List<Endpoint> list)
	{
		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (ProxyGroup group in SettingsManager.Instance.ProxyGroups ?? new List<ProxyGroup>())
		{
			foreach (ProxyGroupRole role in group.Roles ?? new List<ProxyGroupRole>())
			{
				string server = string.IsNullOrWhiteSpace(role.ServerName) ? group.ServerName : role.ServerName;
				string host = server ?? "";
				int port = 25565;
				int colon = host.LastIndexOf(':');
				if (colon > 0 && int.TryParse(host.Substring(colon + 1), out int parsed))
				{
					port = parsed;
					host = host.Substring(0, colon);
				}
				string key = role.Account + "@" + host + ":" + port;
				if (!map.ContainsKey(key) && !string.IsNullOrWhiteSpace(role.RoleName))
				{
					map[key] = role.RoleName;
				}
			}
		}
		foreach (Endpoint e in list)
		{
			// 分组里已经带上角色名的卡片不能被 baseKey 映射覆盖，
			// 否则同一账号的多个角色会被改成同一个名字。
			if (map.TryGetValue(e.Key, out string? name) && string.IsNullOrWhiteSpace(e.RoleName))
			{
				e.RoleName = name;
			}
		}
	}

	/// <summary>读取主客户端 + 其它「挂号版」客户端里的全部 Nexus 账号，附带版本对应的服务器提示。</summary>
	private static List<(NexusConfigService.Account Account, string Hint)> LoadAllAccounts(string gameDir)
	{
		var result = new List<(NexusConfigService.Account, string)>();
		var dirs = new List<string>();
		void AddDir(string dir)
		{
			if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
			{
				return;
			}
			if (!dirs.Any(x => string.Equals(x, dir, StringComparison.OrdinalIgnoreCase)))
			{
				dirs.Add(dir);
			}
		}
		AddDir(gameDir);
		try
		{
			string pcl = (SettingsManager.Instance.PclPath ?? "").Trim();
			string versions = Path.Combine(Path.GetDirectoryName(pcl) ?? "", ".minecraft", "versions");
			if (Directory.Exists(versions))
			{
				foreach (string dir in Directory.EnumerateDirectories(versions))
				{
					string name = Path.GetFileName(dir);
					if (!name.Contains("挂号版", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					if (IsBackupDir(dir))
					{
						// 备份副本不参与花名册读取，否则会出现重复角色/端口。
						continue;
					}
					if (!File.Exists(Path.Combine(dir, "config", "mythosnexus.json")))
					{
						continue;
					}
					AddDir(dir);
				}
			}
		}
		catch
		{
		}
		foreach (string dir in dirs)
		{
			string hint = InferServerHint(Path.GetFileName(dir));
			try
			{
				NexusConfigService.Snapshot snapshot = NexusConfigService.Load(dir);
				foreach (NexusConfigService.Account account in snapshot.Accounts ?? new List<NexusConfigService.Account>())
				{
					result.Add((account, hint));
				}
			}
			catch (Exception ex)
			{
				LogHelper.Log("[实例多开] 读取客户端花名册失败（" + dir + "）：" + ex.Message);
			}
		}
		return result;
	}

	/// <summary>从版本目录名/服务器名推断它属于哪个服务器预设。</summary>
	private static string InferServerHint(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}
		if (text.Contains("元素", StringComparison.OrdinalIgnoreCase))
		{
			return "元素之诗";
		}
		if (text.Contains("失落", StringComparison.OrdinalIgnoreCase))
		{
			return "失落世界";
		}
		return "";
	}

	/// <summary>
	/// 把花名册里的「服务器名 / 127.0.0.1 / null」解析成程序内预设的真实地址。
	/// 机器人代理列表里存的是「元素之诗:25565」这种展示名，真正中继时必须换成
	/// netease1.yszscraft.cn:25019 之类的实际地址，否则 DNS 解析不到。
	/// </summary>
	private static (string Host, int Port) ResolveTarget(Endpoint e)
	{
		string host = (e.Host ?? "").Trim();
		int port = e.Port > 0 ? e.Port : 25565;
		ServerPreset? preset = FindPreset(host);
		if (preset == null && IsPlaceholderHost(host))
		{
			string hint = !string.IsNullOrWhiteSpace(e.ServerHint) ? e.ServerHint : (SettingsManager.Instance.ProxyPresetName ?? "");
			preset = FindPreset(hint) ?? FindPreset("元素之诗");
		}
		if (preset != null)
		{
			return (preset.Host, preset.Port);
		}
		return (host, port);
	}

	private static ServerPreset? FindPreset(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return null;
		}
		ServerPreset? preset = ServerPresets.Find(name);
		if (preset != null)
		{
			return preset;
		}
		preset = ServerPresets.All.FirstOrDefault(p => string.Equals(p.Host, name, StringComparison.OrdinalIgnoreCase));
		if (preset != null)
		{
			return preset;
		}
		return ServerPresets.All.FirstOrDefault(p =>
			(!string.IsNullOrWhiteSpace(p.Name) && name.Contains(p.Name, StringComparison.OrdinalIgnoreCase))
			|| (!string.IsNullOrWhiteSpace(name) && p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
	}

	/// <summary>
	/// 元素之诗机器人代理的「提醒阈值」（失落世界不限）。
	/// 2026-11-17 起不再硬性限制数量：超过该值只弹一次提醒（最多 3 次），照常启动。
	/// </summary>
	private const int ElementPoetryWarnAt = 5;

	/// <summary>提醒弹窗最多出现的次数（跨进程持久化在 AppSettings.ElementPoetryWarnCount）。</summary>
	private const int ElementPoetryWarnLimit = 3;

	/// <summary>
	/// 把程序的「调试模式」同步给模组：每个客户端目录写 config/nexus-debug.txt（1/0）。
	/// 模组 5 秒复查一次该文件，关闭后不再产出 nexus-memory.log 等诊断日志。
	/// </summary>
	public static void SyncModDebugFlag(bool on)
	{
		try
		{
			string dir = ResolveGameDir();
			if (!string.IsNullOrWhiteSpace(dir))
			{
				try
				{
					string cfgDir = Path.Combine(dir, "config");
					Directory.CreateDirectory(cfgDir);
					File.WriteAllText(Path.Combine(cfgDir, "nexus-debug.txt"), on ? "1" : "0");
				}
				catch
				{
				}
			}
			LogHelper.Log("[调试模式] 模组诊断日志开关 → " + (on ? "开" : "关"));
		}
		catch (Exception ex)
		{
			LogHelper.Log("[调试模式] 同步模组开关失败：" + ex.Message);
		}
	}

	/// <summary>目标地址是否指向元素之诗预设服务器。</summary>
	private static bool IsElementPoetryTarget(string host, int port)
	{
		ServerPreset? preset = ServerPresets.Find("元素之诗");
		return preset != null
			&& string.Equals(preset.Host, host, StringComparison.OrdinalIgnoreCase)
			&& preset.Port == port;
	}

	/// <summary>
	/// 批量启动/停止进行中（由会话多开页置位）。此时不弹模态对话框：
	/// 批量在后台线程串行跑，逐个弹窗会把整段操作卡住，看起来就是「未响应」。
	/// 超限提醒先记账，批量结束后由 <see cref="FlushPendingElementPoetryWarn"/> 合并成一次。
	/// </summary>
	public static volatile bool BatchMode;

	private static int _pendingWarnCount;

	/// <summary>批量结束后调用（主线程）：把批量期间的超限提醒合并弹一次。</summary>
	public static void FlushPendingElementPoetryWarn()
	{
		int pending = System.Threading.Interlocked.Exchange(ref _pendingWarnCount, 0);
		if (pending > 0)
		{
			ShowElementPoetryWarn(pending);
		}
	}

	/// <summary>
	/// 元素之诗第 6 个及以后机器人代理的提醒弹窗（最多 3 次，计数持久化在 AppSettings）。
	/// 只提醒、不拦截：调用方继续正常启动该机器人。
	/// 文案先占位，等正式文案直接替换 Text 即可。
	/// </summary>
	private static void WarnElementPoetryOverLimit(int willBeCount)
	{
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			if (cfg.ElementPoetryWarnCount >= ElementPoetryWarnLimit)
			{
				return;
			}
			cfg.ElementPoetryWarnCount++;
			SettingsManager.Save(cfg);
			LogHelper.Log("[实例多开] 元素之诗机器人代理第 " + willBeCount + " 个，提醒 "
				+ cfg.ElementPoetryWarnCount + "/" + ElementPoetryWarnLimit);
			if (BatchMode)
			{
				_pendingWarnCount = willBeCount;
				return;
			}
			ShowElementPoetryWarn(willBeCount);
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 元素之诗提醒失败：" + ex.Message);
		}
	}

	private static void ShowElementPoetryWarn(int willBeCount)
	{
		try
		{
			MainWindow? win = MainWindow.Instance;
			if (win == null)
			{
				return;
			}
			win.DispatcherQueue.TryEnqueue(async () =>
			{
				try
				{
					if (win.Content?.XamlRoot == null)
					{
						return;
					}
					var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
					{
						Title = "元素之诗 · 机器人代理数量提醒",
						Content = MhyunBox.Pages.NexusNotice.BuildElementPoetryContent(willBeCount),
						PrimaryButtonText = "知道了",
						CloseButtonText = "继续启动",
						DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
						XamlRoot = win.Content.XamlRoot
					};
					await DialogGate.ShowAsync(dialog);
				}
				catch (Exception ex)
				{
					LogHelper.Log("[实例多开] 元素之诗提醒弹窗失败：" + ex.Message);
				}
			});
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 元素之诗提醒失败：" + ex.Message);
		}
	}

	private static bool IsPlaceholderHost(string host)
	{
		if (string.IsNullOrWhiteSpace(host))
		{
			return true;
		}
		string value = host.Trim();
		return string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(value, "127.0.0.1", StringComparison.Ordinal)
			|| string.Equals(value, "::1", StringComparison.Ordinal)
			|| string.Equals(value, "0.0.0.0", StringComparison.Ordinal);
	}

	/// <summary>真正的空/占位主机（不含 127.0.0.1，机器人账号本来就指向本机代理）。</summary>
	private static bool IsInvalidHost(string host)
	{
		if (string.IsNullOrWhiteSpace(host))
		{
			return true;
		}
		string value = host.Trim();
		return string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(value, "(null)", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(value, "undefined", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>清理设置里遗留的空地址角色绑定（如 Notel@null:25565）并持久化。</summary>
	private static void SanitizeSavedEndpoints()
	{
		AppSettings cfg = SettingsManager.Instance;
		List<NexusEndpointSetting>? raw = cfg.NexusEndpoints;
		if (raw == null || raw.Count == 0)
		{
			return;
		}
		var cleaned = new List<NexusEndpointSetting>(raw.Count);
		foreach (NexusEndpointSetting item in raw)
		{
			if (item == null || string.IsNullOrWhiteSpace(item.Key))
			{
				continue;
			}
			if (!TryParseKey(item.Key, out _, out string host, out int port)
				|| IsInvalidHost(host) || port <= 0)
			{
				LogHelper.Log("[实例多开] 已清理无效角色绑定：" + item.Key);
				continue;
			}
			cleaned.Add(item);
		}
		if (cleaned.Count != raw.Count)
		{
			cfg.NexusEndpoints = cleaned;
			SettingsManager.Save(cfg);
		}
	}

	/// <summary>把 user@host:port 拆回角色、服务器与端口。</summary>
	private static bool TryParseKey(string key, out string user, out string host, out int port)
	{
		user = "";
		host = "";
		port = 25565;
		if (string.IsNullOrWhiteSpace(key))
		{
			return false;
		}
		int at = key.LastIndexOf('@');
		if (at <= 0 || at >= key.Length - 1)
		{
			return false;
		}
		user = key.Substring(0, at);
		string server = key.Substring(at + 1);
		int colon = server.LastIndexOf(':');
		if (colon <= 0 || colon >= server.Length - 1)
		{
			host = server;
			return true;
		}
		host = server.Substring(0, colon);
		return int.TryParse(server.Substring(colon + 1), out port) && port > 0;
	}

	/// <summary>保存本地端口 / 启用状态 / 每卡片自启动开关（写回 AppSettings）。角色名随条目一起存，删除/启用按卡片键精确匹配。</summary>
	public static void Save(IEnumerable<Endpoint> endpoints)
	{
		AppSettings cfg = SettingsManager.Instance;
		cfg.NexusEndpoints = endpoints
			.Select(e => new NexusEndpointSetting
			{
				Key = e.Key,
				RoleName = e.RoleName ?? "",
				ListenPort = e.ListenPort,
				Enabled = e.Enabled,
				AutoStart = e.AutoStart
			})
			.ToList();
		SettingsManager.Save(cfg);
	}

	/// <summary>只更新一张卡片的「自启动机器人代理」勾选并持久化；不影响其它卡片。</summary>
	public static bool SetCardAutoStart(Endpoint e, bool autoStart)
	{
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			string cardKey = e.CardKey;
			NexusEndpointSetting? item = (cfg.NexusEndpoints ?? new List<NexusEndpointSetting>())
				.FirstOrDefault(x => string.Equals(CardKeyOf(x), cardKey, StringComparison.OrdinalIgnoreCase));
			bool changed = false;
			if (item == null)
			{
				cfg.NexusEndpoints.Add(new NexusEndpointSetting
				{
					Key = e.Key,
					RoleName = e.RoleName ?? "",
					ListenPort = e.ListenPort,
					Enabled = e.Enabled,
					AutoStart = autoStart
				});
				changed = true;
			}
			else
			{
				// 旧数据是「裸 Key」条目（没有角色名）。直接改它会连带影响同账号的其它
				// 角色卡（那些卡在 Load 里也回退到同一条），所以先把它升级成带角色名的
				// 卡片键，让这张卡的开关从此独立。
				if (string.IsNullOrWhiteSpace(item.RoleName) && !string.IsNullOrWhiteSpace(e.RoleName))
				{
					item.RoleName = e.RoleName;
					changed = true;
				}
				if (item.AutoStart != autoStart)
				{
					item.AutoStart = autoStart;
					changed = true;
				}
			}
			if (changed)
			{
				SettingsManager.Save(cfg);
				LogHelper.Log("[实例多开] 卡片自启动 = " + (autoStart ? "开" : "关") + "：" + cardKey);
			}
			return true;
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 保存卡片自启动开关失败：" + ex.Message);
			return false;
		}
	}

	/// <summary>
	/// 只启动指定卡片（用于「实例多开」的卡片启动按钮）。按卡片键匹配，同一账号的其它角色不会被连带启动；
	/// 旧调用方传入裸 Key（账号@服务器:端口）时按 baseKey 兜底匹配。
	/// </summary>
	public static int StartMembers(string gameDir, IEnumerable<string> cardKeys)
	{
		var wanted = new HashSet<string>(cardKeys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
		if (wanted.Count == 0)
		{
			return 0;
		}
		List<Endpoint> all = Load(gameDir);
		IEnumerable<Endpoint> source = all.Where(e => wanted.Contains(e.CardKey));
		if (!source.Any())
		{
			source = all.Where(e => wanted.Contains(e.Key));
		}
		int started = Start(source);
		// 批量模式（会话多开的批量启动/重启）里每张卡都会走这里：
		// 逐卡重写全部客户端账号既慢又会重复触发登录流程，改为批量结束后统一写一次。
		if (!BatchMode)
		{
			ApplyRoleFiles(all, ProxyCaptureService.ActiveBindingsSnapshot().Select(b => b.ListenPort).ToList());
		}
		return started;
	}

	/// <summary>是否是「机器人代理」卡（只有这类卡才会写客户端机器人账号）。</summary>
	private static bool IsBotGroup(Endpoint e)
	{
		return string.Equals(e.Group, "机器人代理", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 某台客户端上的机器人代理卡片重新写一遍（账号 + 绑定）。
	/// 由 <see cref="GameSessionWatcher"/> 在游戏实例启动 / 进服时调用：
	/// 模组每 3 秒热加载 config/mythosnexus.json，账号一出现就会自动把机器人会话建起来，
	/// 所以「游戏重启后卡片没了、机器人不在了」只需要把卡写回去即可。
	/// </summary>
	public static int RecreateBotCardsForClient(string clientDir)
	{
		if (string.IsNullOrWhiteSpace(clientDir))
		{
			return 0;
		}
		try
		{
			string gameDir = ResolveGameDir();
			if (string.IsNullOrWhiteSpace(gameDir))
			{
				return 0;
			}
			List<Endpoint> all = Load(gameDir);
			int count = 0;
			foreach (Endpoint e in all)
			{
				if (!e.Enabled || !IsBotGroup(e) || e.ListenPort <= 0 || !InDisplayScope(e))
				{
					continue;
				}
				if (!string.Equals(ClientDirOf(e), clientDir, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				string botUser = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
				if (EnsureBotAccount(e.ServerHint, botUser, e.ListenPort, "机器人代理", e.CardKey, ClientDirOf(e)))
				{
					count++;
				}
			}
			return count;
		}
		catch (Exception ex)
		{
			LogHelper.Log("[游戏监听] 重写机器人代理卡片失败：" + ex.Message);
			return 0;
		}
	}

	/// <summary>
	/// 让某台客户端上勾了「自启动」的机器人代理会话重新起来。
	///
	/// 注意：**只补没有在监听的那几个**，已经在跑的保持原样。
	/// 早先的实现是把所有自启动卡全部断开重连，结果 12 个账号在同一秒重新登录，
	/// 网易直接按「登录环境异常 code=32」拒掉一大片（日志里那一串风控就是这个）。
	/// 模组侧已经有 3~6 秒的自动重连，掉线的会话会自己回来，不需要从程序侧硬重启。
	/// 补齐动作放到后台线程逐个执行，每个间隔 1.2 秒，避免同一秒多账号登录。
	/// </summary>
	public static int RestartAutoStartBotsForClient(string clientDir)
	{
		if (string.IsNullOrWhiteSpace(clientDir))
		{
			return 0;
		}
		try
		{
			string gameDir = ResolveGameDir();
			if (string.IsNullOrWhiteSpace(gameDir))
			{
				return 0;
			}
			List<Endpoint> all = Load(gameDir);
			List<Endpoint> targets = all
				.Where(e => e.Enabled && e.AutoStart && IsBotGroup(e) && e.ListenPort > 0 && InDisplayScope(e)
					&& string.Equals(ClientDirOf(e), clientDir, StringComparison.OrdinalIgnoreCase))
				.ToList();
			if (targets.Count == 0)
			{
				return 0;
			}
			HashSet<int> bindingPorts = ProxyCaptureService.ActiveBindingsSnapshot()
				.Select(b => b.ListenPort)
				.ToHashSet();
			List<Endpoint> missing = targets.Where(e => !bindingPorts.Contains(e.ListenPort)).ToList();
			if (missing.Count == 0)
			{
				LogHelper.Log("[游戏监听] 自启动机器人代理 " + targets.Count + " 个都已在监听，无需重启");
				return 0;
			}
			// 后台逐个补监听：同一秒拉起多个账号会被网易判成异常登录环境。
			_ = System.Threading.Tasks.Task.Run(async () =>
			{
				int ok = 0;
				foreach (Endpoint e in missing)
				{
					try
					{
						if (StartMembers(gameDir, new[] { e.CardKey }) > 0)
						{
							ok++;
						}
					}
					catch (Exception ex)
					{
						LogHelper.Log("[游戏监听] 补启动机器人失败（" + e.CardKey + "）：" + ex.Message);
					}
					await System.Threading.Tasks.Task.Delay(1200).ConfigureAwait(false);
				}
				LogHelper.Log("[游戏监听] 补齐自启动机器人代理：" + ok + "/" + missing.Count + " 个已建立监听");
			});
			LogHelper.Log("[游戏监听] 自启动机器人代理 " + targets.Count + " 个，其中 " + missing.Count + " 个缺监听，开始逐个补齐");
			return missing.Count;
		}
		catch (Exception ex)
		{
			LogHelper.Log("[游戏监听] 重启自启动机器人代理失败：" + ex.Message);
			return 0;
		}
	}

	/// <summary>
	/// 快速启动的组端口优先级最高：分组端口重排后，把组内角色的新端口同步到
	/// 「会话多开」的卡片配置（NexusEndpoints）并写进客户端 mythosnexus.json。
	/// 返回被改动的 (角色名, 旧端口, 新端口)，供调用方把在跑的代理挪到新端口。
	/// </summary>
	public static List<(string RoleName, int OldPort, int NewPort)> SyncGroupRolePorts(ProxyGroup group)
	{
		var changed = new List<(string, int, int)>();
		if (group?.Roles == null || group.Roles.Count == 0)
		{
			return changed;
		}
		AppSettings cfg = SettingsManager.Instance;
		List<NexusEndpointSetting> list = cfg.NexusEndpoints ?? new List<NexusEndpointSetting>();
		string server = (group.ServerName ?? "").Trim();
		foreach (ProxyGroupRole role in group.Roles)
		{
			if (role == null || string.IsNullOrWhiteSpace(role.RoleName) || role.ListenPort <= 0)
			{
				continue;
			}
			List<NexusEndpointSetting> matched = list
				.Where(s => s != null && string.Equals((s.RoleName ?? "").Trim(), role.RoleName.Trim(), StringComparison.OrdinalIgnoreCase))
				.ToList();
			if (server.Length > 0)
			{
				List<NexusEndpointSetting> byServer = matched
					.Where(s => (s.Key ?? "").IndexOf(server, StringComparison.OrdinalIgnoreCase) >= 0)
					.ToList();
				if (byServer.Count > 0)
				{
					matched = byServer;
				}
			}
			int oldPort = 0;
			foreach (NexusEndpointSetting s in matched)
			{
				if (s.ListenPort == role.ListenPort)
				{
					continue;
				}
				if (oldPort == 0)
				{
					oldPort = s.ListenPort;
				}
				s.ListenPort = role.ListenPort;
			}
			// 客户端账号文件跟着改，否则游戏/机器人会一直连旧端口
			string hint = string.IsNullOrWhiteSpace(role.ServerName) ? server : role.ServerName;
			if (!string.IsNullOrWhiteSpace(hint))
			{
				EnsureBotAccount(hint, role.RoleName, role.ListenPort);
			}
			if (oldPort != role.ListenPort)
			{
				changed.Add((role.RoleName, oldPort, role.ListenPort));
			}
		}
		SettingsManager.Save(cfg);
		return changed;
	}

	/// <summary>
	/// 把正在跑的某个角色代理从旧端口挪到新端口：先停旧端口（含同角色脱盒监听），
	/// 再按新端口重新建立机器人监听。返回是否重新监听成功。
	/// </summary>
	public static bool MoveRoleProxy(string gameDir, string roleName, int oldPort, int newPort)
	{
		try
		{
			IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> live = ProxyCaptureService.ActiveBindingsSnapshot();
			if (live.Any(b => b.ListenPort == newPort))
			{
				return true;   // 已经在新端口上跑着
			}
			bool hadOld = oldPort > 0 && live.Any(b => b.ListenPort == oldPort
				&& (string.Equals((b.LoginName ?? "").Trim(), (roleName ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
					|| (b.Tag ?? "").StartsWith("nexus-", StringComparison.OrdinalIgnoreCase)));
			if (!hadOld)
			{
				// 该角色本来就没在跑：只改配置，不主动拉起，避免「改个端口就把机器人全拉起来」。
				LogHelper.Log("[快速启动] 端口更新：" + roleName + " " + newPort + "（该角色未在运行，仅改配置）");
				return true;
			}
			if (oldPort > 0)
			{
				ProxyCaptureService.StopBindingForRole(oldPort, roleName);
			}
			List<Endpoint> all = Load(gameDir);
			Endpoint? ep = all.FirstOrDefault(e => e.ListenPort == newPort
					&& string.Equals((e.RoleName ?? "").Trim(), (roleName ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
				?? all.FirstOrDefault(e => e.ListenPort == newPort);
			if (ep == null)
			{
				LogHelper.Log("[快速启动] 端口迁移：找不到端口 " + newPort + " 对应的卡片（角色 " + roleName + "）");
				return false;
			}
			int started = StartMembers(gameDir, new[] { ep.CardKey });
			bool listening = ProxyCaptureService.ActiveBindingsSnapshot().Any(b => b.ListenPort == newPort);
			LogHelper.Log("[快速启动] 端口迁移：" + roleName + " " + oldPort + " → " + newPort
				+ "，已提交 " + started + " 条，监听=" + listening);
			return listening;
		}
		catch (Exception ex)
		{
			LogHelper.Log("[快速启动] 端口迁移失败（" + roleName + " → " + newPort + "）：" + ex.Message);
			return false;
		}
	}

	/// <summary>
	/// 巡检去重（带端口回写）：同一「角色 + 目标服务器」只留一条监听。
	/// 停掉重复端口后，把卡片配置与客户端账号改指向保留的那个端口，
	/// 否则客户端还盯着已经被停掉的端口，表现就是「游戏开了但机器人挂不上」。
	/// 返回停掉的重复监听数。
	/// </summary>
	public static int DedupeRoleProxiesAndRetarget(string gameDir)
	{
		int removed = 0;
		try
		{
			IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> active = ProxyCaptureService.ActiveBindingsSnapshot();
			var keep = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			var duplicates = new List<(int DupPort, int KeepPort, string Login)>();
			foreach (ProxyCaptureService.ActiveBindingInfo b in active)
			{
				string key = (b.LoginName ?? "").Trim() + "|"
					+ (b.TargetHost ?? "").ToLowerInvariant() + ":" + b.TargetPort;
				if (keep.TryGetValue(key, out int keepPort))
				{
					duplicates.Add((b.ListenPort, keepPort, (b.LoginName ?? "").Trim()));
				}
				else
				{
					keep[key] = b.ListenPort;
				}
			}
			if (duplicates.Count == 0)
			{
				return 0;
			}
			AppSettings cfg = SettingsManager.Instance;
			bool changed = false;
			foreach ((int dupPort, int keepPort, string login) in duplicates)
			{
				if (!ProxyCaptureService.StopBindingForRole(dupPort, login))
				{
					continue;
				}
				removed++;
				foreach (NexusEndpointSetting s in cfg.NexusEndpoints ?? new List<NexusEndpointSetting>())
				{
					if (s.ListenPort == dupPort
						&& string.Equals((s.RoleName ?? "").Trim(), login, StringComparison.OrdinalIgnoreCase))
					{
						s.ListenPort = keepPort;
						changed = true;
					}
				}
				LogHelper.Log("[代理抓取] 去重：角色 " + login + " 重复监听 127.0.0.1:" + dupPort
					+ " 已停掉，配置改指向 " + keepPort);
			}
			if (changed)
			{
				SettingsManager.Save(cfg);
				ApplyRoleFilesNow(gameDir);
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[代理抓取] 巡检去重失败：" + ex.Message);
		}
		return removed;
	}

	/// <summary>把客户端账号/端口统一写一次（批量操作结束时由页面调用）。</summary>
	public static void ApplyRoleFilesNow(string gameDir)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(gameDir))
			{
				return;
			}
			List<Endpoint> all = Load(gameDir);
			ApplyRoleFiles(all, ProxyCaptureService.ActiveBindingsSnapshot().Select(b => b.ListenPort).ToList());
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 批量写回客户端账号失败：" + ex.Message);
		}
	}

	/// <summary>
	/// 删除卡片时把快速启动分组里对应的角色一并删掉：
	/// 按「账号@服务器:端口 + 角色名」精确匹配；没有角色名的卡片再按本地端口兜底，
	/// 避免同账号其它角色被误删。返回删除的角色数。
	/// </summary>
	public static int RemoveQuickStartRole(Endpoint e)
	{
		int removed = 0;
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			string roleName = e.RoleName ?? "";
			foreach (ProxyGroup group in cfg.ProxyGroups ?? new List<ProxyGroup>())
			{
				List<ProxyGroupRole>? roles = group.Roles;
				if (roles == null || roles.Count == 0)
				{
					continue;
				}
				for (int i = roles.Count - 1; i >= 0; i--)
				{
					ProxyGroupRole role = roles[i];
					if (!string.Equals(KeyOf(role, group), e.Key, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					bool roleMatch = string.IsNullOrWhiteSpace(roleName)
						? role.ListenPort == e.ListenPort || role.ListenPort <= 0
						: string.Equals(role.RoleName ?? "", roleName, StringComparison.OrdinalIgnoreCase);
					if (!roleMatch)
					{
						continue;
					}
					roles.RemoveAt(i);
					removed++;
					LogHelper.Log("[实例多开] 已删除快速启动角色：" + (string.IsNullOrWhiteSpace(role.RoleName) ? role.Account : role.RoleName)
						+ "（分组 " + group.Name + "）");
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 删除快速启动角色失败：" + ex.Message);
		}
		return removed;
	}

	/// <summary>在快速启动分组里找到这个机器人代理对应的角色（拿 Channel + Account 去匹配网易登录态）。</summary>
	private static ProxyGroupRole? FindBotRole(Endpoint e)
	{
		IEnumerable<ProxyGroup> groups = SettingsManager.Instance.ProxyGroups ?? new List<ProxyGroup>();
		ProxyGroupRole? byName = groups
			.SelectMany(g => g.Roles ?? new List<ProxyGroupRole>())
			.FirstOrDefault(r => !string.IsNullOrWhiteSpace(e.RoleName)
				&& string.Equals(r.RoleName, e.RoleName, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(r.Account, e.User, StringComparison.OrdinalIgnoreCase));
		return byName ?? groups
			.SelectMany(g => g.Roles ?? new List<ProxyGroupRole>())
			.FirstOrDefault(r => string.Equals(r.Account, e.User, StringComparison.OrdinalIgnoreCase));
	}

	private static int Start(IEnumerable<Endpoint> source)
	{
		var bindings = new List<ProxyCaptureService.ProxyBinding>();
		// 绑定与端点的对应关系：StartBatch 可能把端口改成其它空闲端口，
		// 启动完成后要把实际端口写回配置和客户端账号，否则游戏会一直连旧端口。
		var plan = new List<(ProxyCaptureService.ProxyBinding Binding, Endpoint Endpoint, string LoginName, bool IsBot, bool IsMarket)>();
		// 元素之诗服务器最多只允许 5 个机器人代理同时生效；失落世界不限。
		// 只统计机器人侧监听（nexus-bot），不把脱盒代理 / 行情角色算进上限。
		IReadOnlyList<ProxyCaptureService.ActiveBindingInfo> activeBindings = ProxyCaptureService.ActiveBindingsSnapshot();
		int eleBotCount = activeBindings.Count(b => string.Equals(b.Tag, "nexus-bot", StringComparison.OrdinalIgnoreCase)
			&& IsElementPoetryTarget(b.TargetHost ?? "", b.TargetPort));
		string marketKey = (SettingsManager.Instance.MarketAccountKey ?? "").Trim();
		foreach (Endpoint e in source.Where(e => e.Enabled && e.ListenPort > 0 && !string.IsNullOrWhiteSpace(e.User) && !string.IsNullOrWhiteSpace(e.Host)))
		{
			(string host, int port) = ResolveTarget(e);
			bool isBot = string.Equals(e.Group, "机器人代理", StringComparison.OrdinalIgnoreCase);
			// 行情角色只由真人客户端登录（行情模组只作用于真人角色），不再为它建机器人会话。
			bool isMarket = marketKey.Length > 0 && string.Equals(e.Key, marketKey, StringComparison.OrdinalIgnoreCase);
			bool alreadyNexusListening = activeBindings.Any(b => b.ListenPort == e.ListenPort
				&& (b.Tag ?? "").StartsWith("nexus-", StringComparison.OrdinalIgnoreCase));
			if (isBot && !isMarket && IsElementPoetryTarget(host, port) && !alreadyNexusListening)
			{
				eleBotCount++;
				// 硬上限已取消：第 6 个及以后照常启动，只在第一次超过阈值时提醒。
				if (eleBotCount > ElementPoetryWarnAt)
				{
					WarnElementPoetryOverLimit(eleBotCount);
				}
			}
			string loginName = string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName;
			// 同一「角色 + 目标服务器」只允许一条代理。快速启动的脱盒代理和会话多开的
			// 机器人代理会各自算出一个端口（历史上还漂移过），于是同一账号出现两条连接、
			// 互相顶号。这里优先复用已在跑的端口，让两边收敛到同一个端口上。
			int reusePort = 0;
			foreach (ProxyCaptureService.ActiveBindingInfo ab in activeBindings)
			{
				if (string.Equals((ab.LoginName ?? "").Trim(), (loginName ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
					&& string.Equals(ab.TargetHost ?? "", host, StringComparison.OrdinalIgnoreCase)
					&& ab.TargetPort == port)
				{
					reusePort = ab.ListenPort;
					break;
				}
			}
			if (reusePort > 0 && reusePort != e.ListenPort)
			{
				LogHelper.Log("[实例多开] " + loginName + "@" + host + ":" + port
					+ " 已有代理在 127.0.0.1:" + reusePort + "，本次复用该端口（不再新开第二条连接）");
			}
			int authIndex = -1;
			string authKey = "";
			if (isBot || isMarket)
			{
				ProxyGroupRole? role = FindBotRole(e);
				if (role != null)
				{
					authIndex = ProxyStarter.ResolveAccountIndex(role, AccountStore.All.ToList(), out _);
				}
				if (authIndex >= 0 && authIndex < AccountStore.All.Count)
				{
					AccountStore.LocalAccount authAcct = AccountStore.All[authIndex];
					authKey = (authAcct.Channel ?? "") + "|" + (authAcct.Account ?? "");
				}
				if (authIndex < 0)
				{
					LogHelper.Log("[实例多开] 警告：" + e.Key + " 没找到可用的网易登录态，进服会因认证失败被踢（请先在账号管理登录该账号）");
				}
			}
			LogHelper.Log("[实例多开] " + e.User + " 本地 127.0.0.1:" + e.ListenPort + " -> " + e.Host + ":" + e.Port
				+ "（登录名 " + loginName + (authIndex >= 0 ? "，认证账号 #" + authIndex : "，无认证") + "）"
				+ (string.Equals(host, e.Host, StringComparison.OrdinalIgnoreCase) && port == e.Port ? "" : "（预设地址 " + host + ":" + port + "）"));
			ProxyCaptureService.ProxyBinding binding = new ProxyCaptureService.ProxyBinding
			{
				// 已有同角色代理时用它正在跑的端口，避免同账号两条连接互相顶号。
				ListenPort = reusePort > 0 ? reusePort : e.ListenPort,
				LoginName = loginName,
				TargetHost = host,
				TargetPort = port,
				AuthAccountIndex = authIndex,
				AuthAccountKey = authKey,
				Tag = isMarket ? "nexus-market" : (isBot ? "nexus-bot" : "nexus-manual"),
				// 端口不是写死的：被占用时自动换空闲端口，启动后把实际端口写回配置和账号。
				RequireExactPort = false,
				AllowPortFallback = true
			};
			bindings.Add(binding);
			plan.Add((binding, e, loginName, isBot, isMarket));
		}
		if (bindings.Count == 0)
		{
			return 0;
		}
		// StartBatch 是追加监听，不会打断代理启动页已有的监听
		ProxyCaptureService.StartBatch(bindings, bindings[0].TargetHost ?? "", bindings[0].TargetPort);
		// 只有真正建立起来的监听才写回端口/账号；被跳过（目标无效、监听失败）的绑定不能改配置，
		// 否则客户端会被指到一个没人监听的端口上一直重连。
		// 注意：允许「复用已有的同角色监听」，所以这里只按端口判定是否可用。
		HashSet<int> listeningPorts = new HashSet<int>(
			ProxyCaptureService.ActiveBindingsSnapshot().Select(x => x.ListenPort));
		foreach ((ProxyCaptureService.ProxyBinding binding, Endpoint endpoint, string loginName, bool isBot, bool isMarket) in plan)
		{
			int actual = binding.ListenPort;
			if (actual <= 0 || !listeningPorts.Contains(actual))
			{
				LogHelper.Log("[端口自适配] " + endpoint.Key + " 监听未建立，保留配置端口 " + endpoint.ListenPort);
				continue;
			}
			if (actual != endpoint.ListenPort)
			{
				LogHelper.Log("[端口自适配] " + endpoint.Key + " 配置端口 " + endpoint.ListenPort + " 不可用，已改用 " + actual);
				endpoint.ListenPort = actual;
				PersistEndpointPort(endpoint, actual);
			}
			if (isBot && !isMarket)
			{
				// 机器人号代理：把账号写进对应客户端的 Nexus 配置，端口用实际监听端口。
				EnsureBotAccount(endpoint.ServerHint, loginName, actual, "机器人代理", endpoint.CardKey, ClientDirOf(endpoint));
			}
			else if (isMarket)
			{
				// 行情角色必须由真人客户端连本端口，不能同时被机器人会话占用。
				RemoveBotAccount(endpoint.ServerHint, loginName, actual, ClientDirOf(endpoint));
			}
		}
		return bindings.Count;
	}

	/// <summary>
	/// 端口自适配后把实际端口写回「实例多开」配置和快速启动分组，
	/// 卡片、下次启动、客户端账号都用同一个端口，不会出现用户不知道端口变了的情况。
	/// 按卡片键（Key + 角色名）精确写回，同一账号的其它角色不受影响。
	/// </summary>
	private static void PersistEndpointPort(Endpoint endpoint, int port)
	{
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			string cardKey = endpoint.CardKey;
			string roleName = endpoint.RoleName ?? "";
			bool changed = false;
			NexusEndpointSetting? saved = (cfg.NexusEndpoints ?? new List<NexusEndpointSetting>())
				.FirstOrDefault(x => string.Equals(CardKeyOf(x), cardKey, StringComparison.OrdinalIgnoreCase)
					|| (string.IsNullOrWhiteSpace(roleName)
						&& string.Equals(x.Key, endpoint.Key, StringComparison.OrdinalIgnoreCase)
						&& (x.ListenPort == endpoint.ListenPort || x.ListenPort <= 0)));
			if (saved != null && saved.ListenPort != port)
			{
				saved.ListenPort = port;
				changed = true;
			}
			foreach (ProxyGroup group in cfg.ProxyGroups ?? new List<ProxyGroup>())
			{
				foreach (ProxyGroupRole role in group.Roles ?? new List<ProxyGroupRole>())
				{
					if (!string.Equals(KeyOf(role, group), endpoint.Key, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					bool roleMatch = string.IsNullOrWhiteSpace(roleName)
						? role.ListenPort == endpoint.ListenPort || role.ListenPort <= 0
						: string.Equals(role.RoleName ?? "", roleName, StringComparison.OrdinalIgnoreCase);
					if (roleMatch && role.ListenPort != port)
					{
						role.ListenPort = port;
						changed = true;
					}
				}
			}
			if (changed)
			{
				SettingsManager.Save(cfg);
			}
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[实例多开] 端口自适配写回失败");
		}
	}

	/// <summary>
	/// 把「行情角色 / 画面显示角色」的设置写成对应客户端目录里的文件：
	///  - market-prices/reconnect.txt：只有行情角色的客户端写 enabled=true 且地址指向它自己的本地端口，
	///    其它客户端写 enabled=false，避免残留死端口（如 127.0.0.1:20011）反复重连报 NPE；
	///    行情模组实际所在客户端由 mythosnexus.json 的账号归属决定（合并版等非挂号版也会写入），
	///    不能只依赖 ResolveClientDir 的挂号版解析，否则实际启动的客户端会一直按旧端口自动重连；
	///  - config/mythosnexus.json 的 viewAccount：只写画面角色所在客户端，游戏内自动挂载它的视角。
	/// 这样同一个实例同时具备「交易行行情（仅一个真人角色）+ 机器人代理」。
	/// </summary>
	public static void ApplyRoleFiles(IEnumerable<Endpoint> endpoints, ICollection<int>? startedPorts = null)
	{
		try
		{
			List<Endpoint> list = endpoints?.ToList() ?? new List<Endpoint>();
			AppSettings cfg = SettingsManager.Instance;
			string marketKey = (cfg.MarketAccountKey ?? "").Trim();
			string viewKey = (cfg.ViewAccountKey ?? "").Trim();
			Endpoint? market = marketKey.Length == 0 ? null
				: list.FirstOrDefault(x => string.Equals(x.Key, marketKey, StringComparison.OrdinalIgnoreCase));
			Endpoint? view = viewKey.Length == 0 ? null
				: list.FirstOrDefault(x => string.Equals(x.Key, viewKey, StringComparison.OrdinalIgnoreCase));
			// 行情只有在该端口确实被监听时才能让客户端自动重连过去，否则写 false 避免连死端口。
			bool marketLive = market != null && (startedPorts == null || startedPorts.Contains(market.ListenPort));
			var dirs = new List<string>();
			foreach (Endpoint e in list)
			{
				string dir = ClientDirOf(e);
				if (!string.IsNullOrWhiteSpace(dir) && !dirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
				{
					dirs.Add(dir);
				}
			}
			// 行情模组可能装在合并版等非挂号版客户端：全部纳入写入范围，避免它们保留旧端口自动重连。
			foreach (string marketClient in EnumerateMarketClientDirs())
			{
				if (!dirs.Contains(marketClient, StringComparer.OrdinalIgnoreCase))
				{
					dirs.Add(marketClient);
				}
			}
			List<string> marketDirs = market == null ? new List<string>() : ResolveMarketClientDirs(market);
			string viewDir = view == null ? "" : ClientDirOf(view);
			string viewName = view == null ? "" : (string.IsNullOrWhiteSpace(view.RoleName) ? view.User : view.RoleName);
			string marketOwner = market == null ? "" : (string.IsNullOrWhiteSpace(market.RoleName) ? market.User : market.RoleName);
			foreach (string dir in dirs)
			{
				bool isMarketDir = marketLive && marketDirs.Contains(dir, StringComparer.OrdinalIgnoreCase);
				WriteMarketReconnect(dir, isMarketDir ? "127.0.0.1:" + market!.ListenPort : "", isMarketDir,
					isMarketDir ? marketOwner : "");
				NexusConfigService.SaveViewAccount(dir,
					string.Equals(dir, viewDir, StringComparison.OrdinalIgnoreCase) ? viewName : "");
			}
			if (market != null)
			{
				LogHelper.Log("[行情角色] " + market.Key + (marketLive
					? " → 127.0.0.1:" + market.ListenPort + "（仅该真人角色启用行情，机器人代理不作为机器人会话）"
					: " 的本地端口未监听，行情自动重连已关闭"));
				if (marketLive)
				{
					foreach (string marketClient in marketDirs)
					{
						LogHelper.Log("[行情角色] 目标客户端：" + Path.GetFileName(marketClient) + "（owner=" + marketOwner + "）");
						// 行情角色不参与 MythosNexus 的自动进区（不随机传送），
						// 走路进交易行仍由行情模组的 owner 逻辑负责；binding 与 accounts 都写 0。
						NexusConfigService.SetLocalAutoJoinZone(marketClient, marketOwner, market!.ListenPort, 0);
						LogHelper.Log("[行情角色] 已关闭自动进区：" + Path.GetFileName(marketClient)
							+ "（" + marketOwner + "@127.0.0.1:" + market.ListenPort + "）");
					}
				}
			}
			if (view != null)
			{
				LogHelper.Log("[画面角色] 实例 " + Path.GetFileName(viewDir) + " 启动后自动显示 " + viewName + " 的画面");
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[行情角色] 写入行情/画面配置失败：" + ex.Message);
		}
	}

	/// <summary>写行情模组的掉线重连配置；客户端里没有行情模组时跳过。</summary>
	private static void WriteMarketReconnect(string clientDir, string address, bool enabled, string owner = "")
	{
		if (string.IsNullOrWhiteSpace(clientDir))
		{
			return;
		}
		string mods = Path.Combine(clientDir, "mods");
		string marketDir = Path.Combine(clientDir, "market-prices");
		bool hasMarket = Directory.Exists(marketDir)
			|| (Directory.Exists(mods) && Directory.GetFiles(mods, "*交易行行情*.jar").Length > 0);
		if (!hasMarket)
		{
			return;
		}
		Directory.CreateDirectory(marketDir);
		var sb = new System.Text.StringBuilder();
		sb.AppendLine("# 掉线自动重连（MhyunBox 自动生成）");
		sb.AppendLine("enabled=" + (enabled ? "true" : "false"));
		sb.AppendLine("address=" + (string.IsNullOrWhiteSpace(address) ? "127.0.0.1:25565" : address));
		sb.AppendLine("delay=8");
		sb.AppendLine("menuAutoConnect=" + (enabled ? "true" : "false"));
		sb.AppendLine("menuDelay=15");
		string path = Path.Combine(marketDir, "reconnect.txt");
		File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
		// owner.txt = 行情模组只对这个账号的角色生效（v17 门禁读取；不存在/为空则不限制）。
		string ownerPath = Path.Combine(marketDir, "owner.txt");
		if (enabled && !string.IsNullOrWhiteSpace(owner))
		{
			File.WriteAllText(ownerPath, owner.Trim() + Environment.NewLine, new System.Text.UTF8Encoding(false));
		}
		else if (File.Exists(ownerPath))
		{
			File.Delete(ownerPath);
		}
		if (enabled)
		{
			LogHelper.Log("[行情角色] 已写行情重连配置：" + path + " → " + address);
		}
		// 交给机器人代理模组（MythosNexus）的「行情角色 / 走路触发」桥：
		// 只有被指定的行情角色才允许它转发给行情模组执行「走去交易区」，别的角色一律不触发。
		WriteMarketRoleBridge(clientDir, enabled ? owner : "", enabled);
	}

	/// <summary>
	/// 写机器人代理模组读取的行情桥文件（config 目录）：
	///  - nexus-market-role.txt：脱盒指定的行情角色名；空 = 本客户端不参与行情；
	///  - nexus-market-walk.txt：走路指令，只有「指定的行情角色发生变化」时写一次 start，
	///    取消指定时写 stop。模组消费后会清空该文件，所以不会反复触发。
	/// 这样行情模组不再靠自己的时机主动进交易行，而是由脱盒指定、机器人代理模组转发。
	/// </summary>
	private static void WriteMarketRoleBridge(string clientDir, string roleName, bool enabled)
	{
		try
		{
			string cfgDir = Path.Combine(clientDir, "config");
			Directory.CreateDirectory(cfgDir);
			string rolePath = Path.Combine(cfgDir, "nexus-market-role.txt");
			string cmdPath = Path.Combine(cfgDir, "nexus-market-walk.txt");
			string want = enabled ? (roleName ?? "").Trim() : "";
			string old = File.Exists(rolePath) ? File.ReadAllText(rolePath).Trim() : "";
			if (!string.Equals(old, want, StringComparison.Ordinal))
			{
				File.WriteAllText(rolePath, want, new System.Text.UTF8Encoding(false));
				File.WriteAllText(cmdPath, want.Length == 0 ? "stop" : "start", new System.Text.UTF8Encoding(false));
				LogHelper.Log("[行情角色] 行情桥已更新：" + Path.GetFileName(clientDir)
					+ " role=" + (want.Length == 0 ? "(无)" : want) + " cmd=" + (want.Length == 0 ? "stop" : "start"));
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[行情角色] 写行情桥失败：" + ex.Message);
		}
	}

	/// <summary>
	/// 程序启动时恢复机器人代理（与服务器代理自启 AutoStartProxyOnStartup 完全独立）：
	/// 总开关 = AppSettings.AutoStartBotOnStartup，且只启动卡片上勾了「自启动」的机器人角色。
	/// 客户端机器人账号清理的口径是「已启用的机器人卡全部保留」，只清掉被删除 / 被停用的端口，
	/// 避免每次重启程序都把这批卡片从 mythosnexus.json 里抹掉（抹掉后模组界面就没有卡片、也建不出机器人）。
	/// 总开关关闭时仍然全清 —— 那是用户明确的关停语义。
	/// 返回是否启动了机器人监听。
	/// </summary>
	public static bool TryAutoStartBotsOnLaunch()
	{
		try
		{
			AppSettings cfg = SettingsManager.Instance;
			bool masterOn = cfg.AutoStartBotOnStartup;
			string dir = ResolveGameDir();
			if (string.IsNullOrWhiteSpace(dir))
			{
				return false;
			}
			List<Endpoint> all = Load(dir);
			// 「要保留哪些客户端机器人账号」= 所有**已启用**的机器人代理卡（不管有没有勾自启动）。
			// 2026-11-18 修正：以前这里只保留「勾了自启动」的端口，于是程序每重启一次就把
			// 没勾自启动的机器人账号从 mythosnexus.json 里清掉一次 —— 元素之诗那批卡片
			// 在模组界面里越重启越少、最后干脆建不出机器人，根因就是这条。
			// 现在只有「被删除 / 被停用」的卡片才会被清理；关总开关时仍然全清（那是明确的关停语义）。
			var keepPorts = new HashSet<int>();
			// 「显示组」作用域：选了组就只挂组里的机器人，组外卡片不写客户端、不自启。
			HashSet<string>? scope = DisplayScopeCardKeys();
			if (scope != null)
			{
				LogHelper.Log("[实例多开] 显示组作用域生效：只挂「" + (cfg.DisplayGroupName ?? "")
					+ "」组内的机器人（" + scope.Count + " 个角色键）");
			}
			// 将要自动运行的机器人：总开关关 = 空；开 = 勾了自启动且启用的机器人卡。
			List<Endpoint> autoBots = new List<Endpoint>();
			foreach (Endpoint e in all)
			{
				if (!e.Enabled || !IsBotGroup(e) || e.ListenPort <= 0)
				{
					continue;
				}
				if (scope != null && !scope.Contains(e.Key))
				{
					continue;
				}
				if (masterOn)
				{
					keepPorts.Add(e.ListenPort);
				}
				if (masterOn && e.AutoStart)
				{
					autoBots.Add(e);
				}
			}
			PruneBotAccountsByPorts(all, keepPorts);
			// 卡片和客户端账号必须一一对应：启动时把「已启用但客户端里没有」的机器人账号补回去，
			// 否则第二个组（另一台客户端）会出现「卡片在、游戏里没机器人」的情况。
			EnsureMissingBotAccounts(all);
			if (autoBots.Count == 0)
			{
				if (masterOn)
				{
					LogHelper.Log("[实例多开] 机器人自启已开但没有勾选「自启动」的卡片，未启动机器人");
				}
				else
				{
					LogHelper.Log("[实例多开] 机器人自启总开关 = 关：已清理客户端机器人账号，游戏不会再自动挂机器人");
				}
				return false;
			}
			int started = Start(autoBots);
			ApplyRoleFiles(all, ProxyCaptureService.ActiveBindingsSnapshot().Select(b => b.ListenPort).ToList());
			string names = string.Join("、", autoBots
				.Take(8)
				.Select(e => string.IsNullOrWhiteSpace(e.RoleName) ? e.User : e.RoleName));
			if (autoBots.Count > 8)
			{
				names += " 等 " + autoBots.Count + " 个";
			}
			LogHelper.Log("[实例多开] 程序启动自动机器人代理：勾选 " + autoBots.Count + " 卡（" + names
				+ "），已启动 " + started + " 个监听");
			// 明确告诉用户「哪几张卡是自启动恢复的」，避免出现「某个角色自己跑起来了」的错觉。
			MainWindow.Instance?.ShowToast("已按「自启动」恢复 " + started + " 个机器人代理：" + names);
			return started > 0;
		}
		catch (Exception ex)
		{
			LogHelper.Log("[实例多开] 启动机器人自启失败：" + ex.Message);
			return false;
		}
	}

	/// <summary>
	/// 按给定端口集合清理客户端里的机器人账号：note = MhyunBox 机器人号代理 且
	/// 端口不在集合内的条目全部移除。集合为空 = 全部清理。
	/// </summary>
	private static void PruneBotAccountsByPorts(List<Endpoint> endpoints, HashSet<int> allowedPorts)
	{
		foreach (IGrouping<string, Endpoint> group in endpoints.GroupBy(e => ClientDirOf(e)))
		{
			string dir = group.Key;
			if (string.IsNullOrWhiteSpace(dir))
			{
				continue;
			}
			try
			{
				NexusConfigService.Snapshot snap = NexusConfigService.Load(dir);
				foreach (NexusConfigService.Account account in snap.Accounts)
				{
					if (!IsPlaceholderHost(account.Host)
						|| !string.Equals(account.Note, "MhyunBox 机器人号代理", StringComparison.Ordinal)
						|| allowedPorts.Contains(account.Port))
					{
						continue;
					}
					NexusConfigService.RemoveAccount(dir, account.Key);
					LogHelper.Log("[实例多开] 清理不自动运行的机器人账号：" + account.Key + " → " + Path.GetFileName(dir));
				}
				// 孤儿绑定（绑定还在、账号已没了）一并清掉，避免同键账号重建时把 autoReconnect=true 带回来。
				int orphans = NexusConfigService.PruneOrphanMarkerBindings(dir, allowedPorts);
				if (orphans > 0)
				{
					LogHelper.Log("[实例多开] 清理孤儿机器人绑定 " + orphans + " 条 → " + Path.GetFileName(dir));
				}
			}
			catch (Exception ex)
			{
				LogHelper.Log("[实例多开] 清理机器人账号失败（" + Path.GetFileName(dir) + "）：" + ex.Message);
			}
		}
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MhyunBox.Services;

public static class ServerPresets
{
	public static readonly List<ServerPreset> All = new List<ServerPreset>
	{
		new ServerPreset
		{
			Name = "元素之诗",
			Host = "netease1.yszscraft.cn",
			Port = 25019,
			GameId = 4658261307332877912L,
			IpPool = new string[5] { "101.245.164.239", "101.245.164.238", "101.245.164.237", "101.245.164.228", "101.245.162.119" }
		},
		new ServerPreset
		{
			Name = "失落世界",
			Host = "netease1.mclw.cc",
			Port = 25565,
			GameId = 4624172676215685077L,
			IpPool = new string[3] { "1.95.232.232", "1.95.232.233", "1.95.232.231" }
		}
	};

	static ServerPresets()
	{
		LoadDiscovered();
	}

	public static ServerPreset? Find(string name)
	{
		foreach (ServerPreset item in All)
		{
			if (string.Equals(item.Name, name, StringComparison.Ordinal))
			{
				return item;
			}
		}
		return null;
	}

	/// <summary>
	/// 把「服务器列表」页发现的服务器注册为动态预设（host/port 已由 /item-address/get 解析）。
	/// 按 GameId 去重；名字与现有预设冲突时追加序号。成功返回实际生效的预设，已存在返回 null。
	/// </summary>
	public static ServerPreset? RegisterDiscovered(string name, string host, int port, long gameId, string[]? ipPool = null)
	{
		if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host) || port <= 0)
		{
			return null;
		}
		if (gameId > 0)
		{
			foreach (ServerPreset existing in All)
			{
				if (existing.GameId == gameId)
				{
					return null;
				}
			}
		}
		string finalName = name.Trim();
		int suffix = 2;
		while (Find(finalName) != null)
		{
			finalName = name.Trim() + " (" + suffix + ")";
			suffix++;
		}
		ServerPreset preset = new ServerPreset
		{
			Name = finalName,
			Host = host.Trim(),
			Port = port,
			GameId = gameId,
			IpPool = ipPool ?? Array.Empty<string>()
		};
		All.Add(preset);
		SaveDiscovered();
		LogHelper.Log("[服务器列表] 已注册动态预设: " + finalName + " -> " + host + ":" + port + " (gameId=" + gameId + ")");
		return preset;
	}

	/// <summary>是否已存在该 gameId 的预设。</summary>
	public static bool ContainsGameId(long gameId)
	{
		if (gameId <= 0)
		{
			return false;
		}
		foreach (ServerPreset item in All)
		{
			if (item.GameId == gameId)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>内置默认预设（元素之诗 / 失落世界），不可删除。</summary>
	public static bool IsBuiltin(ServerPreset preset)
	{
		return preset != null && (preset.GameId == 4658261307332877912L || preset.GameId == 4624172676215685077L);
	}

	/// <summary>删除动态注册的服务器预设（内置预设不可删）。成功返回 true。</summary>
	public static bool Remove(ServerPreset preset)
	{
		if (preset == null || IsBuiltin(preset))
		{
			return false;
		}
		if (!All.Remove(preset))
		{
			return false;
		}
		SaveDiscovered();
		LogHelper.Log("[服务器列表] 已删除动态预设: " + preset.Name);
		return true;
	}

	// ---------- 动态预设持久化 ----------

	private static string DiscoveredPath => System.IO.Path.Combine(AppContext.BaseDirectory, "MhyunBox_Logs", "discovered_servers.json");

	private static void LoadDiscovered()
	{
		try
		{
			if (!File.Exists(DiscoveredPath))
			{
				return;
			}
			List<ServerPreset>? list = JsonSerializer.Deserialize<List<ServerPreset>>(File.ReadAllText(DiscoveredPath));
			if (list == null)
			{
				return;
			}
			foreach (ServerPreset p in list)
			{
				if (p == null || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Host) || p.Port <= 0)
				{
					continue;
				}
				if (Find(p.Name) != null || (p.GameId > 0 && ContainsGameId(p.GameId)))
				{
					continue;
				}
				All.Add(p);
			}
		}
		catch
		{
		}
	}

	private static void SaveDiscovered()
	{
		try
		{
			string? dir = System.IO.Path.GetDirectoryName(DiscoveredPath);
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}
			List<ServerPreset> discovered = new List<ServerPreset>();
			foreach (ServerPreset p in All)
			{
				// 内置两个预设不入盘
				if (p.GameId == 4658261307332877912L || p.GameId == 4624172676215685077L)
				{
					continue;
				}
				discovered.Add(p);
			}
			File.WriteAllText(DiscoveredPath, JsonSerializer.Serialize(discovered, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch
		{
		}
	}
}

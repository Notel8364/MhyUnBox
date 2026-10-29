using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MhyunBox.Services;

internal class ServerRegistry
{
	public Dictionary<long, ServerEntry> Entries { get; set; } = new Dictionary<long, ServerEntry>();

	private static string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "MhyunBox_Logs", "server_registry.json");

	public static ServerRegistry Load()
	{
		ServerRegistry serverRegistry;
		try
		{
			serverRegistry = (File.Exists(Path) ? (JsonSerializer.Deserialize<ServerRegistry>(File.ReadAllText(Path)) ?? new ServerRegistry()) : new ServerRegistry());
		}
		catch
		{
			serverRegistry = new ServerRegistry();
		}
		serverRegistry.Entries.TryAdd(4658261307332877912L, new ServerEntry
		{
			GameId = 4658261307332877912L,
			Name = "元素之诗",
			Module = "204",
			Frozen = false
		});
		serverRegistry.Entries.TryAdd(4624172676215685077L, new ServerEntry
		{
			GameId = 4624172676215685077L,
			Name = "失落世界",
			Module = "204",
			Frozen = false
		});
		if (serverRegistry.Entries.TryGetValue(4624172676215685077L, out var slEntry) && slEntry.Frozen)
		{
			slEntry.Frozen = false;
		}
		return serverRegistry;
	}

	public void Save()
	{
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
		File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions
		{
			WriteIndented = true
		}));
	}
}

using System;

namespace MhyunBox.Services;

internal class ServerEntry
{
	public long GameId { get; set; }

	public string? Name { get; set; }

	public string? Module { get; set; }

	public bool Frozen { get; set; }

	public string? LastUrl { get; set; }

	public string? LastHash { get; set; }

	public DateTime? InstalledAt { get; set; }
}

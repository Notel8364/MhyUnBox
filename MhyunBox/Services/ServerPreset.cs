using System;

namespace MhyunBox.Services;

public sealed class ServerPreset
{
	public string Name { get; set; } = "";

	public string Host { get; set; } = "";

	public int Port { get; set; }

	public long GameId { get; set; }

	public string[] IpPool { get; set; } = Array.Empty<string>();

	public bool IsPending
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(Host))
			{
				return Port <= 0;
			}
			return true;
		}
	}
}

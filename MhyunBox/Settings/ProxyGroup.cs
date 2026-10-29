using System.Collections.Generic;

namespace MhyunBox.Settings;

public sealed class ProxyGroup
{
	public string Name { get; set; } = "";

	public string ServerName { get; set; } = "";

	public List<ProxyGroupRole> Roles { get; set; } = new List<ProxyGroupRole>();

	public int BasePort { get; set; }
}

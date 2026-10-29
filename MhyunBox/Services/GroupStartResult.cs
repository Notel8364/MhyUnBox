using System;
using System.Collections.Generic;

namespace MhyunBox.Services;

public sealed class GroupStartResult
{
	public bool ActivatedMissing { get; set; }

	public bool PresetUnavailable { get; set; }

	public bool Started { get; set; }

	public int StartedRoles { get; set; }

	public Dictionary<string, List<ProxyCaptureService.ProxyBinding>> ByServer { get; set; } = new Dictionary<string, List<ProxyCaptureService.ProxyBinding>>(StringComparer.Ordinal);
}

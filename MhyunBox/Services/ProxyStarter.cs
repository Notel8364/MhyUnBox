using System;
using System.Collections.Generic;
using System.Linq;
using MhyunBox.Settings;

namespace MhyunBox.Services;

public static class ProxyStarter
{
	public static List<ProxyCaptureService.ProxyBinding> BuildRoleBindings(ServerPreset preset, int basePort)
	{
		List<ProxyCaptureService.ProxyBinding> list = new List<ProxyCaptureService.ProxyBinding>();
		List<AccountStore.LocalAccount> list2 = AccountStore.All.ToList();
		HashSet<(long, string)> reserved = new HashSet<(long, string)>();
		int cursor = basePort;
		for (int i = 0; i < list2.Count; i++)
		{
			AccountStore.LocalAccount localAccount = list2[i];
			if (!localAccount.IsProxyUsable || localAccount.ServerRoles == null || localAccount.ServerRoles.Count == 0)
			{
				continue;
			}
			if (!preset.IsPending && localAccount.ServerRoles.TryGetValue(preset.Name, out var value) && value != null && value.Count > 0)
			{
				foreach (string item in value.OrderBy<string, string>((string r) => r, StringComparer.Ordinal))
				{
					if (!RoleReserved(reserved, preset.GameId, item))
					{
						AddBinding(list, ref cursor, i, preset.Host, preset.Port, item);
					}
				}
			}
			if (!localAccount.ServerRoles.TryGetValue("未分组", out var value2) || value2 == null || value2.Count <= 0)
			{
				continue;
			}
			foreach (string item2 in value2.OrderBy<string, string>((string r) => r, StringComparer.Ordinal))
			{
				if (!RoleReserved(reserved, preset.GameId, item2))
				{
					AddBinding(list, ref cursor, i, preset.Host, preset.Port, item2);
				}
			}
		}
		return list;
	}

	private static bool RoleReserved(HashSet<(long, string)> reserved, long gameId, string roleName)
	{
		if (gameId <= 0)
		{
			return false;
		}
		string text = roleName ?? "";
		if (!reserved.Add((gameId, text)))
		{
			LogHelper.Log($"[代理启动] 同 GameId={gameId} 的角色「{text}」已被绑定，跳过重复实例（避免同 UUID 双在线）");
			return true;
		}
		return false;
	}

	public static List<ProxyCaptureService.ProxyBinding> BuildSavedBindings(AppSettings settings, ServerPreset preset, int basePort, out bool usedSaved)
	{
		List<ProxyCaptureService.ProxyBinding> list = new List<ProxyCaptureService.ProxyBinding>();
		usedSaved = false;
		if (settings == null || settings.LastProxyBindings == null || settings.LastProxyBindings.Count == 0)
		{
			return list;
		}
		List<AccountStore.LocalAccount> list2 = AccountStore.All.ToList();
		HashSet<(long, string)> reserved = new HashSet<(long, string)>();
		foreach (SavedProxyBinding saved in settings.LastProxyBindings)
		{
			if (!string.IsNullOrWhiteSpace(saved.RoleName) && !string.IsNullOrWhiteSpace(saved.Account) && (string.Equals(saved.ServerName, preset.Name, StringComparison.Ordinal) || (string.IsNullOrWhiteSpace(saved.ServerName) && string.Equals(settings.LastProxyPresetName, preset.Name, StringComparison.Ordinal))))
			{
				int num = list2.FindIndex((AccountStore.LocalAccount a) => string.Equals(a.Channel, saved.Channel, StringComparison.Ordinal) && string.Equals(a.Account, saved.Account, StringComparison.OrdinalIgnoreCase));
				if (num >= 0 && list2[num].IsProxyUsable && HasRole(list2[num], preset, saved.RoleName) && !RoleReserved(reserved, preset.GameId, saved.RoleName))
				{
					list.Add(new ProxyCaptureService.ProxyBinding
					{
						ListenPort = ((saved.ListenPort > 0) ? saved.ListenPort : basePort),
						LoginName = saved.RoleName,
						TargetHost = preset.Host,
						TargetPort = preset.Port,
						AuthAccountIndex = num
					});
				}
			}
		}
		usedSaved = list.Count > 0;
		return list;
	}

	public static List<ProxyCaptureService.ProxyBinding> BuildGroupBindings(AppSettings settings, ProxyGroup group, ServerPreset? fallbackPreset, out bool usedAny, out int skipped, out bool remappedAny)
	{
		List<ProxyCaptureService.ProxyBinding> list = new List<ProxyCaptureService.ProxyBinding>();
		usedAny = false;
		skipped = 0;
		remappedAny = false;
		if (settings == null || group == null || group.Roles == null || group.Roles.Count == 0)
		{
			return list;
		}
		List<AccountStore.LocalAccount> list2 = AccountStore.All.ToList();
		HashSet<(long, string)> reserved = new HashSet<(long, string)>();
		foreach (ProxyGroupRole role in group.Roles)
		{
			if (string.IsNullOrWhiteSpace(role.RoleName) || string.IsNullOrWhiteSpace(role.Account))
			{
				skipped++;
				continue;
			}
			ServerPreset serverPreset = ServerPresets.Find(role.ServerName);
			if (serverPreset == null)
			{
				serverPreset = fallbackPreset;
			}
			if (serverPreset == null || serverPreset.IsPending || string.IsNullOrWhiteSpace(serverPreset.Host))
			{
				skipped++;
				continue;
			}
			int num = ResolveAccountIndex(role, list2, out var remapped);
			if (num < 0 || !list2[num].IsProxyUsable)
			{
				skipped++;
				continue;
			}
			if (remapped)
			{
				remappedAny = true;
				role.Channel = list2[num].Channel;
				role.Account = list2[num].Account;
			}
			if (!HasRole(list2[num], serverPreset, role.RoleName))
			{
				skipped++;
				continue;
			}
			if (RoleReserved(reserved, serverPreset.GameId, role.RoleName))
			{
				skipped++;
				continue;
			}
			list.Add(new ProxyCaptureService.ProxyBinding
			{
				ListenPort = ((role.ListenPort > 0) ? role.ListenPort : 0),
				LoginName = role.RoleName,
				TargetHost = serverPreset.Host,
				TargetPort = serverPreset.Port,
				AuthAccountIndex = num
			});
			usedAny = true;
		}
		return list;
	}

	public static int ResolveAccountIndex(ProxyGroupRole role, List<AccountStore.LocalAccount> accounts, out bool remapped)
	{
		remapped = false;
		if (role == null || accounts == null || accounts.Count == 0)
		{
			return -1;
		}
		int num = accounts.FindIndex((AccountStore.LocalAccount a) => string.Equals(a.Channel ?? "", role.Channel ?? "", StringComparison.Ordinal) && string.Equals(a.Account ?? "", role.Account ?? "", StringComparison.OrdinalIgnoreCase));
		if (num >= 0 && accounts[num].IsProxyUsable)
		{
			return num;
		}
		int num2 = accounts.FindIndex((AccountStore.LocalAccount a) => a.IsProxyUsable && !string.IsNullOrWhiteSpace(a.Account) && string.Equals(a.Account, role.Account ?? "", StringComparison.OrdinalIgnoreCase));
		if (num2 >= 0)
		{
			remapped = true;
			return num2;
		}
		num2 = accounts.FindIndex((AccountStore.LocalAccount a) => a.IsProxyUsable && !string.IsNullOrWhiteSpace(a.Nickname) && string.Equals(a.Nickname, role.Account ?? "", StringComparison.OrdinalIgnoreCase));
		if (num2 >= 0)
		{
			remapped = true;
			return num2;
		}
		return num;
	}

	public static void RememberStarted(ServerPreset? preset, IReadOnlyList<ProxyCaptureService.ProxyBinding> bindings)
	{
		AppSettings instance = SettingsManager.Instance;
		List<SavedProxyBinding> list = new List<SavedProxyBinding>();
		if (instance.LastProxyBindings != null && preset != null)
		{
			list.AddRange(instance.LastProxyBindings.Where((SavedProxyBinding s) => !string.Equals(s.ServerName, preset.Name, StringComparison.Ordinal)));
		}
		List<AccountStore.LocalAccount> list2 = AccountStore.All.ToList();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		if (bindings != null && preset != null)
		{
			foreach (ProxyCaptureService.ProxyBinding binding in bindings)
			{
				if (!string.IsNullOrWhiteSpace(binding.LoginName) && binding.AuthAccountIndex >= 0 && binding.AuthAccountIndex < list2.Count)
				{
					AccountStore.LocalAccount localAccount = list2[binding.AuthAccountIndex];
					hashSet.Add(localAccount.Channel + "\u0001" + localAccount.Account.ToLowerInvariant() + "\u0001" + (binding.LoginName ?? "").ToLowerInvariant());
					list.Add(new SavedProxyBinding
					{
						ServerName = preset.Name,
						Channel = localAccount.Channel,
						Account = localAccount.Account,
						RoleName = binding.LoginName,
						ListenPort = binding.ListenPort
					});
				}
			}
			foreach (ProxyCaptureService.ActiveBindingInfo item2 in ProxyCaptureService.ActiveBindingsSnapshot())
			{
				if (item2 != null && !string.IsNullOrWhiteSpace(item2.LoginName) && item2.AuthAccountIndex >= 0 && item2.AuthAccountIndex < list2.Count && string.Equals(item2.TargetHost, preset.Host, StringComparison.OrdinalIgnoreCase))
				{
					AccountStore.LocalAccount localAccount2 = list2[item2.AuthAccountIndex];
					string item = localAccount2.Channel + "\u0001" + localAccount2.Account.ToLowerInvariant() + "\u0001" + (item2.LoginName ?? "").ToLowerInvariant();
					if (!hashSet.Contains(item))
					{
						hashSet.Add(item);
						list.Add(new SavedProxyBinding
						{
							ServerName = preset.Name,
							Channel = localAccount2.Channel,
							Account = localAccount2.Account,
							RoleName = item2.LoginName,
							ListenPort = item2.ListenPort
						});
					}
				}
			}
		}
		instance.LastProxyPresetName = preset?.Name ?? "";
		instance.LastProxyBindings = list;
		SettingsManager.Save(instance);
	}

	public static void ClearRemembered()
	{
		AppSettings instance = SettingsManager.Instance;
		instance.LastProxyPresetName = "";
		instance.LastProxyBindings = new List<SavedProxyBinding>();
		SettingsManager.Save(instance);
	}

	public static void RememberLastLaunch(string source, string summary)
	{
		AppSettings instance = SettingsManager.Instance;
		List<AccountStore.LocalAccount> list = AccountStore.All.ToList();
		List<SavedProxyBinding> list2 = new List<SavedProxyBinding>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (ProxyCaptureService.ActiveBindingInfo info in ProxyCaptureService.ActiveBindingsSnapshot())
		{
			if (info == null || string.IsNullOrWhiteSpace(info.LoginName) || info.AuthAccountIndex < 0 || info.AuthAccountIndex >= list.Count)
			{
				continue;
			}
			AccountStore.LocalAccount localAccount = list[info.AuthAccountIndex];
			string item = localAccount.Channel + "\u0001" + localAccount.Account.ToLowerInvariant() + "\u0001" + (info.LoginName ?? "").ToLowerInvariant();
			if (hashSet.Add(item))
			{
				ServerPreset serverPreset = ServerPresets.All.FirstOrDefault((ServerPreset p) => string.Equals(p.Host, info.TargetHost, StringComparison.OrdinalIgnoreCase));
				list2.Add(new SavedProxyBinding
				{
					ServerName = (serverPreset?.Name ?? info.TargetHost ?? ""),
					Channel = localAccount.Channel,
					Account = localAccount.Account,
					RoleName = (info.LoginName ?? ""),
					ListenPort = info.ListenPort
				});
			}
		}
		instance.LastLaunchSource = source ?? "";
		instance.LastLaunchSummary = summary ?? "";
		instance.LastLaunchBindings = list2;
		SettingsManager.Save(instance);
		LogHelper.Log($"[自启记忆] 已记住本次启动方式：{source} {summary}，共 {list2.Count} 个角色端口");
	}

	public static List<ProxyCaptureService.ProxyBinding> BuildLastLaunchBindings(out string source, out string summary, out List<string> skippedReasons)
	{
		source = "";
		summary = "";
		skippedReasons = new List<string>();
		List<ProxyCaptureService.ProxyBinding> list = new List<ProxyCaptureService.ProxyBinding>();
		AppSettings instance = SettingsManager.Instance;
		if (instance.LastLaunchBindings == null || instance.LastLaunchBindings.Count == 0)
		{
			return list;
		}
		source = instance.LastLaunchSource ?? "";
		summary = instance.LastLaunchSummary ?? "";
		List<AccountStore.LocalAccount> list2 = AccountStore.All.ToList();
		HashSet<(long, string)> reserved = new HashSet<(long, string)>();
		foreach (SavedProxyBinding saved in instance.LastLaunchBindings)
		{
			if (string.IsNullOrWhiteSpace(saved.RoleName) || string.IsNullOrWhiteSpace(saved.Account))
			{
				skippedReasons.Add("记录不完整：" + saved.RoleName + "（无账号）");
				continue;
			}
			ServerPreset serverPreset = ServerPresets.Find(saved.ServerName);
			if (serverPreset == null || serverPreset.IsPending || string.IsNullOrWhiteSpace(serverPreset.Host))
			{
				skippedReasons.Add("服务器预设不可用：" + saved.ServerName);
				continue;
			}
			int num = list2.FindIndex((AccountStore.LocalAccount a) => string.Equals(a.Channel, saved.Channel, StringComparison.Ordinal) && string.Equals(a.Account, saved.Account, StringComparison.OrdinalIgnoreCase));
			if (num < 0)
			{
				skippedReasons.Add("账号不存在：" + saved.Account);
				continue;
			}
			if (!list2[num].IsProxyUsable)
			{
				skippedReasons.Add("账号无可用登录态：" + saved.Account);
				continue;
			}
			if (RoleReserved(reserved, serverPreset.GameId, saved.RoleName))
			{
				skippedReasons.Add("同 GameId 重复角色：" + saved.RoleName);
				continue;
			}
			list.Add(new ProxyCaptureService.ProxyBinding
			{
				ListenPort = saved.ListenPort,
				LoginName = saved.RoleName,
				TargetHost = serverPreset.Host,
				TargetPort = serverPreset.Port,
				AuthAccountIndex = num
			});
		}
		return list;
	}

	private static bool HasRole(AccountStore.LocalAccount acc, ServerPreset preset, string roleName)
	{
		if (acc.ServerRoles == null || acc.ServerRoles.Count == 0)
		{
			return false;
		}
		if (!preset.IsPending && acc.ServerRoles.TryGetValue(preset.Name, out var value) && value != null && value.Contains<string>(roleName, StringComparer.Ordinal))
		{
			return true;
		}
		if (acc.ServerRoles.TryGetValue("未分组", out var value2) && value2 != null && value2.Contains<string>(roleName, StringComparer.Ordinal))
		{
			return true;
		}
		return false;
	}

	private static void AddBinding(List<ProxyCaptureService.ProxyBinding> bindings, ref int cursor, int accountIndex, string host, int port, string roleName)
	{
		int num = ProxyCaptureService.NextFreePort(cursor);
		if (num <= 0)
		{
			num = cursor;
		}
		cursor = num + 1;
		bindings.Add(new ProxyCaptureService.ProxyBinding
		{
			ListenPort = num,
			LoginName = roleName,
			TargetHost = host,
			TargetPort = port,
			AuthAccountIndex = accountIndex
		});
	}
}

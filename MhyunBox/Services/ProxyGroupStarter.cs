using System;
using System.Collections.Generic;
using System.Linq;
using MhyunBox.Settings;

namespace MhyunBox.Services;

public static class ProxyGroupStarter
{
	public static GroupStartResult StartGroup(ProxyGroup? group, Action<string>? log = null)
	{
		GroupStartResult groupStartResult = new GroupStartResult();
		if (group == null || group.Roles == null || group.Roles.Count == 0)
		{
			return groupStartResult;
		}
		if (!ActivationService.IsActivated)
		{
			groupStartResult.ActivatedMissing = true;
			return groupStartResult;
		}
		ServerPreset serverPreset = ServerPresets.Find(group.ServerName);
		if (serverPreset == null || serverPreset.IsPending || string.IsNullOrWhiteSpace(serverPreset.Host))
		{
			groupStartResult.PresetUnavailable = true;
			return groupStartResult;
		}
		List<ProxyCaptureService.ProxyBinding> list = ProxyStarter.BuildGroupBindings(SettingsManager.Instance, group, serverPreset, out var usedAny, out var _, out var remappedAny);
		if (remappedAny)
		{
			SettingsManager.Save(SettingsManager.Instance);
		}
		if (!usedAny || list.Count == 0)
		{
			return groupStartResult;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (ProxyCaptureService.ProxyBinding b in list)
		{
			if (b.ListenPort <= 0)
			{
				continue;
			}
			string item = b.AuthAccountIndex + "\0" + b.LoginName + "\0" + b.ListenPort;
			List<ProxyCaptureService.ActiveBindingInfo> list2 = (from x in ProxyCaptureService.ActiveBindingsSnapshot()
				where x.AuthAccountIndex == b.AuthAccountIndex && string.Equals(x.LoginName, b.LoginName, StringComparison.Ordinal)
				select x).ToList();
			if (list2.Any((ProxyCaptureService.ActiveBindingInfo x) => x.ListenPort == b.ListenPort))
			{
				hashSet.Add(item);
			}
			else
			{
				if (hashSet.Contains(item))
				{
					continue;
				}
				if (list2.Count > 0)
				{
					log?.Invoke("状态: " + b.LoginName + " 已用端口 " + list2[0].ListenPort + " 在连，本组端口 " + b.ListenPort + " 与之一致，去重合并。与服务器重复连接会持续连接中止，请勿同时启动两组相同角色\n");
					hashSet.Add(item);
					continue;
				}
				ProxyCaptureService.ActiveBindingInfo activeBindingInfo = ProxyCaptureService.ActiveBindingsSnapshot().FirstOrDefault((ProxyCaptureService.ActiveBindingInfo x) => x.ListenPort == b.ListenPort && x.AuthAccountIndex != b.AuthAccountIndex);
				if (activeBindingInfo != null)
				{
					log?.Invoke("状态: " + b.LoginName + " 端口 " + b.ListenPort + " 已被其他代理（" + activeBindingInfo.LoginName + "）使用，跳过（不覆盖已有代理）\n");
				}
				else
				{
					hashSet.Add(item);
					ProxyCaptureService.TargetFromHandshake = true;
					ProxyCaptureService.StartBatch(new ProxyCaptureService.ProxyBinding[1] { b }, b.TargetHost, b.TargetPort);
				}
			}
		}
		if (!ProxyCaptureService.IsRunning)
		{
			return groupStartResult;
		}
		Dictionary<string, List<ProxyCaptureService.ProxyBinding>> dictionary = new Dictionary<string, List<ProxyCaptureService.ProxyBinding>>(StringComparer.Ordinal);
		foreach (ProxyCaptureService.ProxyBinding item2 in list)
		{
			ServerPreset serverPreset2 = ServerPresets.Find(item2.TargetHost ?? "") ?? serverPreset;
			string text = ((serverPreset2 != null) ? serverPreset2.Name : serverPreset.Name);
			if (!dictionary.TryGetValue(text, out var value))
			{
				List<ProxyCaptureService.ProxyBinding> list3 = (dictionary[text] = new List<ProxyCaptureService.ProxyBinding>());
				value = list3;
			}
			value.Add(item2);
			log?.Invoke("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] 已开启分组「" + group.Name + "」代理 " + item2.LoginName + " -> " + item2.ListenPort + "（" + text + "）\n");
		}
		foreach (KeyValuePair<string, List<ProxyCaptureService.ProxyBinding>> item3 in dictionary)
		{
			ServerPreset preset = ServerPresets.Find(item3.Key);
			List<ProxyCaptureService.ProxyBinding> bindings = item3.Value.Select((ProxyCaptureService.ProxyBinding x) => new ProxyCaptureService.ProxyBinding
			{
				ListenPort = x.ListenPort,
				LoginName = x.LoginName,
				TargetHost = x.TargetHost,
				TargetPort = x.TargetPort,
				AuthAccountIndex = x.AuthAccountIndex
			}).ToList();
			ProxyStarter.RememberStarted(preset, bindings);
		}
		groupStartResult.Started = true;
		groupStartResult.StartedRoles = dictionary.Sum((KeyValuePair<string, List<ProxyCaptureService.ProxyBinding>> kv) => kv.Value.Count);
		groupStartResult.ByServer = dictionary;
		ProxyStarter.RememberLastLaunch("快速启动", "分组「" + group.Name + "」共 " + groupStartResult.StartedRoles + " 个角色端口");
		return groupStartResult;
	}
}

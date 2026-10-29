using System;
using System.Collections.Generic;

namespace MhyunBox.Services;

/// <summary>
/// 账号「疑似在别处登录」运行时提醒。
/// 仅提醒：不锁定、不置会话无效、不影响脱盒与代理；重新登录成功即清除。
/// </summary>
public static class ElsewhereLoginNotice
{
	public sealed class Notice
	{
		public string Reason { get; set; } = "";

		public DateTime Time { get; set; }
	}

	private static readonly object Sync = new object();

	private static readonly Dictionary<string, Notice> Map = new Dictionary<string, Notice>(StringComparer.Ordinal);

	/// <summary>提醒变化事件（UI 层订阅后自行回到 UI 线程刷新）。</summary>
	public static event Action? Changed;

	public static string Key(string channel, string account)
	{
		return channel + "|" + account;
	}

	public static void Mark(string channel, string account, string reason)
	{
		if (string.IsNullOrWhiteSpace(account))
		{
			return;
		}
		if (reason.Length > 120)
		{
			reason = reason.Substring(0, 120);
		}
		bool added;
		lock (Sync)
		{
			added = !Map.ContainsKey(Key(channel, account));
			Map[Key(channel, account)] = new Notice
			{
				Reason = reason,
				Time = DateTime.Now
			};
		}
		LogHelper.Force("[别处登录] 标记账号 " + channel + " " + account + "：" + reason);
		Changed?.Invoke();
	}

	public static void Clear(string channel, string account)
	{
		bool removed;
		lock (Sync)
		{
			removed = Map.Remove(Key(channel, account));
		}
		if (removed)
		{
			LogHelper.Force("[别处登录] 清除标记 " + channel + " " + account);
			Changed?.Invoke();
		}
	}

	public static Notice? Get(string channel, string account)
	{
		lock (Sync)
		{
			return Map.TryGetValue(Key(channel, account), out Notice value) ? value : null;
		}
	}

	/// <summary>判断接口错误/踢出文案是否含「在别处登录」特征。</summary>
	public static bool IsElsewhereMessage(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return false;
		}
		string lowered = message.ToLowerInvariant();
		string[] array = ElsewherePhrases;
		foreach (string value in array)
		{
			if (lowered.Contains(value))
			{
				return true;
			}
		}
		return false;
	}

	public static readonly string[] ElsewherePhrases = new string[13]
	{
		"别处", "其他设备", "其它设备", "异地", "顶号", "挤下线", "挤掉", "重复登录", "已在别",
		"new device", "another device", "logged in elsewhere", "elsewhere"
	};
}

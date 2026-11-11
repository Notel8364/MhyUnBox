using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>
/// 游戏内进入会话失败时的自动兜底（2026-11-07 修复）。
///
/// 旧实现「每次失败都触发重登 + 90 秒后再一次 + 先清零重试计数」，无任何全局限流，
/// 是登录风暴主因之一。现在恢复 v3.8.0 的保守策略：
///   1) 风控冷静期内一律跳过（RiskNotice 全局闸门）；
///   2) 风控文案不进入计数，直接登记冷静期；
///   3) 2 分钟窗口内累计 3 次失败才触发一次兜底；
///   4) 触发后 10 分钟冷却，期间不再重复安排；每轮只发一次请求，取消 90 秒二次重拉。
/// 会话失效类问题由进服链路按需处理，本兜底只负责低频保底。
/// </summary>
public static class AccountInvalidationDetector
{
	public sealed class Info
	{
		public string Channel = "";

		public string Account = "";

		public string Nickname = "";

		public string Reason = "";
	}

	private static readonly object Sync = new object();

	private const int Threshold = 3;

	private static readonly TimeSpan Window = TimeSpan.FromMinutes(2.0);

	private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10.0);

	private static readonly Dictionary<string, (int Count, DateTime First)> Failures = new Dictionary<string, (int, DateTime)>(StringComparer.OrdinalIgnoreCase);

	private static readonly Dictionary<string, DateTime> LastAttempt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	/// <summary>上报一次「游戏内进入会话失败」，按低频策略安排自动兜底。</summary>
	public static void ReportInGameSessionFailure(string channel, string account, string nickname, string reason = "")
	{
		string ch = channel ?? "";
		string acc = account ?? "";
		string nick = nickname ?? "";
		if (string.IsNullOrWhiteSpace(acc))
		{
			return;
		}
		// 官方启动器运行时不安排任何兜底重登，避免把它的同账号会话顶掉。
		if (GameProcessDetector.IsOfficialLauncherRunning())
		{
			LogHelper.Log("[账号检测] 检测到官方启动器正在运行，跳过自动兜底重登以避免顶号：" + nick + "/" + acc);
			return;
		}
		// 风控文案本身不是会话失效，不应触发任何重登。
		if (!string.IsNullOrWhiteSpace(reason) && NeteaseLogin.IsRiskControlText(reason))
		{
			AccountAutoLoginService.ReportRiskControl(ch, acc, reason);
			return;
		}
		string key = ch.ToLowerInvariant() + "\u0001" + acc.Trim().ToLowerInvariant();
		DateTime utcNow = DateTime.UtcNow;
		lock (Sync)
		{
			if (LastAttempt.TryGetValue(key, out DateTime last) && utcNow - last < Cooldown)
			{
				return;
			}
			if (!Failures.TryGetValue(key, out (int, DateTime) f) || utcNow - f.Item2 > Window)
			{
				Failures[key] = (1, utcNow);
				return;
			}
			if (f.Item1 + 1 < Threshold)
			{
				Failures[key] = (f.Item1 + 1, f.Item2);
				return;
			}
			Failures.Remove(key);
			LastAttempt[key] = utcNow;
		}
		LogHelper.Log($"[账号检测] 账号「{nick}」2 分钟内累计 {Threshold} 次进服会话失败，安排一次低频自动兜底重登（{Cooldown.TotalMinutes:F0} 分钟内不重复）{(string.IsNullOrWhiteSpace(reason) ? "" : ("（" + reason + "）"))}");
		_ = TryAutoReloginAsync(new Info
		{
			Channel = ch,
			Account = acc,
			Nickname = string.IsNullOrWhiteSpace(nick) ? acc : nick,
			Reason = reason ?? ""
		});
	}

	private static async Task TryAutoReloginAsync(Info info)
	{
		try
		{
			bool ok = await AccountAutoLoginService.EnsureLoginForAsync(info.Channel, info.Account).ConfigureAwait(continueOnCapturedContext: false);
			LogHelper.Log(ok
				? "[账号检测] 账号「" + info.Nickname + "」自动兜底重登成功（不重启代理，下次进入会话使用新 token）"
				: "[账号检测] 账号「" + info.Nickname + "」自动兜底重登未成功（本地记录保留，进服时会按需重新拉取）");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[账号检测] 自动兜底重登异常");
		}
	}
}

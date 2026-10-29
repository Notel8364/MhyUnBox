using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>
/// 游戏内进入会话失败时的自动兜底：不做失败次数上限、不停止代理、不标记会话失效、不重启代理。
/// 失败即按冷却间隔自动重登拉取 token；4399 账号带明文账密时会自动重登 4399 换新 Cookie（持续挂机防过期掉号）。
/// 账号本地记录/分组/角色/端口记忆全程保留。
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

	private static readonly Dictionary<string, DateTime> LastAttempt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// 上报一次「游戏内进入会话失败」，触发后台自动重登兜底。
	/// </summary>
	public static void ReportInGameSessionFailure(string channel, string account, string nickname, string reason = "")
	{
		string text = channel ?? "";
		string text2 = account ?? "";
		string text3 = nickname ?? "";
		if (string.IsNullOrWhiteSpace(text2))
		{
			return;
		}
		string key = text.ToLowerInvariant() + "\u0001" + text2.Trim().ToLowerInvariant();
		DateTime utcNow = DateTime.UtcNow;
		lock (Sync)
		{
			// 不再做 2 分钟冷却限流：重登本身有账号级重试上限（2 次）与随机间隔兜底，
			// 冷却只会拖慢真实失效后的恢复；直接每次失败都允许触发重登。
			LastAttempt[key] = utcNow;
		}
		LogHelper.Log($"[账号检测] 游戏内进入会话失败，安排自动重登拉取 token：{text3}/{text2}{(string.IsNullOrWhiteSpace(reason) ? "" : ("（" + reason + "）"))}");
		_ = TryAutoReloginAsync(new Info
		{
			Channel = text,
			Account = text2,
			Nickname = string.IsNullOrWhiteSpace(text3) ? text2 : text3,
			Reason = reason ?? ""
		});
	}

	private static async Task TryAutoReloginAsync(Info info)
	{
		bool ok = false;
		// 先清零该账号的重试计数再走重登 —— 详见 ResetRetryFor 的说明。
		// 不清零的话 EnsureLoginForAsync 会在入口直接 return false，
		// 表现为「日志写了自动重登，实际一次请求都没发」，兜底形同虚设。
		AccountAutoLoginService.ResetRetryFor(info.Channel, info.Account);
		try
		{
			ok = await AccountAutoLoginService.EnsureLoginForAsync(info.Channel, info.Account).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[账号检测] 自动重登异常");
		}
		if (ok)
		{
			// 重登成功不重启代理：绑定/监听与登录态无关，且加入路径每次都会用 Cookie 现拉新 token；
			// 重启会杀掉正在游戏中的其他连接（表现为"玩着玩着突然连接中止"）
			LogHelper.Log("[账号检测] 账号「" + info.Nickname + "」自动重登成功（不重启代理，下次进入会话将使用新 token）");
			return;
		}
		try
		{
			LogHelper.Log("[账号检测] 账号「" + info.Nickname + "」自动重登未成功（保存了账密的账号会自动用账密换新会话），90 秒后再兜底重拉一次（本地记录/分组/角色全部保留）");
			await Task.Delay(TimeSpan.FromSeconds(90)).ConfigureAwait(continueOnCapturedContext: false);
			// 延迟兜底同样先清零：间隔 90 秒，属于全新一轮，不该被上一轮的计数锁死
			AccountAutoLoginService.ResetRetryFor(info.Channel, info.Account);
			bool flag = await AccountAutoLoginService.EnsureLoginForAsync(info.Channel, info.Account).ConfigureAwait(continueOnCapturedContext: false);
			if (flag)
			{
				LogHelper.Log("[账号检测] 账号「" + info.Nickname + "」兜底重拉成功（不重启代理）");
			}
			else
			{
				LogHelper.Log("[账号检测] 账号「" + info.Nickname + "」兜底重拉仍未成功（本地记录保留，游戏期保活/下次进入会再自动拉取）");
			}
		}
		catch (Exception ex2)
		{
			LogHelper.LogError(ex2, "[账号检测] 兜底重拉异常");
		}
	}
}

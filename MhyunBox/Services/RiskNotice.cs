using System;

namespace MhyunBox.Services;

/// <summary>
/// 网易 IP 级登录环境风控（login abnormally）标记状态广播。
/// 程序侧不设任何请求门槛（设计原则不变）——这里只做「信息展示」：
/// 网易内部风控时长无公开文档，社区经验值约 1~2 小时，程序取 60 分钟做经验恢复期倒计时，
/// 仅提示用户预计恢复时间，任何请求照常发出，成功后立即清除标记。
/// </summary>
public static class RiskNotice
{
	/// <summary>经验恢复期（分钟）。无官方数据，取社区经验下限。</summary>
	public const int RecoveryMinutes = 60;

	/// <summary>当前标记的预计恢复时间点（最近一次触发 + RecoveryMinutes）。</summary>
	public static DateTimeOffset? MarkedUntil { get; private set; }

	/// <summary>最近触发的账号（打码后），仅用于展示。</summary>
	public static string? AccountMasked { get; private set; }

	/// <summary>最近触发的原因摘要（如 login-otp / GameStart）。</summary>
	public static string? Source { get; private set; }

	/// <summary>标记/清除时触发（UI 订阅刷新横幅）。</summary>
	public static event Action? Changed;

	/// <summary>登记一次风控标记。已有更晚的恢复时间点时保留更晚的。</summary>
	public static void Mark(string maskedAccount, string source)
	{
		DateTimeOffset until = DateTimeOffset.Now.AddMinutes(RecoveryMinutes);
		bool changed = false;
		if (MarkedUntil == null || MarkedUntil < until)
		{
			MarkedUntil = until;
			changed = true;
		}
		AccountMasked = maskedAccount;
		Source = source;
		if (changed || MarkedUntil > DateTimeOffset.Now)
		{
			Changed?.Invoke();
		}
	}

	/// <summary>任一登录/重登成功后清除标记（网易已放行说明风控已解除）。</summary>
	public static void Clear()
	{
		if (MarkedUntil == null)
		{
			return;
		}
		MarkedUntil = null;
		AccountMasked = null;
		Source = null;
		Changed?.Invoke();
	}

	/// <summary>是否处于标记期（时间自然到期也算解除，但保留到下一次 Mark 才真正清 UI）。</summary>
	public static bool IsActive
	{
		get
		{
			DateTimeOffset? markedUntil = MarkedUntil;
			return markedUntil.HasValue && markedUntil.Value > DateTimeOffset.Now;
		}
	}

	/// <summary>倒计时展示文本；不活跃时返回 null。</summary>
	public static string? BannerText()
	{
		if (!IsActive)
		{
			return null;
		}
		TimeSpan remaining = MarkedUntil!.Value - DateTimeOffset.Now;
		string text = "⚠ 网易 IP 风控标记中（" + (Source ?? "登录") + "，账号 " + (AccountMasked ?? "?") + "）：经验恢复期约 " + RecoveryMinutes + " 分钟，剩余 " + remaining.ToString(@"mm\:ss") + "。程序仍会照常自动重试，成功后本提示自动消失；若持续异常建议更换网络环境（如手机热点）。";
		return text;
	}
}

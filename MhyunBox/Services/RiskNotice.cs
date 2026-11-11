using System;

namespace MhyunBox.Services;

/// <summary>
/// 【已停用】网易登录环境风控冷静期 / 请求闸门。
///
/// 用户 2026-11-11 确认「现在没有登录环境异常相关问题」，要求去掉账号绑定页的
/// 风控提醒横幅及其下所有限制机制（全局冷静期、自动链路禁发、连续失败暂停）。
/// 程序侧不再对登录 / 保活 / 兜底重登设任何自设门槛。
///
/// 为避免重建 20+ 处调用点，这里保留同名 API 作为**无副作用兼容空壳**：
/// Mark/Clear 不再保存任何状态，IsActive 与 ShouldBlockAutomaticRequests 恒为 false，
/// BannerText 恒为 null，Changed 永不触发。后续可安全删除本类及全部引用。
/// </summary>
public static class RiskNotice
{
	public const int RecoveryMinutes = 0;

	public static DateTimeOffset? MarkedUntil => null;

	public static string? AccountMasked => null;

	public static string? Source => null;

	public static event Action? Changed
	{
		add { }
		remove { }
	}

	public static void Mark(string maskedAccount, string source)
	{
	}

	public static void Clear()
	{
	}

	public static bool IsActive => false;

	public static bool ShouldBlockAutomaticRequests => false;

	public static TimeSpan Remaining => TimeSpan.Zero;

	public static string? BannerText() => null;
}

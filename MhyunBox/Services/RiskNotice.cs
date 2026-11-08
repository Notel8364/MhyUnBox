using System;

namespace MhyunBox.Services;

/// <summary>
/// 网易 IP 级登录环境风控（login abnormally / code=32）标记与【真实请求闸门】。
///
/// 修复说明（2026-11-07）：此前这里只做「信息展示」，程序侧不设任何请求门槛，
/// 命中风控后后台保活与失败兜底仍照常发出账密登录，形成自我强化的登录风暴，
/// 导致同机所有账号（含官方启动器 / QQ 邮箱账号）一起被网易标记。
/// 现在 Mark 即进入全局冷静期：
///   - 自动链路（周期保活、账密保活、失败兜底重登、自动重登）一律跳过；
///   - 手动登录（账号绑定页 / 手动点登录）不受限，成功后 Clear 立即解除标记；
///   - 冷静期内持续被拒会顺延，直到出现一次成功登录或自然到期。
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

	private static readonly object Sync = new object();

	/// <summary>登记一次风控标记。持续被拒时顺延，保证冷静期不被后续重试冲掉。</summary>
	public static void Mark(string maskedAccount, string source)
	{
		DateTimeOffset until = DateTimeOffset.Now.AddMinutes(RecoveryMinutes);
		bool changed;
		lock (Sync)
		{
			changed = MarkedUntil == null || MarkedUntil < until;
			if (changed)
			{
				MarkedUntil = until;
			}
			AccountMasked = maskedAccount;
			Source = source;
		}
		if (changed || IsActive)
		{
			Changed?.Invoke();
		}
	}

	/// <summary>任一登录成功后清除标记（网易已放行说明风控已解除）。</summary>
	public static void Clear()
	{
		bool had;
		lock (Sync)
		{
			had = MarkedUntil != null;
			MarkedUntil = null;
			AccountMasked = null;
			Source = null;
		}
		if (had)
		{
			Changed?.Invoke();
		}
	}

	/// <summary>是否处于冷静期（时间自然到期也算解除）。</summary>
	public static bool IsActive
	{
		get
		{
			lock (Sync)
			{
				return MarkedUntil.HasValue && MarkedUntil.Value > DateTimeOffset.Now;
			}
		}
	}

	/// <summary>自动链路的统一闸门：冷静期内禁止一切自动登录 / 后台保活 / 失败兜底重登请求。</summary>
	public static bool ShouldBlockAutomaticRequests => IsActive;

	/// <summary>冷静期剩余时间；不活跃时为零。</summary>
	public static TimeSpan Remaining
	{
		get
		{
			lock (Sync)
			{
				return MarkedUntil.HasValue && MarkedUntil.Value > DateTimeOffset.Now
					? MarkedUntil.Value - DateTimeOffset.Now
					: TimeSpan.Zero;
			}
		}
	}

	/// <summary>倒计时展示文本；不活跃时返回 null。</summary>
	public static string? BannerText()
	{
		if (!IsActive)
		{
			return null;
		}
		return "⚠ 网易 IP 风控冷静期（" + (Source ?? "登录") + "，账号 " + (AccountMasked ?? "?") + "）：剩余 "
			+ Remaining.ToString(@"mm\:ss") + "。已暂停自动登录 / 后台保活 / 失败兜底重登；手动登录不受限，成功后自动解除。若持续异常建议更换网络环境（如手机热点）。";
	}
}

using System;
using System.Linq;

namespace MhyunBox.Services.BjdProxy;

/// <summary>
/// 布吉岛专用 A/B 会话共享代理的门禁（**唯一真源**）。
///
/// 这个能力只对布吉岛服务器开放：它把 A 客户端已登录的远端连接复制给 B，
/// 与 <see cref="ProxyCaptureService"/> 那套「按账号重新走网易云认证」的
/// 通用代理是两套互不替代的机制，不能混用、也不能把门禁加到通用代理里。
///
/// 约定：任何入口（UI 可见性、按钮点击、注入前复核、自启恢复、分组启动）
/// 都必须经过本类的判定，禁止在别处重复硬编码 GameId 或服务器名。
/// </summary>
public static class BjdGate
{
	/// <summary>布吉岛服务器（布吉岛·新玩法上线）的网易 GameId。判定只认这个值。</summary>
	public const long GameId = 4661334467366178884L;

	/// <summary>与 <see cref="ServerPresets"/> 中的预设名保持一致。</summary>
	public const string PresetName = "布吉岛·新玩法上线";

	/// <summary>预设上游地址（来自运行时日志 pc.bjdmc.net:25565）。</summary>
	public const string Host = "pc.bjdmc.net";

	/// <summary>预设上游端口。</summary>
	public const int Port = 25565;

	/// <summary>
	/// 客户端窗口标题关键词。注入器靠它定位目标 java 进程，
	/// 与 SakuraTools / WtfNel 的既有做法一致（窗口标题可被伪装，
	/// 所以它还只是「选中布吉岛预设」之后的第二道确认，真正的主判据是 GameId）。
	/// </summary>
	public const string WindowKeyword = "布吉岛";

	/// <summary>按 GameId 判定。</summary>
	public static bool IsBjd(long gameId) => gameId == GameId;

	/// <summary>按预设判定：必须是已解析（非 IsPending）且 GameId 命中。</summary>
	public static bool IsBjd(ServerPreset? preset)
		=> preset != null && !preset.IsPending && IsBjd(preset.GameId);

}

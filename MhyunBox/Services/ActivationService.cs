using System;

namespace MhyunBox.Services;

/// <summary>
/// 开源版本：不再存在卡密/激活机制，所有功能默认全量开放。
/// 保留原 API 形状（IsActivated / FormatRemaining / StateChanged 等）以兼容既有调用点。
/// </summary>
public static class ActivationService
{
	public static bool IsActivated => true;

	public static string FormatRemaining()
	{
		return "开源版本 · 永久可用";
	}

	public static (bool Ok, int Days, string Message) Activate(string cardKey)
	{
		return (Ok: true, Days: 0, Message: "开源版本无需激活，所有功能已开放");
	}

	public static void Deactivate()
	{
		try
		{
			SecureStore.Delete("activation");
		}
		catch
		{
		}
	}

	public static event Action? StateChanged;
}

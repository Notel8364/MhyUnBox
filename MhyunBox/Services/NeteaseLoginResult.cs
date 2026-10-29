namespace MhyunBox.Services;

public sealed class NeteaseLoginResult
{
	public bool Success;

	public string Message = "";

	public string Nickname = "";

	public string EntityId = "";

	public string Token = "";

	public string Channel = "";

	public string CookieJson = "";

	public string VerifyUrl = "";

	// 失败分类（不参与序列化/落盘，仅用于自动重登与诊断）：
	// SessionExpired=会话确定性失效，可立即换会话；RiskControl=网易风控/登录环境异常，应进入冷静期避免频繁重登。
	public bool SessionExpired;

	public bool RiskControl;

	public string FailureStage = "";

	public string FailureStatus = "";
}

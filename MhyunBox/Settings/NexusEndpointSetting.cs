namespace MhyunBox.Settings;

/// <summary>
/// 实例多开的单条本地端口映射配置：Key = user@host:port，
/// ListenPort 是游戏侧要连接的 127.0.0.1 本地端口，Enabled 表示是否参与连接/重连。
/// RoleName 是卡片对应的角色名：同一账号 + 同一服务器可以挂多个角色（各占一个本地端口），
/// 删除 / 启用 / 隐藏都必须按 Key + RoleName 精确匹配，不能只用 Key，否则同账号的其它角色会被误删。
/// </summary>
public sealed class NexusEndpointSetting
{
	public string Key { get; set; } = "";

	/// <summary>角色名；空表示旧版本数据或没有角色名的卡片（此时卡片以 Key 唯一）。</summary>
	public string RoleName { get; set; } = "";

	public int ListenPort { get; set; }

	public bool Enabled { get; set; } = true;

	/// <summary>该角色是否参与「程序启动时自动启动机器人代理」；总开关是 AppSettings.AutoStartBotOnStartup。</summary>
	public bool AutoStart { get; set; }

	/// <summary>
	/// 机器人账号写进哪个客户端目录（空 = 按目标服务器自动解析）。
	/// 单客户端 10 个会话到顶，超出的卡片会自动过渡到第二个同服客户端，结果记在这里。
	/// </summary>
	public string ClientDir { get; set; } = "";
}

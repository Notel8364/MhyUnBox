using System;

namespace MhyunBox.Settings;

/// <summary>
/// 「新建启动卡」：一份独立的客户端启动配置。
/// 每张卡都可以单独启动一个官方客户端（允许同时多开），
/// 与服务器预设不同——启动卡不参与代理自动匹配，只按自己保存的配置启动。
/// </summary>
public sealed class ClientLaunchCard
{
	/// <summary>卡片名称（下拉里显示为「启动卡 · 名称」）。</summary>
	public string Name { get; set; } = "";

	/// <summary>关联的服务器预设名（空 = 不指定）。仅用来判定是否需要假启动器 + 代理进服。</summary>
	public string PresetName { get; set; } = "";

	/// <summary>客户端 .minecraft 根目录（留空 = 自动查找）。</summary>
	public string ClientRoot { get; set; } = "";

	/// <summary>指定 versions\&lt;name&gt; 版本目录名（留空 = 自动选择；机器人宿主需选挂号版）。</summary>
	public string VersionDir { get; set; } = "";

	/// <summary>直连地址 host:port（留空 = 启动后不自动进服）。</summary>
	public string DirectAddress { get; set; } = "";

	/// <summary>JRE 目录（留空 = 自动查找）。</summary>
	public string JavaHome { get; set; } = "";

	/// <summary>程序启动时自动按这张卡拉起客户端（每张卡独立）。</summary>
	public bool AutoStart { get; set; }
}

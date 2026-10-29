using System.Collections.Generic;

namespace MhyunBox.Services;

public sealed class ModInjectRule
{
	public List<string> Keywords { get; } = new List<string>();

	public string SourceFileName { get; set; } = "";

	public string TargetFileName { get; set; } = "";

	public string ModId { get; set; } = "";

	public string ModVersion { get; set; } = "";

	/// <summary>同族旧版识别关键词（客户端 mods 文件名转小写后包含即算同族，注入最新后其余删除）</summary>
	public string Family { get; set; } = "";

	/// <summary>moreshow=元素之诗显示模组（官网自动更新）；cptd=MhyCPTD（受 PCL 区开关控制）</summary>
	public string Kind { get; set; } = "";

	/// <summary>非空时从该 autoindex 页解析最新 .jar 下载更新（如 https://elementsystem.xyz/downloads/）</summary>
	public string DownloadPage { get; set; } = "";
}

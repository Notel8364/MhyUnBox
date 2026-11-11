using System;

namespace MhyunBox.Services;

/// <summary>
/// 服务器列表自动发现（已停用）。
/// 现在服务器预设只保留内置的「元素之诗 / 失落世界」，不再后台主动从服务器列表抓取并注册其它预设；
/// 需要其它服务器时，由用户在「服务器列表」页手动「添加为服务器预设」。
/// </summary>
public static class ServerAutoDiscovery
{
	private static int _reported;

	/// <summary>入口：保留签名兼容旧调用，实际不做任何网络请求与预设新增。</summary>
	public static void Start(int delaySeconds = 8)
	{
		if (System.Threading.Interlocked.Exchange(ref _reported, 1) == 0)
		{
			LogHelper.Log("[服务器自动发现] 已停用：预设只保留「元素之诗 / 失落世界」，不再后台主动添加其它服务器。");
		}
	}
}

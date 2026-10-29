using System;
using System.Linq;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>
/// 服务器列表自动发现：登录会话就绪后，后台用已登录网易账号拉取服务器列表前几页，
/// 解析进入地址并自动注册为预设（ServerPresets.RegisterDiscovered 自带去重，
/// 内置默认预设「元素之诗 / 失落世界」保留不动）。全程后台静默，任何失败不影响使用。
/// </summary>
public static class ServerAutoDiscovery
{
	private static int _running;

	/// <summary>入口：启动后台自动发现（可重复调用，重入被忽略）。delaySeconds 用于错开启动高峰。</summary>
	public static void Start(int delaySeconds = 8)
	{
		if (System.Threading.Interlocked.Exchange(ref _running, 1) == 1)
		{
			return;
		}
		_ = Task.Run(async delegate
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(continueOnCapturedContext: false);
				await RunAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
			catch
			{
			}
			finally
			{
				System.Threading.Interlocked.Exchange(ref _running, 0);
			}
		});
	}

	private static async Task RunAsync()
	{
		AccountStore.LocalAccount? acc = AccountStore.All.FirstOrDefault((AccountStore.LocalAccount a) => a.IsLoggedIn && a.Channel == "netease" && !string.IsNullOrEmpty(a.EntityId) && !string.IsNullOrEmpty(a.Token));
		if (acc == null)
		{
			LogHelper.Log("[服务器自动发现] 暂无已登录网易账号，跳过本轮（下次登录后自动重试）");
			return;
		}
		LogHelper.Log("[服务器自动发现] 开始: 账号 " + Mask(acc.Account));
		int discovered = 0;
		int skipped = 0;
		try
		{
			var list = await NeteaseServerListService.ListServersAsync(acc.EntityId, acc.Token, 0, 10).ConfigureAwait(continueOnCapturedContext: false);
			foreach (var item in list.Take(10))
			{
				try
				{
					// EntityId 是字符串形式的 gameId，解析失败直接跳过该服务器
					if (!long.TryParse(item.EntityId, out long gameId) || gameId <= 0)
					{
						skipped++;
						continue;
					}
					if (ServerPresets.ContainsGameId(gameId))
					{
						skipped++;
						continue;
					}
					var info = await NeteaseServerListService.ResolveAddressAsync(acc.EntityId, acc.Token, item.EntityId).ConfigureAwait(continueOnCapturedContext: false);
					if (!info.Ok || string.IsNullOrWhiteSpace(info.Ip) || info.Port <= 0)
					{
						continue;
					}
					ServerPreset? preset = ServerPresets.RegisterDiscovered(item.Name, info.Ip, info.Port, gameId);
					if (preset != null)
					{
						discovered++;
					}
					else
					{
						skipped++;
					}
					await Task.Delay(800).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.Log("[服务器自动发现] 拉取失败（不影响使用）: " + ex.Message);
			return;
		}
		LogHelper.Log("[服务器自动发现] 完成: 新增预设 " + discovered + " 个，跳过（已存在）" + skipped + " 个");
	}

	private static string Mask(string account)
	{
		if (string.IsNullOrEmpty(account) || account.Length <= 3)
		{
			return "***";
		}
		return account.Substring(0, 3) + "***";
	}
}

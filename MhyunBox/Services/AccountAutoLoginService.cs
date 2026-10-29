using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

public static class AccountAutoLoginService
{
	private static readonly Dictionary<string, int> RetryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	// 设计原则（2026-10-29 定版）：程序侧不设任何自作主张的"风控冷静期"，服务器和网易侧没有的限制
	// 程序也不替它们猜：进不去、被拦截都照样照常发请求，仅记录日志供排查。
	// 唯一例外是用户本轮明确要求的：自动重登连续失败 3 次 → 暂停自动登录 30 分钟（手动不受限，见下方注释）。

	// 同一账号同一时间只允许一个重登任务，避免 S003/S004 两路连接同时失败时各登一次、重复请求。
	private static readonly Dictionary<string, Task<bool>> InFlight = new Dictionary<string, Task<bool>>(StringComparer.OrdinalIgnoreCase);

	// 用户定版（2026-10-29）：自动重登连续失败达到 3 次时，暂停该账号的自动登录 30 分钟
	//（防止会话彻底坏掉时无限循环打扰网易）；手动登录/手动会话拉取不受任何限制，
	// 且手动登录成功后立即解除暂停。计数只统计「自动路径」的整体连续失败，
	// 不受 ResetRetryFor（后台兜底新一轮清零）影响。
	private const int MaxConsecutiveAutoFailures = 3;

	private static readonly TimeSpan AutoSuspendDuration = TimeSpan.FromMinutes(30.0);

	private static readonly Dictionary<string, int> ConsecutiveAutoFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private static readonly Dictionary<string, DateTimeOffset> AutoSuspendedUntil = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

	private static readonly object InFlightSync = new object();

	private static readonly Random DelayRandom = new Random();

	private static CancellationTokenSource? _cts;

	public static string StatusText { get; private set; } = "";

	// 最近一次 ReloginAsync 的失败原因（成功时清空），供自动兜底判断是否为"会话已死"类确定性失败
	public static string LastReloginMessage { get; private set; } = "";

	public static event Action? StatusChanged;

	public static void Start()
	{
		// 会话保活（每 15 分钟给开了自动登录且有本地凭证的账号重拉 token）
		StartPeriodicRefresh();
		// 账密级保活（每 90 分钟给保存了明文账密的账号用账密重新登录换全新会话，4399 换 Cookie / 网易账密重登）
		StartCredentialRefresh();
	}

	private static bool _periodicStarted;

	// 每 15 分钟给开了自动登录且有本地凭证的账号重拉一次 token。
	// 旧版是 5 分钟，且失败后立刻走账密重登：4 个账号每小时会发几十次 login-otp，
	// 网易侧很容易返回 login abnormally，并且新 token 还可能顶掉游戏内正在用的旧会话。
	// 进服时 NeteaseAuthBridge 本来就会现拉一次 token，所以后台保活只做低频预热，不再做账密回退。
	public static void StartPeriodicRefresh()
	{
		if (_periodicStarted)
		{
			return;
		}
		_periodicStarted = true;
		Task.Run(async () =>
		{
			while (true)
			{
				try
				{
					await Task.Delay(TimeSpan.FromMinutes(15.0)).ConfigureAwait(continueOnCapturedContext: false);
					List<AccountStore.LocalAccount> accounts = AccountStore.All.Where((AccountStore.LocalAccount a) => a.AutoLogin && !string.IsNullOrWhiteSpace(a.CookieJson)).ToList();
					if (accounts.Count > 0)
					{
						LogHelper.Log("[自动登录] 周期保活开始（每 15 分钟），共 " + accounts.Count + " 个账号");
						foreach (AccountStore.LocalAccount acc in accounts)
						{
							try
							{
								if (string.IsNullOrWhiteSpace(acc.CookieJson))
								{
									continue;
								}
								if (ProxyCaptureService.IsAccountInGame(acc.Channel, acc.Account))
								{
									// 游戏内会话正在使用旧 token，此时再 login-otp 可能把旧会话顶掉，
									// 表现为「玩着玩着网易云验证失败」。进服/重连时自然会重新拉取。
									LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 正在游戏内，跳过周期保活");
									continue;
								}
								if (IsAutoLoginSuspended(acc.Channel, acc.Account))
								{
									LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 自动登录暂停期（连续重登失败 3 次），跳过周期保活");
									continue;
								}
								bool flag = await ReloginViaCookieAsync(acc).ConfigureAwait(continueOnCapturedContext: false);
								ResetRetry(acc);
								if (flag)
								{
									AccountStore.SetLoginState(acc.Channel, acc.Account, loggedIn: true);
								}
								await Task.Delay(RandomDelay()).ConfigureAwait(continueOnCapturedContext: false);
							}
							catch (Exception ex2)
							{
								LogHelper.LogError(ex2, "[自动登录] 周期保活单账号异常: " + Mask(acc.Account));
							}
						}
					}
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "[自动登录] 周期保活异常");
				}
			}
		});
	}

	private static bool _credentialStarted;

	// 每 90 分钟给保存了明文账密的账号用账密直接重新登录一次，拿全新 Cookie+token。
	// 旧版 30 分钟 + 每个账号都登录，账号多时会被网易当成批量登录；拉长到 90 分钟并跳过游戏内账号。
	public static void StartCredentialRefresh()
	{
		if (_credentialStarted)
		{
			return;
		}
		_credentialStarted = true;
		Task.Run(async () =>
		{
			while (true)
			{
				try
				{
					await Task.Delay(TimeSpan.FromMinutes(90.0)).ConfigureAwait(continueOnCapturedContext: false);
					List<AccountStore.LocalAccount> accounts = AccountStore.All.Where((AccountStore.LocalAccount a) => a.AutoLogin && a.HasCredentials).ToList();
					LogHelper.Log("[自动登录] 账密保活检查（每 90 分钟）：有账密账号 " + accounts.Count + " 个");
					if (accounts.Count > 0)
					{
						foreach (AccountStore.LocalAccount acc in accounts)
						{
							try
							{
								if (ProxyCaptureService.IsAccountInGame(acc.Channel, acc.Account))
								{
									LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 正在游戏内，跳过账密保活");
									continue;
								}
								if (IsAutoLoginSuspended(acc.Channel, acc.Account))
								{
									LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 自动登录暂停期（连续重登失败 3 次），跳过账密保活");
									continue;
								}
								bool flag = acc.Is4399
									? await ReloginVia4399Async(acc, default(CancellationToken)).ConfigureAwait(continueOnCapturedContext: false)
									: await ReloginViaNeteasePasswordAsync(acc, default(CancellationToken)).ConfigureAwait(continueOnCapturedContext: false);
								ResetRetry(acc);
								if (flag)
								{
									AccountStore.SetLoginState(acc.Channel, acc.Account, loggedIn: true);
								}
								await Task.Delay(RandomDelay()).ConfigureAwait(continueOnCapturedContext: false);
							}
							catch (Exception ex2)
							{
								LogHelper.LogError(ex2, "[自动登录] 账密保活单账号异常: " + Mask(acc.Account));
							}
						}
					}
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "[自动登录] 账密保活异常");
				}
			}
		});
	}

	public static void Cancel()
	{
		try
		{
			_cts?.Cancel();
		}
		catch
		{
		}
	}

	public static async Task RunAsync(CancellationToken token = default(CancellationToken))
	{
		int ok = 0;
		int fail = 0;
		int skip = 0;
		try
		{
			List<AccountStore.LocalAccount> accounts = AccountStore.All.Where((AccountStore.LocalAccount a) => !string.IsNullOrWhiteSpace(a.CookieJson)).ToList();
			LogHelper.Log("[自动登录] 会话拉取开始，共 " + accounts.Count + " 个账号（手动触发或按需拉取）");
			int index = 0;
			foreach (AccountStore.LocalAccount acc in accounts)
			{
				if (!token.IsCancellationRequested)
				{
					index++;
					SetStatus("自动登录 " + index + "/" + accounts.Count + "：" + Mask(acc.Account));
					if (string.IsNullOrWhiteSpace(acc.CookieJson))
					{
						skip++;
						LogHelper.Log("[自动登录] " + acc.Channel + " " + Mask(acc.Account) + " 无本地凭证，跳过");
					}
					else if (await ReloginAsync(acc, token).ConfigureAwait(continueOnCapturedContext: false))
					{
						AccountStore.SetLoginState(acc.Channel, acc.Account, loggedIn: true);
						// 手动会话拉取成功同样解除自动登录暂停（手动不受限，成功即说明会话已恢复）
						ClearAutoLoginSuspension(acc.Channel, acc.Account);
						ok++;
					}
					else
					{
						fail++;
					}
					ResetRetry(acc);
					if (index < accounts.Count && !token.IsCancellationRequested)
					{
						await Task.Delay(RandomDelay(), token).ConfigureAwait(continueOnCapturedContext: false);
					}
					continue;
				}
				break;
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			LogHelper.LogError(ex3, "[自动登录] 会话拉取异常");
		}
		finally
		{
			SetStatus("会话拉取完成：成功 " + ok + " 个" + ((fail > 0) ? ("，失败 " + fail + " 个") : "") + ((skip > 0) ? ("，跳过 " + skip + " 个") : ""));
			LogHelper.Log("[自动登录] 会话拉取结束：成功 " + ok + "，失败 " + fail + "，跳过 " + skip);
		}
	}

	public static async Task<bool> ReloginAsync(AccountStore.LocalAccount acc, CancellationToken token = default(CancellationToken))
	{
		try
		{
			// 先用本地 Cookie 拉一次会话；成功路径与 ReloginViaCookieAsync 完全一致，直接复用不重复实现
			if (await ReloginViaCookieAsync(acc, token).ConfigureAwait(continueOnCapturedContext: false))
			{
				return true;
			}
			// 拉取失败只记日志：不自动取消登录态、不标记失效（账号可能仍可用，由实际进入会话失败时按需重登兜底）
			LogHelper.Log("[自动登录] 拉取失败: " + acc.Channel + " " + Mask(acc.Account) + " => " + LastReloginMessage);
			// 保存了明文账密的账号：Cookie 级拉取失败立即用账密重登换新会话
			//（4399=重登 4399 换新 Cookie；网易云=账密重新登录网易。不等失效判定，持续挂机）
			// 注意：即使是网易拦截类失败（login abnormally）也照常继续账密重登——程序侧不做冷静期门槛。
			if (acc.HasCredentials)
			{
				return await (acc.Is4399
					? ReloginVia4399Async(acc, token)
					: ReloginViaNeteasePasswordAsync(acc, token)).ConfigureAwait(continueOnCapturedContext: false);
			}
			return false;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			LogHelper.LogError(ex3, "[自动登录] 拉取异常: " + acc.Channel + " " + Mask(acc.Account));
			return false;
		}
	}

	// 用保存的 4399 明文账密重登 4399 换新 Cookie，再走一次网易会话拉取。
	// 成功则新 Cookie 已写回账号记录（BindVerified），后续拉取/进服直接用新会话。
	private static async Task<bool> ReloginVia4399Async(AccountStore.LocalAccount acc, CancellationToken token)
	{
		try
		{
			LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 本地会话已失效，改用保存的 4399 账密直接重登换新 Cookie");
			string cookie;
			using (Login4399Service svc = new Login4399Service())
			{
				Login4399Result r = await Task.Run(() => svc.LoginAsync(acc.LoginUserPlain, acc.LoginPassPlain), token).ConfigureAwait(continueOnCapturedContext: false);
				if (!r.Success || string.IsNullOrWhiteSpace(r.CookieJson))
				{
					LastReloginMessage = "4399 重登失败：" + r.Message;
					LogHelper.Log("[自动登录] 4399 账密重登失败: " + Mask(acc.Account) + " => " + r.Message + "（请到账号绑定页重新验证）");
					return false;
				}
				cookie = r.CookieJson;
			}
			NeteaseLogin login = new NeteaseLogin();
			NeteaseLoginResult result;
			try
			{
				result = await Task.Run(() => login.LoginWithCookieAsync(cookie, acc.Channel), token).ConfigureAwait(continueOnCapturedContext: false);
			}
			finally
			{
				((IDisposable)login).Dispose();
			}
			if (!result.Success || string.IsNullOrWhiteSpace(result.EntityId) || string.IsNullOrWhiteSpace(result.Token))
			{
				LastReloginMessage = result.Message ?? "";
				if (result.RiskControl || NeteaseLogin.IsRiskControlText(LastReloginMessage))
				{
					LogHelper.Log("[自动登录] 4399 换新 Cookie 后仍被网易拦截: " + Mask(acc.Account) + " => " + LastReloginMessage + "（仅记录，不设冷静期，下次照常请求）");
					return false;
				}
				LogHelper.Log("[自动登录] 4399 换新 Cookie 后拉取仍失败: " + Mask(acc.Account) + " => " + LastReloginMessage);
				return false;
			}
			LastReloginMessage = "";
			result.CookieJson = cookie;
			if (string.IsNullOrWhiteSpace(result.Nickname))
			{
				result.Nickname = acc.Nickname;
			}
			AccountStore.BindVerified(acc.Channel, acc.Account, result);
			AccountStore.SetLoginState(acc.Channel, acc.Account, loggedIn: true);
			ClearRiskCooldown(acc);
			RiskNotice.Clear();
			ElsewhereLoginNotice.Clear(acc.Channel, acc.Account);
			LogHelper.Log("[自动登录] 4399 账密重登成功，已换新 Cookie 并拉取 token: " + acc.Channel + " " + Mask(acc.Account));
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[自动登录] 4399 账密重登异常: " + acc.Channel + " " + Mask(acc.Account));
			return false;
		}
	}

	// 用保存的网易明文账密直接重新登录网易换新会话（网易云账号的持续挂机链路）。
	// 成功则新 Cookie 已写回账号记录（BindVerified），后续拉取/进服直接用新会话。
	private static async Task<bool> ReloginViaNeteasePasswordAsync(AccountStore.LocalAccount acc, CancellationToken token = default(CancellationToken))
	{
		try
		{
			LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 用保存的网易账密重新登录换新会话");
			NeteaseLoginResult result;
			NeteaseLogin login = new NeteaseLogin();
			try
			{
				result = await Task.Run(() => login.LoginAsync(acc.LoginUserPlain, acc.LoginPassPlain), token).ConfigureAwait(continueOnCapturedContext: false);
			}
			finally
			{
				((IDisposable)login).Dispose();
			}
			if (!result.Success || string.IsNullOrWhiteSpace(result.EntityId) || string.IsNullOrWhiteSpace(result.Token))
			{
				LastReloginMessage = result.Message ?? "";
				if (result.RiskControl || NeteaseLogin.IsRiskControlText(LastReloginMessage))
				{
					LogHelper.Log("[自动登录] 网易账密重登被网易拦截: " + Mask(acc.Account) + " => " + LastReloginMessage + "（仅记录，不设冷静期，下次照常请求）");
					return false;
				}
				LogHelper.Log("[自动登录] 网易账密重登失败: " + Mask(acc.Account) + " => " + LastReloginMessage + "（请到账号绑定页重新验证）");
				return false;
			}
			LastReloginMessage = "";
			if (string.IsNullOrWhiteSpace(result.CookieJson))
			{
				LogHelper.Log("[自动登录] 网易账密重登成功但未返回 Cookie，保留旧 Cookie 继续使用");
				result.CookieJson = acc.CookieJson;
			}
			if (string.IsNullOrWhiteSpace(result.Nickname))
			{
				result.Nickname = acc.Nickname;
			}
			AccountStore.BindVerified(acc.Channel, acc.Account, result);
			AccountStore.SetLoginState(acc.Channel, acc.Account, loggedIn: true);
			ClearRiskCooldown(acc);
			RiskNotice.Clear();
			ElsewhereLoginNotice.Clear(acc.Channel, acc.Account);
			LogHelper.Log("[自动登录] 网易账密重登成功，已换新会话: " + acc.Channel + " " + Mask(acc.Account));
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[自动登录] 网易账密重登异常: " + acc.Channel + " " + Mask(acc.Account));
			return false;
		}
	}

	/// <summary>
	/// 同一账号的自动重登做单飞合并：S003/S004 两路代理同时报会话失效时，
	/// 只发一次账密登录，另一路直接复用结果，避免把账号级 2 次重试额度瞬间耗尽。
	/// </summary>
	public static Task<bool> EnsureLoginForAsync(string channel, string account, CancellationToken token = default(CancellationToken))
	{
		string key = (channel ?? "") + "|" + (account ?? "").Trim();
		lock (InFlightSync)
		{
			// 用户定版限制：连续 3 次自动重登失败后暂停自动登录 30 分钟（手动登录走 ReloginAsync，不经过这里，不受限）
			if (AutoSuspendedUntil.TryGetValue(key, out DateTimeOffset suspendedUntil) && suspendedUntil > DateTimeOffset.Now)
			{
				TimeSpan remaining = suspendedUntil - DateTimeOffset.Now;
				SetStatus("自动登录已暂停（连续失败 3 次），约 " + (int)Math.Ceiling(remaining.TotalMinutes) + " 分钟后恢复：" + Mask(account));
				LogHelper.Log("[自动登录] " + Mask(account) + " 处于自动重登暂停期（连续失败 3 次触发），剩余约 " + (int)Math.Ceiling(remaining.TotalMinutes) + " 分钟，本次自动重登跳过（手动登录不受影响）");
				return Task.FromResult(false);
			}
			if (AutoSuspendedUntil.ContainsKey(key))
			{
				// 暂停已到期，恢复正常自动重登并重新计数
				AutoSuspendedUntil.Remove(key);
				ConsecutiveAutoFailures.Remove(key);
				LogHelper.Log("[自动登录] " + Mask(account) + " 暂停期已过，恢复自动重登");
			}
			if (InFlight.TryGetValue(key, out Task<bool>? running) && !running.IsCompleted)
			{
				LogHelper.Log("[自动登录] " + Mask(account) + " 已有重登请求进行中，复用本次结果");
				return running;
			}
			Task<bool> task = EnsureLoginForAsyncCore(channel, account, token);
			InFlight[key] = task;
			return AwaitAndClearAsync(key, task);
		}
	}

	private static async Task<bool> AwaitAndClearAsync(string key, Task<bool> task)
	{
		try
		{
			bool ok = await task.ConfigureAwait(continueOnCapturedContext: false);
			lock (InFlightSync)
			{
				if (ok)
				{
					ConsecutiveAutoFailures.Remove(key);
				}
				else
				{
					ConsecutiveAutoFailures.TryGetValue(key, out int failures);
					failures++;
					ConsecutiveAutoFailures[key] = failures;
					if (failures >= MaxConsecutiveAutoFailures && !AutoSuspendedUntil.ContainsKey(key))
					{
						DateTimeOffset until = DateTimeOffset.Now.Add(AutoSuspendDuration);
						AutoSuspendedUntil[key] = until;
						LogHelper.Force("[自动登录] 账号 " + Mask(key.Split('|')[1]) + " 连续 " + failures + " 次自动重登失败，自动登录已暂停 30 分钟（至 " + until.ToLocalTime().ToString("HH:mm") + "）。手动登录/手动验证不受限制，手动成功后立即恢复");
						SetStatus("自动登录已暂停 30 分钟（连续失败 3 次）：" + Mask(key.Split('|')[1]));
					}
				}
			}
			return ok;
		}
		finally
		{
			lock (InFlightSync)
			{
				if (InFlight.TryGetValue(key, out Task<bool>? current) && current == task)
				{
					InFlight.Remove(key);
				}
			}
		}
	}

	private static async Task<bool> EnsureLoginForAsyncCore(string channel, string account, CancellationToken token)
	{
		AccountStore.LocalAccount acc = AccountStore.All.FirstOrDefault((AccountStore.LocalAccount a) => string.Equals(a.Channel, channel, StringComparison.Ordinal) && string.Equals(a.Account, account, StringComparison.OrdinalIgnoreCase));
		if (acc == null)
		{
			return false;
		}
		LastReloginMessage = "";
		string key = Key(acc);
		RetryCounts.TryGetValue(key, out var value);
		// 不设重试上限与冷静期：会话失效就照常重登，进不去也照常请求（设计原则见文件头注释）。
		value++;
		RetryCounts[key] = value;
		SetStatus("会话失效，正在重登（第 " + value + " 次）：" + Mask(acc.Account));
		// 保存了明文账密的账号：会话第一次失效就直接用账密重登换全新会话，
		// 不先试旧 Cookie（进入会话都失败了旧 Cookie 大概率已死，白耗一次请求还拖慢恢复）。
		// 账密重登失败再回退试一次旧 Cookie（可能只是网络抖动）。
		//
		// 关键修正（Auth fail 兜底不生效的根因）：这一段必须放在「是否有 Cookie」判断之前。
		// 旧逻辑在无 CookieJson（会话为空/已被清空）时直接 return false，
		// 于是账号绑定页保存的明文账密永远用不上，Auth fail 只能靠后台 90 秒兜底，
		// 玩家当场就被踢下线。账密重登本身不需要任何 Cookie，应当优先尝试。
		if (acc.HasCredentials)
		{
			bool okPlain = (acc.Is4399
				? await ReloginVia4399Async(acc, token).ConfigureAwait(continueOnCapturedContext: false)
				: await ReloginViaNeteasePasswordAsync(acc, token).ConfigureAwait(continueOnCapturedContext: false));
			if (okPlain)
			{
				ResetRetry(acc);
				SetStatus("重登成功：" + Mask(acc.Account));
				return true;
			}
			// 不因"被网易拦截"而跳过旧 Cookie 回退：照常试一次（可能只是网络抖动）。
			if (!string.IsNullOrWhiteSpace(acc.CookieJson))
			{
				bool okCookie = await ReloginViaCookieAsync(acc, token).ConfigureAwait(continueOnCapturedContext: false);
				if (okCookie)
				{
					ResetRetry(acc);
					SetStatus("重登成功：" + Mask(acc.Account));
					return true;
				}
			}
			return false;
		}
		if (string.IsNullOrWhiteSpace(acc.CookieJson))
		{
			LastReloginMessage = "无本地会话且未保存明文账密，无法自动重登（请到账号绑定页点「账密」补登，或重新验证绑定）";
			LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 既无本地凭证也无明文账密，无法自动重登");
			return false;
		}
		bool flag = await ReloginAsync(acc, token).ConfigureAwait(continueOnCapturedContext: false);
		if (flag)
		{
			ResetRetry(acc);
			SetStatus("重登成功：" + Mask(acc.Account));
		}
		return flag;
	}

	// 仅用本地 Cookie 拉取一次会话（不做账密回退，账密路径由调用方决定），成功则 BindVerified。
	private static async Task<bool> ReloginViaCookieAsync(AccountStore.LocalAccount acc, CancellationToken token = default(CancellationToken))
	{
		try
		{
			NeteaseLogin login = new NeteaseLogin();
			NeteaseLoginResult result;
			try
			{
				result = await Task.Run(() => login.LoginWithCookieAsync(acc.CookieJson, acc.Channel), token).ConfigureAwait(continueOnCapturedContext: false);
			}
			finally
			{
				((IDisposable)login).Dispose();
			}
			if (!result.Success || string.IsNullOrWhiteSpace(result.EntityId) || string.IsNullOrWhiteSpace(result.Token))
			{
				LastReloginMessage = result.Message ?? "";
				LogHelper.Log("[自动登录] Cookie 拉取失败: " + acc.Channel + " " + Mask(acc.Account) + " => " + LastReloginMessage);
				return false;
			}
			LastReloginMessage = "";
			result.CookieJson = acc.CookieJson;
			if (string.IsNullOrWhiteSpace(result.Nickname))
			{
				result.Nickname = acc.Nickname;
			}
			AccountStore.BindVerified(acc.Channel, acc.Account, result);
			AccountStore.SetLoginState(acc.Channel, acc.Account, loggedIn: true);
			LogHelper.Log("[自动登录] Cookie 拉取成功: " + acc.Channel + " " + Mask(acc.Account));
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[自动登录] Cookie 拉取异常: " + acc.Channel + " " + Mask(acc.Account));
			return false;
		}
	}

	public static void MarkEnterSuccess(string channel, string account)
	{
		AccountStore.LocalAccount localAccount = AccountStore.All.FirstOrDefault((AccountStore.LocalAccount a) => string.Equals(a.Channel, channel, StringComparison.Ordinal) && string.Equals(a.Account, account, StringComparison.OrdinalIgnoreCase));
		if (localAccount != null)
		{
			ResetRetry(localAccount);
			// 进服成功说明会话可用，解除自动登录暂停（如有）
			ClearAutoLoginSuspension(channel, account);
		}
	}

	/// <summary>
	/// 按 渠道+账号 清零自动重登的尝试计数。
	///
	/// 存在的必要性（原来是「重登永远不生效」的元凶之一）：
	/// EnsureLoginForAsync 有账号级 2 次上限，而 NeteaseAuthBridge 当场重试失败后会
	/// 立刻转 AccountInvalidationDetector 兜底 —— 兜底里又调一次 EnsureLoginForAsync，
	/// 此时计数已到上限被直接拒绝，90 秒后的延迟兜底同样被拒绝，
	/// 于是一旦当场重登没成功，这个账号在本进程内就再也重登不了，只能重启程序。
	/// 后台兜底是「新一轮尝试」（间隔 90 秒，不会形成风暴），理应先清零再试。
	/// </summary>
	public static void ResetRetryFor(string channel, string account)
	{
		if (string.IsNullOrWhiteSpace(account))
		{
			return;
		}
		RetryCounts.Remove((channel ?? "") + "|" + account);
	}

	/// <summary>该账号当前已累计的自动重登次数（诊断/UI 提示用）。</summary>
	public static int GetRetryCount(string channel, string account)
	{
		if (string.IsNullOrWhiteSpace(account))
		{
			return 0;
		}
		if (RetryCounts.TryGetValue((channel ?? "") + "|" + account, out int value))
		{
			return value;
		}
		return 0;
	}

	/// <summary>
	/// 该账号的自动登录是否处于暂停期（连续 3 次自动重登失败触发，30 分钟）。
	/// 周期保活等自动链路用这个跳过暂停账号；手动登录不应检查此方法（手动不受限）。
	/// </summary>
	public static bool IsAutoLoginSuspended(string channel, string account)
	{
		string key = (channel ?? "") + "|" + (account ?? "").Trim();
		lock (InFlightSync)
		{
			if (AutoSuspendedUntil.TryGetValue(key, out DateTimeOffset until))
			{
				if (until > DateTimeOffset.Now)
				{
					return true;
				}
				// 暂停到期：顺便清掉状态，下次自动重登从零计数
				AutoSuspendedUntil.Remove(key);
				ConsecutiveAutoFailures.Remove(key);
			}
		}
		return false;
	}

	/// <summary>
	/// 解除自动登录暂停并清零连续失败计数。手动登录/手动验证成功后调用——
	/// 手动能登上说明会话已恢复，没理由再让自动链路等满 30 分钟。
	/// </summary>
	public static void ClearAutoLoginSuspension(string channel, string account)
	{
		string key = (channel ?? "") + "|" + (account ?? "").Trim();
		lock (InFlightSync)
		{
			bool had = AutoSuspendedUntil.Remove(key);
			ConsecutiveAutoFailures.Remove(key);
			if (had)
			{
				LogHelper.Log("[自动登录] " + Mask(account) + " 手动登录成功，自动登录暂停已提前解除");
			}
		}
	}

	/// <summary>
	/// 供认证桥等外部链路记录一次网易拦截（例如 GameStart 阶段返回 login abnormally）。
	/// 程序侧不设冷静期：只记日志，不影响任何请求节奏（设计原则见文件头注释）。
	/// </summary>
	public static void ReportRiskControl(string channel, string account, string message)
	{
		if (string.IsNullOrWhiteSpace(account) || !NeteaseLogin.IsRiskControlText(message))
		{
			return;
		}
		LogHelper.Log("[自动登录] " + Mask(account) + " 记录一次网易拦截: " + (message ?? "") + "（仅记录，不设冷静期，请求照常）");
		RiskNotice.Mark(Mask(account), "进服验证");
	}

	private static void SetRiskCooldown(AccountStore.LocalAccount acc, string message)
	{
		// 已废弃冷静期机制：仅保留日志+风控标记倒计时提示（纯信息展示，不设任何请求门槛）
		LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 命中网易拦截（" + (message ?? "") + "），仅记录不设冷静期");
		RiskNotice.Mark(Mask(acc.Account), "自动登录");
	}

	private static bool IsRiskCoolingDown(string channel, string account, out TimeSpan remaining)
	{
		// 已废弃冷静期机制：永远不在冷静期，任何调用方照常发请求
		remaining = TimeSpan.Zero;
		return false;
	}

	private static void ClearRiskCooldown(AccountStore.LocalAccount acc)
	{
		// 已废弃冷静期机制：无需要清理的状态
	}

	private static void ResetRetry(AccountStore.LocalAccount acc)
	{
		RetryCounts.Remove(Key(acc));
	}

	private static string Key(AccountStore.LocalAccount acc)
	{
		return acc.Channel + "|" + acc.Account;
	}

	private static int RandomDelay()
	{
		lock (DelayRandom)
		{
			return DelayRandom.Next(100, 1001);
		}
	}

	private static string Mask(string value)
	{
		return AccountStore.Mask(value);
	}

	private static void SetStatus(string text)
	{
		StatusText = text ?? "";
		StatusChanged?.Invoke();
	}
}

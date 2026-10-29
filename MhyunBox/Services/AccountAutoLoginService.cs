using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

public static class AccountAutoLoginService
{
	private static readonly Dictionary<string, int> RetryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	// 网易风控/登录环境异常（login abnormally）账号级冷静期：期间不再发任何登录请求。
	private static readonly Dictionary<string, DateTime> RiskCooldownUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	private static readonly object RiskSync = new object();

	private static readonly TimeSpan RiskCooldown = TimeSpan.FromSeconds(30.0);

	// 重登次数达到上限后的「默认风控期」：程序级暂停，冷却过后该账号的重登上限追加 1 次。
	private static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(10.0);

	private static DateTime DefaultCooldownUntilUtc = DateTime.MinValue;

	// 各账号的重登上限追加次数（重登成功后清零，上限回到默认值）。
	private static readonly Dictionary<string, int> RetryBonus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	// 同一账号同一时间只允许一个重登任务，避免 S003/S004 两路连接同时失败时各登一次、
	// 既浪费次数又把网易风控触发得更狠。
	private static readonly Dictionary<string, Task<bool>> InFlight = new Dictionary<string, Task<bool>>(StringComparer.OrdinalIgnoreCase);

	private static readonly object InFlightSync = new object();

	/// <summary>单账号自动重登次数上限（默认 5）。达到上限后进入默认风控期 10 秒，冷却过后上限追加 1 次；重登成功后计数清零、上限回到默认。</summary>
	private const int MaxRetry = 5;

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
								if (IsRiskCoolingDown(acc.Channel, acc.Account, out TimeSpan remaining))
								{
									LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 风控冷静期剩余 " + (int)Math.Ceiling(remaining.TotalSeconds) + " 秒，跳过周期保活");
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
								if (IsRiskCoolingDown(acc.Channel, acc.Account, out TimeSpan remaining))
								{
									LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 风控冷静期剩余 " + (int)Math.Ceiling(remaining.TotalSeconds) + " 秒，跳过账密保活");
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
			// login abnormally 是风控/登录环境异常：继续账密重登只会加重风控，直接结束本次。
			if (NeteaseLogin.IsRiskControlText(LastReloginMessage))
			{
				SetRiskCooldown(acc, LastReloginMessage);
				LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 本次为网易风控/登录环境异常，跳过账密重登，进入冷静期");
				return false;
			}
			// 保存了明文账密的账号：Cookie 级拉取失败立即用账密重登换新会话
			//（4399=重登 4399 换新 Cookie；网易云=账密重新登录网易。不等失效判定，持续挂机）
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
			if (IsRiskCoolingDown(acc.Channel, acc.Account, out TimeSpan remaining))
			{
				LastReloginMessage = "网易登录环境异常冷静期，剩余 " + (int)Math.Ceiling(remaining.TotalSeconds) + " 秒";
				LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 处于风控冷静期，跳过 4399 账密重登");
				return false;
			}
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
					SetRiskCooldown(acc, LastReloginMessage);
					LogHelper.Log("[自动登录] 4399 换新 Cookie 后仍被网易风控拦截: " + Mask(acc.Account) + " => " + LastReloginMessage + "（已进入 " + (int)RiskCooldown.TotalSeconds + " 秒冷静期，请勿频繁重登）");
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
			if (IsRiskCoolingDown(acc.Channel, acc.Account, out TimeSpan remaining))
			{
				LastReloginMessage = "网易登录环境异常冷静期，剩余 " + (int)Math.Ceiling(remaining.TotalSeconds) + " 秒";
				LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 处于风控冷静期，跳过网易账密重登");
				return false;
			}
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
					SetRiskCooldown(acc, LastReloginMessage);
					LogHelper.Log("[自动登录] 网易账密重登被风控拦截: " + Mask(acc.Account) + " => " + LastReloginMessage + "（已进入 " + (int)RiskCooldown.TotalSeconds + " 秒冷静期，请勿频繁重登）");
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
			return await task.ConfigureAwait(continueOnCapturedContext: false);
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
		if (IsRiskCoolingDown(acc.Channel, acc.Account, out TimeSpan remaining))
		{
			LastReloginMessage = "网易登录环境异常冷静期，剩余 " + (int)Math.Ceiling(remaining.TotalSeconds) + " 秒";
			SetStatus("风控冷静期，暂不重登：" + Mask(acc.Account));
			LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 处于风控冷静期，跳过本次自动重登（剩余 " + (int)Math.Ceiling(remaining.TotalSeconds) + " 秒）");
			return false;
		}
		if (IsDefaultCoolingDown(out TimeSpan defaultRemaining))
		{
			LastReloginMessage = "重登次数达上限，默认风控期，剩余 " + (int)Math.Ceiling(defaultRemaining.TotalSeconds) + " 秒";
			SetStatus("默认风控期，暂不重登：" + Mask(acc.Account));
			LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 处于默认风控期（重登次数达上限），跳过本次自动重登（剩余 " + (int)Math.Ceiling(defaultRemaining.TotalSeconds) + " 秒）");
			return false;
		}
		LastReloginMessage = "";
		string key = Key(acc);
		RetryCounts.TryGetValue(key, out var value);
		int limit = MaxRetry + GetRetryBonus(key);
		if (value >= limit)
		{
			// 达到重登上限：进入默认风控期 10 秒，冷却过后该账号上限追加 1 次（成功后清零回到默认）。
			EnterDefaultCooldown(acc, value, limit);
			return false;
		}
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
			// 账密登录已明确被网易风控拦截：不能再拿旧 Cookie 试，越试越糟。
			if (NeteaseLogin.IsRiskControlText(LastReloginMessage))
			{
				SetRiskCooldown(acc, LastReloginMessage);
				SetStatus("网易风控冷静期，暂不重登：" + Mask(acc.Account));
				return false;
			}
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
	/// 供认证桥等外部链路登记一次网易风控/登录环境异常（例如 GameStart 阶段返回 login abnormally）。
	/// 与重登路径命中风控时的处理一致：进入账号级冷静期，期间不再自动发任何登录请求，
	/// 避免越登越糟。非风控文本直接忽略。
	/// </summary>
	public static void ReportRiskControl(string channel, string account, string message)
	{
		if (string.IsNullOrWhiteSpace(account) || !NeteaseLogin.IsRiskControlText(message))
		{
			return;
		}
		AccountStore.LocalAccount? acc = AccountStore.All.FirstOrDefault((AccountStore.LocalAccount a) => string.Equals(a.Channel, channel ?? "", StringComparison.Ordinal) && string.Equals(a.Account, account.Trim(), StringComparison.OrdinalIgnoreCase));
		if (acc == null)
		{
			return;
		}
		SetRiskCooldown(acc, message);
	}

	private static void SetRiskCooldown(AccountStore.LocalAccount acc, string message)
	{
		try
		{
			DateTime until = DateTime.UtcNow + RiskCooldown;
			lock (RiskSync)
			{
				RiskCooldownUntil[Key(acc)] = until;
			}
			LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 命中网易风控/登录环境异常（" + (message ?? "") + "），进入 " + (int)RiskCooldown.TotalSeconds + " 秒冷静期至 " + until.ToLocalTime().ToString("HH:mm:ss"));
		}
		catch
		{
		}
	}

	private static bool IsRiskCoolingDown(string channel, string account, out TimeSpan remaining)
	{
		remaining = TimeSpan.Zero;
		if (string.IsNullOrWhiteSpace(account))
		{
			return false;
		}
		string key = (channel ?? "") + "|" + account.Trim();
		DateTime until;
		lock (RiskSync)
		{
			if (!RiskCooldownUntil.TryGetValue(key, out until))
			{
				return false;
			}
		}
		TimeSpan left = until - DateTime.UtcNow;
		if (left <= TimeSpan.Zero)
		{
			lock (RiskSync)
			{
				RiskCooldownUntil.Remove(key);
			}
			return false;
		}
		remaining = left;
		return true;
	}

	private static void ClearRiskCooldown(AccountStore.LocalAccount acc)
	{
		lock (RiskSync)
		{
			RiskCooldownUntil.Remove(Key(acc));
		}
	}

	private static void ResetRetry(AccountStore.LocalAccount acc)
	{
		RetryCounts.Remove(Key(acc));
		// 重登成功即重置：上限追加次数也清零，回到默认上限（5 次）
		lock (RiskSync)
		{
			RetryBonus.Remove(Key(acc));
		}
	}

	/// <summary>程序级默认风控期（重登次数达到上限后触发）：期间所有账号的自动重登暂停。</summary>
	private static bool IsDefaultCoolingDown(out TimeSpan remaining)
	{
		remaining = TimeSpan.Zero;
		TimeSpan left = DefaultCooldownUntilUtc - DateTime.UtcNow;
		if (left <= TimeSpan.Zero)
		{
			return false;
		}
		remaining = left;
		return true;
	}

	/// <summary>达到重登上限：进入默认风控期 10 秒，冷却过后该账号上限追加 1 次。</summary>
	private static void EnterDefaultCooldown(AccountStore.LocalAccount acc, int usedCount, int limit)
	{
		DateTime until = DateTime.UtcNow + DefaultCooldown;
		string key = Key(acc);
		int newLimit;
		lock (RiskSync)
		{
			DefaultCooldownUntilUtc = until;
			RetryBonus.TryGetValue(key, out int bonus);
			RetryBonus[key] = bonus + 1;
			newLimit = limit + 1;
		}
		LogHelper.Log("[自动登录] " + Mask(acc.Account) + " 已重试 " + usedCount + " 次达到上限 " + limit + "，进入 " + (int)DefaultCooldown.TotalSeconds + " 秒默认风控期至 " + until.ToLocalTime().ToString("HH:mm:ss") + "，冷却后该账号上限提升至 " + newLimit + " 次（重登成功后恢复默认）");
	}

	private static int GetRetryBonus(string key)
	{
		lock (RiskSync)
		{
			return RetryBonus.TryGetValue(key, out int bonus) ? bonus : 0;
		}
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

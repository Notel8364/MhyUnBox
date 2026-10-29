using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

public sealed class NeteaseAuthBridge
{
	public const long DefaultGameId = 4658261307332877912L;

	private string _serverIdStr = "";

	private byte[]? _realPubDer;

	private byte[]? _verifyToken;

	private byte[]? _ownSecret;

	private static string? _modsCache;

	private static DateTime _modsCacheAt;

	private string _failChannel = "";

	private string _failAccount = "";

	private string _failNickname = "";

	public long GameId { get; set; } = 4658261307332877912L;

	public string? ServerName { get; set; }

	public byte[] OwnSecret => _ownSecret ?? Array.Empty<byte>();

	public int AccountIndex { get; set; } = -1;

	public string LastFailReason { get; private set; } = "";

	public bool ParseEncryptionRequest(byte[] packet)
	{
		try
		{
			int pos = 0;
			if (NetShared.ReadVarInt(packet, ref pos) != 1)
			{
				return false;
			}
			_serverIdStr = NetShared.ReadString(packet, ref pos);
			_realPubDer = NetShared.ReadBytes(packet, ref pos);
			_verifyToken = NetShared.ReadBytes(packet, ref pos);
			LogHelper.Log($"[认证] 已拦截解析 EncryptionRequest: serverId={SecretRedaction.Fingerprint(_serverIdStr)} pubKey={_realPubDer.Length}B verifyToken={_verifyToken.Length}B 包尾随={packet.Length - pos}B");
			return true;
		}
		catch
		{
			return false;
		}
	}

	public byte[] GenerateOwnSecret()
	{
		_ownSecret = new byte[16];
		RandomNumberGenerator.Fill(_ownSecret);
		string text = Convert.ToHexString(_ownSecret);
		LogHelper.Log("[认证] 代理自生成共享密钥 " + text.Length + " 位");
		return _ownSecret;
	}

	public byte[] BuildOwnEncryptionResponse()
	{
		if (_ownSecret == null || _realPubDer == null || _verifyToken == null)
		{
			throw new InvalidOperationException("尚未解析 EncryptionRequest 或生成 secret");
		}
		byte[] bytes = EncryptWithRealPub(_ownSecret);
		byte[] bytes2 = EncryptWithRealPub(_verifyToken);
		using MemoryStream memoryStream = new MemoryStream();
		NetShared.WriteVarInt(memoryStream, 1);
		NetShared.WriteBytes(memoryStream, bytes);
		NetShared.WriteBytes(memoryStream, bytes2);
		byte[] array = memoryStream.ToArray();
		byte[] array2 = NetShared.WriteVarIntBytes(array.Length);
		byte[] array3 = new byte[array2.Length + array.Length];
		Array.Copy(array2, 0, array3, 0, array2.Length);
		Array.Copy(array, 0, array3, array2.Length, array.Length);
		return array3;
	}

	public string ComputeOwnCertification()
	{
		if (_ownSecret == null || _realPubDer == null)
		{
			throw new InvalidOperationException("尚未生成 secret 或解析公钥");
		}
		return ComputeSha1Hex(_serverIdStr, _ownSecret, _realPubDer);
	}

	public async Task<bool> JoinServerAsync(CancellationToken ct)
	{
		string text = ComputeOwnCertification();
		LogHelper.Log("[认证] certification 已生成(" + text.Length + "位)");
		return await AuthenticateAndJoinAsync(text, ct);
	}

	public static bool IsRiskControlMessage(string msg)
	{
		return NeteaseLogin.IsRiskControlText(msg);
	}

	public static bool IsSessionInvalidMessage(string msg)
	{
		if (string.IsNullOrWhiteSpace(msg))
		{
			return false;
		}
		// login abnormally 属于风控抖动，调用方会先用 IsRiskControlMessage 分流；
		// 这里仍返回 true，保证旧调用点（例如角色刷新）不会把它当成普通错误静默掉。
		if (IsRiskControlMessage(msg))
		{
			return true;
		}
		if (msg.Contains("token expired", StringComparison.OrdinalIgnoreCase) || msg.Contains("token 过期", StringComparison.Ordinal) || msg.Contains("会话超时", StringComparison.Ordinal))
		{
			return true;
		}
		if (msg.Contains("auth fail", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (!msg.StartsWith("会话为空") && !msg.StartsWith("会话校验失败"))
		{
			if (msg.StartsWith("会话已失效"))
			{
				return !msg.Contains("网络异常");
			}
			return false;
		}
		return true;
	}

	/// <summary>
	/// 判断 joinServer 的失败报文是否属于「网易认证服务器拒绝本次会话」这一类确定性信号。
	/// 这类信号在客户端渲染为「验证失败: Auth fail」，说明本地会话被踢/过期，
	/// 靠刷盐修不好，只能换一张新会话（明文账密重登）。
	/// </summary>
	private static bool IsAuthFailMessage(string msg)
	{
		if (string.IsNullOrWhiteSpace(msg))
		{
			return false;
		}
		if (msg.IndexOf("Auth fail", StringComparison.OrdinalIgnoreCase) >= 0 || msg.Contains("验证失败", StringComparison.Ordinal) || msg.StartsWith("join type=", StringComparison.Ordinal))
		{
			return true;
		}
		// 认证服务器对已失效会话的其它常见回执，同样按「换会话」处理
		return msg.Contains("invalid token", StringComparison.OrdinalIgnoreCase)
			|| msg.Contains("session", StringComparison.OrdinalIgnoreCase) && msg.Contains("invalid", StringComparison.OrdinalIgnoreCase)
			|| msg.Contains("未登录", StringComparison.Ordinal)
			|| msg.Contains("登录已失效", StringComparison.Ordinal);
	}

	/// <summary>
	/// 重登后重新定位账号记录。
	/// 不能用建链时的 accountIndex 直接取：BindVerified 若按 EntityId 命中到另一条记录、
	/// 或判定为新账号追加到末尾，下标就会错位，导致重试时又用回那张已经死掉的旧会话
	/// （表现为「自动重登成功但重试 join 仍失败」）。这里优先按 渠道+账号名 精确回查，
	/// 查不到再退回下标，最后退回最后一条最新写入的记录。
	/// </summary>
	private static AccountStore.LocalAccount? ResolveFreshAccount(string channel, string account, int accountIndex)
	{
		IReadOnlyList<AccountStore.LocalAccount> all = AccountStore.All;
		AccountStore.LocalAccount? byName = all.FirstOrDefault((AccountStore.LocalAccount a) => string.Equals(a.Channel, channel, StringComparison.Ordinal) && string.Equals(a.Account, account, StringComparison.OrdinalIgnoreCase));
		if (byName != null && !string.IsNullOrWhiteSpace(byName.EntityId) && !string.IsNullOrWhiteSpace(byName.Token))
		{
			return byName;
		}
		if (accountIndex >= 0 && accountIndex < all.Count)
		{
			AccountStore.LocalAccount localAccount = all[accountIndex];
			if (localAccount != null && !string.IsNullOrWhiteSpace(localAccount.EntityId) && !string.IsNullOrWhiteSpace(localAccount.Token))
			{
				return localAccount;
			}
		}
		return byName;
	}

	private async Task<bool> AuthenticateAndJoinAsync(string certification, CancellationToken ct)
	{
		try
		{
			int accountIndex = AccountIndex;
			LogHelper.Log($"[认证] 网易云认证启动: 账号索引={accountIndex} GameId={GameId}");
			IReadOnlyList<AccountStore.LocalAccount> list = AccountStore.All;
			if (accountIndex < 0 || accountIndex >= list.Count)
			{
				LastFailReason = "本地账号记录缺失（索引越界），请到账号绑定页重新绑定账号";
				LogHelper.Log($"[认证] 账号索引越界 {accountIndex}（可用 {list.Count} 个）");
				return false;
			}
			AccountStore.LocalAccount account = list[accountIndex];
			_failChannel = account.Channel;
			_failAccount = account.Account;
			_failNickname = account.Nickname;
			string entityId;
			string token;
			using (NeteaseLogin login = new NeteaseLogin())
			{
				NeteaseLoginResult neteaseLoginResult = await login.LoginWithCookieAsync(account.CookieJson, account.Channel).ConfigureAwait(continueOnCapturedContext: false);
				if (!neteaseLoginResult.Success)
				{
					string text2 = neteaseLoginResult.Message ?? "";
					// 新游戏报错 login abnormally 属于网易风控/登录环境异常：
					// 程序侧不设冷静期：记录后交后台兜底重登（会话失效即照常请求）。
					if (neteaseLoginResult.RiskControl || IsRiskControlMessage(text2))
					{
						// 文案已含具体原因（登录环境异常/会话失效），不再叠加前缀
						LastFailReason = text2 + "。程序会自动重试，无需手动操作";
						LogHelper.Force($"[认证] 账号「{account.Nickname}」login-otp 被网易拦截（{text2}），交后台兜底重登（照常请求）");
						AccountInvalidationDetector.ReportInGameSessionFailure(_failChannel, _failAccount, _failNickname, text2);
						return false;
					}
					// 确定性会话失效信号（token expired / 会话为空 / 会话校验失败，已排除"网络异常"抖动）：
					// 与游戏内「验证失败: Auth fail」同一机制——立即自动明文重登换新会话
					//（有账密→直接账密重登；无账密→回退试旧 Cookie；账号级 2 次重试上限防风暴），
					// 成功后原地使用新会话继续本次认证，不再直接把客户端踢下线。
					if (IsSessionInvalidMessage(text2))
					{
						LogHelper.Force($"[认证] 账号「{account.Nickname}」网易云会话已失效（{text2}），正在自动明文重登换取新会话…");
						bool relogined = await AccountAutoLoginService.EnsureLoginForAsync(account.Channel, account.Account, ct).ConfigureAwait(continueOnCapturedContext: false);
						if (relogined)
						{
							AccountStore.LocalAccount? fresh = ResolveFreshAccount(_failChannel, _failAccount, accountIndex);
							if (fresh != null)
							{
								entityId = fresh.EntityId;
								token = fresh.Token;
								account = fresh;
								LogHelper.Log($"[认证] 自动明文重登成功，本次认证换用新会话: entityId已获取({entityId.Length}位) token已获取({token.Length}位)");
								goto GotSession;
							}
							LogHelper.Log("[认证] 自动重登后未取到新会话字段，按失败处理");
						}
						else
						{
							// 原地重登未成功（无账密/重试上限/账密也失效）：交账号检测器后台兜底
							// （与 Auth fail 同链路，90 秒后再试一次），本次认证仍失败
							AccountInvalidationDetector.ReportInGameSessionFailure(_failChannel, _failAccount, _failNickname, text2);
						}
					}
					LastFailReason = "网易云会话校验失败：" + text2;
					LogHelper.Log("[认证] 会话刷新失败: " + text2);
					return false;
				}
				entityId = neteaseLoginResult.EntityId;
				token = neteaseLoginResult.Token;
				LogHelper.Log("[认证] 会话刷新成功: entityId已获取(" + entityId.Length + "位) token已获取(" + token.Length + "位)");
			}
		GotSession:
			if (GameId <= 0 && !string.IsNullOrWhiteSpace(ServerName))
			{
				string text3 = await NeteaseRoleService.ResolveServerIdAsync(entityId, token, ServerName, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (!string.IsNullOrWhiteSpace(text3) && long.TryParse(text3, out var result) && result > 0)
				{
					GameId = result;
					LogHelper.Log($"[认证] 动态解析 GameId 成功: ServerName={ServerName} => GameId={GameId}");
				}
				else
				{
					GameId = 4658261307332877912L;
					LogHelper.Log($"[认证] 动态解析 GameId 失败（ServerName={ServerName}），使用默认 GameId={GameId}");
				}
			}
			(bool, string) tuple = await NeteaseYggdrasil.StartGameSessionAsync(entityId, token, GameId.ToString()).ConfigureAwait(continueOnCapturedContext: false);
			if (!tuple.Item1)
			{
				string gsMsg = tuple.Item2 ?? "";
				// GameStart（login-start/start）阶段也可能直接返回 login abnormally（本次换到的会话刚被拒绝）：
				// 程序侧不设冷静期——记录并交后台兜底重登（90 秒后照常再请求）。
				if (IsRiskControlMessage(gsMsg))
				{
					LastFailReason = "网易返回登录环境异常：游戏会话启动失败，" + gsMsg + "。程序会自动重试，无需手动操作";
					LogHelper.Force($"[认证] 账号「{account.Nickname}」GameStart 阶段网易登录环境异常（{gsMsg}），记录并交后台兜底重登");
					AccountAutoLoginService.ReportRiskControl(account.Channel, account.Account, gsMsg);
					AccountInvalidationDetector.ReportInGameSessionFailure(_failChannel, _failAccount, _failNickname, gsMsg);
					return false;
				}
				LastFailReason = "服务器会话启动失败：" + gsMsg;
				LogHelper.Log("[认证] GameStart 失败: " + gsMsg);
				// 会话类确定性失效（auth fail / token expired / 会话已失效）：同样交后台兜底换新会话
				if (IsSessionInvalidMessage(gsMsg))
				{
					AccountInvalidationDetector.ReportInGameSessionFailure(_failChannel, _failAccount, _failNickname, gsMsg);
				}
				return false;
			}
			LogHelper.Log("[认证] GameStart 成功: " + tuple.Item2);
			string modsJson = await GetModsJsonAsync(entityId, token, ct);
			YggdrasilGameProfile profile = new YggdrasilGameProfile
			{
				GameId = GameId.ToString(),
				GameVersion = "1.12.2",
				BootstrapMd5 = "A895FE657915D58F55919CEACD30209D",
				DatFileMd5 = "51581ADD89B8AC5A0D8CCDD0E33EE1DE",
				ModsJson = modsJson,
				UserId = int.Parse(entityId),
				UserToken = token
			};
			YggdrasilResult yggdrasilResult = await NeteaseYggdrasil.JoinServerAsync(profile, certification, null, null, ct);
			if (!yggdrasilResult.Success)
			{
				LastFailReason = "网易云认证未通过：" + yggdrasilResult.Message;
				LogHelper.Log("[认证] Yggdrasil join 失败: " + yggdrasilResult.Message);
				// 只检测「验证失败: Auth fail」这一类确定性信号：它说明本次 join 被网易认证服务器拒绝
				//（join type= 拒绝码在游戏端即渲染为「验证失败: Auth fail」）。盐/版本家族的错误已在
				// JoinServerAsync 内部自愈（刷盐+版本梯子+盐值源轮换），走到这里多为会话被踢/过期。
				string yggMsg = yggdrasilResult.Message ?? "";
				// join 阶段也可能直接返回 login abnormally（例如会话刚被风控标记）。
				if (IsRiskControlMessage(yggMsg))
				{
					LastFailReason = "网易登录环境异常（风控拦截）：" + yggMsg + "。请等待 5-10 分钟后重试，期间不要频繁登录";
					LogHelper.Force($"[认证] 账号「{account.Nickname}」join 阶段网易登录环境异常（{yggMsg}），跳过立即重登");
					AccountInvalidationDetector.ReportInGameSessionFailure(_failChannel, _failAccount, _failNickname, yggMsg);
					return false;
				}
				bool authFail = IsAuthFailMessage(yggMsg);
				if (authFail)
				{
					// 与 token expired 同机制：原地等一次自动明文重登，成功后用新会话重试本次 join，
					// 玩家无需重连；重登成功就不再报后台兜底，避免并发账密登录撞「您的帐号在另一处登录」。
					LogHelper.Force($"[会话提醒] 账号「{account.Nickname}」进入游戏时网易云 join 被拒（{yggMsg}），正在自动重登换取新会话并重试…");
					bool relogined2 = await AccountAutoLoginService.EnsureLoginForAsync(account.Channel, account.Account, ct).ConfigureAwait(continueOnCapturedContext: false);
					if (relogined2)
					{
						AccountStore.LocalAccount? fresh2 = ResolveFreshAccount(account.Channel, account.Account, accountIndex);
						if (fresh2 != null)
						{
							LogHelper.Log($"[认证] Auth fail 后自动重登成功，用新会话重试: entityId已获取({fresh2.EntityId.Length}位) token已获取({fresh2.Token.Length}位)");
							var (flag3, text5) = await NeteaseYggdrasil.StartGameSessionAsync(fresh2.EntityId, fresh2.Token, GameId.ToString()).ConfigureAwait(continueOnCapturedContext: false);
							if (flag3)
							{
								LogHelper.Log("[认证] GameStart 成功(重试): " + text5);
								string modsJson2 = await GetModsJsonAsync(fresh2.EntityId, fresh2.Token, ct);
								YggdrasilGameProfile profile2 = new YggdrasilGameProfile
								{
									GameId = GameId.ToString(),
									GameVersion = "1.12.2",
									BootstrapMd5 = "A895FE657915D58F55919CEACD30209D",
									DatFileMd5 = "51581ADD89B8AC5A0D8CCDD0E33EE1DE",
									ModsJson = modsJson2,
									UserId = int.Parse(fresh2.EntityId),
									UserToken = fresh2.Token
								};
								YggdrasilResult retryResult = await NeteaseYggdrasil.JoinServerAsync(profile2, certification, null, null, ct);
								if (retryResult.Success)
								{
									LogHelper.Log($"[认证] Auth fail 后重试 joinServer 通过: 昵称={account.Nickname} gameId={profile2.GameId}");
									SyncRolesAfterJoin(fresh2, fresh2.EntityId, fresh2.Token);
									return true;
								}
								LastFailReason = "网易云认证未通过（自动重登后重试仍失败）：" + retryResult.Message;
								LogHelper.Log("[认证] 自动重登后重试 join 仍失败: " + retryResult.Message + "（若为 join type=0x10 拒绝码，多为 ModsJson/服务器侧策略，重登修不了，需查盐值或服务器）");
							}
							else
							{
								string retryGsMsg = text5 ?? "";
								// 重登后 GameStart 仍失败：若为 login abnormally，记录并交后台兜底（照常再请求）；
								// 否则按普通会话启动失败记录。
								if (IsRiskControlMessage(retryGsMsg))
								{
									LastFailReason = "网易返回登录环境异常：自动重登后游戏会话启动仍失败，" + retryGsMsg + "。程序会自动重试";
									LogHelper.Force($"[认证] 账号「{account.Nickname}」重登后 GameStart 阶段网易登录环境异常（{retryGsMsg}），记录并交后台兜底重登");
									AccountAutoLoginService.ReportRiskControl(account.Channel, account.Account, retryGsMsg);
									AccountInvalidationDetector.ReportInGameSessionFailure(_failChannel, _failAccount, _failNickname, retryGsMsg);
								}
								else
								{
									LastFailReason = "服务器会话启动失败（自动重登后重试）：" + retryGsMsg;
									LogHelper.Log("[认证] 自动重登后 GameStart 失败: " + retryGsMsg);
								}
							}
						}
						else
						{
							LogHelper.Log("[认证] 自动重登后未取到新会话字段，按失败处理");
						}
					}
					else
					{
						// 原地重登未成功（无账密/重试上限/账密也失效）：交账号检测器后台兜底，修下一次进入
						AccountInvalidationDetector.ReportInGameSessionFailure(_failChannel, _failAccount, _failNickname, "验证失败: Auth fail");
					}
				}
				return false;
			}
			LogHelper.Log($"[认证] Yggdrasil joinServer 通过: 昵称={account.Nickname} gameId={profile.GameId} modsJson={profile.ModsJson.Length}B");
			SyncRolesAfterJoin(account, entityId, token);
			return true;
		}
		catch (OperationCanceledException)
		{
			LastFailReason = "认证超时（已取消）";
			LogHelper.Log("[认证] 认证超时（已取消）");
			return false;
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			LastFailReason = "认证流程异常：" + ex3.Message;
			LogHelper.LogError(ex3, "[认证] 认证流程异常");
			return false;
		}
	}

	private void SyncRolesAfterJoin(AccountStore.LocalAccount account, string entityId, string token)
	{
		if (string.IsNullOrWhiteSpace(entityId) || string.IsNullOrWhiteSpace(token))
		{
			return;
		}
		Task.Run(async delegate
		{
			try
			{
				(bool, Dictionary<string, List<string>>, string) tuple = await NeteaseRoleService.CaptureRolesAsync(serverNames: ServerPresets.All.Select((ServerPreset p) => p.Name).ToList(), userId: entityId, userToken: token);
				if (tuple.Item1)
				{
					AccountStore.UpdateRoles(account.Channel, account.Account, tuple.Item2);
					account.EntityId = entityId;
					account.Token = token;
					AccountStore.Save(account);
					LogHelper.Log("[角色同步] 进服后已同步角色: " + tuple.Item3);
				}
				else
				{
					LogHelper.Log("[角色同步] 进服后角色同步失败: " + tuple.Item3);
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				LogHelper.LogError(ex2, "[角色同步] 异常");
			}
		});
	}

	private async Task<string> GetModsJsonAsync(string entityId, string userToken, CancellationToken ct)
	{
		string modsCache = _modsCache;
		if (modsCache != null && DateTime.UtcNow - _modsCacheAt < TimeSpan.FromHours(1.0))
		{
			return modsCache;
		}
		var (flag, text, text2) = await NeteaseYggdrasil.FetchGameModsAsync(entityId, userToken).ConfigureAwait(continueOnCapturedContext: false);
		if (flag && text != null)
		{
			_modsCache = text;
			_modsCacheAt = DateTime.UtcNow;
			LogHelper.Log($"[认证-mods] ModsJson({text.Length}B)");
			return text;
		}
		// 恒写：ModsJson 拉取失败走空列表兜底是 join 被拒(0x10)的头号嫌疑，非调试模式也要留痕
		LogHelper.Force("[认证] 官方 Mod 列表获取失败: " + text2 + "，使用空列表（服务器可能拒绝）");
		return "{\"mods\":[]}";
	}

	private byte[] EncryptWithRealPub(byte[] plain)
	{
		using RSA rSA = RSA.Create(1024);
		rSA.ImportSubjectPublicKeyInfo(_realPubDer, out var _);
		return rSA.Encrypt(plain, RSAEncryptionPadding.Pkcs1);
	}

	private static string ComputeSha1Hex(string serverId, byte[] secret, byte[] pubDer)
	{
		using SHA1 sHA = SHA1.Create();
		using MemoryStream memoryStream = new MemoryStream();
		byte[] bytes = Encoding.GetEncoding("ISO-8859-1").GetBytes(serverId);
		memoryStream.Write(bytes, 0, bytes.Length);
		memoryStream.Write(secret, 0, secret.Length);
		memoryStream.Write(pubDer, 0, pubDer.Length);
		memoryStream.Position = 0L;
		byte[] array = sHA.ComputeHash(memoryStream);
		Array.Reverse(array);
		BigInteger bigInteger = new BigInteger(array);
		return ((bigInteger < 0L) ? ("-" + (-bigInteger).ToString("x")) : bigInteger.ToString("x")).TrimStart('0');
	}
}

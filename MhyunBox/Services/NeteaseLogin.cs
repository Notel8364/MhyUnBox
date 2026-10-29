using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

public sealed class NeteaseLogin : IDisposable
{
	private sealed class MpayUser
	{
		[JsonPropertyName("id")]
		public string Id { get; set; } = "";

		[JsonPropertyName("token")]
		public string Token { get; set; } = "";

		[JsonPropertyName("nickname")]
		public string Nickname { get; set; } = "";

		[JsonPropertyName("display_username")]
		public string DisplayUsername { get; set; } = "";

		[JsonPropertyName("login_channel")]
		public string LoginChannel { get; set; } = "";
	}

	private sealed class MpayUserResponse
	{
		[JsonPropertyName("user")]
		public MpayUser? User { get; set; }
	}

	private sealed class VerifyInfo
	{
		[JsonPropertyName("code")]
		public int Code { get; set; }

		[JsonPropertyName("reason")]
		public string Reason { get; set; } = "";

		[JsonPropertyName("message")]
		public string Message { get; set; } = "";

		[JsonPropertyName("verify_url")]
		public string VerifyUrl { get; set; } = "";
	}

	private sealed class EntitySmsTicket
	{
		[JsonPropertyName("ticket")]
		public string Ticket { get; set; } = "";

		[JsonPropertyName("guide_text")]
		public string GuideText { get; set; } = "";

		[JsonPropertyName("related_emails")]
		public string[] RelatedEmails { get; set; } = Array.Empty<string>();

		[JsonPropertyName("related_accounts")]
		public string[] RelatedAccounts { get; set; } = Array.Empty<string>();
	}

	private sealed class EntityWrap<T>
	{
		[JsonPropertyName("code")]
		public int Code { get; set; }

		[JsonPropertyName("message")]
		public string Message { get; set; } = "";

		[JsonPropertyName("entity")]
		public T? Data { get; set; }
	}

	private sealed class LoginOtp
	{
		[JsonPropertyName("otp_token")]
		public string OtpToken { get; set; } = "";

		[JsonPropertyName("aid")]
		public int Aid { get; set; }

		[JsonPropertyName("lock_time")]
		public int LockTime { get; set; }
	}

	private sealed class AuthOtp
	{
		[JsonPropertyName("entity_id")]
		public string EntityId { get; set; } = "";

		[JsonPropertyName("account")]
		public string Account { get; set; } = "";

		[JsonPropertyName("token")]
		public string Token { get; set; } = "";
	}

	private sealed class EntityUsersParameters
	{
		[JsonPropertyName("password")]
		public string Password { get; set; } = "";

		[JsonPropertyName("unique_id")]
		public string Unique { get; set; } = "";

		[JsonPropertyName("username")]
		public string Username { get; set; } = "";
	}

	private sealed class EntityX19Cookie
	{
		[JsonPropertyName("gameid")]
		public string GameId { get; set; } = "x19";

		[JsonPropertyName("login_channel")]
		public string LoginChannel { get; set; } = "netease";

		[JsonPropertyName("app_channel")]
		public string AppChannel { get; set; } = "netease";

		[JsonPropertyName("platform")]
		public string Platform { get; set; } = "pc";

		[JsonPropertyName("sdkuid")]
		public string SdkUid { get; set; } = "";

		[JsonPropertyName("sessionid")]
		public string SessionId { get; set; } = "";

		[JsonPropertyName("sdk_version")]
		public string SdkVersion { get; set; } = "4.2.0";

		[JsonPropertyName("udid")]
		public string Udid { get; set; } = "";

		[JsonPropertyName("deviceid")]
		public string DeviceId { get; set; } = "";

		[JsonPropertyName("aim_info")]
		public string AimInfo { get; set; } = "{\"aim\":\"127.0.0.1\",\"country\":\"CN\",\"tz\":\"+0800\",\"tzid\":\"\"}";
	}

	private sealed class EntityAuthenticationVersion
	{
		[JsonPropertyName("version")]
		public string Version { get; set; } = "";

		[JsonPropertyName("launcher_md5")]
		public string LauncherMd5 { get; set; } = "";

		[JsonPropertyName("updater_md5")]
		public string UpdaterMd5 { get; set; } = "";
	}

	private sealed class EntityAuthenticationDetail
	{
		[JsonPropertyName("os_name")]
		public string OsName { get; set; } = "windows";

		[JsonPropertyName("os_ver")]
		public string OsVer { get; set; } = "Microsoft Windows 11 专业版";

		[JsonPropertyName("mac_addr")]
		public string MacAddr { get; set; } = RandomMac();

		[JsonPropertyName("udid")]
		public string Udid { get; set; } = "";

		[JsonPropertyName("app_ver")]
		public string AppVersion { get; set; } = "";

		[JsonPropertyName("sdk_ver")]
		public string SdkVersion { get; set; } = "";

		[JsonPropertyName("network")]
		public string Network { get; set; } = "";

		[JsonPropertyName("disk")]
		public string Disk { get; set; } = "";

		[JsonPropertyName("is64bit")]
		public string Is64Bit { get; set; } = "1";

		[JsonPropertyName("video_card1")]
		public string VideoCard1 { get; set; } = "Microsoft Hyper-V 视频";

		[JsonPropertyName("video_card2")]
		public string VideoCard2 { get; set; } = "Microsoft Remote Display Adapter";

		[JsonPropertyName("video_card3")]
		public string VideoCard3 { get; set; } = "";

		[JsonPropertyName("video_card4")]
		public string VideoCard4 { get; set; } = "";

		[JsonPropertyName("launcher_type")]
		public string LauncherType { get; set; } = "PC_java";

		[JsonPropertyName("pay_channel")]
		public string PayChannel { get; set; } = "";

		[JsonPropertyName("dotnet_ver")]
		public string DotnetVersion { get; set; } = "4.8.0";

		[JsonPropertyName("cpu_type")]
		public string CpuType { get; set; } = "Intel(R) Core(TM) i9-14900KF";

		[JsonPropertyName("ram_size")]
		public string RamSize { get; set; } = "8589934592";

		[JsonPropertyName("device_width")]
		public string DeviceWidth { get; set; } = "1920";

		[JsonPropertyName("device_height")]
		public string DeviceHeight { get; set; } = "1080";

		[JsonPropertyName("os_detail")]
		public string OsDetail { get; set; } = "10.0.22621";
	}

	private sealed class EntityAuthenticationData
	{
		[JsonPropertyName("sa_data")]
		public string SaData { get; set; } = "";

		[JsonPropertyName("sauth_json")]
		public string AuthJson { get; set; } = "";

		[JsonPropertyName("version")]
		public EntityAuthenticationVersion? Version { get; set; }

		[JsonPropertyName("sdkuid")]
		public string? SdkUid { get; set; }

		[JsonPropertyName("aid")]
		public string Aid { get; set; } = "";

		[JsonPropertyName("hasMessage")]
		public bool HasMessage { get; set; }

		[JsonPropertyName("hasGmail")]
		public bool HasGmail { get; set; }

		[JsonPropertyName("otp_token")]
		public string OtpToken { get; set; } = "";

		[JsonPropertyName("otp_pwd")]
		public string? OtpPwd { get; set; }

		[JsonPropertyName("lock_time")]
		public int LockTime { get; set; }

		[JsonPropertyName("env")]
		public string? Env { get; set; }

		[JsonPropertyName("min_engine_version")]
		public string? MinEngineVersion { get; set; }

		[JsonPropertyName("min_patch_version")]
		public string? MinPatchVersion { get; set; }

		[JsonPropertyName("verify_status")]
		public int VerifyStatus { get; set; }

		[JsonPropertyName("unisdk_login_json")]
		public string? UniSdkLoginJson { get; set; }

		[JsonPropertyName("token")]
		public string? Token { get; set; }

		[JsonPropertyName("is_register")]
		public bool IsRegister { get; set; } = true;

		[JsonPropertyName("entity_id")]
		public string? EntityId { get; set; }
	}

	private const string GameId = "aecfrxodyqaaaajp-g-x19";

	private const string FallbackVersion = "2.4.1";

	private const string ServiceBase = "https://service.mkey.163.com";

	private const string CoreBase = "https://x19obtcore.nie.netease.com:8443";

	private const string PatchListUrl = "https://x19.update.netease.com/pl/x19_java_patchlist";

	private static readonly string BuiltinGuid = SecretBox.DecodeString("OmNuU2xfXANWRkQQSE8Z5DpnY1NrAAEHV0JFSk0ZGrc=");

	private static readonly string BuiltinDeviceJson = SecretBox.DecodeString("I3c/BSkPBgNMSw1QFR5eu3o0NgEoE1EPDxJDHgQVSOMqeD9Cc0QOAxdTTFBKQk+5bmFsUGlXU1JdQEETSk1LsW40bFhpX1JQWEBARV4HAQ==");

	private static readonly JsonSerializerOptions Relaxed = new JsonSerializerOptions
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	private static readonly object _versionLock = new object();

	private static string? _cachedVersion;

	// 同一台机器上的 login-otp 串行化并保持最小间隔：
	// 多个账号同时进服/后台保活会在几百毫秒内连发多次登录请求，网易风控很容易判定为 login abnormally。
	private static readonly SemaphoreSlim SessionGate = new SemaphoreSlim(1, 1);

	private static DateTime _lastSessionRequestUtc = DateTime.MinValue;

	private const int MinSessionIntervalMs = 1200;

	// 关键：UseProxy=false 绕过系统代理/加速器。玩家常挂加速器（只为游戏端口），若跟随系统代理，
	// 网易账号 API 会被代理劫持导致登录失败（开发者无代理一直正常，用户挂加速器全挂）。
	private readonly HttpClient _http = new HttpClient(new HttpClientHandler
	{
		UseProxy = false
	})
	{
		Timeout = TimeSpan.FromSeconds(45.0)
	};

	private string? _unique;

	private string? _deviceId;

	private byte[]? _deviceKey;

	private string? _gameVersion;

	private string? _udid;

	private string? _macAddr;

	private string? _diskId;

	public void Dispose()
	{
		_http.Dispose();
		GC.SuppressFinalize(this);
	}

	public async Task<NeteaseLoginResult> LoginAsync(string email, string password, bool createSession = true)
	{
		if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
		{
			return new NeteaseLoginResult
			{
				Success = false,
				Message = "邮箱或密码不能为空"
			};
		}
		email = email.Trim();
		try
		{
			EnsureDevice();
			await EnsureVersionAsync();
			var (user, text, error) = await LoginEmailAsync(email, password);
			if (user == null)
			{
				if (!string.IsNullOrWhiteSpace(text))
				{
					return new NeteaseLoginResult
					{
						Success = false,
						Message = "该账号需要安全验证，请在浏览器中完成后再重试",
						VerifyUrl = text
					};
				}
				return new NeteaseLoginResult
				{
					Success = false,
					Message = FriendlyError(error)
				};
			}
			string cookie = GenerateCookie(user, _deviceId);
			if (!createSession)
			{
				return new NeteaseLoginResult
				{
					Success = true,
					Nickname = user.Nickname,
					Channel = "netease",
					CookieJson = cookie,
					Message = "邮箱账号校验通过（未建立游戏会话）"
				};
			}
			(AuthOtp, string) tuple2 = await LoginSessionAsync(cookie);
			AuthOtp item = tuple2.Item1;
			string item2 = tuple2.Item2;
			NeteaseLoginResult neteaseLoginResult = new NeteaseLoginResult
			{
				Success = true,
				Nickname = user.Nickname,
				Channel = "netease",
				CookieJson = cookie
			};
			if (item != null)
			{
				neteaseLoginResult.EntityId = item.EntityId;
				neteaseLoginResult.Token = item.Token;
				neteaseLoginResult.Message = "登录成功（网易账号校验通过）";
			}
			else
			{
				neteaseLoginResult.Message = "账号校验通过，但游戏会话获取失败：" + item2;
				neteaseLoginResult.RiskControl = IsRiskControlText(item2);
				neteaseLoginResult.SessionExpired = IsSessionExpiredText(item2);
				neteaseLoginResult.FailureStatus = item2 ?? "";
				neteaseLoginResult.FailureStage = "login-otp";
			}
			return neteaseLoginResult;
		}
		catch (Exception ex)
		{
			return new NeteaseLoginResult
			{
				Success = false,
				Message = "登录异常：" + FriendlyNetError(ex)
			};
		}
	}

	/// <summary>网络类异常时附带直连诊断提示（登录请求已绕过系统代理，直连网易失败多为本地网络/加速器干扰）。</summary>
	private static string FriendlyNetError(Exception ex)
	{
		string msg = ex.Message ?? "";
		bool isNet = ex is HttpRequestException
			|| ex is TimeoutException
			|| ex is System.Net.Sockets.SocketException
			|| msg.Contains("连接", StringComparison.Ordinal)
			|| msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)
			|| msg.Contains("response", StringComparison.OrdinalIgnoreCase);
		return isNet
			? msg + "（登录为直连网易，不经过加速器；若开启了加速器/VPN/系统代理，请关闭后重试）"
			: msg;
	}

	public string GetDeviceUnique()
	{
		EnsureDevice();
		return _unique;
	}

	public async Task<NeteaseLoginResult> LoginWithCookieAsync(string cookieJson, string channel, bool createSession = true)
	{
		if (string.IsNullOrWhiteSpace(cookieJson))
		{
			return new NeteaseLoginResult
			{
				Success = false,
				Message = "会话为空，请重新验证"
			};
		}
		try
		{
			EnsureDevice();
			await EnsureVersionAsync();
			if (!createSession)
			{
				bool flag = !string.Equals(channel, "netease", StringComparison.Ordinal);
				if (flag)
				{
					flag = !(await AuthSessionAsync(cookieJson));
				}
				if (flag)
				{
					return new NeteaseLoginResult
					{
						Success = false,
						Message = "会话校验失败，请重新验证"
					};
				}
				return new NeteaseLoginResult
				{
					Success = true,
					Message = "会话有效（未建立游戏会话）",
					Channel = channel,
					CookieJson = cookieJson
				};
			}
			var (authOtp, text) = await LoginSessionAsync(cookieJson);
			if (authOtp == null)
			{
				return new NeteaseLoginResult
				{
					Success = false,
					Message = "会话已失效，请重新验证：" + text,
					SessionExpired = true,
					RiskControl = IsRiskControlText(text),
					FailureStage = "login-otp",
					FailureStatus = text ?? ""
				};
			}
			if (!string.Equals(channel, "netease", StringComparison.Ordinal))
			{
				AuthSessionAsync(cookieJson);
			}
			return new NeteaseLoginResult
			{
				Success = true,
				Message = "已用保存的会话自动登录",
				EntityId = authOtp.EntityId,
				Token = authOtp.Token,
				Channel = channel
			};
		}
		catch (Exception ex)
		{
			return new NeteaseLoginResult
			{
				Success = false,
				Message = "自动登录异常：" + ex.Message
			};
		}
	}

	private async Task<bool> AuthSessionAsync(string cookieJson)
	{
		LogHelper.Log("[网易校验] uni_sauth 请求体: " + cookieJson.Length + "B");
		try
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://mgbsdk.matrix.netease.com/x19/sdk/uni_sauth");
			request.Headers.TryAddWithoutValidation("User-Agent", "WPFLauncher/0.0.0.0");
			request.Content = new StringContent(cookieJson, Encoding.UTF8, "application/json");
			using HttpResponseMessage response = await _http.SendAsync(request);
			LogHelper.Log("[网易校验] uni_sauth 状态: " + (int)response.StatusCode);
			string text = await response.Content.ReadAsStringAsync();
			LogHelper.Log("[网易校验] uni_sauth 响应: " + text.Length + "B, HTTP " + (int)response.StatusCode);
			if (!response.IsSuccessStatusCode)
			{
				return false;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(text);
			if (!jsonDocument.RootElement.TryGetProperty("code", out var value))
			{
				return false;
			}
			int value2;
			if (value.ValueKind == JsonValueKind.Number)
			{
				return value.TryGetInt32(out value2) && value2 == 200;
			}
			return value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "200", StringComparison.Ordinal);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			// uni_sauth 的失败绝大多数是网络波动导致的超时/取消（TaskCanceledException），
			// 恒写会把日志刷满。这里走门控版：只有开调试模式才记录详情。
			LogHelper.LogTransientError(ex2, "[网易校验] uni_sauth 异常");
			return false;
		}
	}

	public async Task<NeteaseLoginResult> LoginWithSmsCodeAsync(string phone, string smsCode, bool createSession = true)
	{
		if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(smsCode))
		{
			return new NeteaseLoginResult
			{
				Success = false,
				Message = "手机号或验证码不能为空"
			};
		}
		phone = phone.Trim();
		smsCode = smsCode.Trim();
		try
		{
			EnsureDevice();
			await EnsureVersionAsync();
			EntitySmsTicket entitySmsTicket = await VerifySmsCodeAsync(phone, smsCode);
			if (entitySmsTicket == null || string.IsNullOrWhiteSpace(entitySmsTicket.Ticket))
			{
				return new NeteaseLoginResult
				{
					Success = false,
					Message = "验证码校验失败，请检查后重试"
				};
			}
			MpayUser user = await FinishSmsCodeAsync(phone, entitySmsTicket.Ticket);
			if (user == null)
			{
				return new NeteaseLoginResult
				{
					Success = false,
					Message = "验证码登录失败，请重试"
				};
			}
			string cookie = GenerateCookie(user, _deviceId);
			if (!createSession)
			{
				return new NeteaseLoginResult
				{
					Success = true,
					Nickname = user.Nickname,
					Channel = "netease-mobile",
					CookieJson = cookie,
					Message = "手机号校验通过（未建立游戏会话）"
				};
			}
			(AuthOtp, string) tuple = await LoginSessionAsync(cookie);
			AuthOtp item = tuple.Item1;
			string item2 = tuple.Item2;
			NeteaseLoginResult neteaseLoginResult = new NeteaseLoginResult
			{
				Success = true,
				Nickname = user.Nickname,
				Channel = "netease-mobile",
				CookieJson = cookie
			};
			if (item != null)
			{
				neteaseLoginResult.EntityId = item.EntityId;
				neteaseLoginResult.Token = item.Token;
				neteaseLoginResult.Message = "手机号验证码登录成功";
			}
			else
			{
				neteaseLoginResult.Message = "账号校验通过，但游戏会话获取失败：" + item2;
				neteaseLoginResult.RiskControl = IsRiskControlText(item2);
				neteaseLoginResult.SessionExpired = IsSessionExpiredText(item2);
				neteaseLoginResult.FailureStatus = item2 ?? "";
				neteaseLoginResult.FailureStage = "login-otp";
			}
			return neteaseLoginResult;
		}
		catch (Exception ex)
		{
			return new NeteaseLoginResult
			{
				Success = false,
				Message = "手机号登录异常：" + ex.Message
			};
		}
	}

	public async Task<(bool Ok, string Message)> SendSmsCodeAsync(string phone)
	{
		if (string.IsNullOrWhiteSpace(phone))
		{
			return (Ok: false, Message: "手机号不能为空");
		}
		phone = phone.Trim();
		try
		{
			EnsureDevice();
			await EnsureVersionAsync();
			Dictionary<string, string> dictionary = BuildBaseParams();
			dictionary["device_id"] = _deviceId;
			dictionary["mobile"] = phone;
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://service.mkey.163.com/mpay/api/users/login/mobile/get_sms");
			request.Content = new StringContent(NetShared.FormUrlEncode(dictionary), Encoding.UTF8, "application/x-www-form-urlencoded");
			using HttpResponseMessage response = await _http.SendAsync(request);
			if (response.IsSuccessStatusCode)
			{
				return (Ok: true, Message: "验证码已发送，请查收短信");
			}
			return (Ok: false, Message: "验证码发送失败：" + FriendlyError(await response.Content.ReadAsStringAsync()));
		}
		catch (Exception ex)
		{
			return (Ok: false, Message: "发送验证码异常：" + ex.Message);
		}
	}

	private async Task<EntitySmsTicket?> VerifySmsCodeAsync(string phone, string smsCode)
	{
		Dictionary<string, string> dictionary = BuildBaseParams();
		dictionary["device_id"] = _deviceId;
		dictionary["mobile"] = phone;
		dictionary["smscode"] = smsCode;
		dictionary["up_content"] = "";
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://service.mkey.163.com/mpay/api/users/login/mobile/verify_sms");
		request.Content = new StringContent(NetShared.FormUrlEncode(dictionary), Encoding.UTF8, "application/x-www-form-urlencoded");
		using HttpResponseMessage response = await _http.SendAsync(request);
		string json = await response.Content.ReadAsStringAsync();
		if (!response.IsSuccessStatusCode)
		{
			return null;
		}
		return JsonSerializer.Deserialize<EntitySmsTicket>(json);
	}

	private async Task<MpayUser?> FinishSmsCodeAsync(string phone, string ticket)
	{
		string text = Convert.ToBase64String(Encoding.UTF8.GetBytes(phone));
		Dictionary<string, string> dictionary = BuildBaseParams();
		dictionary["device_id"] = _deviceId;
		dictionary["opt_fields"] = "nickname,avatar,realname_status,mobile_bind_status,mask_related_mobile,related_login_status";
		dictionary["ticket"] = ticket;
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://service.mkey.163.com/mpay/api/users/login/mobile/finish?un=" + text);
		request.Content = new StringContent(NetShared.FormUrlEncode(dictionary), Encoding.UTF8, "application/x-www-form-urlencoded");
		using HttpResponseMessage response = await _http.SendAsync(request);
		string json = await response.Content.ReadAsStringAsync();
		if (!response.IsSuccessStatusCode)
		{
			return null;
		}
		return JsonSerializer.Deserialize<MpayUserResponse>(json)?.User;
	}

	private static readonly object _deviceLock = new object();

	private void EnsureDevice()
	{
		if (_unique != null)
		{
			return;
		}
		lock (_deviceLock)
		{
			if (_unique != null)
			{
				return;
			}
			string? guid = SecureStore.Load("x19_guid");
			string? deviceJson = SecureStore.Load("x19_device");
			// 旧版把作者机器的 GUID 编译进程序，所有安装共用同一个 unique_id，
			// 多账号/多用户同时登录时很容易被网易判成 login abnormally。这里按安装生成唯一值。
			bool sharedBuiltinGuid = !string.IsNullOrWhiteSpace(guid) && string.Equals(guid!.Trim(), BuiltinGuid, StringComparison.OrdinalIgnoreCase);
			if (string.IsNullOrWhiteSpace(guid) || sharedBuiltinGuid)
			{
				guid = Guid.NewGuid().ToString("N");
				SecureStore.Save("x19_guid", guid);
				LogHelper.Log("[网易登录] 已生成本机唯一设备号（" + (sharedBuiltinGuid ? "替换内置共享值" : "首次运行") + "），长度=" + guid.Length);
			}
			if (string.IsNullOrWhiteSpace(deviceJson))
			{
				deviceJson = BuiltinDeviceJson;
				SecureStore.Save("x19_device", deviceJson);
				LogHelper.Log("[网易登录] 未找到设备密钥，使用内置兼容设备（该标识无法按安装隔离，仅用于密码加密）");
			}
			_unique = guid!.Trim();
			using JsonDocument jsonDocument = JsonDocument.Parse(deviceJson);
			JsonElement property = jsonDocument.RootElement.GetProperty("device");
			_deviceId = property.GetProperty("id").GetString();
			string text3 = property.GetProperty("key").GetString();
			_deviceKey = NeteaseCipher.HexToBytes(text3 ?? "") ?? throw new InvalidOperationException("device.key 解析失败");
			EnsureFingerprint();
		}
	}

	/// <summary>
	/// authentication-otp 里的 udid / mac_addr / disk 必须是稳定值。
	/// 旧实现每次登录都随机生成，连续保活时同一账号的设备指纹几分钟变一次，是触发 login abnormally 的高危因素。
	/// </summary>
	private void EnsureFingerprint()
	{
		if (!string.IsNullOrWhiteSpace(_udid) && !string.IsNullOrWhiteSpace(_macAddr) && !string.IsNullOrWhiteSpace(_diskId))
		{
			return;
		}
		lock (_deviceLock)
		{
			if (!string.IsNullOrWhiteSpace(_udid) && !string.IsNullOrWhiteSpace(_macAddr) && !string.IsNullOrWhiteSpace(_diskId))
			{
				return;
			}
			Fingerprint? fingerprint = null;
			string? text = SecureStore.Load("x19_fp");
			if (!string.IsNullOrWhiteSpace(text))
			{
				try
				{
					fingerprint = JsonSerializer.Deserialize<Fingerprint>(text);
				}
				catch
				{
				}
			}
			if (fingerprint == null || string.IsNullOrWhiteSpace(fingerprint.Udid) || string.IsNullOrWhiteSpace(fingerprint.Mac) || string.IsNullOrWhiteSpace(fingerprint.Disk))
			{
				fingerprint = new Fingerprint
				{
					Udid = "0000000000000000" + NeteaseCipher.RandomHex(8),
					Mac = RandomMac(),
					Disk = NeteaseCipher.RandomHex(8)
				};
				SecureStore.Save("x19_fp", JsonSerializer.Serialize(fingerprint, Relaxed));
				LogHelper.Log("[网易登录] 已生成本机稳定设备指纹（udid/mac/disk），后续登录不再每次变化");
			}
			_udid = fingerprint.Udid;
			_macAddr = fingerprint.Mac;
			_diskId = fingerprint.Disk;
		}
	}

	private sealed class Fingerprint
	{
		[JsonPropertyName("udid")]
		public string Udid { get; set; } = "";

		[JsonPropertyName("mac")]
		public string Mac { get; set; } = "";

		[JsonPropertyName("disk")]
		public string Disk { get; set; } = "";
	}

	private async Task EnsureVersionAsync()
	{
		if (_gameVersion != null)
		{
			return;
		}
		lock (_versionLock)
		{
			_gameVersion = _cachedVersion;
		}
		if (_gameVersion == null)
		{
			string text = await FetchLatestVersionAsync();
			lock (_versionLock)
			{
				_cachedVersion = text;
				_gameVersion = text;
			}
		}
	}

	private async Task<string> FetchLatestVersionAsync()
	{
		try
		{
			string text = await _http.GetStringAsync("https://x19.update.netease.com/pl/x19_java_patchlist");
			int num = text.LastIndexOf(',');
			if (num <= 0)
			{
				return "2.4.1";
			}
			using JsonDocument jsonDocument = JsonDocument.Parse("{" + text.Substring(0, num) + "}");
			return (jsonDocument.RootElement.EnumerateObject().LastOrDefault().Value.ValueKind == JsonValueKind.Undefined) ? "2.4.1" : jsonDocument.RootElement.EnumerateObject().Last().Name;
		}
		catch
		{
			return "2.4.1";
		}
	}

	private async Task<(MpayUser? User, string? VerifyUrl, string? Error)> LoginEmailAsync(string email, string password)
	{
		string value = Convert.ToHexString(NeteaseCipher.MpayEncrypt(JsonSerializer.Serialize(new EntityUsersParameters
		{
			Password = NeteaseCipher.Md5HexLower(password),
			Unique = _unique,
			Username = email
		}, Relaxed), _deviceKey)).ToLowerInvariant();
		Dictionary<string, string> dictionary = BuildBaseParams();
		dictionary["opt_fields"] = "nickname,avatar,realname_status,mobile_bind_status,mask_related_mobile,related_login_status";
		dictionary["params"] = value;
		dictionary["un"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(email));
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{"https://service.mkey.163.com"}/mpay/games/{"aecfrxodyqaaaajp-g-x19"}/devices/{_deviceId}/users");
		request.Content = new StringContent(NetShared.FormUrlEncode(dictionary), Encoding.UTF8, "application/x-www-form-urlencoded");
		using HttpResponseMessage response = await _http.SendAsync(request);
		string text = await response.Content.ReadAsStringAsync();
		if (!response.IsSuccessStatusCode)
		{
			try
			{
				VerifyInfo verifyInfo = JsonSerializer.Deserialize<VerifyInfo>(text);
				if (verifyInfo != null && verifyInfo.Code == 1351)
				{
					return (User: null, VerifyUrl: verifyInfo.VerifyUrl, Error: verifyInfo.Reason);
				}
				if (verifyInfo != null && !string.IsNullOrWhiteSpace(verifyInfo.Message))
				{
					return (User: null, VerifyUrl: null, Error: verifyInfo.Message);
				}
			}
			catch
			{
			}
			return (User: null, VerifyUrl: null, Error: text);
		}
		return (User: JsonSerializer.Deserialize<MpayUserResponse>(text)?.User, VerifyUrl: null, Error: null);
	}

	private string GenerateCookie(MpayUser user, string deviceId)
	{
		EnsureFingerprint();
		return JsonSerializer.Serialize(new EntityX19Cookie
		{
			SdkUid = user.Id,
			SessionId = user.Token,
			Udid = _udid ?? "",
			DeviceId = deviceId
		}, Relaxed);
	}

	private async Task<(AuthOtp? Otp, string? Error)> LoginSessionAsync(string cookieJson)
	{
		bool entered = false;
		try
		{
			entered = await SessionGate.WaitAsync(TimeSpan.FromSeconds(60.0)).ConfigureAwait(continueOnCapturedContext: false);
			if (!entered)
			{
				return (Otp: null, Error: "login-otp 排队超时（本机登录请求过于频繁）");
			}
			double elapsedMs = (DateTime.UtcNow - _lastSessionRequestUtc).TotalMilliseconds;
			if (elapsedMs < (double)MinSessionIntervalMs)
			{
				int waitMs = Math.Max(0, MinSessionIntervalMs - (int)elapsedMs);
				if (waitMs > 0)
				{
					await Task.Delay(waitMs).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			return await LoginSessionCoreAsync(cookieJson).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			LogHelper.Log("[网易登录] login-otp 流程异常: " + ex.Message);
			return (Otp: null, Error: "login-otp 流程异常：" + ex.Message);
		}
		finally
		{
			if (entered)
			{
				_lastSessionRequestUtc = DateTime.UtcNow;
				SessionGate.Release();
			}
		}
	}

	private async Task<(AuthOtp? Otp, string? Error)> LoginSessionCoreAsync(string cookieJson)
	{
		LogCookieShape(cookieJson);
		string content = JsonSerializer.Serialize(new
		{
			sauth_json = cookieJson
		}, Relaxed);
		using HttpRequestMessage req1 = new HttpRequestMessage(HttpMethod.Post, "https://x19obtcore.nie.netease.com:8443/login-otp");
		req1.Headers.TryAddWithoutValidation("User-Agent", "WPFLauncher/0.0.0.0");
		req1.Content = new StringContent(content, Encoding.UTF8, "application/json");
		using HttpResponseMessage resp1 = await _http.SendAsync(req1);
		string text = await resp1.Content.ReadAsStringAsync();
		EntityWrap<JsonElement?>? entityWrap = null;
		try
		{
			entityWrap = JsonSerializer.Deserialize<EntityWrap<JsonElement?>>(text);
		}
		catch (Exception ex2)
		{
			LogHelper.Log($"[网易登录] login-otp 响应解析失败: HTTP {(int)resp1.StatusCode}, {text.Length}B, {ex2.Message}");
		}
		string status = ExtractEntityStatus(entityWrap);
		int code = entityWrap?.Code ?? -1;
		LogHelper.Log($"[网易登录] login-otp: HTTP {(int)resp1.StatusCode}, 响应 {text.Length}B, code={code}, status={Safe(status)}, message={Safe(entityWrap?.Message)}");
		if (entityWrap == null || entityWrap.Code != 0 || !entityWrap.Data.HasValue || entityWrap.Data.Value.ValueKind != JsonValueKind.Object)
		{
			return (Otp: null, Error: BuildSessionError("login-otp", status, entityWrap?.Message, code));
		}
		LoginOtp? loginOtp = null;
		try
		{
			loginOtp = JsonSerializer.Deserialize<LoginOtp>(entityWrap.Data.Value.GetRawText());
		}
		catch
		{
		}
		if (loginOtp == null || string.IsNullOrWhiteSpace(loginOtp.OtpToken))
		{
			return (Otp: null, Error: BuildSessionError("login-otp", status, "otp_token 为空", code));
		}
		LogHelper.Log($"[网易登录] login-otp 成功: aid={loginOtp.Aid}, otp_token长度={loginOtp.OtpToken.Length}");
		EnsureFingerprint();
		EntityAuthenticationDetail value = new EntityAuthenticationDetail
		{
			Udid = _udid ?? "",
			MacAddr = _macAddr ?? RandomMac(),
			AppVersion = _gameVersion,
			PayChannel = "netease",
			Disk = _diskId ?? ""
		};
		EntityAuthenticationData value2 = new EntityAuthenticationData
		{
			SaData = JsonSerializer.Serialize(value, Relaxed),
			AuthJson = cookieJson,
			Version = new EntityAuthenticationVersion
			{
				Version = _gameVersion
			},
			Aid = loginOtp.Aid.ToString(),
			OtpToken = loginOtp.OtpToken,
			LockTime = 0
		};
		byte[] content2 = NeteaseCipher.HttpEncrypt(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value2, Relaxed)));
		using HttpRequestMessage req2 = new HttpRequestMessage(HttpMethod.Post, "https://x19obtcore.nie.netease.com:8443/authentication-otp");
		req2.Content = new ByteArrayContent(content2);
		using HttpResponseMessage resp2 = await _http.SendAsync(req2);
		byte[] raw2 = await resp2.Content.ReadAsByteArrayAsync();
		byte[]? array = NeteaseCipher.HttpDecrypt(raw2);
		if (array == null)
		{
			LogHelper.Log($"[网易登录] authentication-otp: HTTP {(int)resp2.StatusCode}, 响应 {raw2.Length}B, 解密失败");
			return (Otp: null, Error: "authentication-otp 响应无法解密（HTTP " + (int)resp2.StatusCode + "）");
		}
		EntityWrap<AuthOtp>? entityWrap2 = null;
		try
		{
			entityWrap2 = JsonSerializer.Deserialize<EntityWrap<AuthOtp>>(Encoding.UTF8.GetString(array));
		}
		catch
		{
		}
		int code2 = entityWrap2?.Code ?? -1;
		LogHelper.Log($"[网易登录] authentication-otp: HTTP {(int)resp2.StatusCode}, 解密后 {array.Length}B, code={code2}, message={Safe(entityWrap2?.Message)}, 有会话字段={entityWrap2?.Data != null}");
		if (entityWrap2 == null || entityWrap2.Code != 0 || entityWrap2.Data == null)
		{
			return (Otp: null, Error: BuildSessionError("authentication-otp", "", entityWrap2?.Message, code2));
		}
		if (string.IsNullOrWhiteSpace(entityWrap2.Data.EntityId) || string.IsNullOrWhiteSpace(entityWrap2.Data.Token))
		{
			return (Otp: null, Error: "authentication-otp 返回空会话字段");
		}
		return (Otp: entityWrap2.Data, Error: null);
	}

	private static string ExtractEntityStatus(EntityWrap<JsonElement?>? wrap)
	{
		if (wrap != null && wrap.Data.HasValue && wrap.Data.Value.ValueKind == JsonValueKind.Object)
		{
			try
			{
				if (wrap.Data.Value.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String)
				{
					string text = value.GetString() ?? "";
					if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, "success", StringComparison.OrdinalIgnoreCase))
					{
						return text;
					}
				}
			}
			catch
			{
			}
		}
		if (!string.IsNullOrWhiteSpace(wrap?.Message))
		{
			return wrap.Message;
		}
		if (wrap != null && wrap.Code != 0)
		{
			return "code=" + wrap.Code;
		}
		return "空响应";
	}

	private static string BuildSessionError(string stage, string? status, string? message, int code)
	{
		string detail = (!string.IsNullOrWhiteSpace(status) ? status : message);
		if (string.IsNullOrWhiteSpace(detail))
		{
			detail = ((code != 0) ? ("code=" + code) : "空响应");
		}
		if (IsRiskControlText(detail))
		{
			return stage + " 登录环境异常（" + detail + "）";
		}
		if (IsSessionExpiredText(detail))
		{
			return stage + " 会话失效（" + detail + "）";
		}
		return stage + " 失败（" + detail + ((code != 0) ? (", code=" + code) : "") + "）";
	}

	private static string Safe(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "<空>";
		}
		string text2 = text.Replace('\r', ' ').Replace('\n', ' ');
		return (text2.Length > 160) ? (text2.Substring(0, 160) + "...") : text2;
	}

	private static void LogCookieShape(string cookieJson)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(cookieJson);
			JsonElement root = jsonDocument.RootElement;
			string Get(string name)
			{
				if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
				{
					return value.GetString() ?? "";
				}
				return "";
			}
			LogHelper.Log("[网易登录] 会话材料: gameid=" + Safe(Get("gameid")) + " login_channel=" + Safe(Get("login_channel")) + " sdk_ver=" + Safe(Get("sdk_version")) + " sdkuid=" + SecretRedaction.Fingerprint(Get("sdkuid")) + " sessionid=" + SecretRedaction.Fingerprint(Get("sessionid")) + " udid=" + SecretRedaction.Fingerprint(Get("udid")) + " deviceid=" + SecretRedaction.Fingerprint(Get("deviceid")));
		}
		catch (Exception ex)
		{
			LogHelper.Log("[网易登录] 会话材料解析失败: " + ex.Message);
		}
	}

	/// <summary>网易风控/登录环境异常（login abnormally 等）：这种失败重试越快越糟，应进入冷静期。</summary>
	public static bool IsRiskControlText(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string text2 = text.ToLowerInvariant();
		return text2.Contains("login abnormally")
			|| text2.Contains("login abnormal")
			|| text2.Contains("abnormal login")
			|| text2.Contains("risk control")
			|| text.Contains("登录环境异常")
			|| text.Contains("环境异常")
			|| text.Contains("登录行为异常")
			|| text.Contains("账号异常")
			|| text.Contains("风控")
			|| text.Contains("操作频繁")
			|| text.Contains("登录频繁")
			|| text.Contains("too frequent");
	}

	/// <summary>确定性会话失效（auth fail / token expired 等）：可以立即换一张新会话。</summary>
	public static bool IsSessionExpiredText(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string text2 = text.ToLowerInvariant();
		return text2.Contains("auth fail")
			|| text2.Contains("token expired")
			|| text.Contains("token 过期")
			|| text.Contains("会话已失效")
			|| text.Contains("会话为空")
			|| text.Contains("会话校验失败")
			|| text.Contains("会话超时")
			|| text.Contains("未登录")
			|| text.Contains("登录已失效");
	}

	private static string FriendlyError(string? error)
	{
		if (string.IsNullOrWhiteSpace(error))
		{
			return "登录失败";
		}
		string text = error.ToLowerInvariant();
		if (text.Contains("password") || text.Contains("密码") || text.Contains("账号或密码") || text.Contains("邮箱或密码"))
		{
			return "邮箱或密码错误";
		}
		if (error.Length > 300)
		{
			return error.Substring(0, 300) + "…";
		}
		return error;
	}

	private Dictionary<string, string> BuildBaseParams()
	{
		return new Dictionary<string, string>
		{
			["app_channel"] = "netease",
			["app_mode"] = "2",
			["app_type"] = "games",
			["arch"] = "win_x64",
			["cv"] = "c4.2.0",
			["mcount_app_key"] = "EEkEEXLymcNjM42yLY3Bn6AO15aGy4yq",
			["mcount_transaction_id"] = "0",
			["process_id"] = Environment.ProcessId.ToString(),
			["sv"] = "10.0.22621",
			["updater_cv"] = "c1.0.0",
			["game_id"] = "aecfrxodyqaaaajp-g-x19",
			["gv"] = _gameVersion ?? "2.4.1"
		};
	}

	private static string RandomMac()
	{
		byte[] array = new byte[6];
		Random.Shared.NextBytes(array);
		array[0] = (byte)((array[0] & 0xFE) | 2);
		StringBuilder stringBuilder = new StringBuilder();
		byte[] array2 = array;
		byte[] array3 = array2;
		foreach (byte b in array3)
		{
			stringBuilder.Append(b.ToString("X2"));
		}
		return stringBuilder.ToString();
	}
}

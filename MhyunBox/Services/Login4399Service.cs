using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MhyunBox.Services;

public sealed class Login4399Service : IDisposable
{
	private sealed class Entity4399Response
	{
		[JsonPropertyName("code")]
		public int Code { get; set; }

		[JsonPropertyName("msg")]
		public string Msg { get; set; } = "";

		[JsonPropertyName("data")]
		public Entity4399ResponseData? Data { get; set; }
	}

	private sealed class Entity4399ResponseData
	{
		[JsonPropertyName("sdk_login_data")]
		public string SdkLoginData { get; set; } = "";
	}

	private sealed class EntityPhoneUsers
	{
		[JsonPropertyName("code")]
		public int Code { get; set; }

		[JsonPropertyName("message")]
		public string Message { get; set; } = "";

		[JsonPropertyName("result")]
		public EntityPhoneUser[]? Result { get; set; }
	}

	private sealed class EntityPhoneUser
	{
		[JsonPropertyName("username")]
		public string Username { get; set; } = "";

		[JsonPropertyName("nick")]
		public string Nick { get; set; } = "";

		[JsonPropertyName("uid")]
		public string Uid { get; set; } = "";
	}

	private sealed class EntityMgbSdkCookie
	{
		[JsonPropertyName("timestamp")]
		public string Timestamp { get; set; } = "";

		[JsonPropertyName("userid")]
		public string UserId { get; set; } = "";

		[JsonPropertyName("realname")]
		public string RealName { get; set; } = "{\"realname_type\":\"0\"}";

		[JsonPropertyName("gameid")]
		public string GameId { get; set; } = "";

		[JsonPropertyName("login_channel")]
		public string LoginChannel { get; set; } = "";

		[JsonPropertyName("app_channel")]
		public string AppChannel { get; set; } = "";

		[JsonPropertyName("platform")]
		public string Platform { get; set; } = "pc";

		[JsonPropertyName("sdkuid")]
		public string SdkUid { get; set; } = "";

		[JsonPropertyName("sessionid")]
		public string SessionId { get; set; } = "";

		[JsonPropertyName("sdk_version")]
		public string SdkVersion { get; set; } = "1.0.0";

		[JsonPropertyName("udid")]
		public string Udid { get; set; } = "";

		[JsonPropertyName("deviceid")]
		public string DeviceId { get; set; } = "";

		[JsonPropertyName("aim_info")]
		public string AimInfo { get; set; } = "{\"aim\":\"127.0.0.1\",\"tz\":\"+0800\",\"tzid\":\"\",\"country\":\"CN\"}";

		[JsonPropertyName("client_login_sn")]
		public string ClientLoginSn { get; set; } = "";

		[JsonPropertyName("gas_token")]
		public string GasToken { get; set; } = "";

		[JsonPropertyName("source_platform")]
		public string SourcePlatform { get; set; } = "pc";

		[JsonPropertyName("ip")]
		public string Ip { get; set; } = "127.0.0.1";
	}

	private sealed class ParameterBuilder
	{
		private readonly Dictionary<string, string> _parameters = new Dictionary<string, string>();

		public ParameterBuilder()
		{
		}

		public ParameterBuilder(string query)
		{
			if (string.IsNullOrWhiteSpace(query))
			{
				return;
			}
			string[] array = query.Split('&');
			for (int i = 0; i < array.Length; i++)
			{
				string[] array2 = array[i].Split('=');
				if (array2.Length == 2)
				{
					_parameters[array2[0]] = Uri.UnescapeDataString(array2[1]);
				}
			}
		}

		public string Get(string key)
		{
			if (!_parameters.TryGetValue(key, out var value))
			{
				return "";
			}
			return value;
		}

		public IEnumerable<string> Names()
		{
			return _parameters.Keys;
		}

		public ParameterBuilder Append(string key, string value)
		{
			_parameters[key] = value;
			return this;
		}

		public string FormUrlEncode()
		{
			List<string> list = new List<string>(_parameters.Count);
			foreach (KeyValuePair<string, string> parameter in _parameters)
			{
				list.Add(Uri.EscapeDataString(parameter.Key) + "=" + Uri.EscapeDataString(parameter.Value));
			}
			return string.Join("&", list);
		}
	}

	private const string AppId = "kid_wdsj";

	private const string GameUrl = "https://cdn.h5wan.4399sj.com/microterminal-h5-frame?game_id=500352";

	private const string GameId = "x19";

	private const string PtLoginCryptoKey = "lzYW5qaXVqa";

	private const string PtLoginBase = "https://ptlogin.4399.com";

	private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

	private static readonly JsonSerializerOptions Relaxed = new JsonSerializerOptions
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	private readonly CookieContainer _cookies = new CookieContainer();

	private readonly HttpClient _http;

	public Login4399Service()
	{
		_http = new HttpClient(new HttpClientHandler
		{
			CookieContainer = _cookies,
			UseCookies = true,
			AllowAutoRedirect = true,
			UseProxy = false
		});
		_http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
		_http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
	}

	public void Dispose()
	{
		_http.Dispose();
		GC.SuppressFinalize(this);
	}

	public async Task<Login4399Result> LoginAsync(string username, string password)
	{
		if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
		{
			return new Login4399Result
			{
				Success = false,
				Message = "4399 账号或密码不能为空"
			};
		}
		username = username.Trim();
		try
		{
			for (int attempt = 1; attempt <= 2; attempt++)
			{
				if (attempt > 1)
				{
					LogHelper.Log("[4399登录] 命中风控，等待 30 秒后自动重试（第 2 次尝试）…");
					await Task.Delay(TimeSpan.FromSeconds(30.0));
				}
				Dictionary<string, string> form = BuildLoginParams();
				form["username"] = username;
				form["password"] = "";
				string text;
				using (HttpResponseMessage frame = await _http.SendAsync(BuildPtLoginRequest(HttpMethod.Post, "https://ptlogin.4399.com/ptlogin/loginFrame.do?v=1", "https://ptlogin.4399.com/", xhr: false, form)))
				{
					text = await frame.Content.ReadAsStringAsync();
				}
				if (text.Contains("账号异常，请输入验证码"))
				{
					return new Login4399Result
					{
						Success = false,
						Message = "该 4399 账号需要验证码（暂不支持自动识别，请先用官方启动器登录一次）"
					};
				}
				await Task.Delay(Random.Shared.Next(900, 1500));
				string text2;
				using (HttpResponseMessage frame2 = await _http.SendAsync(BuildPtLoginRequest(HttpMethod.Get, "https://ptlogin.4399.com/ptlogin/verify.do?username=" + Uri.EscapeDataString(username) + "&appId=kid_wdsj&t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "&inputWidth=iptw2&v=1", "https://ptlogin.4399.com/ptlogin/loginFrame.do?v=1", xhr: true)))
				{
					text2 = await frame2.Content.ReadAsStringAsync();
				}
				LogHelper.Log("[4399登录] verify.do 响应: " + RedactBody(text2));
				if (text2.Trim() != "0")
				{
					return new Login4399Result
					{
						Success = false,
						Message = "该 4399 账号需要验证码（暂不支持自动识别，请先用官方启动器登录一次）"
					};
				}
				await Task.Delay(Random.Shared.Next(800, 1200));
				form["password"] = Encrypt4399(password);
				LogHelper.Log("[4399登录] 密码已按 sec=1 加密提交");
				HttpStatusCode status = HttpStatusCode.OK;
				string text3;
				using (HttpResponseMessage frame3 = await _http.SendAsync(BuildPtLoginRequest(HttpMethod.Post, "https://ptlogin.4399.com/ptlogin/login.do?v=1", "https://ptlogin.4399.com/ptlogin/loginFrame.do?v=1", xhr: false, form)))
				{
					if (!frame3.IsSuccessStatusCode)
					{
						return new Login4399Result
						{
							Success = false,
							Message = "4399 登录失败（HTTP " + (int)frame3.StatusCode + "）"
						};
					}
					LogHelper.Log("[4399登录] login.do 状态: " + (int)frame3.StatusCode + " " + frame3.ReasonPhrase);
					foreach (KeyValuePair<string, IEnumerable<string>> header in frame3.Headers)
					{
						LogHelper.Log("[4399登录] login.do 响应头 " + header.Key + ": " + RedactHeaderValue(header.Key, header.Value));
					}
					foreach (KeyValuePair<string, IEnumerable<string>> header2 in frame3.Content.Headers)
					{
						LogHelper.Log("[4399登录] login.do 内容头 " + header2.Key + ": " + RedactHeaderValue(header2.Key, header2.Value));
					}
					text3 = await frame3.Content.ReadAsStringAsync();
					status = frame3.StatusCode;
				}
				LogHelper.Log("[4399登录] login.do 响应体(换行转义): " + RedactBody(text3));
				string text4 = RiskControlMessage(status, text3);
				if (!string.IsNullOrWhiteSpace(text4))
				{
					LogHelper.Log("[4399登录] 命中风控: " + text4);
					if (attempt != 1)
					{
						return new Login4399Result
						{
							Success = false,
							Message = text4
						};
					}
					continue;
				}
				string text5 = ExtractFormError(text3);
				if (!string.IsNullOrWhiteSpace(text5))
				{
					LogHelper.Log("[4399登录] 表单页提示: " + text5);
					if (text5.Contains("验证码") || text5.Contains("滑块") || text5.Contains("图形"))
					{
						return new Login4399Result
						{
							Success = false,
							Message = "该 4399 账号需要验证码（暂不支持自动识别，请先用官方启动器登录一次）"
						};
					}
					if (text5.Contains("不存在") || text5.Contains("密码") || text5.Contains("错误"))
					{
						return new Login4399Result
						{
							Success = false,
							Message = "4399 账号或密码错误（" + text5 + "）"
						};
					}
					return new Login4399Result
					{
						Success = false,
						Message = "4399 登录未成功：" + text5
					};
				}
				LogHelper.Log("[4399登录] 表单页无错误提示，继续完成会话（页面长度 " + text3.Length + "）");
				LogHelper.Log("[4399登录] login.do 响应体: " + RedactBody(text3));
				try
				{
					string text6 = "";
					foreach (Cookie cookie3 in _cookies.GetCookies(new Uri("https://ptlogin.4399.com/")))
					{
						text6 = text6 + cookie3.Name + " | ";
					}
					LogHelper.Log("[4399登录] login.do 后 Cookie 名称: " + (string.IsNullOrWhiteSpace(text6) ? "(空)" : text6));
					CookieCollection cookies = _cookies.GetCookies(new Uri("https://ptlogin.4399.com/"));
					bool flag = false;
					foreach (Cookie item in cookies)
					{
						if (item.Name == "Pauth" || item.Name == "Uauth" || item.Name == "Xauth")
						{
							flag = true;
							break;
						}
					}
					LogHelper.Log("[4399登录] 认证Cookie(Pauth/Uauth/Xauth)是否已设置: " + flag);
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "[4399登录] 读取 Cookie 容器失败");
				}
				return await CompleteLoginAsync("4399 账号或密码错误");
			}
			return new Login4399Result
			{
				Success = false,
				Message = "4399 登录异常，请稍后重试"
			};
		}
		catch (Exception ex2)
		{
			string text7 = ex2.Message.ToLowerInvariant();
			string message = ((!text7.Contains("sessionid")) ? (text7.Contains("unactived") ? "账号未激活，请先使用官方启动器进入游戏一次" : ("4399 登录异常：" + ex2.Message)) : "4399 账号或密码错误");
			return new Login4399Result
			{
				Success = false,
				Message = message
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
			string url = "https://ptlogin.4399.com/ptlogin/sendPhoneLoginCode.do?phone=" + Uri.EscapeDataString(phone) + "&appId=kid_wdsj&v=1&sig=&t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			string text;
			using (HttpResponseMessage resp = await _http.SendAsync(BuildPtLoginRequest(HttpMethod.Get, url, "https://ptlogin.4399.com/", xhr: true)))
			{
				text = await resp.Content.ReadAsStringAsync();
			}
			string text2 = text.Trim();
			string[] array = text2.Split(',');
			if (array.Length != 0 && (array[0] == "1" || array[0] == "3" || array[0] == "4"))
			{
				return (Ok: true, Message: "验证码已发送，请查收短信（60 秒后可重发）");
			}
			if (array.Length != 0 && array[0] == "2")
			{
				return (Ok: false, Message: "发送验证码需要图形验证码（暂不支持自动识别，请稍后再试或先用官方登录页获取一次）");
			}
			return (Ok: false, Message: "验证码发送失败：" + Shorten(text2));
		}
		catch (Exception ex)
		{
			return (Ok: false, Message: "发送验证码异常：" + ex.Message);
		}
	}

	public async Task<Login4399Result> LoginWithSmsCodeAsync(string phone, string smsCode)
	{
		if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(smsCode))
		{
			return new Login4399Result
			{
				Success = false,
				Message = "手机号或验证码不能为空"
			};
		}
		phone = phone.Trim();
		smsCode = smsCode.Trim();
		try
		{
			string text;
			using (HttpResponseMessage resp = await _http.SendAsync(BuildPtLoginRequest(HttpMethod.Get, "https://ptlogin.4399.com/ptlogin/getPhoneLoginUsers.do?phone=" + Uri.EscapeDataString(phone) + "&phoneCaptcha=" + Uri.EscapeDataString(smsCode), "https://ptlogin.4399.com/", xhr: true)))
			{
				text = await resp.Content.ReadAsStringAsync();
			}
			EntityPhoneUsers entityPhoneUsers = TryParseUsers(text);
			if (entityPhoneUsers == null || entityPhoneUsers.Code != 300)
			{
				string text2 = ((entityPhoneUsers != null && !string.IsNullOrWhiteSpace(entityPhoneUsers.Message)) ? entityPhoneUsers.Message : Shorten(text));
				return new Login4399Result
				{
					Success = false,
					Message = (string.IsNullOrWhiteSpace(text2) ? "短信验证码校验失败，请检查后重试" : text2)
				};
			}
			Dictionary<string, string> dictionary = BuildPhoneLoginParams();
			dictionary["phone"] = phone;
			dictionary["phoneCaptcha"] = smsCode;
			if (entityPhoneUsers.Result != null && entityPhoneUsers.Result.Length != 0 && !string.IsNullOrWhiteSpace(entityPhoneUsers.Result[0].Username))
			{
				dictionary["username"] = entityPhoneUsers.Result[0].Username;
			}
			using (HttpResponseMessage resp2 = await _http.SendAsync(BuildPtLoginRequest(HttpMethod.Post, "https://ptlogin.4399.com/ptlogin/phoneLogin.do?v=1", "https://ptlogin.4399.com/", xhr: false, dictionary)))
			{
				if (!resp2.IsSuccessStatusCode)
				{
					return new Login4399Result
					{
						Success = false,
						Message = "4399 登录失败（HTTP " + (int)resp2.StatusCode + "）"
					};
				}
				string text3 = await resp2.Content.ReadAsStringAsync();
				string text4 = RiskControlMessage(resp2.StatusCode, text3);
				if (!string.IsNullOrWhiteSpace(text4))
				{
					LogHelper.Log("[4399登录] 手机登录命中风控: " + text4);
					return new Login4399Result
					{
						Success = false,
						Message = text4
					};
				}
				string text5 = text3.Trim();
				if (text5.Length > 0 && !text5.StartsWith("<") && (text5.Contains("验证码错误") || text5.Contains("验证码已失效") || text5.Contains("已过期") || text5.StartsWith("{") || text5.StartsWith("错误") || text5.StartsWith("验证码")))
				{
					return new Login4399Result
					{
						Success = false,
						Message = Shorten(text5)
					};
				}
			}
			return await CompleteLoginAsync("4399 短信登录失败");
		}
		catch (Exception ex)
		{
			string text6 = ex.Message.ToLowerInvariant();
			string message = ((!text6.Contains("sessionid")) ? (text6.Contains("unactived") ? "账号未激活，请先使用官方启动器进入游戏一次" : ("4399 短信登录异常：" + ex.Message)) : "4399 短信登录失败：会话异常");
			return new Login4399Result
			{
				Success = false,
				Message = message
			};
		}
	}

	private async Task<Login4399Result> CompleteLoginAsync(string sessionFailMessage)
	{
		string unique;
		using (NeteaseLogin neteaseLogin = new NeteaseLogin())
		{
			unique = neteaseLogin.GetDeviceUnique();
		}
		string text = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
		string text2 = "http://cdn.h5wan.4399sj.com/microterminal-h5-frame/?game_id=500352";
		string text3 = "null";
		try
		{
			Cookie cookie = _cookies.GetCookies(new Uri("https://ptlogin.4399.com/"))["Pnick"];
			if (cookie != null && !string.IsNullOrWhiteSpace(cookie.Value))
			{
				text3 = Uri.EscapeDataString(Uri.UnescapeDataString(cookie.Value));
			}
		}
		catch
		{
		}
		string stringToEscape = "http://cdn.h5wan.4399sj.com/microterminal-h5-frame/?game_id=500352&rand_time=" + text;
		string text4 = Uri.EscapeDataString("http://ptlogin.4399.com/resource/ucenter.html?action=login&appId=kid_wdsj&loginLevel=8&regLevel=8&bizId=2201001794&externalLogin=qq&qrLogin=true&layout=vertical&level=101&css=http://microgame.5054399.net/v2/resource/cssSdk/default/login_new.css&v=2018_11_26_16&postLoginHandler=redirect&checkLoginUserCookie=true&redirectUrl=" + Uri.EscapeDataString(Uri.EscapeDataString(stringToEscape)));
		string text5 = "https://ptlogin.4399.com/ptlogin/checkKidLoginUserCookie.do?appId=kid_wdsj&gameUrl=" + text2 + "&rand_time=" + text + "&nick=" + text3 + "&onLineStart=false&show=1&isCrossDomain=1&retUrl=" + text4;
		LogHelper.Log("[4399登录] checkKid 请求路径: " + SafeUrlPath(text5));
		string text6 = await FollowRedirectsAsync(text5, "[4399登录] checkKidLoginUserCookie.do");
		LogHelper.Log("[4399登录] checkKidLoginUserCookie.do 最终地址: " + SafeUrlPath(text6));
		if (string.IsNullOrWhiteSpace(text6))
		{
			return new Login4399Result
			{
				Success = false,
				Message = sessionFailMessage + "：未取到跳转信息"
			};
		}
		int startIndex = text6.LastIndexOf('?') + 1;
		string text7 = text6.Substring(startIndex);
		if (string.IsNullOrWhiteSpace(text7) || text7 == text6)
		{
			text7 = "";
		}
		string callback = string.Concat("jQuery1830", str3: DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), str1: RandomDigits16(), str2: "_");
		string text8 = "https://microgame.5054399.net/v2/service/sdk/info?" + new ParameterBuilder().Append("callback", callback).Append("queryStr", text7).FormUrlEncode();
		LogHelper.Log("[4399登录] sdk/info 请求: " + SafeUrlPath(text8));
		using HttpResponseMessage uniResp = await _http.GetAsync(text8);
		string text9 = await uniResp.Content.ReadAsStringAsync();
		LogHelper.Log("[4399登录] sdk/info 响应: " + RedactBody(text9));
		int num = text9.IndexOf(callback + "(", StringComparison.Ordinal);
		if (num < 0)
		{
			return new Login4399Result
			{
				Success = false,
				Message = sessionFailMessage + "：uni-auth 响应异常"
			};
		}
		num += callback.Length + 1;
		Entity4399Response entity4399Response = JsonSerializer.Deserialize<Entity4399Response>(text9.Substring(num, text9.Length - 1 - num));
		if (entity4399Response == null || entity4399Response.Data == null)
		{
			return new Login4399Result
			{
				Success = false,
				Message = sessionFailMessage + "：无法解析账号信息"
			};
		}
		ParameterBuilder parameterBuilder = new ParameterBuilder(entity4399Response.Data.SdkLoginData);
		LogHelper.Log("[4399登录] sdk_login_data 字段: " + string.Join(",", parameterBuilder.Names()));
		string text10 = parameterBuilder.Get("token");
		LogHelper.Log("[4399登录] 解析结果 username=" + parameterBuilder.Get("username") + " uid=" + parameterBuilder.Get("uid") + " token=" + Mask(text10) + " time=" + parameterBuilder.Get("time"));
		string text11 = GenerateSAuth(unique, parameterBuilder.Get("username"), parameterBuilder.Get("uid"), text10, parameterBuilder.Get("time"));
		LogHelper.Log("[4399登录] 生成的 SAuth cookie: " + Mask(text11));
		return new Login4399Result
		{
			Success = true,
			CookieJson = text11
		};
	}

	private async Task<string> FollowRedirectsAsync(string startUrl, string logTag)
	{
		string current = startUrl;
		for (int hop = 0; hop < 10; hop++)
		{
			using HttpResponseMessage httpResponseMessage = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead);
			LogHelper.Log(logTag + " 状态: " + (int)httpResponseMessage.StatusCode + " " + httpResponseMessage.ReasonPhrase);
			if (httpResponseMessage.IsSuccessStatusCode)
			{
				return httpResponseMessage.RequestMessage?.RequestUri?.ToString() ?? current;
			}
			Uri uri = httpResponseMessage.Headers.Location;
			if (uri == null)
			{
				return httpResponseMessage.RequestMessage?.RequestUri?.ToString() ?? current;
			}
			if (!uri.IsAbsoluteUri && httpResponseMessage.RequestMessage?.RequestUri != null)
			{
				uri = new Uri(httpResponseMessage.RequestMessage.RequestUri, uri);
			}
			LogHelper.Log(logTag + " 跳转 " + (hop + 1) + " -> " + Shorten(uri.ToString()));
			current = uri.ToString();
		}
		return current;
	}

	private static EntityPhoneUsers? TryParseUsers(string body)
	{
		try
		{
			return JsonSerializer.Deserialize<EntityPhoneUsers>(body);
		}
		catch
		{
			return null;
		}
	}

	private static string RedactBody(string body)
	{
		if (body == null)
		{
			return "(空响应)";
		}
		if (body.Length == 0)
		{
			return "(空响应)";
		}
		string text = (SensitiveMarker(body) ? "，含凭证字段，内容已隐藏" : "");
		return "长度 " + body.Length + text;
	}

	private static string RedactHeaderValue(string name, IEnumerable<string> values)
	{
		if (values == null)
		{
			return "(空)";
		}
		bool flag = IsSensitiveName(name);
		string text = "";
		foreach (string value in values)
		{
			if (text.Length > 0)
			{
				text += " | ";
			}
			if (!flag)
			{
				text += value;
				continue;
			}
			int num = value.IndexOf('=');
			text += ((num < 0) ? value : (value.Substring(0, num + 1) + Mask(value.Substring(num + 1))));
		}
		return text;
	}

	private static bool IsSensitiveName(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return false;
		}
		string text = name.ToLowerInvariant();
		if (!text.Contains("cookie") && !text.Contains("token") && !text.Contains("secret") && !text.Contains("auth") && !text.Contains("password"))
		{
			return text.Contains("sauth");
		}
		return true;
	}

	private static bool SensitiveMarker(string body)
	{
		if (string.IsNullOrEmpty(body))
		{
			return false;
		}
		string text = body.ToLowerInvariant();
		if (!text.Contains("token") && !text.Contains("sauth") && !text.Contains("password") && !text.Contains("sdklogindata") && !text.Contains("cookie") && !text.Contains("secret") && !text.Contains("ptlogin"))
		{
			return text.Contains("credential");
		}
		return true;
	}

	private static string Shorten(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "未知原因";
		}
		if (text.Length > 200)
		{
			return text.Substring(0, 200) + "…";
		}
		return text;
	}

	private static string SafeUrlPath(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return "null";
		}
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri result))
		{
			return "(无法解析的地址，长度" + url.Length + ")";
		}
		string text = "";
		string[] array = result.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
		string[] array2 = array;
		foreach (string text2 in array2)
		{
			text = text + text2.Split('=')[0] + " | ";
		}
		return result.Host + result.AbsolutePath + (string.IsNullOrEmpty(text) ? "" : (" ?" + text));
	}

	private static string Mask(string secret)
	{
		if (string.IsNullOrEmpty(secret))
		{
			return "(空)";
		}
		if (secret.Length <= 8)
		{
			return "***(" + secret.Length + "位)";
		}
		return secret.Substring(0, 2) + "***" + secret.Substring(secret.Length - 2) + "(" + secret.Length + "位)";
	}

	private static string RiskControlMessage(HttpStatusCode status, string body)
	{
		string text = body.Trim();
		if (text.Contains("请稍后再试") || text.Contains("稍后再试") || text.Contains("频繁") || text.Contains("访问过多") || text.Contains("操作过"))
		{
			return "4399 风控拦截（访问过于频繁），请等待一段时间（建议 10 分钟以上）后重试，频繁重试会延长风控时间";
		}
		if (status == HttpStatusCode.Accepted && text.Length > 0 && text.Length <= 64 && !text.StartsWith("<", StringComparison.Ordinal))
		{
			return "4399 登录被风控拦截（HTTP 202），请稍后重试";
		}
		return "";
	}

	private static string ExtractFormError(string html)
	{
		try
		{
			int num = html.IndexOf("login_err_tip", StringComparison.Ordinal);
			if (num < 0)
			{
				return "";
			}
			int num2 = html.IndexOf('>', num);
			if (num2 < 0)
			{
				return "";
			}
			int num3 = html.IndexOf('<', num2 + 1);
			if (num3 < 0)
			{
				return "";
			}
			return DecodeEntities(html.Substring(num2 + 1, num3 - num2 - 1).Trim());
		}
		catch
		{
			return "";
		}
	}

	private static string DecodeEntities(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}
		return text.Replace("&nbsp;", " ").Replace("&#39;", "'").Replace("&quot;", "\"")
			.Replace("&amp;", "&")
			.Replace("&lt;", "<")
			.Replace("&gt;", ">");
	}

	private static Dictionary<string, string> BuildLoginParams()
	{
		return new Dictionary<string, string>
		{
			["appId"] = "kid_wdsj",
			["autoLogin"] = "on",
			["bizId"] = "2201001794",
			["css"] = "http://microgame.5054399.net/v2/resource/cssSdk/default/login_new.css",
			["displayMode"] = "popup",
			["externalLogin"] = "qq",
			["gameId"] = "wd",
			["iframeId"] = "popup_login_frame",
			["includeFcmInfo"] = "false",
			["layout"] = "vertical",
			["layoutSelfAdapting"] = "true",
			["level"] = "8",
			["loginFrom"] = "uframe",
			["mainDivId"] = "popup_login_div",
			["postLoginHandler"] = "default",
			["redirectUrl"] = "",
			["regLevel"] = "8",
			["sec"] = "1",
			["sessionId"] = "",
			["userNameLabel"] = "4399用户名",
			["userNameTip"] = "请输入4399用户名",
			["welcomeTip"] = "欢迎回到4399"
		};
	}

	private static Dictionary<string, string> BuildPhoneLoginParams()
	{
		return new Dictionary<string, string>
		{
			["appId"] = "kid_wdsj",
			["autoLogin"] = "on",
			["bizId"] = "2201001794",
			["css"] = "https://microgame.5054399.net/v2/resource/cssSdk/default/login.css",
			["displayMode"] = "popup",
			["externalLogin"] = "qq",
			["gameId"] = "wd",
			["includeFcmInfo"] = "false",
			["layout"] = "vertical",
			["layoutSelfAdapting"] = "true",
			["level"] = "8",
			["loginFrom"] = "uframe",
			["loginLevel"] = "8",
			["loginMode"] = "login_phone",
			["mainDivId"] = "popup_phonelogin_div",
			["postLoginHandler"] = "default",
			["redirectUrl"] = "",
			["regLevel"] = "8",
			["sec"] = "1",
			["sessionId"] = "",
			["userNameLabel"] = "4399用户名",
			["userNameTip"] = "请输入4399用户名",
			["welcomeTip"] = "欢迎回到4399"
		};
	}

	private static string Encrypt4399(string plaintext)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
		byte[] array = new byte[8];
		RandomNumberGenerator.Fill(array);
		byte[] bytes2 = Encoding.UTF8.GetBytes("lzYW5qaXVqa");
		byte[] array3;
		byte[] array4;
		byte[] iV;
		using (MD5 mD = MD5.Create())
		{
			byte[] array2 = ConcatBytes(bytes2, array);
			array3 = mD.ComputeHash(array2);
			array4 = mD.ComputeHash(ConcatBytes(array3, array2));
			iV = mD.ComputeHash(ConcatBytes(array4, array2));
		}
		byte[] key = ConcatBytes(array3, array4);
		using Aes aes = Aes.Create();
		aes.KeySize = 256;
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.PKCS7;
		aes.Key = key;
		aes.IV = iV;
		using ICryptoTransform cryptoTransform = aes.CreateEncryptor();
		byte[] array5 = cryptoTransform.TransformFinalBlock(bytes, 0, bytes.Length);
		return Convert.ToBase64String(ConcatBytes(Encoding.ASCII.GetBytes("Salted__"), array, array5));
	}

	private static byte[] ConcatBytes(params byte[][] arrays)
	{
		int num = 0;
		byte[][] array = arrays;
		byte[][] array2 = array;
		foreach (byte[] array3 in array2)
		{
			num += array3.Length;
		}
		byte[] array4 = new byte[num];
		int num2 = 0;
		array = arrays;
		byte[][] array5 = array;
		foreach (byte[] array6 in array5)
		{
			Buffer.BlockCopy(array6, 0, array4, num2, array6.Length);
			num2 += array6.Length;
		}
		return array4;
	}

	private static string GenerateSAuth(string deviceId, string userid, string sdkUid, string sessionId, string timestamp)
	{
		return JsonSerializer.Serialize(new EntityMgbSdkCookie
		{
			Timestamp = timestamp,
			UserId = userid,
			GameId = "x19",
			LoginChannel = "4399pc",
			AppChannel = "4399pc",
			Platform = "pc",
			SdkUid = sdkUid,
			SessionId = sessionId,
			SdkVersion = "1.0.0",
			Udid = deviceId.ToUpperInvariant(),
			DeviceId = deviceId.ToUpperInvariant(),
			AimInfo = "{\"aim\":\"127.0.0.1\",\"country\":\"CN\",\"tz\":\"+0800\",\"tzid\":\"\"}",
			ClientLoginSn = deviceId.ToUpperInvariant(),
			SourcePlatform = "pc",
			Ip = "127.0.0.1"
		}, Relaxed);
	}

	private static string RandomDigits16()
	{
		StringBuilder stringBuilder = new StringBuilder(16);
		for (int i = 0; i < 16; i++)
		{
			stringBuilder.Append((char)(48 + Random.Shared.Next(10)));
		}
		return stringBuilder.ToString();
	}

	private static HttpRequestMessage BuildPtLoginRequest(HttpMethod method, string url, string referer, bool xhr = false, Dictionary<string, string>? form = null)
	{
		HttpRequestMessage httpRequestMessage = new HttpRequestMessage(method, url);
		httpRequestMessage.Headers.TryAddWithoutValidation("Accept", xhr ? "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8" : "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
		httpRequestMessage.Headers.TryAddWithoutValidation("Referer", referer);
		httpRequestMessage.Headers.TryAddWithoutValidation("Origin", "https://ptlogin.4399.com");
		httpRequestMessage.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
		httpRequestMessage.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", xhr ? "cors" : "navigate");
		httpRequestMessage.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", xhr ? "empty" : "document");
		if (form != null)
		{
			httpRequestMessage.Content = new StringContent(NetShared.FormUrlEncode(form), Encoding.UTF8, "application/x-www-form-urlencoded");
		}
		return httpRequestMessage;
	}
}

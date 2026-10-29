using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MhyunBox.Settings;

namespace MhyunBox.Services;

using System.IO;
using System.Security.Cryptography;
using System.Text;

// 账密本地加密器：AES-CBC + 随机 IV，密钥由机器名/用户名+固定盐派生（跨机器不可解，账密本来就只在本机用）。
// 密文格式 "ENC1:" + Base64(IV + 密文)；不带前缀的旧数据（历史明文记录）原样返回，读后无需迁移（写入时自动转密文）。
internal static class CredentialProtector
{
	private const string Prefix = "ENC1:";

	private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("MhyunBox::" + Environment.MachineName + "::" + Environment.UserName + "::cred-v1"));

	public static string Encrypt(string plain)
	{
		if (string.IsNullOrEmpty(plain))
		{
			return "";
		}
		try
		{
			using Aes aes = Aes.Create();
			aes.Key = Key;
			aes.GenerateIV();
			using MemoryStream ms = new MemoryStream();
			ms.Write(aes.IV);
			using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
			{
				byte[] data = Encoding.UTF8.GetBytes(plain);
				cs.Write(data, 0, data.Length);
			}
			return Prefix + Convert.ToBase64String(ms.ToArray());
		}
		catch
		{
			return "";
		}
	}

	public static string Decrypt(string stored)
	{
		if (string.IsNullOrEmpty(stored))
		{
			return "";
		}
		if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
		{
			// 旧版本明文记录：原样返回保持兼容
			return stored;
		}
		try
		{
			byte[] data = Convert.FromBase64String(stored.Substring(Prefix.Length));
			if (data.Length <= 16)
			{
				return "";
			}
			using Aes aes = Aes.Create();
			aes.Key = Key;
			aes.IV = data.AsSpan(0, 16).ToArray();
			using MemoryStream ms = new MemoryStream(data, 16, data.Length - 16);
			using CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
			using StreamReader reader = new StreamReader(cs, new UTF8Encoding(false));
			return reader.ReadToEnd();
		}
		catch
		{
			return "";
		}
	}
}

public static class AccountStore
{
	public sealed class LocalAccount
	{
		public string Channel { get; set; } = "";

		public string Account { get; set; } = "";

		public string Nickname { get; set; } = "";

		public string EntityId { get; set; } = "";

		public string Token { get; set; } = "";

		public string CookieJson { get; set; } = "";

		// 账密本地加密存储（AES-CBC，机器维度密钥；json 文件里不再出现明文）。
		// 用途：网易绑定会话是一次性短期凭证，自动重登链路（每 30 分钟账密保活 / 失效兜底）用它直接换新会话；
		// 仅账密登录的账号有值（4399 账密 / 网易账密直登），短信验证码登录/COOKIE 导入的账号为空。
		// json 序列化直接持久化这两个字段（密文）；代码里要用明文时读 LoginUserPlain/LoginPassPlain，写入用 SetCredentials。
		public string LoginUser { get; set; } = "";

		public string LoginPass { get; set; } = "";

		// 程序未解锁时一律返回空：账密保活/重登链路拿不到明文
		public string LoginUserPlain => (AppLock.IsUnlocked ? CredentialProtector.Decrypt(LoginUser) : "");

		public string LoginPassPlain => (AppLock.IsUnlocked ? CredentialProtector.Decrypt(LoginPass) : "");

		public void SetCredentials(string user, string pass)
		{
			LoginUser = CredentialProtector.Encrypt(user ?? "");
			LoginPass = CredentialProtector.Encrypt(pass ?? "");
		}

		public bool HasCredentials => !string.IsNullOrWhiteSpace(LoginUserPlain) && !string.IsNullOrWhiteSpace(LoginPassPlain);

		public bool Is4399 => Channel != null && Channel.StartsWith("4399", StringComparison.Ordinal);

		public List<string> RoleNames { get; set; } = new List<string>();

		public Dictionary<string, List<string>> ServerRoles { get; set; } = new Dictionary<string, List<string>>();

		public DateTime SavedAtUtc { get; set; }

		public string Remark { get; set; } = "";

		public bool? LoggedIn { get; set; } = true;

		public bool AutoLogin { get; set; }

		public bool IsLoggedIn => LoggedIn ?? true;

		public bool HasCookie => AppLock.IsUnlocked && !string.IsNullOrWhiteSpace(CookieJson);

		public bool IsProxyUsable => HasCookie;
	}

	private const string OldStoreKey = "local_account";

	private static List<LocalAccount>? _list;

	// I/O 并发保护：账密保活/自动重登在后台线程 Save，UI 线程同时读写同一列表，
	// 且序列化落盘时会「原地加密→序列化→还原」敏感字段，必须与其它读写互斥
	private static readonly object IoLock = new object();

	// 未解锁（程序账号未登录）时账密数据整体不可用：内存不加载、接口返回空。
	private static IReadOnlyList<LocalAccount> Empty => Array.Empty<LocalAccount>();

	public static IReadOnlyList<LocalAccount> All => (!AppLock.IsUnlocked) ? Empty : (_list ?? (_list = LoadList())).AsReadOnly();

	public static LocalAccount? Current
	{
		get
		{
			if (!AppLock.IsUnlocked)
			{
				return null;
			}
			List<LocalAccount> list = _list ?? (_list = LoadList());
			if (list.Count <= 0)
			{
				return null;
			}
			return list[list.Count - 1];
		}
	}

	public static bool AnyAutoLogin => AppLock.IsUnlocked && All.Any((LocalAccount a) => a.AutoLogin);

	public static event Action? Changed;

	/// <summary>解锁后由 AppLock 事件触发：丢弃旧缓存并通知 UI 重新读取。</summary>
	public static void ReloadFromStore()
	{
		_list = null;
		Changed?.Invoke();
	}

	/// <summary>上锁后调用：立刻丢弃内存中的明文账密。</summary>
	public static void DropInMemory()
	{
		_list = null;
	}

	// 敏感字段（Cookie/Token/EntityId）落盘前用数据密钥二次加密（"ENC2:"），读回时解密。
	// 网易账密（LoginUser/LoginPass）只保留机器密钥层（ENC1）——它要在程序锁定态参与
	// 「网易账号验证解锁」的通道建立，不能再叠一层登录后才能解的密。
	// 采用"原地加密→序列化→finally 还原"，避免改动 LocalAccount 结构与各处读写逻辑。
	private static void EncryptSensitive(LocalAccount a)
	{
		a.CookieJson = AppLock.VaultEncrypt(a.CookieJson);
		a.Token = AppLock.VaultEncrypt(a.Token);
		a.EntityId = AppLock.VaultEncrypt(a.EntityId);
	}

	private static void DecryptSensitive(LocalAccount a)
	{
		a.CookieJson = AppLock.VaultDecrypt(a.CookieJson);
		a.Token = AppLock.VaultDecrypt(a.Token);
		a.EntityId = AppLock.VaultDecrypt(a.EntityId);
	}

	private static string SerializeList(List<LocalAccount> list)
	{
		int n = list.Count;
		string[] backup = new string[n * 3];
		try
		{
			for (int i = 0; i < n; i++)
			{
				LocalAccount a = list[i];
				int b = i * 3;
				backup[b] = a.CookieJson;
				backup[b + 1] = a.Token;
				backup[b + 2] = a.EntityId;
				EncryptSensitive(a);
			}
			return JsonSerializer.Serialize(list);
		}
		finally
		{
			for (int i = 0; i < n; i++)
			{
				LocalAccount a = list[i];
				int b = i * 3;
				a.CookieJson = backup[b];
				a.Token = backup[b + 1];
				a.EntityId = backup[b + 2];
			}
		}
	}

	private static void SaveList(List<LocalAccount> list)
	{
		lock (IoLock)
		{
			SecureStore.Save("local_accounts", SerializeList(list));
		}
	}

	private static List<LocalAccount> LoadList()
	{
		if (!AppLock.IsUnlocked)
		{
			return new List<LocalAccount>();
		}
		lock (IoLock)
		{
		string text = SecureStore.Load("local_accounts");
		if (!string.IsNullOrWhiteSpace(text))
		{
			try
			{
				List<LocalAccount> loaded = JsonSerializer.Deserialize<List<LocalAccount>>(text) ?? new List<LocalAccount>();
				foreach (LocalAccount acc in loaded)
				{
					DecryptSensitive(acc);
				}
				MigratePlaintextCredentials(loaded);
				return loaded;
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[账号存储] local_accounts 解析失败，已备份原始数据避免被覆盖丢失");
				SecureStore.Save("local_accounts_broken_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), text);
			}
		}
		string text2 = SecureStore.Load("local_account");
		List<LocalAccount> list = new List<LocalAccount>();
		if (!string.IsNullOrWhiteSpace(text2))
		{
			try
			{
				LocalAccount localAccount = JsonSerializer.Deserialize<LocalAccount>(text2);
				if (localAccount != null && !string.IsNullOrWhiteSpace(localAccount.Account))
				{
					DecryptSensitive(localAccount);
					list.Add(localAccount);
					MigratePlaintextCredentials(list);
					SaveList(list);
					SecureStore.Delete("local_account");
				}
			}
			catch (Exception ex2)
			{
				LogHelper.LogError(ex2, "[账号存储] 旧版 local_account 解析失败");
			}
		}
		return list;
		}
	}

	// 旧版本明文账密自动迁移：检测到无 ENC1: 前缀的账密记录立即加密回写（升级后首次加载即完成，json 里不再有明文）
	private static void MigratePlaintextCredentials(List<LocalAccount> list)
	{
		bool migrated = false;
		foreach (LocalAccount acc in list)
		{
			if (!string.IsNullOrWhiteSpace(acc.LoginUser) && !acc.LoginUser.StartsWith("ENC1:", StringComparison.Ordinal))
			{
				acc.SetCredentials(acc.LoginUser, acc.LoginPass);
				migrated = true;
			}
		}
		if (migrated)
		{
			SaveList(list);
			LogHelper.Log("[账号存储] 已将历史明文账密迁移为本地加密存储");
		}
	}

	public static void Save(LocalAccount account)
	{
		if (!AppLock.IsUnlocked)
		{
			return;
		}
		List<LocalAccount> list = _list ?? (_list = LoadList());
		account.SavedAtUtc = DateTime.UtcNow;
		int num = list.FindIndex((LocalAccount a) => string.Equals(a.Channel, account.Channel, StringComparison.Ordinal) && string.Equals(a.Account, account.Account, StringComparison.OrdinalIgnoreCase));
		if (num < 0 && !string.IsNullOrWhiteSpace(account.EntityId))
		{
			num = list.FindIndex((LocalAccount a) => string.Equals(a.Channel, account.Channel, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.EntityId) && string.Equals(a.EntityId, account.EntityId, StringComparison.Ordinal));
		}
		if (num >= 0)
		{
			if (account.RoleNames.Count == 0)
			{
				List<string> roleNames = list[num].RoleNames;
				if (roleNames != null && roleNames.Count > 0)
				{
					account.RoleNames = list[num].RoleNames;
				}
			}
			if ((account.ServerRoles == null || account.ServerRoles.Count == 0) && list[num].ServerRoles != null && list[num].ServerRoles.Count > 0)
			{
				account.ServerRoles = list[num].ServerRoles;
			}
			if (string.IsNullOrEmpty(account.Remark))
			{
				account.Remark = list[num].Remark;
			}
			account.AutoLogin = account.AutoLogin || list[num].AutoLogin;
			// 保留账密：BindVerified 拉取/重登构造的新记录不带账密，若不搬运会把已保存的加密账密清掉，
			// 导致之后会话失效时 HasCredentials=false、账密重登永远不触发（实测踩坑）
			if (string.IsNullOrEmpty(account.LoginUser) && !string.IsNullOrEmpty(list[num].LoginUser))
			{
				account.LoginUser = list[num].LoginUser;
				account.LoginPass = list[num].LoginPass;
			}
			list[num] = account;
		}
		else
		{
			list.Add(account);
		}
		SaveList(list);
		Changed?.Invoke();
	}

	public static void Remove(string channel, string account)
	{
		if (!AppLock.IsUnlocked)
		{
			return;
		}
		List<LocalAccount> list = _list ?? (_list = LoadList());
		int num = list.RemoveAll((LocalAccount a) => string.Equals(a.Channel, channel, StringComparison.Ordinal) && string.Equals(a.Account, account, StringComparison.OrdinalIgnoreCase));
		if (num > 0)
		{
			SaveList(list);
		}
		PurgeGroupAndPortData(channel, account);
		if (num > 0)
		{
			Changed?.Invoke();
		}
	}

	private static void PurgeGroupAndPortData(string channel, string account)
	{
		try
		{
			AppSettings instance = SettingsManager.Instance;
			if (instance == null)
			{
				return;
			}
			if (instance.ProxyGroups != null)
			{
				foreach (ProxyGroup proxyGroup in instance.ProxyGroups)
				{
					if (proxyGroup.Roles != null)
					{
						proxyGroup.Roles.RemoveAll((ProxyGroupRole r) => string.Equals(r.Channel ?? "", channel ?? "", StringComparison.Ordinal) && string.Equals(r.Account ?? "", account ?? "", StringComparison.OrdinalIgnoreCase));
					}
				}
				instance.ProxyGroups.RemoveAll((ProxyGroup g) => g.Roles == null || g.Roles.Count == 0);
			}
			if (instance.RemovedAccountPortMemory != null)
			{
				instance.RemovedAccountPortMemory.RemoveAll((SavedProxyBinding x) => string.Equals(x.Channel, channel, StringComparison.Ordinal) && string.Equals(x.Account, account, StringComparison.OrdinalIgnoreCase));
			}
			if (instance.LastProxyBindings != null)
			{
				instance.LastProxyBindings = instance.LastProxyBindings.Where((SavedProxyBinding x) => !string.Equals(x.Channel, channel, StringComparison.Ordinal) || !string.Equals(x.Account, account, StringComparison.OrdinalIgnoreCase)).ToList();
			}
			if (instance.GroupRoleChecked != null)
			{
				string ch = channel ?? "";
				string acc = account ?? "";
				foreach (string item in instance.GroupRoleChecked.Keys.Where(delegate(string k)
				{
					if (k != null)
					{
						string[] array = k.Split('\0');
						if (array != null && array.Length >= 5 && string.Equals(array[2] ?? "", ch, StringComparison.Ordinal))
						{
							return string.Equals(array[3] ?? "", acc, StringComparison.OrdinalIgnoreCase);
						}
					}
					return false;
				}).ToList())
				{
					instance.GroupRoleChecked.Remove(item);
				}
			}
			SettingsManager.Save(instance);
			LogHelper.Log("[账号] 已清理账号 " + account + "：分组角色记录、端口记忆、最近启动绑定、分组勾选记忆");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "清理已删除账号关联数据失败");
		}
	}

	public static void Clear()
	{
		if (!AppLock.IsUnlocked)
		{
			return;
		}
		_list = new List<LocalAccount>();
		SecureStore.Delete("local_accounts");
		SecureStore.Delete("local_account");
		try
		{
			AppSettings instance = SettingsManager.Instance;
			if (instance != null)
			{
				instance.ProxyGroups?.Clear();
				instance.RemovedAccountPortMemory?.Clear();
				instance.LastProxyBindings?.Clear();
				instance.LastProxyPresetName = "";
				instance.GroupRoleChecked?.Clear();
				instance.LastAutoStartGroupNames?.Clear();
				SettingsManager.Save(instance);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "清空账号时清理关联数据失败");
		}
		Changed?.Invoke();
	}

	public static string Mask(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "";
		}
		if (value.Length <= 4)
		{
			return new string('*', value.Length);
		}
		return value.Substring(0, 2) + "****" + value.Substring(value.Length - 2);
	}

	public static (bool Ok, string Message) BindVerified(string channel, string account, NeteaseLoginResult result)
	{
		if (!AppLock.IsUnlocked)
		{
			return (Ok: false, Message: "程序账号未解锁，请先在「本机锁」页登录程序账号");
		}
		if (string.IsNullOrWhiteSpace(account))
		{
			return (Ok: false, Message: "账号不能为空");
		}
		string effectiveChannel = (string.IsNullOrWhiteSpace(channel) ? result.Channel : channel);
		LocalAccount localAccount = null;
		if (!string.IsNullOrWhiteSpace(result.EntityId))
		{
			localAccount = (_list ?? (_list = LoadList())).FirstOrDefault((LocalAccount a) => string.Equals(a.Channel, effectiveChannel, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.EntityId) && string.Equals(a.EntityId, result.EntityId, StringComparison.Ordinal) && !string.Equals(a.Account, account, StringComparison.OrdinalIgnoreCase));
		}
		Save(new LocalAccount
		{
			Channel = effectiveChannel,
			Account = account,
			Nickname = result.Nickname,
			EntityId = result.EntityId,
			Token = result.Token,
			CookieJson = result.CookieJson
		});
		string text = (string.IsNullOrWhiteSpace(result.Nickname) ? Mask(account) : result.Nickname);
		if (localAccount != null)
		{
			string text2 = (string.IsNullOrWhiteSpace(localAccount.Nickname) ? Mask(localAccount.Account) : localAccount.Nickname);
			return (Ok: true, Message: "检测到「" + text2 + "」与本次账号是同一网易账号（EntityId=" + Mask(result.EntityId) + "），已合并为一条，避免重复绑定");
		}
		return (Ok: true, Message: "已在 " + effectiveChannel + " 接口验证并保存：" + text);
	}

	public static bool RefreshRolesVerified(string channel, string account, NeteaseLoginResult result, IReadOnlyDictionary<string, List<string>> serverRoles)
	{
		if (!AppLock.IsUnlocked || string.IsNullOrWhiteSpace(account))
		{
			return false;
		}
		string effectiveChannel = (string.IsNullOrWhiteSpace(channel) ? result.Channel : channel);
		List<LocalAccount> list = _list ?? (_list = LoadList());
		int num = list.FindIndex((LocalAccount a) => string.Equals(a.Channel, effectiveChannel, StringComparison.Ordinal) && string.Equals(a.Account, account, StringComparison.OrdinalIgnoreCase));
		if (num < 0 && !string.IsNullOrWhiteSpace(result.EntityId))
		{
			num = list.FindIndex((LocalAccount a) => string.Equals(a.Channel, effectiveChannel, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.EntityId) && string.Equals(a.EntityId, result.EntityId, StringComparison.Ordinal));
		}
		if (num < 0)
		{
			return false;
		}
		LocalAccount localAccount = list[num];
		localAccount.EntityId = result.EntityId;
		localAccount.Token = result.Token;
		localAccount.CookieJson = result.CookieJson;
		if (!string.IsNullOrWhiteSpace(result.Nickname))
		{
			localAccount.Nickname = result.Nickname;
		}
		localAccount.LoggedIn = true;
		localAccount.SavedAtUtc = DateTime.UtcNow;
		if (serverRoles != null)
		{
			Dictionary<string, List<string>> dictionary = new Dictionary<string, List<string>>(StringComparer.Ordinal);
			foreach (KeyValuePair<string, List<string>> serverRole in serverRoles)
			{
				string text = (serverRole.Key ?? "").Trim();
				if (text.Length != 0 && serverRole.Value != null && serverRole.Value.Count > 0)
				{
					dictionary[text] = serverRole.Value.Where((string r) => !string.IsNullOrWhiteSpace(r)).Distinct().Take(20)
						.ToList();
				}
			}
			if (localAccount.ServerRoles != null)
			{
				foreach (KeyValuePair<string, List<string>> serverRole2 in localAccount.ServerRoles)
				{
					if (!dictionary.ContainsKey(serverRole2.Key))
					{
						dictionary[serverRole2.Key] = (serverRole2.Value ?? new List<string>()).Distinct().Take(20).ToList();
					}
				}
			}
			localAccount.ServerRoles = dictionary;
			localAccount.RoleNames = new List<string>();
		}
		SaveList(list);
		Changed?.Invoke();
		return true;
	}

	public static void SetLoginState(string channel, string account, bool loggedIn)
	{
		if (!AppLock.IsUnlocked)
		{
			return;
		}
		List<LocalAccount> list = _list ?? (_list = LoadList());
		int num = list.FindIndex((LocalAccount a) => string.Equals(a.Channel, channel, StringComparison.Ordinal) && string.Equals(a.Account, account, StringComparison.OrdinalIgnoreCase));
		if (num >= 0)
		{
			list[num].LoggedIn = loggedIn;
			SaveList(list);
			Changed?.Invoke();
		}
	}

	public static void SetAutoLoginAll(bool autoLogin)
	{
		if (!AppLock.IsUnlocked)
		{
			return;
		}
		List<LocalAccount> list = _list ?? (_list = LoadList());
		bool flag = false;
		foreach (LocalAccount item in list)
		{
			if (item.AutoLogin != autoLogin)
			{
				item.AutoLogin = autoLogin;
				flag = true;
			}
		}
		if (flag)
		{
			SaveList(list);
			Changed?.Invoke();
		}
	}

	public static void LogoutAll()
	{
		if (!AppLock.IsUnlocked)
		{
			return;
		}
		List<LocalAccount> list = _list ?? (_list = LoadList());
		bool flag = false;
		foreach (LocalAccount item in list)
		{
			if (item.LoggedIn != false)
			{
				item.LoggedIn = false;
				flag = true;
			}
		}
		if (flag)
		{
			SaveList(list);
			Changed?.Invoke();
		}
	}

	public static void UpdateRoles<T>(string channel, string account, IReadOnlyDictionary<string, T> serverRoles) where T : IReadOnlyList<string>
	{
		if (!AppLock.IsUnlocked)
		{
			return;
		}
		List<LocalAccount> list = _list ?? (_list = LoadList());
		LocalAccount localAccount = list.FirstOrDefault((LocalAccount a) => string.Equals(a.Channel, channel, StringComparison.Ordinal) && string.Equals(a.Account, account, StringComparison.OrdinalIgnoreCase));
		if (localAccount == null && !string.IsNullOrWhiteSpace(account))
		{
			LocalAccount localAccount2 = list.FirstOrDefault((LocalAccount a) => string.Equals(a.Channel, channel, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.EntityId) && string.Equals(a.EntityId, account, StringComparison.Ordinal));
			if (localAccount2 != null)
			{
				localAccount = localAccount2;
			}
		}
		if (localAccount == null)
		{
			return;
		}
		Dictionary<string, List<string>> dictionary = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (KeyValuePair<string, T> serverRole in serverRoles)
		{
			string text = (serverRole.Key ?? "").Trim();
			if (text.Length != 0)
			{
				List<string> list2 = new List<string>();
				if (localAccount.ServerRoles != null && localAccount.ServerRoles.TryGetValue(text, out var value) && value != null)
				{
					list2.AddRange(value);
				}
				list2.AddRange(serverRole.Value.Where((string r) => !string.IsNullOrWhiteSpace(r)));
				dictionary[text] = list2.Distinct().Take(20).ToList();
			}
		}
		if (localAccount.ServerRoles != null)
		{
			foreach (KeyValuePair<string, List<string>> serverRole2 in localAccount.ServerRoles)
			{
				if (!dictionary.ContainsKey(serverRole2.Key))
				{
					dictionary[serverRole2.Key] = serverRole2.Value.Distinct().Take(20).ToList();
				}
			}
		}
		HashSet<string> hashSet = new HashSet<string>(dictionary.Values.SelectMany((List<string> v) => v), StringComparer.Ordinal);
		List<string> list3 = new List<string>();
		if (localAccount.RoleNames != null)
		{
			foreach (string roleName in localAccount.RoleNames)
			{
				if (!hashSet.Contains(roleName))
				{
					list3.Add(roleName);
				}
			}
		}
		if (list3.Count > 0)
		{
			dictionary["未分组"] = list3.Distinct().Take(20).ToList();
		}
		localAccount.ServerRoles = dictionary;
		localAccount.RoleNames = new List<string>();
		SaveList(list);
		Changed?.Invoke();
	}
}

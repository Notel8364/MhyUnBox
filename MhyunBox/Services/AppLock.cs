using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MhyunBox.Services;

// 程序本机锁 + 账密二次加密：
//  1. 首次启动弹窗创建「本机程序账号」；账号记录(用户名/盐/验证值)经 SecureStore(DPAPI) 保存在本机。
//  2. 创建账号时随机生成 32 字节数据密钥（AES-256），用「口令派生密钥」(PBKDF2-SHA256, 10万次) 包裹后保存；
//     程序每次启动都处于锁定态，只有登录程序账号才能解开数据密钥。
//  3. 未解锁期间，AccountStore 的所有账密/Cookie 数据整体不可访问（内存中不持有明文），
//     落盘的敏感字段再叠加一层 "ENC2:" 数据密钥加密（另外一把密钥），即使拿到本地文件也读不出明文。
public static class AppLock
{
	private sealed class LockRecord
	{
		public string User { get; set; } = "";

		public string Salt { get; set; } = "";

		// 旧版验证值（PBKDF2），仅兼容升级前的本机记录
		public string Verify { get; set; } = "";

		// 现行验证值：MD5(salt + 密码) 十六进制 —— 单向哈希，本机不存密码明文也不存可逆密文
		public string VerifyMd5 { get; set; } = "";

		public string WrapSalt { get; set; } = "";

		public string Wrapped { get; set; } = "";

		// 网易验证通道：用「已绑定网易账密」派生密钥对数据密钥的额外包裹（网易账号验证解锁用）。
		// 旧字段保留为第一个验证账号的镜像（兼容），完整列表在 NetWraps。
		public string NetAccount { get; set; } = "";

		public string NetWrapSalt { get; set; } = "";

		public string NetWrapped { get; set; } = "";

		// 验证账号包裹列表（默认自动取第一个登入的账号，最多 3 个）
		public List<NetWrapEntry>? NetWraps { get; set; }

		// 用户在「账号绑定」页指定的验证账号列表（后缀容错匹配）；为空则自动取第一个带账密的账号
		public string NetAccountPreferred { get; set; } = "";

		public List<string>? NetAccountPreferredList { get; set; }

		// 启动时自动登录程序账号（仅本机）：开启后用 DPAPI 保护的本机密钥自动解开数据密钥，
		// 不再每次启动要求输入；关闭则每次启动都要求登录程序账号 / 网易账号验证。
		public bool AutoUnlock { get; set; }

		public string AutoWrapped { get; set; } = "";

		public DateTime CreatedAtUtc { get; set; }
	}

	private const string RecordName = "applock_record";

	private const int Pbkdf2Iterations = 100_000;

	private const string VaultPrefix = "ENC2:";

	private static LockRecord? _record;

	private static byte[]? _dataKey;

	// 并发保护：上锁/刷新验证通道可能来自不同线程（UI 点击 vs AccountStore.Changed 回调），
	// 用同一把锁避免「上锁瞬间 _dataKey 置空」与「重建包裹读到空密钥」互相踩踏
	private static readonly object Sync = new object();

	static AppLock()
	{
		_record = LoadRecord();
		// 账密数据变化时自动刷新网易验证通道的包裹
		AccountStore.Changed += delegate
		{
			RefreshNeteaseWrap();
		};
	}

	/// <summary>本机程序账号是否已创建。</summary>
	public static bool HasAccount => _record != null;

	/// <summary>程序是否已解锁（数据密钥已在内存中）。</summary>
	public static bool IsUnlocked => _dataKey != null;

	/// <summary>当前程序账号名（未创建返回 null）。</summary>
	public static string? AccountName => _record?.User;

	/// <summary>程序账号名的掩码展示（如 "hq***"）——账号名一律匿名打码，只露前几位。</summary>
	public static string AccountNameMasked => MaskName(_record?.User);

	/// <summary>解锁成功后触发（订阅方应重新加载账密数据）。</summary>
	public static event Action? Unlocked;

	/// <summary>上锁后触发（订阅方应立刻丢弃内存中的明文数据）。</summary>
	public static event Action? Locked;

	/// <summary>创建本机程序账号（仅首次）。成功后程序自动进入已解锁状态。</summary>
	public static (bool Ok, string Message) CreateAccount(string user, string pass, string confirm)
	{
		if (_record != null)
		{
			return (false, "本机程序账号已存在");
		}
		user = (user ?? "").Trim();
		if (user.Length < 2)
		{
			return (false, "账号名至少 2 个字符");
		}
		if (string.IsNullOrEmpty(pass) || pass.Length < 4)
		{
			return (false, "密码至少 4 位");
		}
		if (pass != confirm)
		{
			return (false, "两次输入的密码不一致");
		}
		try
		{
			byte[] salt = RandomNumberGenerator.GetBytes(16);
			byte[] wrapSalt = RandomNumberGenerator.GetBytes(16);
			// 随机生成数据密钥（AES-256），用口令派生密钥包裹后落盘；
			// 密码本机只留 MD5 单向哈希用于验证，包裹密钥由 MD5 十六进制再经 PBKDF2 派生
			byte[] dataKey = RandomNumberGenerator.GetBytes(32);
			byte[] wrapped = AesEncrypt(Pbkdf2(Md5Hex(pass), wrapSalt), dataKey);
			LockRecord record = new LockRecord
			{
				User = user,
				Salt = Convert.ToBase64String(salt),
				VerifyMd5 = PasswordHash(salt, pass),
				WrapSalt = Convert.ToBase64String(wrapSalt),
				Wrapped = Convert.ToBase64String(wrapped),
				CreatedAtUtc = DateTime.UtcNow
			};
			SecureStore.Save(RecordName, JsonSerializer.Serialize(record));
			_record = record;
			_dataKey = dataKey;
			LogHelper.Log("[程序锁] 本机程序账号已创建（数据密钥已随机生成并包裹保存）");
			try
			{
				Unlocked?.Invoke();
			}
			catch
			{
			}
			AccountStore.ReloadFromStore();
			return (true, "账号创建成功");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[程序锁] 创建账号失败");
			return (false, "创建失败：" + ex.Message);
		}
	}

	/// <summary>登录程序账号并解锁。成功返回 true。</summary>
	public static (bool Ok, string Message) Unlock(string user, string pass)
	{
		LockRecord? record = _record ?? LoadRecord();
		if (record == null)
		{
			return (false, "尚未创建本机程序账号");
		}
		if (string.IsNullOrWhiteSpace(pass))
		{
			return (false, "请输入密码");
		}
		// 账号名容错：可不填（只凭密码验证）；填了则支持后缀容错（如 "hqx" ↔ "hqx@163.com"），
		// 避免改名/合并邮箱后缀后"名称对不上"导致正确密码也解不开
		string typedUser = (user ?? "").Trim();
		if (typedUser.Length > 0 && !SameAccountName(typedUser, record.User))
		{
			return (false, "账号名不正确（可不填，或输入账号内容/主体名）");
		}
		try
		{
			byte[] wrapped = Convert.FromBase64String(record.Wrapped);
			byte[] dataKey;
			if (!string.IsNullOrEmpty(record.VerifyMd5))
			{
				// 现行：MD5 单向验证 + 由 MD5 十六进制派生包裹密钥（兼容早期错误拼接的哈希）
				byte[] salt = Convert.FromBase64String(record.Salt);
				if (PasswordHash(salt, pass) != record.VerifyMd5 && LegacyPasswordHash(salt, pass) != record.VerifyMd5)
				{
					return (false, "密码不正确");
				}
				if (LegacyPasswordHash(salt, pass) == record.VerifyMd5)
				{
					// 旧值迁移为正确拼接
					record.VerifyMd5 = PasswordHash(salt, pass);
					SecureStore.Save(RecordName, JsonSerializer.Serialize(record));
				}
				dataKey = AesDecrypt(Pbkdf2(Md5Hex(pass), Convert.FromBase64String(record.WrapSalt)), wrapped);
			}
			else
			{
				// 旧版记录（PBKDF2 验证）兼容
				byte[] salt = Convert.FromBase64String(record.Salt);
				byte[] expect = Convert.FromBase64String(record.Verify);
				if (!CryptographicOperations.FixedTimeEquals(Pbkdf2(pass, salt), expect))
				{
					return (false, "密码不正确");
				}
				byte[] wrapSalt = Convert.FromBase64String(record.WrapSalt);
				try
				{
					dataKey = AesDecrypt(Pbkdf2(pass, wrapSalt), wrapped);
				}
				catch
				{
					// 更早构建的兜底：旧版曾误用裸 wrapSalt 当包裹密钥，这里恢复后立即升级记录
					dataKey = AesDecrypt(wrapSalt, wrapped);
				}
				// 升级为 MD5 验证 + 统一派生方式
				record.VerifyMd5 = PasswordHash(salt, pass);
				record.Wrapped = Convert.ToBase64String(AesEncrypt(Pbkdf2(Md5Hex(pass), wrapSalt), dataKey));
				SecureStore.Save(RecordName, JsonSerializer.Serialize(record));
			}
			if (dataKey.Length != 32)
			{
				return (false, "数据密钥校验失败");
			}
			_record = record;
			_dataKey = dataKey;
			LogHelper.Log("[程序锁] 已解锁，账密数据可用");
			try
			{
				Unlocked?.Invoke();
			}
			catch
			{
			}
			AccountStore.ReloadFromStore();
			return (true, "解锁成功");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[程序锁] 解锁失败");
			return (false, "解锁失败：数据密钥无法解开（密码错误或数据损坏）");
		}
	}

	/// <summary>重新上锁：立刻丢弃内存中的数据密钥，账密数据变为不可用。</summary>
	public static void Lock()
	{
		if (_dataKey == null)
		{
			return;
		}
		byte[]? key;
		lock (Sync)
		{
			key = _dataKey;
			_dataKey = null;
		}
		if (key == null)
		{
			return;
		}
		Array.Clear(key);
		LogHelper.Log("[程序锁] 已上锁");
		try
		{
			Locked?.Invoke();
		}
		catch
		{
		}
		AccountStore.ReloadFromStore();
	}

	// ---------- 启动自动登录程序账号（默认启动解锁） ----------

	private const string MachineKeyName = "applock_machinekey";

	/// <summary>是否已开启「启动时自动登录程序账号」。</summary>
	public static bool IsAutoUnlock
	{
		get
		{
			LockRecord? record = _record ?? LoadRecord();
			return record != null && record.AutoUnlock && !string.IsNullOrEmpty(record.AutoWrapped);
		}
	}

	/// <summary>开关「启动时自动登录程序账号」（须已解锁）。开启后下次启动不再要求输入；关闭则每次启动都要求登录。</summary>
	public static (bool Ok, string Message) SetAutoUnlock(bool enabled)
	{
		LockRecord? record = _record ?? LoadRecord();
		if (record == null)
		{
			return (false, "尚未创建本机程序账号");
		}
		try
		{
			if (enabled)
			{
				if (!IsUnlocked || _dataKey == null)
				{
					return (false, "请先解锁程序再开启");
				}
				string? machineKeyB64 = SecureStore.Load(MachineKeyName);
				byte[] machineKey;
				if (string.IsNullOrWhiteSpace(machineKeyB64))
				{
					machineKey = RandomNumberGenerator.GetBytes(32);
					SecureStore.Save(MachineKeyName, Convert.ToBase64String(machineKey));
				}
				else
				{
					machineKey = Convert.FromBase64String(machineKeyB64);
				}
				record.AutoWrapped = Convert.ToBase64String(AesEncrypt(machineKey, _dataKey));
				record.AutoUnlock = true;
				LogHelper.Log("[程序锁] 已开启：启动时自动登录程序账号（本机）");
			}
			else
			{
				record.AutoUnlock = false;
				record.AutoWrapped = "";
				SecureStore.Delete(MachineKeyName);
				LogHelper.Log("[程序锁] 已关闭：启动时自动登录程序账号");
			}
			SecureStore.Save(RecordName, JsonSerializer.Serialize(record));
			_record = record;
			return (true, enabled ? "已开启自动登录" : "已关闭自动登录");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[程序锁] 设置自动登录失败");
			return (false, "设置失败：" + ex.Message);
		}
	}

	/// <summary>启动时尝试静默解锁（仅当开启自动登录且本机密钥可用）。返回是否解锁成功。</summary>
	public static bool TryAutoUnlock()
	{
		if (IsUnlocked)
		{
			return true;
		}
		LockRecord? record = _record ?? LoadRecord();
		if (record == null || !record.AutoUnlock || string.IsNullOrEmpty(record.AutoWrapped))
		{
			return false;
		}
		try
		{
			string? machineKeyB64 = SecureStore.Load(MachineKeyName);
			if (string.IsNullOrWhiteSpace(machineKeyB64))
			{
				return false;
			}
			byte[] dataKey = AesDecrypt(Convert.FromBase64String(machineKeyB64), Convert.FromBase64String(record.AutoWrapped));
			if (dataKey.Length != 32)
			{
				return false;
			}
			_record = record;
			_dataKey = dataKey;
			LogHelper.Log("[程序锁] 已自动登录程序账号（本机自动解锁）");
			try
			{
				Unlocked?.Invoke();
			}
			catch
			{
			}
			AccountStore.ReloadFromStore();
			return true;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[程序锁] 自动解锁失败，将要求手动登录");
			return false;
		}
	}

	/// <summary>
	/// 首次启动不建独立程序账号，直接用「可登录的网易账号」创建：
	/// 程序账号名 = 网易账号，程序密码 = 该网易账号密码（同样只存 MD5 单向哈希），
	/// 并默认把该账号指定为网易验证账号（调用方随后登录该账号并保存到账号绑定）。
	/// </summary>
	public static (bool Ok, string Message) CreateAccountFromNetease(string account, string password)
	{
		(bool Ok, string Message) result = CreateAccount(account, password, password);
		if (!result.Ok)
		{
			return result;
		}
		if (_record != null)
		{
			_record.NetAccountPreferred = (account ?? "").Trim();
			_record.NetAccountPreferredList = new List<string>();
			if (!string.IsNullOrWhiteSpace(account))
			{
				_record.NetAccountPreferredList.Add(account.Trim());
			}
			SecureStore.Save(RecordName, JsonSerializer.Serialize(_record));
			LogHelper.Log("[程序锁] 已用网易账号创建程序账号（" + MaskName(account) + "），并指定为验证账号");
		}
		return (true, "已用网易账号创建并登录程序账号");
	}

	/// <summary>验证账号包裹条目。</summary>
	public sealed class NetWrapEntry
	{
		public string Account = "";

		public string WrapSalt = "";

		public string Wrapped = "";
	}

	/// <summary>取验证账号包裹列表（自动从旧单账号字段迁移）。</summary>
	private static List<NetWrapEntry> GetNetWraps(LockRecord record)
	{
		if (record.NetWraps != null)
		{
			return record.NetWraps;
		}
		List<NetWrapEntry> list = new List<NetWrapEntry>();
		if (!string.IsNullOrEmpty(record.NetWrapped))
		{
			list.Add(new NetWrapEntry
			{
				Account = record.NetAccount,
				WrapSalt = record.NetWrapSalt,
				Wrapped = record.NetWrapped
			});
		}
		record.NetWraps = list;
		return list;
	}

	/// <summary>取用户指定的验证账号列表（自动从旧单账号字段迁移）。</summary>
	private static List<string> GetPreferredList(LockRecord record)
	{
		if (record.NetAccountPreferredList != null)
		{
			return record.NetAccountPreferredList;
		}
		List<string> list = new List<string>();
		if (!string.IsNullOrWhiteSpace(record.NetAccountPreferred))
		{
			list.Add(record.NetAccountPreferred.Trim());
		}
		record.NetAccountPreferredList = list;
		return list;
	}

	/// <summary>
	/// 网易账号验证解锁：输入本程序验证账号列表中的网易账号 + 其账密（默认自动含第一个登入账号，上限 3 个）。
	/// 数据密钥有一重用「已绑定网易账密」派生密钥做的额外包裹（RefreshNeteaseWrap 维护），
	/// 输入的账密与之匹配即可在程序锁定态直接解开数据密钥 —— 无需网络、无需程序密码。
	/// 账号名支持后缀容错（如本机存 "hqx@xxx"、输入 "hqx" 也能匹配；派生一律用本机存的完整账号）。
	/// </summary>
	public static (bool Ok, string Message) UnlockByNetease(string account, string password)
	{
		LockRecord? record = _record ?? LoadRecord();
		if (record == null)
		{
			return (false, "尚未创建本机程序账号");
		}
		List<NetWrapEntry> wraps = GetNetWraps(record);
		if (wraps.Count == 0)
		{
			return (false, "本机还没有可用的网易验证通道（解锁一次程序后会自动建立）");
		}
		account = (account ?? "").Trim();
		if (account.Length == 0 || string.IsNullOrEmpty(password))
		{
			return (false, "请输入本程序验证账号的网易账号和密码");
		}
		NetWrapEntry? entry = null;
		foreach (NetWrapEntry w in wraps)
		{
			if (SameAccountName(account, w.Account))
			{
				entry = w;
				break;
			}
		}
		if (entry == null)
		{
			List<string> masked = new List<string>();
			foreach (NetWrapEntry w in wraps)
			{
				masked.Add(MaskName(w.Account));
			}
			return (false, "该账号不在本机验证账号列表中（当前验证账号：" + string.Join("、", masked) + "）");
		}
		try
		{
			// 派生一律用本机存的完整账号名，输入的账号只做身份匹配
			byte[] key = Pbkdf2(Md5Hex(entry.Account + password), Convert.FromBase64String(entry.WrapSalt));
			byte[] dataKey = AesDecrypt(key, Convert.FromBase64String(entry.Wrapped));
			if (dataKey.Length != 32)
			{
				return (false, "数据密钥校验失败");
			}
			_record = record;
			_dataKey = dataKey;
			LogHelper.Log("[程序锁] 已通过网易账号验证解锁（" + MaskName(entry.Account) + "）");
			try
			{
				Unlocked?.Invoke();
			}
			catch
			{
			}
			AccountStore.ReloadFromStore();
			return (true, "解锁成功");
		}
		catch
		{
			return (false, "网易账号账密不正确");
		}
	}

	/// <summary>仅验证不改锁态：程序密码是否正确（改密弹窗用）。兼容早期错误拼接的旧哈希。</summary>
	public static bool VerifyProgramPassword(string pass)
	{
		LockRecord? record = _record ?? LoadRecord();
		if (record == null || string.IsNullOrEmpty(record.VerifyMd5))
		{
			return false;
		}
		byte[] salt = Convert.FromBase64String(record.Salt);
		return PasswordHash(salt, pass) == record.VerifyMd5 || LegacyPasswordHash(salt, pass) == record.VerifyMd5;
	}

	/// <summary>仅验证不改锁态：网易账号账密是否与本机验证账号列表中某个账密一致（改密弹窗用，须已解锁）。账号名支持后缀容错。</summary>
	public static bool VerifyByNetease(string account, string password)
	{
		LockRecord? record = _record ?? LoadRecord();
		if (record == null)
		{
			return false;
		}
		List<NetWrapEntry> wraps = GetNetWraps(record);
		if (wraps.Count == 0)
		{
			return false;
		}
		foreach (NetWrapEntry w in wraps)
		{
			if (!SameAccountName((account ?? "").Trim(), w.Account))
			{
				continue;
			}
			try
			{
				byte[] key = Pbkdf2(Md5Hex(w.Account + (password ?? "")), Convert.FromBase64String(w.WrapSalt));
				byte[] dataKey = AesDecrypt(key, Convert.FromBase64String(w.Wrapped));
				if (dataKey.Length == 32)
				{
					return true;
				}
			}
			catch
			{
			}
		}
		return false;
	}

	/// <summary>
	/// 账号名匹配（后缀容错）：忽略大小写与首尾空白；
	/// 完全相等，或短的一方是长的一方去掉「@/_-#. 空格」后缀后的主体（如 "hqx" ↔ "hqx@163.com"）。
	/// </summary>
	public static bool SameAccountName(string? a, string? b)
	{
		string s1 = (a ?? "").Trim();
		string s2 = (b ?? "").Trim();
		if (s1.Length == 0 || s2.Length == 0)
		{
			return false;
		}
		if (string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string shortName = s1.Length <= s2.Length ? s1 : s2;
		string longName = s1.Length <= s2.Length ? s2 : s1;
		if (longName.Length <= shortName.Length || !longName.StartsWith(shortName, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		char sep = longName[shortName.Length];
		return "@_-#. ".IndexOf(sep) >= 0;
	}

	/// <summary>
	/// 建立/刷新「网易账密 → 数据密钥」的额外包裹（默认自动 1 个、上限 3 个验证账号）：
	/// 用户未指定验证账号时，按登入顺序自动收录带账密账号（前三个登入的账号自动成为验证账号）；
	/// 用户已指定 1-3 个时只建立指定账号的包裹（「取消验证」不会被自动补回来）。
	/// 每个账号用 MD5(账号+账密) 派生密钥把数据密钥单独包一层。解锁后与账密数据变化时自动调用。
	/// </summary>
	public static void RefreshNeteaseWrap()
	{
		if (!IsUnlocked || _record == null)
		{
			return;
		}
		try
		{
			// 全程持锁：与 Lock()（上锁清空数据密钥）互斥，避免重建包裹时密钥被清空
			lock (Sync)
			{
				if (!IsUnlocked || _dataKey == null || _record == null)
				{
					return;
				}
				// 汇总目标账号：指定列表（后缀容错去重）→ 自动补录带账密账号
				List<string> preferred = GetPreferredList(_record);
			List<AccountStore.LocalAccount> targets = new List<AccountStore.LocalAccount>();
			AccountStore.LocalAccount? fallback = null;
			foreach (AccountStore.LocalAccount a in AccountStore.All)
			{
				if (!a.HasCredentials)
				{
					continue;
				}
				if (fallback == null)
				{
					fallback = a;
				}
				foreach (string p in preferred)
				{
					if (targets.Count < 3 && SameAccountName(a.Account, p) && !targets.Any((AccountStore.LocalAccount t) => SameAccountName(t.Account, a.Account)))
					{
						targets.Add(a);
						break;
					}
				}
			}
			// 自动补录：仅当用户未指定任何验证账号时，按登入顺序把带账密账号收进列表（网易/4399 均可）。
			// 用户已指定 1-3 个时不再自动补录 —— 否则刚「取消验证」的账号会被立刻补回来，取消等于没取消。
			bool preferredChanged = false;
			if (preferred.Count == 0)
			{
				foreach (AccountStore.LocalAccount a in AccountStore.All)
				{
					if (targets.Count >= 3)
					{
						break;
					}
					if (!a.HasCredentials || targets.Any((AccountStore.LocalAccount t) => SameAccountName(t.Account, a.Account)))
					{
						continue;
					}
					if (!preferred.Any((string p) => SameAccountName(p, a.Account)))
					{
						preferred.Add(a.Account.Trim());
						preferredChanged = true;
					}
					targets.Add(a);
				}
			}
			if (targets.Count == 0 && fallback != null)
			{
				targets.Add(fallback);
			}
			if (targets.Count == 0)
			{
				return;
			}
			if (preferredChanged)
			{
				SecureStore.Save(RecordName, JsonSerializer.Serialize(_record));
				LogHelper.Log("[程序锁] 已自动补录验证账号（当前 " + preferred.Count + "/3）");
			}
			// 逐个建立包裹
			List<NetWrapEntry> wraps = new List<NetWrapEntry>();
			foreach (AccountStore.LocalAccount a in targets)
			{
				byte[] netSalt = RandomNumberGenerator.GetBytes(16);
				byte[] key = Pbkdf2(Md5Hex(a.Account.Trim() + a.LoginPassPlain), netSalt);
				wraps.Add(new NetWrapEntry
				{
					Account = a.Account.Trim(),
					WrapSalt = Convert.ToBase64String(netSalt),
					Wrapped = Convert.ToBase64String(AesEncrypt(key, _dataKey!))
				});
			}
			_record.NetWraps = wraps;
			// 旧单账号字段镜像第一个（兼容旧读取路径）
			_record.NetAccount = wraps[0].Account;
			_record.NetWrapSalt = wraps[0].WrapSalt;
			_record.NetWrapped = wraps[0].Wrapped;
			SecureStore.Save(RecordName, JsonSerializer.Serialize(_record));
			LogHelper.Log("[程序锁] 已建立网易账号验证通道（" + wraps.Count + " 个：" + string.Join("、", wraps.Select((NetWrapEntry w) => MaskName(w.Account))) + "）");
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[程序锁] 建立网易验证通道失败");
		}
	}

	/// <summary>当前验证账号是否为指定账号之一（账号绑定页角标用）。</summary>
	public static bool IsPreferredNetAccount(string account)
	{
		LockRecord? record = _record ?? LoadRecord();
		if (record == null)
		{
			return false;
		}
		foreach (string p in GetPreferredList(record))
		{
			if (SameAccountName(account, p))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>当前指定的验证账号数量（0 = 自动默认）。</summary>
	public static int PreferredNetAccountCount
	{
		get
		{
			LockRecord? record = _record ?? LoadRecord();
			return record == null ? 0 : GetPreferredList(record).Count;
		}
	}

	/// <summary>
	/// 在「账号绑定」页指定/取消某个网易验证账号（后缀容错）。
	/// 「设为验证」须已解锁；「取消验证」锁定态也允许。上限 3 个；不许全部取消 —— 至少保留一个验证账号
	/// （不指定时默认自动取第一个登入账号）。
	/// </summary>
	public static (bool Ok, string Message) SetPreferredNetAccount(string account, bool add)
	{
		LockRecord? record = _record ?? LoadRecord();
		if (record == null)
		{
			return (false, "本机锁记录不存在");
		}
		// 「设为验证」要把新账号账密纳入解锁链路，必须在解锁态操作；
		// 「取消验证」只是移除，锁定态也允许（至少保留一个的约束不变）。
		if (add && !IsUnlocked)
		{
			return (false, "指定验证账号需先解锁程序（取消验证无需解锁）");
		}
		if (record != _record)
		{
			_record = record;
		}
		List<string> list = GetPreferredList(record);
		string name = (account ?? "").Trim();
		if (add)
		{
			foreach (string p in list)
			{
				if (SameAccountName(p, name))
				{
					return (true, "该账号已在验证账号列表中");
				}
			}
			if (list.Count >= 3)
			{
				return (false, "验证账号最多 3 个（已满），请先取消一个再指定");
			}
			list.Add(name);
			SecureStore.Save(RecordName, JsonSerializer.Serialize(_record));
			RefreshNeteaseWrap();
			return (true, "已指定 " + MaskName(name) + " 为验证账号（" + list.Count + "/3）");
		}
		int removed = list.RemoveAll((string p) => SameAccountName(p, name));
		if (removed == 0)
		{
			return (true, "该账号未在验证账号列表中");
		}
		if (list.Count == 0)
		{
			// 不许全部取消：回滚并提示至少保留一个
			list.Add(name);
			return (false, "至少保留一个验证账号，不能全部取消");
		}
		// 同步移除该账号的验证包裹：锁定态取消也立即生效，该账号不再能用于验证解锁
		List<NetWrapEntry> wraps = GetNetWraps(record);
		wraps.RemoveAll((NetWrapEntry w) => SameAccountName(w.Account, name));
		if (wraps.Count == 0 && !string.IsNullOrEmpty(record.NetWrapped))
		{
			// 兜底：包裹被清空时保留旧镜像，保证至少还有一个验证通道
			wraps.Add(new NetWrapEntry
			{
				Account = record.NetAccount,
				WrapSalt = record.NetWrapSalt,
				Wrapped = record.NetWrapped
			});
		}
		if (wraps.Count > 0)
		{
			// 旧单账号字段镜像第一个（兼容旧读取路径）
			record.NetAccount = wraps[0].Account;
			record.NetWrapSalt = wraps[0].WrapSalt;
			record.NetWrapped = wraps[0].Wrapped;
		}
		SecureStore.Save(RecordName, JsonSerializer.Serialize(_record));
		RefreshNeteaseWrap();
		return (true, "已取消 " + MaskName(name) + " 的验证账号（剩 " + list.Count + " 个）");
	}

	/// <summary>网易验证通道使用的第一个账号名（未建立通道返回 null）。</summary>
	public static string? NetAccountName
	{
		get
		{
			LockRecord? record = _record ?? LoadRecord();
			if (record == null)
			{
				return null;
			}
			List<NetWrapEntry> wraps = GetNetWraps(record);
			return (wraps.Count == 0 ? null : wraps[0].Account);
		}
	}

	/// <summary>
	/// 账号被删除时，同步把该账号从验证账号列表与验证包裹中移除（AccountStore.Remove 调用）。
	/// 与手动「取消验证」不同：账号已物理删除，必须移除且允许列表被清空 ——
	/// 清空后回到「自动默认」模式（下次账号登入时 RefreshNeteaseWrap 自动补录）。
	/// 否则会残留已删账号的验证标记：该号已不在账号列表里，「设为验证/取消验证」都碰不到它，
	/// 但它的包裹仍能用于解锁，且占用 3 个验证账号名额。
	/// </summary>
	public static void RemoveAccountFromVerification(string account)
	{
		try
		{
			string name = (account ?? "").Trim();
			if (string.IsNullOrWhiteSpace(name))
			{
				return;
			}
			LockRecord? record = _record ?? LoadRecord();
			if (record == null)
			{
				return;
			}
			if (record != _record)
			{
				_record = record;
			}
			List<string> list = GetPreferredList(record);
			int removedPref = list.RemoveAll((string p) => SameAccountName(p, name));
			if (list.Count == 0 && removedPref > 0)
			{
				// 指定列表被清空：回到自动默认模式，不残留任何已删账号
				record.NetAccountPreferred = "";
				record.NetAccountPreferredList = null;
			}
			List<NetWrapEntry> wraps = GetNetWraps(record);
			int removedWrap = wraps.RemoveAll((NetWrapEntry w) => SameAccountName(w.Account, name));
			if (wraps.Count > 0)
			{
				// 旧单账号字段镜像第一个（兼容旧读取路径）
				record.NetAccount = wraps[0].Account;
				record.NetWrapSalt = wraps[0].WrapSalt;
				record.NetWrapped = wraps[0].Wrapped;
			}
			else if (removedWrap > 0)
			{
				// 包裹全没了（被删账号是最后一个验证账号）：清空旧镜像，
				// 该账号不得再用于解锁；下次账号登入时 RefreshNeteaseWrap 自动重建验证通道
				record.NetAccount = "";
				record.NetWrapSalt = "";
				record.NetWrapped = "";
			}
			if (removedPref > 0 || removedWrap > 0)
			{
				SecureStore.Save(RecordName, JsonSerializer.Serialize(_record));
				RefreshNeteaseWrap();
				LogHelper.Log("[程序锁] 账号 " + MaskName(name) + " 已删除，同步移除其验证账号记录与验证包裹");
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[程序锁] 删除账号后清理验证账号记录失败");
		}
	}

	/// <summary>清空全部账号时，把整个验证账号指定列表与包裹全部移除（回到自动默认模式）。</summary>
	public static void ClearNetVerification()
	{
		try
		{
			LockRecord? record = _record ?? LoadRecord();
			if (record == null)
			{
				return;
			}
			if (record != _record)
			{
				_record = record;
			}
			bool had = !string.IsNullOrEmpty(record.NetAccountPreferred)
				|| (record.NetAccountPreferredList != null && record.NetAccountPreferredList.Count > 0)
				|| !string.IsNullOrEmpty(record.NetWrapped);
			record.NetAccountPreferred = "";
			record.NetAccountPreferredList = null;
			record.NetAccount = "";
			record.NetWrapSalt = "";
			record.NetWrapped = "";
			if (had)
			{
				SecureStore.Save(RecordName, JsonSerializer.Serialize(_record));
				RefreshNeteaseWrap();
				LogHelper.Log("[程序锁] 已清空全部账号，验证账号记录与验证包裹已同步清除（下次登入自动重建）");
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[程序锁] 清空验证账号记录失败");
		}
	}

	/// <summary>验证账号的掩码列表（如 "hq***、te***"），避免弹窗里直接暴露完整账号。</summary>
	public static string NetAccountHint
	{
		get
		{
			LockRecord? record = _record ?? LoadRecord();
			if (record == null)
			{
				return "";
			}
			List<NetWrapEntry> wraps = GetNetWraps(record);
			List<string> masked = new List<string>();
			foreach (NetWrapEntry w in wraps)
			{
				masked.Add(MaskName(w.Account));
			}
			return string.Join("、", masked);
		}
	}

	private static string MaskName(string? name)
	{
		if (string.IsNullOrEmpty(name) || name.Length <= 2)
		{
			return "***";
		}
		return name.Substring(0, 2) + "***";
	}

	/// <summary>更改程序账号密码（须已解锁）。验证身份由调用方完成（程序密码或网易账号登录）。</summary>
	public static (bool Ok, string Message) ChangePassword(string newPass, string confirm)
	{
		if (!IsUnlocked || _record == null)
		{
			return (false, "程序未解锁，无法更改密码");
		}
		if (string.IsNullOrEmpty(newPass) || newPass.Length < 4)
		{
			return (false, "新密码至少 4 位");
		}
		if (newPass != confirm)
		{
			return (false, "两次输入的新密码不一致");
		}
		try
		{
			byte[] salt = RandomNumberGenerator.GetBytes(16);
			byte[] wrapSalt = RandomNumberGenerator.GetBytes(16);
			_record.Salt = Convert.ToBase64String(salt);
			_record.VerifyMd5 = PasswordHash(salt, newPass);
			_record.Verify = "";
			_record.WrapSalt = Convert.ToBase64String(wrapSalt);
			_record.Wrapped = Convert.ToBase64String(AesEncrypt(Pbkdf2(Md5Hex(newPass), wrapSalt), _dataKey!));
			SecureStore.Save(RecordName, JsonSerializer.Serialize(_record));
			LogHelper.Log("[程序锁] 程序账号密码已更改（新 MD5 验证值 + 新包裹密钥）");
			return (true, "密码已更改");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[程序锁] 更改密码失败");
			return (false, "更改失败：" + ex.Message);
		}
	}

	// ---------- 账密敏感字段二次加密（数据密钥层） ----------
	/// <summary>用数据密钥加密敏感字段（"ENC2:" 前缀）。未解锁或失败时原样返回，由调用方保证在解锁态调用。</summary>
	public static string VaultEncrypt(string plain)
	{
		if (string.IsNullOrEmpty(plain))
		{
			return "";
		}
		byte[]? key = _dataKey;
		if (key == null)
		{
			return plain;
		}
		try
		{
			return VaultPrefix + Convert.ToBase64String(AesEncrypt(key, Encoding.UTF8.GetBytes(plain)));
		}
		catch
		{
			return plain;
		}
	}

	/// <summary>解 "ENC2:" 密文；无前缀的旧数据原样返回（自动兼容），未解锁返回空串。</summary>
	public static string VaultDecrypt(string stored)
	{
		if (string.IsNullOrEmpty(stored))
		{
			return "";
		}
		if (!stored.StartsWith(VaultPrefix, StringComparison.Ordinal))
		{
			return stored;
		}
		byte[]? key = _dataKey;
		if (key == null)
		{
			return "";
		}
		try
		{
			byte[] data = Convert.FromBase64String(stored.Substring(VaultPrefix.Length));
			byte[] plain = AesDecrypt(key, data);
			return Encoding.UTF8.GetString(plain);
		}
		catch
		{
			return "";
		}
	}

	// ---------- 内部 ----------

	private static LockRecord? LoadRecord()
	{
		try
		{
			string? text = SecureStore.Load(RecordName);
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}
			return JsonSerializer.Deserialize<LockRecord>(text);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[程序锁] 读取账号记录失败");
			return null;
		}
	}

	private static byte[] Pbkdf2(string pass, byte[] salt)
	{
		using Rfc2898DeriveBytes deriver = new Rfc2898DeriveBytes(pass, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
		return deriver.GetBytes(32);
	}

	private static string Md5Hex(string text)
	{
		using MD5 md5 = MD5.Create();
		return Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(text)));
	}

	// 统一的密码哈希：MD5(salt文本 + 密码)
	// 注意：早期构建误写成 Md5Hex(salt + pass)（byte[] 被装箱成 "System.Byte[]" 字符串），
	// 造成"解锁正常但改密验证永远失败"。现统一为正确拼接，并保留旧值兼容。
	private static string PasswordHash(byte[] salt, string pass)
	{
		return Md5Hex(Encoding.UTF8.GetString(salt) + pass);
	}

	private static string LegacyPasswordHash(byte[] salt, string pass)
	{
		return Md5Hex("System.Byte[]" + pass);
	}

	private static byte[] AesEncrypt(byte[] key, byte[] plain)
	{
		using Aes aes = Aes.Create();
		aes.Key = key;
		aes.GenerateIV();
		using MemoryStream ms = new MemoryStream();
		ms.Write(aes.IV);
		using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
		{
			cs.Write(plain, 0, plain.Length);
		}
		return ms.ToArray();
	}

	private static byte[] AesDecrypt(byte[] key, byte[] data)
	{
		if (data.Length <= 16)
		{
			throw new InvalidDataException("密文长度不合法");
		}
		using Aes aes = Aes.Create();
		aes.Key = key;
		aes.IV = data.AsSpan(0, 16).ToArray();
		using MemoryStream ms = new MemoryStream(data, 16, data.Length - 16);
		using CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
		using MemoryStream output = new MemoryStream();
		cs.CopyTo(output);
		return output.ToArray();
	}
}

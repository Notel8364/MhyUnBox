using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MhyunBox.Services;

/// <summary>
/// 敏感数据密钥提供者（原「本机锁 AppLock」瘦身后）。
/// 用户定版（2026-11-01）：本机锁（程序账号/解锁弹窗/网易验证通道）机制整体移除，
/// 程序启动不再要求解锁，账号数据不再有锁定门控。
/// 保留数据密钥层：Cookie/Token/EntityId 落盘前经 "ENC2:" 加密（AccountStore 调用），
/// 密钥为本机随机生成、首启动时持久化到 SecureStore，之后跨启动复用，不依赖任何解锁动作。
/// 旧版（程序锁时代）用"口令派生密钥"加密的历史数据无法被新密钥解开：
/// 解密失败一律按空处理，失效会话由自动登录链路自行恢复。
/// </summary>
public static class AppLock
{
	private const string VaultPrefix = "ENC2:";

	private const string DataKeyName = "datakey_v2";

	// 旧「本机锁」时代的遗留存储名（用户定版 2026-11-01 移除本机锁）：
	// 开启过「启动时自动登录程序账号」的用户，其数据密钥被机器密钥包裹存放在旧记录里——
	// 首次升级到无锁版本时从这里恢复，旧账号的 Cookie/Token（ENC2）得以解密，不至全部失效。
	private const string LegacyRecordName = "applock_record";

	private const string LegacyMachineKeyName = "applock_machinekey";

	private static byte[]? _dataKey;

	/// <summary>全部可用密钥（主密钥在前）：解密时依次尝试，兼容密钥切换期间的混合密文。</summary>
	private static List<byte[]> _keys = new List<byte[]>();

	private static readonly object Sync = new object();

	/// <summary>
	/// 加载本机数据密钥：恢复的旧程序锁密钥优先做主密钥，此前无锁版本生成过的密钥保留做回退
	/// （新版运行期间重登保存的数据用它加密）；都没有才随机生成。
	/// </summary>
	private static byte[] EnsureDataKey()
	{
		if (_dataKey != null)
		{
			return _dataKey;
		}
		lock (Sync)
		{
			if (_dataKey != null)
			{
				return _dataKey;
			}
			List<byte[]> keys = new List<byte[]>();
			byte[]? legacy = TryRecoverLegacyDataKey();
			if (legacy != null)
			{
				keys.Add(legacy);
			}
			// 读持久化密钥（兼容两种格式：单值 base64 = 旧版单密钥；JSON 字符串数组 = 多密钥回退链）
			try
			{
				string? b64 = SecureStore.Load(DataKeyName);
				if (!string.IsNullOrWhiteSpace(b64))
				{
					if (b64.TrimStart().StartsWith("[", StringComparison.Ordinal))
					{
						using JsonDocument doc = JsonDocument.Parse(b64);
						foreach (JsonElement item in doc.RootElement.EnumerateArray())
						{
							if (item.ValueKind == JsonValueKind.String && item.GetString() is string kb && kb.Length > 0)
							{
								try
								{
									byte[] key = Convert.FromBase64String(kb);
									if (key.Length == 32 && !keys.Any((byte[] k) => k.SequenceEqual(key)))
									{
										keys.Add(key);
									}
								}
								catch
								{
								}
							}
						}
					}
					else
					{
						byte[] key = Convert.FromBase64String(b64);
						if (key.Length == 32 && !keys.Any((byte[] k) => k.SequenceEqual(key)))
						{
							keys.Add(key);
						}
					}
				}
			}
			catch
			{
			}
			if (keys.Count == 0)
			{
				byte[] generated = RandomNumberGenerator.GetBytes(32);
				keys.Add(generated);
				LogHelper.Log("[数据密钥] 已生成本机数据密钥（Cookie/Token 加密落盘用，跨启动复用）");
			}
			try
			{
				// 全部密钥按主次顺序持久化（主密钥在前），下次启动回退链不丢
				SecureStore.Save(DataKeyName, JsonSerializer.Serialize(keys.Select((byte[] k) => Convert.ToBase64String(k)).ToList()));
				if (legacy != null)
				{
					LogHelper.Log("[数据密钥] 已从旧程序锁记录恢复原数据密钥为主密钥（旧账号会话全部可用" + ((keys.Count > 1) ? "；无锁版本期间的密钥保留做解密回退）" : "）"));
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "[数据密钥] 持久化失败（密钥仅本次会话内有效）");
			}
			_keys = keys;
			_dataKey = keys[0];
			return _dataKey;
		}
	}

	/// <summary>从旧程序锁记录恢复数据密钥：record.AutoWrapped = AES(机器密钥, 旧数据密钥)。</summary>
	private static byte[]? TryRecoverLegacyDataKey()
	{
		try
		{
			string? recordJson = SecureStore.Load(LegacyRecordName);
			if (string.IsNullOrWhiteSpace(recordJson))
			{
				return null;
			}
			using JsonDocument doc = JsonDocument.Parse(recordJson);
			if (!doc.RootElement.TryGetProperty("AutoWrapped", out JsonElement aw) || aw.ValueKind != JsonValueKind.String)
			{
				return null;
			}
			string? autoWrapped = aw.GetString();
			if (string.IsNullOrEmpty(autoWrapped))
			{
				return null;
			}
			string? machineKeyB64 = SecureStore.Load(LegacyMachineKeyName);
			if (string.IsNullOrWhiteSpace(machineKeyB64))
			{
				return null;
			}
			byte[] machineKey = Convert.FromBase64String(machineKeyB64);
			byte[] dataKey = AesDecrypt(machineKey, Convert.FromBase64String(autoWrapped));
			return (dataKey.Length == 32) ? dataKey : null;
		}
		catch
		{
			return null;
		}
	}

	// ---------- 账密敏感字段二次加密（数据密钥层） ----------

	/// <summary>用数据密钥加密敏感字段（"ENC2:" 前缀）。失败时原样返回。</summary>
	public static string VaultEncrypt(string plain)
	{
		if (string.IsNullOrEmpty(plain))
		{
			return "";
		}
		try
		{
			return VaultPrefix + Convert.ToBase64String(AesEncrypt(EnsureDataKey(), Encoding.UTF8.GetBytes(plain)));
		}
		catch
		{
			return plain;
		}
	}

	/// <summary>解 "ENC2:" 密文；无前缀的旧数据原样返回（自动兼容）；依次尝试全部可用密钥（兼容密钥切换期间的混合密文），全部失败返回空串。</summary>
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
		EnsureDataKey();
		byte[] data;
		try
		{
			data = Convert.FromBase64String(stored.Substring(VaultPrefix.Length));
		}
		catch
		{
			return "";
		}
		foreach (byte[] key in _keys)
		{
			try
			{
				byte[] plain = AesDecrypt(key, data);
				return Encoding.UTF8.GetString(plain);
			}
			catch
			{
			}
		}
		return "";
	}

	// ---------- 内部 ----------

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

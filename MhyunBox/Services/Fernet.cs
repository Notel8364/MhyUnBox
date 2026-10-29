using System;
using System.Security.Cryptography;

namespace MhyunBox.Services;

/// <summary>
/// Fernet（对称认证加密）兼容实现，与 Python cryptography 库的 Fernet 完全互通。
/// token 结构：base64url( 0x80 || timestamp(8B 大端) || iv(16B) || ciphertext(AES-128-CBC/PKCS7) || hmac-sha256(32B) )
/// key(32B) = signing_key(16B) || encryption_key(16B)
/// 用于 404Launcher CrcSalt 的 3 层加密剥层（404-crc-salt-layer-N:&lt;token&gt; 派生密钥）。
/// </summary>
public static class Fernet
{
	private const byte VersionByte = 128;

	/// <summary>Base64Url 解码（容忍缺失 padding 与 -_ 字符）。</summary>
	public static byte[] Base64UrlDecode(string text)
	{
		string s = text.Replace('-', '+').Replace('_', '/');
		s = s.TrimEnd('=');
		int pad = (4 - s.Length % 4) % 4;
		s = s.PadRight(s.Length + pad, '=');
		return Convert.FromBase64String(s);
	}

	/// <summary>
	/// Base64Url 编码。
	/// 注意：Fernet 规范要求保留 '=' padding（Python cryptography 的 Fernet 在
	/// 构造密钥与解密 token 时都直接调用 base64.urlsafe_b64decode，缺 padding 会抛
	/// "Incorrect padding"）。因此这里不做 TrimEnd('=')，与 404 服务端（Python 侧）完全一致。
	/// 解码端 <see cref="Base64UrlDecode"/> 同时容忍有无 padding。
	/// </summary>
	public static string Base64UrlEncode(byte[] data)
	{
		return Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_');
	}

	/// <summary>按种子串派生 32 字节 Fernet 密钥（sha256）。</summary>
	public static byte[] DeriveKey(string seed)
	{
		return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
	}

	/// <summary>解密 Fernet token；HMAC 校验失败或格式非法返回 false。</summary>
	public static bool TryDecrypt(byte[] key, string token, out byte[]? plaintext)
	{
		plaintext = null;
		if (key == null || key.Length != 32 || string.IsNullOrWhiteSpace(token))
		{
			return false;
		}
		byte[] data;
		try
		{
			data = Base64UrlDecode(token.Trim());
		}
		catch (FormatException)
		{
			return false;
		}
		if (data.Length < 1 + 8 + 16 + 16 + 32)
		{
			return false;
		}
		if (data[0] != VersionByte)
		{
			return false;
		}
		ReadOnlySpan<byte> span = data.AsSpan();
		ReadOnlySpan<byte> signed = span.Slice(0, data.Length - 32);
		ReadOnlySpan<byte> hmac = span.Slice(data.Length - 32);
		byte[] expected;
		using (HMACSHA256 h = new HMACSHA256(key.AsSpan(0, 16).ToArray()))
		{
			expected = h.ComputeHash(signed.ToArray());
		}
		if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected.AsSpan(), hmac))
		{
			return false;
		}
		byte[] iv = span.Slice(9, 16).ToArray();
		byte[] ciphertext = span.Slice(25, data.Length - 25 - 32).ToArray();
		try
		{
			using Aes aes = Aes.Create();
			aes.Key = key.AsSpan(16, 16).ToArray();
			aes.IV = iv;
			aes.Mode = CipherMode.CBC;
			aes.Padding = PaddingMode.PKCS7;
			using ICryptoTransform decryptor = aes.CreateDecryptor();
			plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
			return true;
		}
		catch (CryptographicException)
		{
			plaintext = null;
			return false;
		}
	}

	/// <summary>加密生成 Fernet token（调试/联调用）。</summary>
	public static string Encrypt(byte[] key, byte[] plaintext, long unixTimestamp = 0)
	{
		if (key == null || key.Length != 32)
		{
			throw new ArgumentException("Fernet key must be 32 bytes", "key");
		}
		byte[] iv = RandomNumberGenerator.GetBytes(16);
		byte[] cipher;
		using (Aes aes = Aes.Create())
		{
			aes.Key = key.AsSpan(16, 16).ToArray();
			aes.IV = iv;
			aes.Mode = CipherMode.CBC;
			aes.Padding = PaddingMode.PKCS7;
			using ICryptoTransform enc = aes.CreateEncryptor();
			cipher = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
		}
		byte[] ts = BitConverter.GetBytes(unixTimestamp);
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(ts);
		}
		byte[] body = new byte[1 + 8 + 16 + cipher.Length];
		body[0] = VersionByte;
		Array.Copy(ts, 0, body, 1, 8);
		Array.Copy(iv, 0, body, 9, 16);
		Array.Copy(cipher, 0, body, 25, cipher.Length);
		byte[] sig;
		using (HMACSHA256 h = new HMACSHA256(key.AsSpan(0, 16).ToArray()))
		{
			sig = h.ComputeHash(body);
		}
		byte[] tokenBytes = new byte[body.Length + 32];
		Array.Copy(body, 0, tokenBytes, 0, body.Length);
		Array.Copy(sig, 0, tokenBytes, body.Length, 32);
		return Base64UrlEncode(tokenBytes);
	}
}
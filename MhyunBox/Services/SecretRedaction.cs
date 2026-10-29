using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MhyunBox.Services;

/// <summary>
/// 日志 / 诊断报告中的敏感字节脱敏工具。
/// 令牌只以长度和指纹形式出现，绝不输出原文。
/// </summary>
internal static class SecretRedaction
{
	private const string Placeholder = "[已脱敏]";

	/// <summary>至少 8 字节的连续匹配才视为敏感令牌，避免误伤普通头部字节。</summary>
	private const int MinSecretHexChars = 16;

	/// <summary>把二进制数据转成可安全展示的十六进制摘要（超长部分截断）。</summary>
	public static string Hex(byte[] data, int maxBytes, params byte[][]? secrets)
	{
		if (data == null || data.Length == 0 || maxBytes <= 0)
		{
			return "";
		}
		string hex = Convert.ToHexString(data, 0, Math.Min(maxBytes, data.Length));
		if (secrets == null || secrets.Length == 0)
		{
			return hex;
		}
		List<string> patterns = new List<string>();
		foreach (byte[]? secret in secrets)
		{
			patterns.AddRange(TokenPatterns(secret));
		}
		return RedactHex(hex, patterns);
	}

	/// <summary>对已经是十六进制文本的报文做脱敏，再截断到 <paramref name="maxChars"/> 个字符。</summary>
	public static string HexText(string hex, int maxChars, params string[] secretHexes)
	{
		if (string.IsNullOrEmpty(hex) || maxChars <= 0)
		{
			return "";
		}
		int take = Math.Min(maxChars, hex.Length);
		if ((take & 1) != 0)
		{
			take--;
		}
		string shown = hex.Substring(0, take);
		List<string> patterns = new List<string>();
		if (secretHexes != null)
		{
			foreach (string secret in secretHexes)
			{
				if (!string.IsNullOrEmpty(secret) && secret.Length >= MinSecretHexChars && (secret.Length & 1) == 0)
				{
					patterns.Add(secret);
				}
			}
		}
		return RedactHex(shown, patterns);
	}

	/// <summary>令牌指纹：长度 + SHA-256 前 4 字节，足够区分候选但无法还原令牌。</summary>
	public static string TokenFingerprint(string tokenHex)
	{
		if (string.IsNullOrEmpty(tokenHex))
		{
			return "<空>";
		}
		try
		{
			byte[] bytes = Convert.FromHexString(tokenHex);
			byte[] hash = SHA256.HashData(bytes);
			return $"{bytes.Length}B sha256:{Convert.ToHexString(hash, 0, 4)}";
		}
		catch
		{
			return "<无效>";
		}
	}

	/// <summary>任意字符串的指纹：长度 + SHA-256 前 4 字节，用于日志里替代会话 ID / 哈希原文。</summary>
	public static string Fingerprint(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "<空>";
		}
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
		return $"len={value.Length} sha256:{Convert.ToHexString(hash, 0, 4)}";
	}

	/// <summary>令牌的原始、换半（ChaCha 发密钥形态）、ISO-8859-1→UTF-8 三种报文形态。</summary>
	public static IEnumerable<string> TokenPatterns(byte[]? secret)
	{
		if (secret == null || secret.Length < 8)
		{
			yield break;
		}
		yield return Convert.ToHexString(secret);
		if ((secret.Length & 1) == 0)
		{
			int half = secret.Length / 2;
			byte[] swapped = new byte[secret.Length];
			Array.Copy(secret, half, swapped, 0, half);
			Array.Copy(secret, 0, swapped, half, half);
			yield return Convert.ToHexString(swapped);
		}
		byte[]? utf8 = null;
		try
		{
			utf8 = Encoding.Convert(Encoding.GetEncoding("ISO-8859-1"), Encoding.UTF8, secret);
		}
		catch
		{
		}
		if (utf8 != null && utf8.Length >= 8)
		{
			yield return Convert.ToHexString(utf8);
		}
	}

	private static string RedactHex(string hex, List<string> patterns)
	{
		if (patterns.Count == 0)
		{
			return hex;
		}
		// 截断可能把令牌切成半截：把与任一模式前缀匹配的长尾也脱敏。
		int maskFrom = hex.Length;
		foreach (string pattern in patterns)
		{
			int max = Math.Min(hex.Length, pattern.Length);
			if ((max & 1) != 0)
			{
				max--;
			}
			for (int len = max; len >= MinSecretHexChars; len -= 2)
			{
				if (string.Compare(hex, hex.Length - len, pattern, 0, len, StringComparison.OrdinalIgnoreCase) == 0)
				{
					maskFrom = Math.Min(maskFrom, hex.Length - len);
					break;
				}
			}
		}
		if (maskFrom < hex.Length)
		{
			hex = hex.Substring(0, maskFrom) + Placeholder;
		}
		// 先替换长模式，避免短模式（例如令牌本身）先命中后留下同一载荷的其他敏感字节。
		foreach (string pattern in patterns.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending((string p) => p.Length))
		{
			hex = ReplaceIgnoreCase(hex, pattern, Placeholder);
		}
		return hex;
	}

	private static string ReplaceIgnoreCase(string text, string pattern, string replacement)
	{
		int index = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
		if (index < 0)
		{
			return text;
		}
		StringBuilder sb = new StringBuilder(text.Length);
		int pos = 0;
		while (index >= 0)
		{
			sb.Append(text, pos, index - pos).Append(replacement);
			pos = index + pattern.Length;
			index = text.IndexOf(pattern, pos, StringComparison.OrdinalIgnoreCase);
		}
		sb.Append(text, pos, text.Length - pos);
		return sb.ToString();
	}
}

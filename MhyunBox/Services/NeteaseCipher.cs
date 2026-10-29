using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MhyunBox.Services;

public static class NeteaseCipher
{
	private static readonly byte[][] HttpKeys = (from k in SecretBox.DecodeString("FR5tDTYWEgshJBMWDBYet3QaLyUmCgMvCkcSCxQIGuU2eQ0uPQ4LUwMHIxMtGxnON2x3AhYjCjcpIC8YNx5Msw1lEUw5EwQsHCEBEzRIH+cADRcwcyogDQoIHwATFQnKCWEDLm5KDytfGURFNE4p0xcgb1JoMUkiBiMTIx0eHbY/DyICCyIOSjQ2LhQMKSjYDSM4BBQXAT9CMCwFNxxLzA8PKSovITdTOV0XHwkMHuIQImhYCwU2Hz4kWiE1Ti3uLCYrAjUOHCAKJUZeKipI5TA/EA4bIQk1JAUUMFAvJMUCLW8rNzwcET9DAhESVjLIE2JoOhEQKxcUBxdGFx5Q1j08DFcuM1JQWCBHKy0gNQ==").Split(',')
		select Encoding.ASCII.GetBytes(k)).ToArray();

	private static readonly char[] AsciiLetters = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();

	private static readonly string TokenSalt = SecretBox.DecodeString("aDAcEx0NDQo=");

	public static byte[]? HttpDecrypt(byte[] body)
	{
		return HttpDecrypt(body, HttpKeys);
	}

	public static byte[]? HttpDecrypt(byte[] body, byte[][] keys)
	{
		if (body.Length < 18)
		{
			return null;
		}
		int num = (body[^1] >> 4) & 0xF;
		byte[] rgbIV = body.Take(16).ToArray();
		byte[] array = body.Skip(16).Take(body.Length - 1 - 16).ToArray();
		using Aes aes = Aes.Create();
		aes.Padding = PaddingMode.None;
		using ICryptoTransform cryptoTransform = aes.CreateDecryptor(keys[num], rgbIV);
		byte[] array2 = cryptoTransform.TransformFinalBlock(array, 0, array.Length);
		int num2 = 0;
		int num3 = array2.Length - 1;
		while (num2 < 16 && num3 >= 0)
		{
			if (array2[num3--] != 0)
			{
				num2++;
			}
		}
		return array2.Take(Math.Max(0, num3 + 1)).ToArray();
	}

	public static byte[] HttpEncrypt(byte[] bodyIn)
	{
		return HttpEncrypt(bodyIn, HttpKeys, CipherMode.CBC);
	}

	public static byte[] HttpEncrypt(byte[] bodyIn, byte[][] keys, CipherMode mode)
	{
		byte[] bytes = Encoding.ASCII.GetBytes(RandomAscii(16));
		byte[] array = new byte[(int)(Math.Ceiling((double)(bodyIn.Length + 16) / 16.0) * 16.0)];
		Array.Copy(bodyIn, array, bodyIn.Length);
		Array.Copy(bytes, 0, array, bodyIn.Length, bytes.Length);
		int num = Random.Shared.Next(0, keys.Length);
		byte[] array2;
		using (Aes aes = Aes.Create())
		{
			aes.Mode = mode;
			aes.Padding = PaddingMode.None;
			using ICryptoTransform cryptoTransform = aes.CreateEncryptor(keys[num], bytes);
			array2 = cryptoTransform.TransformFinalBlock(array, 0, array.Length);
		}
		byte[] array3 = new byte[16 + array2.Length + 1];
		Array.Copy(bytes, array3, 16);
		Array.Copy(array2, 0, array3, 16, array2.Length);
		array3[^1] = (byte)((num << 4) | 2);
		return array3;
	}

	public static string RandomAscii(int length)
	{
		StringBuilder stringBuilder = new StringBuilder(length);
		for (int i = 0; i < length; i++)
		{
			stringBuilder.Append(AsciiLetters[Random.Shared.Next(AsciiLetters.Length)]);
		}
		return stringBuilder.ToString();
	}

	public static string RandomHex(int length)
	{
		StringBuilder stringBuilder = new StringBuilder(length);
		for (int i = 0; i < length; i++)
		{
			stringBuilder.Append("0123456789ABCDEF"[Random.Shared.Next("0123456789ABCDEF".Length)]);
		}
		return stringBuilder.ToString();
	}

	public static byte[] MpayEncrypt(string jsonPayload, byte[] deviceKey)
	{
		using Aes aes = Aes.Create();
		aes.Mode = CipherMode.ECB;
		aes.Padding = PaddingMode.PKCS7;
		using ICryptoTransform cryptoTransform = aes.CreateEncryptor(deviceKey, null);
		return cryptoTransform.TransformFinalBlock(Encoding.UTF8.GetBytes(jsonPayload), 0, jsonPayload.Length);
	}

	public static byte[]? HexToBytes(string hex)
	{
		try
		{
			return Convert.FromHexString(hex);
		}
		catch
		{
			return null;
		}
	}

	public static string Md5HexLower(string input)
	{
		return NetShared.Md5Hex(input);
	}

	public static Dictionary<string, string> ComputeHttpRequestToken(string requestPath, string sendBody, string userId, string userToken)
	{
		requestPath = (requestPath.StartsWith('/') ? requestPath : ("/" + requestPath));
		string s = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(userToken))).ToLowerInvariant();
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.Write(Encoding.UTF8.GetBytes(s));
		memoryStream.Write(Encoding.UTF8.GetBytes(sendBody));
		memoryStream.Write(Encoding.UTF8.GetBytes(TokenSalt));
		memoryStream.Write(Encoding.UTF8.GetBytes(requestPath));
		string text = Md5HexLower(Encoding.UTF8.GetString(memoryStream.ToArray()));
		string text2 = NetShared.HexToBinary(text);
		text2 = text2.Substring(6, text2.Length - 6) + text2.Substring(0, 6);
		byte[] bytes = Encoding.UTF8.GetBytes(text);
		ProcessBinaryBlock(text2, bytes);
		string value = (Convert.ToBase64String(bytes, 0, 12) + "1").Replace('+', 'm').Replace('/', 'o');
		return new Dictionary<string, string>
		{
			["user-id"] = userId,
			["user-token"] = value
		};
	}

	private static void ProcessBinaryBlock(string secretBin, byte[] httpToken)
	{
		for (int i = 0; i < secretBin.Length / 8; i++)
		{
			ReadOnlySpan<char> readOnlySpan = secretBin.AsSpan(i * 8, Math.Min(8, secretBin.Length - i * 8));
			byte b = 0;
			for (int j = 0; j < readOnlySpan.Length; j++)
			{
				if (readOnlySpan[7 - j] == '1')
				{
					b |= (byte)(1 << j);
				}
			}
			httpToken[i] ^= b;
		}
	}
}

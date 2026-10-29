using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MhyunBox.Services;

internal static class NetShared
{
	public static string FormUrlEncode(Dictionary<string, string> parameters)
	{
		List<string> list = new List<string>(parameters.Count);
		foreach (KeyValuePair<string, string> parameter in parameters)
		{
			list.Add(Uri.EscapeDataString(parameter.Key) + "=" + Uri.EscapeDataString(parameter.Value));
		}
		return string.Join("&", list);
	}

	public static string HexToBinary(string hexString)
	{
		StringBuilder stringBuilder = new StringBuilder(hexString.Length * 4);
		foreach (char c in hexString)
		{
			stringBuilder.Append(Convert.ToString(c, 2).PadLeft(8, '0'));
		}
		return stringBuilder.ToString();
	}

	public static string Md5Hex(string input)
	{
		return Md5Hex(Encoding.UTF8.GetBytes(input));
	}

	public static string Md5Hex(byte[] input)
	{
		return Convert.ToHexString(MD5.HashData(input)).ToLowerInvariant();
	}

	public static string EscapeJson(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder(value.Length + 16);
		foreach (char c in value)
		{
			switch (c)
			{
			case '"':
				stringBuilder.Append("\\\"");
				continue;
			case '\\':
				stringBuilder.Append("\\\\");
				continue;
			case '\n':
				stringBuilder.Append("\\n");
				continue;
			case '\r':
				stringBuilder.Append("\\r");
				continue;
			case '\t':
				stringBuilder.Append("\\t");
				continue;
			}
			if (c < ' ')
			{
				StringBuilder stringBuilder2 = stringBuilder.Append("\\u");
				int num = c;
				stringBuilder2.Append(num.ToString("x4"));
			}
			else
			{
				stringBuilder.Append(c);
			}
		}
		return stringBuilder.ToString();
	}

	public static int ReadVarInt(byte[] data, ref int pos)
	{
		int num = 0;
		int num2 = 0;
		do
		{
			if (pos >= data.Length)
			{
				throw new EndOfStreamException();
			}
			byte b = data[pos++];
			num |= (b & 0x7F) << num2;
			if ((b & 0x80) == 0)
			{
				return num;
			}
			num2 += 7;
		}
		while (num2 <= 35);
		throw new InvalidDataException("varint too long");
	}

	public static void WriteVarInt(Stream ms, int value)
	{
		while ((value & -128) != 0)
		{
			ms.WriteByte((byte)((value & 0x7F) | 0x80));
			value >>>= 7;
		}
		ms.WriteByte((byte)value);
	}

	public static byte[] WriteVarIntBytes(int value)
	{
		using MemoryStream memoryStream = new MemoryStream();
		WriteVarInt(memoryStream, value);
		return memoryStream.ToArray();
	}

	public static string ReadString(byte[] data, ref int pos)
	{
		int num = ReadVarInt(data, ref pos);
		if (num < 0 || pos + num > data.Length)
		{
			throw new EndOfStreamException();
		}
		string result = Encoding.UTF8.GetString(data, pos, num);
		pos += num;
		return result;
	}

	public static byte[] ReadBytes(byte[] data, ref int pos)
	{
		int num = ReadVarInt(data, ref pos);
		if (num < 0 || pos + num > data.Length)
		{
			throw new EndOfStreamException();
		}
		byte[] array = new byte[num];
		Array.Copy(data, pos, array, 0, num);
		pos += num;
		return array;
	}

	public static void WriteString(Stream ms, string s)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
		WriteBytes(ms, bytes);
	}

	public static void WriteBytes(Stream ms, byte[] bytes)
	{
		WriteVarInt(ms, bytes.Length);
		ms.Write(bytes, 0, bytes.Length);
	}
}

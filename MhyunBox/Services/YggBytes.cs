using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MhyunBox.Services;

internal static class YggBytes
{
	public static byte[] EncodeSha256(this byte[] input)
	{
		return SHA256.HashData(input);
	}

	public static byte[] ToByteArray(this int value, bool littleEndian = true)
	{
		byte[] bytes = BitConverter.GetBytes(value);
		if (BitConverter.IsLittleEndian != littleEndian)
		{
			Array.Reverse(bytes);
		}
		return bytes;
	}

	public static byte[] ToByteArray(this long value, bool littleEndian = true)
	{
		byte[] bytes = BitConverter.GetBytes(value);
		if (BitConverter.IsLittleEndian != littleEndian)
		{
			Array.Reverse(bytes);
		}
		return bytes;
	}

	public static byte[] Xor(this byte[] a, byte[] b)
	{
		byte[] array = new byte[a.Length];
		for (int i = 0; i < a.Length; i++)
		{
			array[i] = (byte)(a[i] ^ b[i]);
		}
		return array;
	}

	public static void WriteInt(this MemoryStream ms, int value, bool littleEndian = true)
	{
		ms.Write(value.ToByteArray(littleEndian));
	}

	public static void WriteShort(this MemoryStream ms, int value, bool littleEndian = true)
	{
		ms.Write(((short)value).ToShortByteArray(littleEndian));
	}

	public static void WriteLong(this MemoryStream ms, long value, bool littleEndian = true)
	{
		ms.Write(value.ToByteArray(littleEndian));
	}

	public static void WriteBytes(this MemoryStream ms, byte[] data)
	{
		ms.Write(data);
	}

	public static void WriteString(this MemoryStream ms, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		ms.WriteByte((byte)bytes.Length);
		ms.Write(bytes);
	}

	public static void WriteByteLengthString(this MemoryStream ms, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		ms.WriteByte((byte)bytes.Length);
		ms.Write(bytes);
	}

	public static void WriteShortString(this MemoryStream ms, string value, bool littleEndian = true)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		ms.WriteShort(bytes.Length, littleEndian);
		ms.Write(bytes);
	}

	public static byte[] ToShortByteArray(this short value, bool littleEndian = true)
	{
		byte[] bytes = BitConverter.GetBytes(value);
		if (BitConverter.IsLittleEndian != littleEndian)
		{
			Array.Reverse(bytes);
		}
		return bytes;
	}

	public static void WriteShortBytes(this MemoryStream ms, byte[] data, bool littleEndian = true)
	{
		ms.WriteShort(data.Length, littleEndian);
		ms.Write(data);
	}

	public static byte[] ToFrame(this MemoryStream ms)
	{
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.WriteShort((int)ms.Length);
		ms.Position = 0L;
		ms.CopyTo(memoryStream);
		return memoryStream.ToArray();
	}
}

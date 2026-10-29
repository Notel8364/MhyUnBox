using System;
using System.Text;

namespace MhyunBox.Services;

public static class SecretBox
{
	public static byte[] Decode(string encoded)
	{
		byte[] array = Convert.FromBase64String(encoded);
		byte[] array2 = ObfKey();
		byte[] array3 = new byte[array.Length];
		for (int i = 0; i < array.Length; i++)
		{
			array3[i] = (byte)(array[i] ^ array2[i % array2.Length]);
		}
		return array3;
	}

	public static string DecodeString(string encoded)
	{
		return Encoding.UTF8.GetString(Decode(encoded));
	}

	private static byte[] ObfKey()
	{
		byte[] array = new byte[16];
		for (int i = 0; i < 16; i++)
		{
			array[i] = (byte)(("7146262055707213"[i] - 48 + 81 + i * 3) & 0xFF);
		}
		return array;
	}
}

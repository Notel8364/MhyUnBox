using System;
using System.IO;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace MhyunBox.Services;

internal sealed class ChaChaPacker : ChaCha7539Engine
{
	public override string AlgorithmName => $"ChaCha{rounds}";

	public ChaChaPacker(byte[] key, byte[] iv, bool encrypt, int rounds = 8)
	{
		base.rounds = rounds;
		Init(encrypt, new ParametersWithIV(new KeyParameter(key), iv));
	}

	public byte[] PackMessage(byte type, byte[] data)
	{
		byte[] array = new byte[data.Length + 10];
		Array.Copy(BitConverter.GetBytes((short)(array.Length - 2)), array, 2);
		array[6] = type;
		array[7] = 136;
		array[8] = 136;
		array[9] = 136;
		Array.Copy(data, 0, array, 10, data.Length);
		uint num = Crc32.Compute(array, 6, array.Length - 6);
		array[2] = (byte)num;
		array[3] = (byte)(num >> 8);
		array[4] = (byte)(num >> 16);
		array[5] = (byte)(num >> 24);
		ProcessBytes(array, 2, array.Length - 2, array, 2);
		return array;
	}

	public (byte, byte[]) UnpackMessage(byte[] data)
	{
		ProcessBytes(data, 0, data.Length, data, 0);
		uint num = Crc32.Compute(data, 4, data.Length - 4);
		uint num2 = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
		if (num2 != num)
		{
			throw new InvalidDataException($"crc mismatch stored={num2:X8} computed={num:X8}");
		}
		byte[] array = new byte[data.Length - 8];
		Array.Copy(data, 8, array, 0, array.Length);
		return (data[4], array);
	}
}

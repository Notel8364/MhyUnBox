using System.Security.Cryptography;

namespace MhyunBox.Services;

internal static class AesEcb
{
	public static byte[] Encrypt(byte[] data, byte[] key)
	{
		using Aes aes = Aes.Create();
		aes.Key = key;
		aes.Mode = CipherMode.ECB;
		aes.Padding = PaddingMode.None;
		return aes.CreateEncryptor().TransformFinalBlock(data, 0, data.Length);
	}
}

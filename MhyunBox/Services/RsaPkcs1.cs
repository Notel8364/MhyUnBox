using System;
using System.IO;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Security;

namespace MhyunBox.Services;

internal static class RsaPkcs1
{
	public static AsymmetricKeyParameter LoadPublicKey(string base64)
	{
		return PublicKeyFactory.CreateKey(Convert.FromBase64String(base64));
	}

	public static AsymmetricKeyParameter LoadPrivateKey(string base64)
	{
		return PrivateKeyFactory.CreateKey(Convert.FromBase64String(base64));
	}

	public static byte[] Rsa(AsymmetricKeyParameter key, byte[] data, bool forEncryption)
	{
		Pkcs1Encoding pkcs1Encoding = new Pkcs1Encoding(new RsaEngine());
		pkcs1Encoding.Init(forEncryption, key);
		using MemoryStream memoryStream = new MemoryStream();
		for (int i = 0; i < data.Length; i += pkcs1Encoding.GetInputBlockSize())
		{
			int length = Math.Min(pkcs1Encoding.GetInputBlockSize(), data.Length - i);
			byte[] array = pkcs1Encoding.ProcessBlock(data, i, length);
			memoryStream.Write(array, 0, array.Length);
		}
		return memoryStream.ToArray();
	}
}

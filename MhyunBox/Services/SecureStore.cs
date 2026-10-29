using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MhyunBox.Services;

public static class SecureStore
{
	private struct DATA_BLOB
	{
		public int cbData;

		public nint pbData;

		public DATA_BLOB(byte[] data)
		{
			pbData = Marshal.AllocHGlobal(data.Length);
			Marshal.Copy(data, 0, pbData, data.Length);
			cbData = data.Length;
		}
	}

	private static readonly string RootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MhyunBox", "secure");

	private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MhyunBox.SecureStore.v1");

	/// <summary>把外部传入的存储名限制在 RootDir 内，防止 ../ 或绝对路径把敏感文件写到目录外。</summary>
	private static string? GetStorePath(string name)
	{
		if (!IsSafeStoreName(name))
		{
			try
			{
				LogHelper.Log("[SecureStore] 拒绝非法存储名（长度=" + (name?.Length ?? 0) + "）");
			}
			catch
			{
			}
			return null;
		}
		string root = Path.GetFullPath(RootDir);
		string full = Path.GetFullPath(Path.Combine(root, name + ".bin"));
		string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				LogHelper.Log("[SecureStore] 拒绝越界存储路径（长度=" + name.Length + "）");
			}
			catch
			{
			}
			return null;
		}
		return full;
	}

	private static bool IsSafeStoreName(string? name)
	{
		if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
		{
			return false;
		}
		foreach (char c in name)
		{
			bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
			if (!ok)
			{
				return false;
			}
		}
		return name != "." && name != ".." && !name.Contains("..");
	}

	public static void Save(string name, string content)
	{
		SaveBytes(name, Encoding.UTF8.GetBytes(content));
	}

	public static void SaveBytes(string name, byte[] content)
	{
		string? text = null;
		try
		{
			Directory.CreateDirectory(RootDir);
			text = GetStorePath(name);
			if (text == null)
			{
				return;
			}
			if (File.Exists(text))
			{
				try
				{
					File.Copy(text, text + ".bak", overwrite: true);
				}
				catch
				{
				}
			}
			string text2 = text + ".tmp";
			File.WriteAllBytes(text2, CryptProtect(content));
			File.Move(text2, text, overwrite: true);
		}
		catch (Exception ex)
		{
			try
			{
				LogHelper.Log("[SecureStore] 保存失败 " + name + ": " + ex.Message);
			}
			catch
			{
			}
			try
			{
				if (text != null && File.Exists(text + ".tmp"))
				{
					File.Delete(text + ".tmp");
				}
			}
			catch
			{
			}
		}
	}

	public static string? Load(string name)
	{
		byte[] array = LoadBytes(name);
		if (array != null)
		{
			return Encoding.UTF8.GetString(array);
		}
		return null;
	}

	public static byte[]? LoadBytes(string name)
	{
		try
		{
			string? path = GetStorePath(name);
			if (path == null || !File.Exists(path))
			{
				return null;
			}
			return CryptUnprotect(File.ReadAllBytes(path));
		}
		catch
		{
			return null;
		}
	}

	public static void Delete(string name)
	{
		try
		{
			string? path = GetStorePath(name);
			if (path == null)
			{
				return;
			}
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			// Save 时会先备份 .bak；删除本体时把备份一并清掉，避免敏感数据残留
			string bak = path + ".bak";
			if (File.Exists(bak))
			{
				File.Delete(bak);
			}
			string tmp = path + ".tmp";
			if (File.Exists(tmp))
			{
				File.Delete(tmp);
			}
		}
		catch
		{
		}
	}

	private static byte[] CryptProtect(byte[] plain)
	{
		DATA_BLOB pDataIn = new DATA_BLOB(plain);
		DATA_BLOB pOptionalEntropy = new DATA_BLOB(Entropy);
		DATA_BLOB pDataOut = default(DATA_BLOB);
		try
		{
			if (!CryptProtectData(ref pDataIn, "MhyunBox", ref pOptionalEntropy, IntPtr.Zero, IntPtr.Zero, 1, ref pDataOut))
			{
				throw new InvalidOperationException("CryptProtectData failed: " + Marshal.GetLastWin32Error());
			}
			byte[] array = new byte[pDataOut.cbData];
			Marshal.Copy(pDataOut.pbData, array, 0, pDataOut.cbData);
			return array;
		}
		finally
		{
			LocalFree(pDataOut.pbData);
			LocalFree(pDataIn.pbData);
			LocalFree(pOptionalEntropy.pbData);
		}
	}

	private static byte[] CryptUnprotect(byte[] encrypted)
	{
		DATA_BLOB pDataIn = new DATA_BLOB(encrypted);
		DATA_BLOB pOptionalEntropy = new DATA_BLOB(Entropy);
		DATA_BLOB pDataOut = default(DATA_BLOB);
		try
		{
			if (!CryptUnprotectData(ref pDataIn, IntPtr.Zero, ref pOptionalEntropy, IntPtr.Zero, IntPtr.Zero, 1, ref pDataOut))
			{
				throw new InvalidOperationException("CryptUnprotectData failed: " + Marshal.GetLastWin32Error());
			}
			byte[] array = new byte[pDataOut.cbData];
			Marshal.Copy(pDataOut.pbData, array, 0, pDataOut.cbData);
			return array;
		}
		finally
		{
			LocalFree(pDataOut.pbData);
			LocalFree(pDataIn.pbData);
			LocalFree(pOptionalEntropy.pbData);
		}
	}

	[DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, ref DATA_BLOB pOptionalEntropy, nint pvReserved, nint pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

	[DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, nint ppszDataDescr, ref DATA_BLOB pOptionalEntropy, nint pvReserved, nint pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

	[DllImport("kernel32.dll")]
	private static extern nint LocalFree(nint hMem);
}

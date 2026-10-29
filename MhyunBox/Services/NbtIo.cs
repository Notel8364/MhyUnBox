using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MhyunBox.Services;

public static class NbtIo
{
	public const byte TagEnd = 0;

	public const byte TagByte = 1;

	public const byte TagShort = 2;

	public const byte TagInt = 3;

	public const byte TagLong = 4;

	public const byte TagFloat = 5;

	public const byte TagDouble = 6;

	public const byte TagByteArray = 7;

	public const byte TagString = 8;

	public const byte TagList = 9;

	public const byte TagCompound = 10;

	public const byte TagIntArray = 11;

	public const byte TagLongArray = 12;

	private const int MaxDepth = 64;

	public static NbtValue? TryLoadFile(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}
			byte[] array = File.ReadAllBytes(path);
			byte[] array2 = array;
			if (array.Length > 2 && array[0] == 31 && array[1] == 139)
			{
				using MemoryStream stream = new MemoryStream(array, writable: false);
				using GZipStream gZipStream = new GZipStream(stream, CompressionMode.Decompress);
				using MemoryStream memoryStream = new MemoryStream();
				gZipStream.CopyTo(memoryStream);
				array2 = memoryStream.ToArray();
			}
			int num = 0;
			if (array2.Length < 2)
			{
				return null;
			}
			byte b = array2[num++];
			if (b == 0)
			{
				return null;
			}
			num += 2 + ReadStringLength(array2, num);
			return ReadPayload(array2, ref num, b, 0);
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[服务器列表] 解析失败: " + path);
			return null;
		}
	}

	public static void SaveFile(string path, NbtValue root)
	{
		try
		{
			using MemoryStream memoryStream = new MemoryStream();
			memoryStream.WriteByte(10);
			WriteString(memoryStream, "");
			WritePayload(memoryStream, root, 0);
			byte[] array = memoryStream.ToArray();
			string directoryName = Path.GetDirectoryName(path);
			if (!string.IsNullOrWhiteSpace(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			string text = path + ".mhyunbox.tmp";
			using (FileStream stream = new FileStream(text, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				using GZipStream gZipStream = new GZipStream(stream, CompressionLevel.Optimal);
				gZipStream.Write(array, 0, array.Length);
			}
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			File.Move(text, path);
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[服务器列表] 写入失败: " + path);
		}
	}

	private static int ReadStringLength(byte[] data, int pos)
	{
		if (pos + 1 >= data.Length)
		{
			throw new InvalidDataException("NBT 字符串长度越界");
		}
		return (data[pos] << 8) | data[pos + 1];
	}

	private static string ReadString(byte[] data, ref int pos)
	{
		int num = ReadStringLength(data, pos);
		pos += 2;
		if (num < 0 || pos + num > data.Length)
		{
			throw new InvalidDataException("NBT 字符串越界");
		}
		string result = Encoding.UTF8.GetString(data, pos, num);
		pos += num;
		return result;
	}

	private static void WriteString(Stream stream, string text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
		stream.WriteByte((byte)((bytes.Length >> 8) & 0xFF));
		stream.WriteByte((byte)(bytes.Length & 0xFF));
		stream.Write(bytes, 0, bytes.Length);
	}

	private static void WriteInt32(Stream stream, int value)
	{
		stream.WriteByte((byte)((value >> 24) & 0xFF));
		stream.WriteByte((byte)((value >> 16) & 0xFF));
		stream.WriteByte((byte)((value >> 8) & 0xFF));
		stream.WriteByte((byte)(value & 0xFF));
	}

	private static void WriteInt64(Stream stream, long value)
	{
		WriteInt32(stream, (int)((value >> 32) & 0xFFFFFFFFu));
		WriteInt32(stream, (int)(value & 0xFFFFFFFFu));
	}

	private static int ReadInt32(byte[] data, ref int pos)
	{
		if (pos + 4 > data.Length)
		{
			throw new InvalidDataException("NBT Int 越界");
		}
		int result = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
		pos += 4;
		return result;
	}

	private static long ReadInt64(byte[] data, ref int pos)
	{
		if (pos + 8 > data.Length)
		{
			throw new InvalidDataException("NBT Long 越界");
		}
		long num = 0L;
		for (int i = 0; i < 8; i++)
		{
			num = (num << 8) | data[pos + i];
		}
		pos += 8;
		return num;
	}

	private static NbtValue ReadPayload(byte[] data, ref int pos, byte type, int depth)
	{
		if (depth > 64)
		{
			throw new InvalidDataException("NBT 嵌套过深");
		}
		switch (type)
		{
		case 1:
		{
			if (pos >= data.Length)
			{
				throw new InvalidDataException("NBT Byte 越界");
			}
			byte b3 = data[pos++];
			return new NbtValue(1, b3);
		}
		case 2:
		{
			if (pos + 2 > data.Length)
			{
				throw new InvalidDataException("NBT Short 越界");
			}
			short num4 = (short)((data[pos] << 8) | data[pos + 1]);
			pos += 2;
			return new NbtValue(2, num4);
		}
		case 3:
			return new NbtValue(3, ReadInt32(data, ref pos));
		case 4:
			return new NbtValue(4, ReadInt64(data, ref pos));
		case 5:
		{
			int value2 = ReadInt32(data, ref pos);
			return new NbtValue(5, BitConverter.ToSingle(BitConverter.GetBytes(value2), 0));
		}
		case 6:
		{
			long value = ReadInt64(data, ref pos);
			return new NbtValue(6, BitConverter.Int64BitsToDouble(value));
		}
		case 7:
		{
			int num2 = ReadInt32(data, ref pos);
			if (num2 < 0 || pos + num2 > data.Length)
			{
				throw new InvalidDataException("NBT ByteArray 越界");
			}
			byte[] array2 = new byte[num2];
			Array.Copy(data, pos, array2, 0, num2);
			pos += num2;
			return new NbtValue(7, array2);
		}
		case 8:
			return new NbtValue(8, ReadString(data, ref pos));
		case 9:
		{
			byte b = data[pos++];
			int num3 = ReadInt32(data, ref pos);
			List<NbtValue> list = new List<NbtValue>();
			if (b == 0 || num3 <= 0)
			{
				return new NbtValue(9, list);
			}
			if (num3 > 1000000)
			{
				throw new InvalidDataException("NBT List 过大");
			}
			for (int j = 0; j < num3; j++)
			{
				list.Add(ReadPayload(data, ref pos, b, depth + 1));
			}
			return new NbtValue(9, list);
		}
		case 10:
		{
			Dictionary<string, NbtValue> dictionary = new Dictionary<string, NbtValue>(StringComparer.Ordinal);
			while (true)
			{
				if (pos >= data.Length)
				{
					throw new InvalidDataException("NBT Compound 越界");
				}
				byte b2 = data[pos++];
				if (b2 == 0)
				{
					break;
				}
				string key = ReadString(data, ref pos);
				dictionary[key] = ReadPayload(data, ref pos, b2, depth + 1);
			}
			return new NbtValue(10, dictionary);
		}
		case 11:
		{
			int num5 = ReadInt32(data, ref pos);
			if (num5 < 0 || pos + num5 * 4 > data.Length)
			{
				throw new InvalidDataException("NBT IntArray 越界");
			}
			int[] array3 = new int[num5];
			for (int k = 0; k < num5; k++)
			{
				array3[k] = ReadInt32(data, ref pos);
			}
			return new NbtValue(11, array3);
		}
		case 12:
		{
			int num = ReadInt32(data, ref pos);
			if (num < 0 || pos + num * 8 > data.Length)
			{
				throw new InvalidDataException("NBT LongArray 越界");
			}
			long[] array = new long[num];
			for (int i = 0; i < num; i++)
			{
				array[i] = ReadInt64(data, ref pos);
			}
			return new NbtValue(12, array);
		}
		default:
			throw new InvalidDataException("未知 NBT 类型: " + type);
		}
	}

	private static void WritePayload(Stream stream, NbtValue value, int depth)
	{
		if (depth > 64)
		{
			throw new InvalidDataException("NBT 嵌套过深");
		}
		switch (value.Type)
		{
		case 1:
			stream.WriteByte(Convert.ToByte(value.Value ?? ((object)(byte)0)));
			break;
		case 2:
			WriteShortPayload(stream, Convert.ToInt16(value.Value ?? ((object)(short)0)));
			break;
		case 3:
			WriteInt32(stream, Convert.ToInt32(value.Value ?? ((object)0)));
			break;
		case 4:
			WriteInt64(stream, Convert.ToInt64(value.Value ?? ((object)0L)));
			break;
		case 5:
			WriteInt32(stream, BitConverter.ToInt32(BitConverter.GetBytes(Convert.ToSingle(value.Value ?? ((object)0f))), 0));
			break;
		case 6:
			WriteInt64(stream, BitConverter.DoubleToInt64Bits(Convert.ToDouble(value.Value ?? ((object)0.0))));
			break;
		case 7:
		{
			byte[] array4 = (value.Value as byte[]) ?? Array.Empty<byte>();
			WriteInt32(stream, array4.Length);
			stream.Write(array4, 0, array4.Length);
			break;
		}
		case 8:
			WriteString(stream, value.AsString());
			break;
		case 9:
		{
			List<NbtValue> list = value.AsList();
			byte value4 = 0;
			if (list.Count > 0)
			{
				value4 = list[0].Type;
			}
			stream.WriteByte(value4);
			WriteInt32(stream, list.Count);
			{
				foreach (NbtValue item in list)
				{
					WritePayload(stream, item, depth + 1);
				}
				break;
			}
		}
		case 10:
			foreach (KeyValuePair<string, NbtValue> item2 in value.AsCompound())
			{
				stream.WriteByte(item2.Value.Type);
				WriteString(stream, item2.Key);
				WritePayload(stream, item2.Value, depth + 1);
			}
			stream.WriteByte(0);
			break;
		case 11:
		{
			int[] array5 = (value.Value as int[]) ?? Array.Empty<int>();
			WriteInt32(stream, array5.Length);
			int[] array6 = array5;
			int[] array7 = array6;
			foreach (int value3 in array7)
			{
				WriteInt32(stream, value3);
			}
			break;
		}
		case 12:
		{
			long[] array = (value.Value as long[]) ?? Array.Empty<long>();
			WriteInt32(stream, array.Length);
			long[] array2 = array;
			long[] array3 = array2;
			foreach (long value2 in array3)
			{
				WriteInt64(stream, value2);
			}
			break;
		}
		default:
			throw new InvalidDataException("未知 NBT 类型: " + value.Type);
		}
	}

	private static void WriteShortPayload(Stream stream, short value)
	{
		stream.WriteByte((byte)((value >> 8) & 0xFF));
		stream.WriteByte((byte)(value & 0xFF));
	}
}

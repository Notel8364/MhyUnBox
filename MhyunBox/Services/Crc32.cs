namespace MhyunBox.Services;

internal static class Crc32
{
	private static readonly uint[] Table = BuildTable();

	public static uint Compute(byte[] data, int offset, int length)
	{
		uint num = uint.MaxValue;
		for (int i = offset; i < offset + length; i++)
		{
			num = (num >> 8) ^ Table[(num ^ data[i]) & 0xFF];
		}
		return num ^ 0xFFFFFFFFu;
	}

	private static uint[] BuildTable()
	{
		uint[] array = new uint[256];
		for (uint num = 0u; num < 256; num++)
		{
			uint num2 = num;
			for (int i = 0; i < 8; i++)
			{
				num2 = (((num2 & 1) != 0) ? (0xEDB88320u ^ (num2 >> 1)) : (num2 >> 1));
			}
			array[num] = num2;
		}
		return array;
	}
}

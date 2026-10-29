using System.Collections.Generic;

namespace MhyunBox.Services;

public sealed class NbtValue
{
	public byte Type;

	public object? Value;

	public NbtValue(byte type, object? value)
	{
		Type = type;
		Value = value;
	}

	public static NbtValue OfString(string text)
	{
		return new NbtValue(8, text ?? "");
	}

	public static NbtValue OfList(List<NbtValue> items)
	{
		return new NbtValue(9, items);
	}

	public static NbtValue OfCompound(Dictionary<string, NbtValue> map)
	{
		return new NbtValue(10, map);
	}

	public Dictionary<string, NbtValue> AsCompound()
	{
		return (Value as Dictionary<string, NbtValue>) ?? new Dictionary<string, NbtValue>();
	}

	public List<NbtValue> AsList()
	{
		return (Value as List<NbtValue>) ?? new List<NbtValue>();
	}

	public string AsString()
	{
		return (Value as string) ?? "";
	}
}

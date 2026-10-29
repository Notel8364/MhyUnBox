using System;
using System.Text;

namespace MhyunBox.Services;

public static class SessionLogStore
{
	private const int MaxChars = 20000;

	private static readonly object Sync = new object();

	private static readonly StringBuilder Tail = new StringBuilder();

	public static event Action? TailChanged;

	public static string Snapshot()
	{
		lock (Sync)
		{
			return Tail.ToString();
		}
	}

	public static void Append(string text)
	{
		lock (Sync)
		{
			Tail.Append(text);
			if (Tail.Length > 20000)
			{
				Tail.Remove(0, Tail.Length - 20000);
			}
		}
		TailChanged?.Invoke();
	}

	public static void Clear()
	{
		lock (Sync)
		{
			Tail.Clear();
		}
		TailChanged?.Invoke();
	}
}

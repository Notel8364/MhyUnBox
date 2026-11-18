using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MhyunBox.Services;

public static class PclLauncherService
{
	public static string WriteServerAddress(string pclExePath, string serverAddress, out bool written)
	{
		written = false;
		try
		{
			string text = Path.Combine(Path.GetDirectoryName(pclExePath) ?? "", ".minecraft");
			if (!Directory.Exists(text))
			{
				return "未找到 PCL 的 .minecraft 目录";
			}
			string text2 = Path.Combine(text, "PCL", "config.json");
			if (File.Exists(text2))
			{
				if (JsonNode.Parse(File.ReadAllText(text2)) is JsonObject jsonObject)
				{
					jsonObject["ServerIp"] = serverAddress;
					File.WriteAllText(text2, jsonObject.ToJsonString(new JsonSerializerOptions
					{
						WriteIndented = true
					}));
					written = true;
					return "已写入直连服务器 " + serverAddress + " -> " + text2;
				}
				return "无法解析 PCL 配置文件 config.json，未修改";
			}
			string text3 = Path.Combine(text, "PCL.ini");
			if (File.Exists(text3))
			{
				List<string> list = new List<string>(File.ReadAllLines(text3));
				bool flag = false;
				for (int i = 0; i < list.Count; i++)
				{
					string text4 = list[i].TrimStart();
					if (text4.StartsWith("ServerIp:", StringComparison.OrdinalIgnoreCase) || text4.StartsWith("ServerIp=", StringComparison.OrdinalIgnoreCase))
					{
						list[i] = "ServerIp:" + serverAddress;
						flag = true;
						break;
					}
				}
				if (!flag)
				{
					list.Add("ServerIp:" + serverAddress);
				}
				File.WriteAllLines(text3, list);
				written = true;
				return "已写入直连服务器 " + serverAddress + " -> " + text3;
			}
			return "未找到 PCL 配置文件（config.json / PCL.ini）";
		}
		catch (Exception ex)
		{
			return "写入 PCL 配置失败：" + ex.Message;
		}
	}

	public static bool Launch(string pclExePath)
	{
		try
		{
			Process.Start(new ProcessStartInfo(pclExePath)
			{
				WorkingDirectory = Path.GetDirectoryName(pclExePath),
				UseShellExecute = true
			});
			return true;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "[PCL] 启动失败: " + pclExePath);
			return false;
		}
	}

	/// <summary>
	/// 多开会话场景的基础省内存 JVM 参数。可在 PCL「高级 JVM 参数」里直接复用。
	/// 注意 Direct 封顶不能太紧：Minecraft 1.12.2 的 ChunkRenderDispatcher 在第一次
	/// 建区块渲染管线时，会按 -Xmx*0.3/10MB 一次建好一批 RegionRenderCacheBuilder
	/// （每个 10MB 直存），-Xmx5836m + 20 线程 = 175 个 ≈ 1.71GB，且只受
	/// -XX:MaxDirectMemorySize 限制。512M（2026-11-17 17:37 崩溃）与 1G 都不够，
	/// 8 核以下的小堆机器才会显得够用。这里给 2560M：1.71GB 池子 + 约 0.8GB 余量。
	/// 若把 -Xmx 调小，池子会同步变小（-Xmx4096m → 1.22GB，-Xmx2048m → 0.61GB）。
	/// </summary>
	public const string MemoryJvmArgs =
		"-XX:MaxMetaspaceSize=384m -XX:ReservedCodeCacheSize=128m " +
		"-XX:MaxDirectMemorySize=2560M -Xss512k " +
		"-Dio.netty.allocator.pageSize=8192 " +
		"-Dio.netty.allocator.maxOrder=7 " +
		"-Dio.netty.allocator.numDirectArenas=2";

	/// <summary>
	/// 把省内存 JVM 参数写入 PCL 的 Setup.ini（LaunchAdvanceJvm）。
	/// 与已有参数按 token 合并，避免覆盖用户手工添加的参数。
	/// </summary>
	public static string WriteJvmArgs(string pclExePath, out bool written)
	{
		written = false;
		try
		{
			string dir = Path.GetDirectoryName(pclExePath) ?? "";
			string ini = Path.Combine(dir, "PCL", "Setup.ini");
			if (!File.Exists(ini))
			{
				return "未找到 PCL 配置 Setup.ini";
			}
			var lines = new List<string>(File.ReadAllLines(ini));
			string wanted = MergeJvmArgs(ExistingJvmArgs(lines), MemoryJvmArgs);
			int idx = -1;
			for (int i = 0; i < lines.Count; i++)
			{
				string t = lines[i].TrimStart();
				if (t.StartsWith("LaunchAdvanceJvm:", StringComparison.OrdinalIgnoreCase))
				{
					idx = i;
					break;
				}
			}
			string current = ExistingJvmArgs(lines);
			if (string.Equals(current, wanted, StringComparison.Ordinal))
			{
				return "PCL JVM 参数已是最新 -> " + ini;
			}
			string line = "LaunchAdvanceJvm:" + wanted;
			if (idx >= 0)
			{
				lines[idx] = line;
			}
			else
			{
				lines.Add(line);
			}
			File.WriteAllLines(ini, lines);
			written = true;
			return "已写入 PCL JVM 参数 -> " + ini + " (" + wanted + ")";
		}
		catch (Exception ex)
		{
			return "写入 PCL JVM 参数失败：" + ex.Message;
		}
	}

	private static string ExistingJvmArgs(List<string> lines)
	{
		foreach (string raw in lines)
		{
			string t = raw.TrimStart();
			if (t.StartsWith("LaunchAdvanceJvm:", StringComparison.OrdinalIgnoreCase))
			{
				return t.Substring("LaunchAdvanceJvm:".Length).Trim();
			}
		}
		return "";
	}

	private static string MergeJvmArgs(string existing, string wanted)
	{
		// 按参数名（-XX:xxx= / -Dxxx= / -Xxxx）做替换式合并：wanted 里带新值的参数
		// 必须顶掉 existing 里的旧值（例如 128M→512M），而不是新旧并存。
		static string NameOf(string token)
		{
			int eq = token.IndexOf('=');
			return eq > 0 ? token.Substring(0, eq) : token;
		}
		var wantedNames = new HashSet<string>(StringComparer.Ordinal);
		foreach (string part in wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			wantedNames.Add(NameOf(part));
		}
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var outArgs = new List<string>();
		foreach (string part in existing.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			if (wantedNames.Contains(NameOf(part)))
			{
				continue;
			}
			if (seen.Add(part))
			{
				outArgs.Add(part);
			}
		}
		foreach (string part in wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			if (seen.Add(part))
			{
				outArgs.Add(part);
			}
		}
		return string.Join(" ", outArgs);
	}
}

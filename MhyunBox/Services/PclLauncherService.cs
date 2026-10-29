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
}

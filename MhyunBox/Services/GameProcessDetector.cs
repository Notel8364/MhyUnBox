using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MhyunBox.Services;

public static class GameProcessDetector
{
	public sealed class FoundGroup
	{
		public string Label = "";

		public List<Process> Processes = new List<Process>();
	}

	private static readonly (string Label, string[] Names)[] Targets = new(string, string[])[2]
	{
		("网易发烧游戏平台", new string[3] { "FeverGamesInstaller", "FeverGamesService", "FeverGamesWeb" }),
		("网易我的世界", new string[2] { "WPFLauncher", "MCLauncher" })
	};

	public static List<FoundGroup> FindRunning()
	{
		List<FoundGroup> list = new List<FoundGroup>();
		try
		{
			Process[] processes = Process.GetProcesses();
			FoundGroup foundGroup = new FoundGroup
			{
				Label = "网易发烧游戏平台"
			};
			FoundGroup foundGroup2 = new FoundGroup
			{
				Label = "网易我的世界"
			};
			Process[] array = processes;
			Process[] array2 = array;
			foreach (Process process in array2)
			{
				bool flag = false;
				try
				{
					string text = process.ProcessName ?? "";
					if (text.Equals("FeverGamesInstaller", StringComparison.OrdinalIgnoreCase) || text.Equals("FeverGamesService", StringComparison.OrdinalIgnoreCase) || text.Equals("FeverGamesWeb", StringComparison.OrdinalIgnoreCase))
					{
						foundGroup.Processes.Add(process);
						flag = true;
					}
					else if (text.Equals("WPFLauncher", StringComparison.OrdinalIgnoreCase) || text.Equals("MCLauncher", StringComparison.OrdinalIgnoreCase))
					{
						foundGroup2.Processes.Add(process);
						flag = true;
					}
					else if ((text.Equals("java", StringComparison.OrdinalIgnoreCase) || text.Equals("javaw", StringComparison.OrdinalIgnoreCase)) && TryGetModulePath(process).IndexOf("MCLDownload", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						foundGroup2.Processes.Add(process);
						flag = true;
					}
				}
				catch
				{
				}
				if (!flag)
				{
					process.Dispose();
				}
			}
			if (foundGroup.Processes.Count > 0)
			{
				list.Add(foundGroup);
			}
			if (foundGroup2.Processes.Count > 0)
			{
				list.Add(foundGroup2);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "游戏进程检测失败");
		}
		return list;
	}

	/// <summary>
	/// 官方网易启动器（WPFLauncher / MCLauncher / 其启动的游戏进程）是否正在运行。
	/// 供所有「自动发起网易账号登录」的链路共用：程序在官方启动器运行时换新会话，
	/// 会把同账号下官方启动器正在用的会话顶掉（表现为启动器掉线 / 要求重登 / 连不上）。
	/// </summary>
	public static bool IsOfficialLauncherRunning()
	{
		try
		{
			foreach (FoundGroup group in FindRunning())
			{
				if (group.Label.Contains("我的世界", StringComparison.Ordinal) && group.Processes.Count > 0)
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static string TryGetModulePath(Process p)
	{
		try
		{
			return p.MainModule?.FileName ?? "";
		}
		catch
		{
			return "";
		}
	}

	public static void Kill(List<Process> processes)
	{
		foreach (Process process in processes)
		{
			int num = -1;
			try
			{
				num = process.Id;
			}
			catch
			{
			}
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch
			{
			}
			try
			{
				process.WaitForExit(3000);
			}
			catch
			{
			}
			if (num > 0)
			{
				TryTaskkill(num);
			}
			process.Dispose();
		}
	}

	private static void TryTaskkill(int id)
	{
		try
		{
			using Process process = Process.Start(new ProcessStartInfo("taskkill.exe", "/F /T /PID " + id)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			process?.WaitForExit(5000);
		}
		catch
		{
		}
	}
}

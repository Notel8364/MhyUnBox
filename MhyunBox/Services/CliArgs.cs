using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MhyunBox.Settings;

namespace MhyunBox.Services;

public static class CliArgs
{
	private static readonly string OutputFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MhyunBox", "cli-output.txt");

	private static readonly HashSet<string> Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--list", "--help", "-h", "--reset" };

	private static readonly HashSet<string> ValueArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"--source", "--music", "--music-folder", "--video-folder", "--bg", "--bg-opacity", "--swing", "--mode", "--theme", "--volume",
		"--listen", "--capture", "--minimize-to-tray", "--show-tray-icon", "--close-keeps-running", "--auth-account", "--login-name", "--config-url"
	};

	public static string? CapturePresetName { get; private set; }

	public static int AuthAccountIndex { get; private set; } = -1;

	public static string? LoginNameOverride { get; private set; }

	public static bool Process(string[] args)
	{
		LogHelper.Log("[CLI] 收到启动参数 " + args.Length + " 项（内容已脱敏）");
		if (args.Length <= 1)
		{
			return false;
		}
		bool flag = false;
		bool flag2 = false;
		bool flag3 = false;
		string text = null;
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		for (int i = 1; i < args.Length; i++)
		{
			string text2 = args[i];
			if (Flags.Contains(text2))
			{
				if (text2 == "--list")
				{
					flag = true;
				}
				else if (text2 == "--reset")
				{
					flag3 = true;
				}
				else
				{
					flag2 = true;
				}
				text = null;
			}
			else if (text2.StartsWith("--", StringComparison.Ordinal))
			{
				string text3 = text2;
				string text4 = null;
				int num = text2.IndexOf('=');
				if (num > 0)
				{
					text3 = text2.Substring(0, num);
					text4 = text2.Substring(num + 1);
				}
				if (ValueArgs.Contains(text3))
				{
					if (text4 == null)
					{
						text = text3;
					}
					else
					{
						dictionary[text3] = text4;
					}
				}
				else
				{
					text = null;
				}
			}
			else if (text != null)
			{
				dictionary[text] = text2;
				text = null;
			}
		}
		if (flag2)
		{
			Dump(Usage(), alsoExit: true);
			return true;
		}
		AppSettings appSettings = (flag3 ? new AppSettings() : SettingsManager.Instance);
		foreach (KeyValuePair<string, string> item in dictionary)
		{
			if (string.Equals(item.Key, "--capture", StringComparison.OrdinalIgnoreCase))
			{
				CapturePresetName = (string.IsNullOrWhiteSpace(item.Value) ? null : item.Value.Trim());
				LogHelper.Log("[CLI] --capture 解析: " + (CapturePresetName ?? "(空)"));
			}
			else if (string.Equals(item.Key, "--auth-account", StringComparison.OrdinalIgnoreCase))
			{
				if (int.TryParse(item.Value, out var result) && result >= 0)
				{
					AuthAccountIndex = result;
					LogHelper.Log($"[CLI] --auth-account 解析: {result}");
				}
			}
			else if (string.Equals(item.Key, "--login-name", StringComparison.OrdinalIgnoreCase))
			{
				LoginNameOverride = (string.IsNullOrWhiteSpace(item.Value) ? null : item.Value.Trim());
				LogHelper.Log("[CLI] --login-name 解析: " + (LoginNameOverride ?? "(空)"));
			}
			else
			{
				Apply(appSettings, item.Key, item.Value);
			}
		}
		SettingsManager.Save(appSettings);
		if (flag)
		{
			Dump(Describe(appSettings, dictionary.Count), alsoExit: true);
			return true;
		}
		return false;
	}

	private static void Apply(AppSettings s, string key, string value)
	{
		string text = key.ToLowerInvariant();
		if (text == null)
		{
			return;
		}
		switch (text.Length)
		{
		case 8:
			switch (text[2])
			{
			case 's':
				if (text == "--source")
				{
					s.MusicSource = Normalize(value, s.MusicSource, "audio", "video");
				}
				break;
			case 'v':
				if (text == "--volume")
				{
					s.MusicVolume = ParseDouble(value, s.MusicVolume, 0.0, 1.0);
				}
				break;
			case 'l':
				if (text == "--listen")
				{
					s.ProxyListenPort = (int)ParseDouble(value, s.ProxyListenPort, 1.0, 65535.0);
				}
				break;
			}
			break;
		case 7:
			switch (text[2])
			{
			case 'm':
				if (text == "--music")
				{
					s.MusicPath = value;
				}
				break;
			case 's':
				if (text == "--swing")
				{
					s.SwingAmplitude = ParseDouble(value, s.SwingAmplitude, 0.0, 100.0);
				}
				break;
			case 't':
				if (text == "--theme")
				{
					s.ThemeMode = Normalize(value, s.ThemeMode, "system", "dark", "light");
				}
				break;
			}
			break;
		case 14:
			switch (text[2])
			{
			case 'm':
				if (text == "--music-folder")
				{
					s.MusicFolder = value;
				}
				break;
			case 'v':
				if (text == "--video-folder")
				{
					s.VideoFolder = value;
				}
				break;
			}
			break;
		case 12:
			switch (text[2])
			{
			case 'b':
				if (text == "--bg-opacity")
				{
					s.BackgroundOpacity = ParseDouble(value, s.BackgroundOpacity, 0.0, 1.0);
				}
				break;
			case 'c':
				if (text == "--config-url")
				{
					s.ConfigUrl = value;
				}
				break;
			}
			break;
		case 4:
			if (text == "--bg")
			{
				s.BackgroundPath = value;
			}
			break;
		case 6:
			if (text == "--mode")
			{
				s.PlaybackMode = Normalize(value, s.PlaybackMode, "sequential", "loop", "random");
			}
			break;
		case 18:
			if (text == "--minimize-to-tray")
			{
				s.MinimizeToTray = ParseBool(value, s.MinimizeToTray);
			}
			break;
		case 16:
			if (text == "--show-tray-icon")
			{
				s.ShowTrayIcon = ParseBool(value, s.ShowTrayIcon);
			}
			break;
		case 21:
			if (text == "--close-keeps-running")
			{
				s.CloseKeepsRunning = ParseBool(value, s.CloseKeepsRunning);
			}
			break;
		}
	}

	private static string Describe(AppSettings s, int overrideCount)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("MhyunBox 有效配置 (cli-output，共应用 " + overrideCount + " 项参数覆盖)：");
		stringBuilder.AppendLine("  ThemeMode           = " + s.ThemeMode);
		stringBuilder.AppendLine("  MusicSource         = " + s.MusicSource);
		stringBuilder.AppendLine("  MusicPath           = " + s.MusicPath);
		stringBuilder.AppendLine("  MusicFolder         = " + s.MusicFolder);
		stringBuilder.AppendLine("  VideoFolder         = " + s.VideoFolder);
		stringBuilder.AppendLine("  BackgroundPath      = " + s.BackgroundPath);
		stringBuilder.AppendLine("  BackgroundOpacity   = " + s.BackgroundOpacity.ToString("0.##", CultureInfo.InvariantCulture));
		stringBuilder.AppendLine("  SwingAmplitude      = " + s.SwingAmplitude.ToString("0.##", CultureInfo.InvariantCulture));
		stringBuilder.AppendLine("  PlaybackMode        = " + s.PlaybackMode);
		stringBuilder.AppendLine("  MusicVolume         = " + s.MusicVolume.ToString("0.##", CultureInfo.InvariantCulture));
		stringBuilder.AppendLine("  ProxyListenPort     = " + s.ProxyListenPort);
		stringBuilder.AppendLine("  MinimizeToTray      = " + s.MinimizeToTray);
		stringBuilder.AppendLine("  ShowTrayIcon        = " + s.ShowTrayIcon);
		stringBuilder.AppendLine("  CloseKeepsRunning   = " + s.CloseKeepsRunning);
		stringBuilder.AppendLine("  MusicOrder.Count    = " + s.MusicOrder.Count);
		stringBuilder.AppendLine("  SkippedMusic.Count  = " + s.SkippedMusic.Count);
		return stringBuilder.ToString();
	}

	private static string Usage()
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("MhyunBox 命令行参数集：");
		stringBuilder.AppendLine("  --source <audio|video>        当前音乐来源");
		stringBuilder.AppendLine("  --music <path>                当前播放曲目（音乐或视频）");
		stringBuilder.AppendLine("  --music-folder <dir>          音乐文件夹");
		stringBuilder.AppendLine("  --video-folder <dir>          视频文件夹");
		stringBuilder.AppendLine("  --bg <path>                   背景视频/图片路径");
		stringBuilder.AppendLine("  --bg-opacity <0-1>            背景不透明度");
		stringBuilder.AppendLine("  --swing <0-100>               摆动幅度");
		stringBuilder.AppendLine("  --mode <sequential|loop|random> 播放模式");
		stringBuilder.AppendLine("  --theme <system|dark|light>   主题");
		stringBuilder.AppendLine("  --volume <0-1>                音乐音量");
		stringBuilder.AppendLine("  --listen <1-65535>            代理监听端口（客户端连 127.0.0.1:该端口）");
		stringBuilder.AppendLine("  --capture <预设名>            启动后自动开始代理抓取（如 \"元素之诗\"）");
		stringBuilder.AppendLine("  --auth-account <账号索引>     配合 --capture 启用网易云认证（本地账号索引，0 起步）");
		stringBuilder.AppendLine("  --login-name <角色名>        配合 --capture 改写 LoginStart 玩家名（如 Notel）");
		stringBuilder.AppendLine("  --minimize-to-tray <true|false>");
		stringBuilder.AppendLine("  --show-tray-icon <true|false>  是否保留托盘小图标");
		stringBuilder.AppendLine("  --close-keeps-running <true|false>");
		stringBuilder.AppendLine("  --list                        输出有效配置到 " + OutputFile + " 后退出");
		stringBuilder.AppendLine("  --reset                       恢复默认设置");
		stringBuilder.AppendLine("  --help / -h                   显示本帮助到 " + OutputFile + " 后退出");
		stringBuilder.AppendLine("参数可用 --key value 或 --key=value 两种写法。");
		return stringBuilder.ToString();
	}

	private static string Normalize(string value, string fallback, params string[] allowed)
	{
		string text = value.Trim().ToLowerInvariant();
		foreach (string text2 in allowed)
		{
			if (text == text2)
			{
				return text2;
			}
		}
		return fallback;
	}

	private static double ParseDouble(string value, double fallback, double min, double max)
	{
		if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			return Math.Clamp(result, min, max);
		}
		return fallback;
	}

	private static bool ParseBool(string value, bool fallback)
	{
		if (bool.TryParse(value, out var result))
		{
			return result;
		}
		return fallback;
	}

	private static void Dump(string content, bool alsoExit)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(OutputFile));
			File.WriteAllText(OutputFile, content, Encoding.UTF8);
		}
		catch
		{
		}
		try
		{
			Console.OutputEncoding = Encoding.UTF8;
			Console.WriteLine(content);
		}
		catch
		{
		}
		if (alsoExit)
		{
			Environment.Exit(0);
		}
	}
}

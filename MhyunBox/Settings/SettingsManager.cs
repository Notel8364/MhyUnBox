using System;
using System.IO;
using System.Text.Json;
using MhyunBox.Services;

namespace MhyunBox.Settings;

public static class SettingsManager
{
	private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MhyunBox", "settings.json");

	private static AppSettings? _instance;

	/// <summary>设置保存互斥：UI 线程与后台（机器人自启 / 账号保活 / 游戏监听）会并发保存，
	/// 共用同一个 .tmp 文件时并发写会互相顶掉，导致其中一次配置丢失。</summary>
	private static readonly object SaveLock = new object();

	public static AppSettings Instance => _instance ?? (_instance = Load());

	public static event Action<AppSettings>? Saved;

	public static AppSettings Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				AppSettings appSettings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
				if (appSettings != null)
				{
					// 2026-11-20 迁移：「更新后自动清理」改为常开（开关已从设置页移除）；旧默认保留天数 7 → 3
					if (!appSettings.AutoCleanupEnabled)
					{
						appSettings.AutoCleanupEnabled = true;
					}
					if (appSettings.RedundantBackupKeepDays == 7)
					{
						appSettings.RedundantBackupKeepDays = 3;
					}
					return appSettings;
				}
				try
				{
					LogHelper.Log("[设置] settings.json 反序列化结果为空，已重置为默认设置");
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			try
			{
				string text = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
				File.Copy(FilePath, text, overwrite: true);
				LogHelper.Log("[设置] settings.json 损坏，已备份到 " + text + " 并重置默认设置: " + ex.Message);
			}
			catch
			{
			}
		}
		return new AppSettings();
	}

	public static void Save(AppSettings settings)
	{
		lock (SaveLock)
		{
			_instance = settings;
			string text = FilePath + ".tmp";
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
				File.WriteAllText(text, JsonSerializer.Serialize(settings, new JsonSerializerOptions
				{
					WriteIndented = true
				}));
				File.Move(text, FilePath, overwrite: true);
			}
			catch (Exception ex)
			{
				try
				{
					LogHelper.Log("[设置] 保存设置失败: " + ex.Message);
				}
				catch
				{
				}
				try
				{
					if (File.Exists(text))
					{
						File.Delete(text);
					}
				}
				catch
				{
				}
			}
		}
		Saved?.Invoke(settings);
	}
}

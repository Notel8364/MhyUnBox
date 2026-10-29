using System;
using System.IO;
using System.Text.Json;
using MhyunBox.Services;

namespace MhyunBox.Settings;

public static class SettingsManager
{
	private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MhyunBox", "settings.json");

	private static AppSettings? _instance;

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
		Saved?.Invoke(settings);
	}
}

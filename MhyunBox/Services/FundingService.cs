using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace MhyunBox.Services;

/// <summary>
/// 资助名单：从仓库的 funding 分支读取 funding.txt（一行一个名字，# 开头为注释），
/// 拉取失败时回退到程序目录 Assets\funding.txt。名单只做展示，不参与任何逻辑。
/// </summary>
public static class FundingService
{
	/// <summary>仓库 funding 分支里的名单文件（与代码分支分开，更新名单不需要发版）。</summary>
	public const string RemoteUrl = "https://raw.githubusercontent.com/Notel8364/MhyUnBox/funding/funding.txt";

	private static readonly object Sync = new object();
	private static List<string>? _cache;
	private static DateTime _cacheAt = DateTime.MinValue;

	/// <summary>读取名单；10 分钟内复用缓存，force=true 强制重新拉取。</summary>
	public static async Task<List<string>> LoadAsync(bool force = false)
	{
		lock (Sync)
		{
			if (!force && _cache != null && (DateTime.Now - _cacheAt).TotalMinutes < 10)
			{
				return new List<string>(_cache);
			}
		}
		var names = new List<string>();
		try
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
			http.DefaultRequestHeaders.UserAgent.ParseAdd("MhyunBox/" + AppVersion.Display);
			string text = await http.GetStringAsync(RemoteUrl).ConfigureAwait(false);
			names = Parse(text);
		}
		catch (Exception ex)
		{
			LogHelper.DebugError(ex, "[资助名单] 远程拉取失败，尝试本地名单");
		}
		if (names.Count == 0)
		{
			try
			{
				string local = Path.Combine(AppContext.BaseDirectory, "Assets", "funding.txt");
				if (File.Exists(local))
				{
					names = Parse(File.ReadAllText(local));
				}
			}
			catch (Exception ex)
			{
				LogHelper.DebugError(ex, "[资助名单] 本地名单读取失败");
			}
		}
		lock (Sync)
		{
			_cache = new List<string>(names);
			_cacheAt = DateTime.Now;
		}
		return names;
	}

	/// <summary>一行一个名字；空行与 # / // 开头的行忽略，保持文件里的原始顺序。</summary>
	private static List<string> Parse(string? text)
	{
		var names = new List<string>();
		if (string.IsNullOrWhiteSpace(text))
		{
			return names;
		}
		foreach (string raw in text.Split('\n'))
		{
			string line = raw.Trim().TrimEnd('\r');
			if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
			{
				continue;
			}
			names.Add(line);
		}
		return names;
	}
}

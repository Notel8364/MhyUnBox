using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MhyunBox.Services;

internal class ItemUrl
{
	public long GameId;

	public string Module = "";

	public string Ver = "";

	public string Hash = "";

	public string PackageName = "";

	public static ItemUrl? Parse(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri result))
		{
			return null;
		}
		string fileName = Path.GetFileName(result.AbsolutePath);
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return null;
		}
		Match match = Regex.Match(fileName, "^item_(\\d+)_(\\d+)_(v\\d+)_([0-9A-Za-z]+)\\.7z$", RegexOptions.IgnoreCase);
		if (!match.Success)
		{
			return null;
		}
		return new ItemUrl
		{
			GameId = long.Parse(match.Groups[1].Value),
			Module = match.Groups[2].Value,
			Ver = match.Groups[3].Value,
			Hash = match.Groups[4].Value,
			PackageName = fileName
		};
	}
}

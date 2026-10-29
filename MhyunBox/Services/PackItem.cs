namespace MhyunBox.Services;

public sealed class PackItem
{
	public string Name { get; set; } = "";

	public string Path { get; set; } = "";

	public bool IsFolder { get; set; }

	public bool IsToolOwned { get; set; }

	public string SizeText { get; set; } = "";
}

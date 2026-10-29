namespace MhyunBox.Services;

public sealed class ClientDetection
{
	public bool Matched;

	public string VersionDir = "";

	public string ModsDir = "";

	public ModInjectRule? Rule;

	public string Detail = "";
}

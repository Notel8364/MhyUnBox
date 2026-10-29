namespace MhyunBox.Settings;

public sealed class SavedProxyBinding
{
	public string ServerName { get; set; } = "";

	public string Channel { get; set; } = "";

	public string Account { get; set; } = "";

	public string RoleName { get; set; } = "";

	public int ListenPort { get; set; }
}

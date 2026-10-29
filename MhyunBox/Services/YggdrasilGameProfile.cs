namespace MhyunBox.Services;

public sealed class YggdrasilGameProfile
{
	public required string GameId { get; set; }

	public required string GameVersion { get; set; }

	public required string BootstrapMd5 { get; set; }

	public required string DatFileMd5 { get; set; }

	public string ModsJson { get; set; } = "{\"mods\":[]}";

	public required int UserId { get; set; }

	public required string UserToken { get; set; }
}

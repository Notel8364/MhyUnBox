namespace MhyunBox.Services;

public sealed class YggdrasilResult
{
	public bool Success { get; init; }

	public string Message { get; init; } = "";

	public string? Server { get; init; }
}

using System;
using System.Reflection;

namespace MhyunBox.Services;

public static class BuildInfo
{
	private static readonly string BuildBatch = SecretBox.DecodeString("OjQvAzdLV1ZcR0ZKTUg=");

	private static string? _installId;

	public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

	public static string BatchToken => BuildBatch;

	public static string InstallId
	{
		get
		{
			if (_installId == null)
			{
				_installId = SecureStore.Load("install_id");
				if (string.IsNullOrWhiteSpace(_installId))
				{
					_installId = Guid.NewGuid().ToString("N").ToUpperInvariant();
					SecureStore.Save("install_id", _installId);
				}
			}
			return _installId;
		}
	}
}

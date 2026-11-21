namespace Totoro.Plugins.Shared;

/// <summary>What plugins send to sites that turn away clients that don't look like a browser. Shared source, linked into the plugins that need it.</summary>
internal static class BrowserHeaders
{
	public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
}

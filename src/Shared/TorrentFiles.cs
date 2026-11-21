using Flurl.Http;
using Totoro.Core.Modules.Streaming;

namespace Totoro.Plugins.Shared;

/// <summary>For torrent streamers that upload a .torrent rather than leave the service to fetch it. Shared source, linked into the debrid plugins.</summary>
internal static class TorrentFiles
{
	/// <summary>Whether the torrent is a .torrent to download (rather than a magnet link).</summary>
	public static bool IsFile(Uri torrent) => torrent.Scheme is "http" or "https";

	/// <summary>Downloads the .torrent; failing to becomes a <see cref="TorrentStreamException"/> saying so.</summary>
	public static async Task<byte[]> DownloadAsync(Uri torrent, CancellationToken ct)
	{
		try
		{
			return await torrent.AbsoluteUri.GetBytesAsync(cancellationToken: ct);
		}
		catch (FlurlHttpException ex)
		{
			throw new TorrentStreamException($"Couldn't download the torrent from {torrent.Host}: {ex.Message}", ex);
		}
	}
}

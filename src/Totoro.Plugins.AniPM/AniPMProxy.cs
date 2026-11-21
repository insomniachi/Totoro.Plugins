using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Totoro.Plugins.Shared;

namespace Totoro.Plugins.AniPM;

/// <summary>
/// Serves Settlar's streams on localhost, fetched with <see cref="Curl"/>: its CDN turns away players' own requests (see
/// there). Stateless: each URL carries its upstream one in its query (u), and playlists have every URL in them made one
/// of the proxy's, so it serves any number of episodes at once. Every request is answered whole and the connection closed.
/// </summary>
internal static partial class AniPMProxy
{
	private static readonly LoopbackServer s_server = new(HandleAsync);

	/// <summary>The local URL of <paramref name="url"/>.</summary>
	/// <param name="name">A file name, so players can tell the format from it (e.g. playlist.m3u8, subtitle.vtt).</param>
	public static Uri Local(Uri url, string name) =>
		new($"http://127.0.0.1:{s_server.Port}/{Uri.EscapeDataString(name)}?u={Uri.EscapeDataString(url.AbsoluteUri)}");

	private static async Task HandleAsync(NetworkStream stream, Uri? target, CancellationToken ct)
	{
		// Only Settlar's hosts: the proxy listens on loopback, but needn't fetch anything for anyone.
		if (HttpUtility.ParseQueryString(target?.Query ?? "")["u"] is not { } u || !Uri.TryCreate(u, UriKind.Absolute, out var url) ||
			url.Scheme != Uri.UriSchemeHttps || !url.Host.EndsWith(".settlar.io", StringComparison.OrdinalIgnoreCase))
		{
			await LoopbackServer.WriteAsync(stream, HttpStatusCode.BadRequest, "text/plain", "Bad request"u8.ToArray(), ct);
			return;
		}

		var (status, type, body) = await Curl.GetAsync(url, null, ct);
		if (status is < 200 or > 299)
		{
			await LoopbackServer.WriteAsync(stream, (HttpStatusCode)(status == 0 ? 502 : status), "text/plain", [], ct);
			return;
		}

		if (body.AsSpan().StartsWith("#EXTM3U"u8))
		{
			var rewritten = RewritePlaylist(Encoding.UTF8.GetString(body), url, subtitles: target!.AbsolutePath == "/subtitles.m3u8");
			await LoopbackServer.WriteAsync(stream, HttpStatusCode.OK, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(rewritten), ct);
			return;
		}

		// shortcut: no ranges, every file is sent whole (HLS players fetch segments whole); add them if one seeks within a file.
		await LoopbackServer.WriteAsync(stream, HttpStatusCode.OK, type ?? "application/octet-stream", body, ct);
	}

	/// <summary>
	/// Every URL in a playlist made one of the proxy's: the master's playlists as playlists (subtitles.m3u8 for subtitle
	/// renditions), the rest as segments, named for what they are: ffmpeg (so mpv) won't open an HLS segment without a
	/// media extension unless it can tell from its content, which it can't for WebVTT.
	/// </summary>
	/// <param name="subtitles">Whether it's a subtitle rendition's playlist (its segments are WebVTT).</param>
	internal static string RewritePlaylist(string text, Uri parent, bool subtitles)
	{
		var name = text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal) ? "playlist.m3u8" : subtitles ? "segment.vtt" : "segment.ts";
		return string.Join('\n', text.Split('\n').Select(line => line.Trim() switch
		{
			"" => line,
			var tag when tag.StartsWith('#') => UriAttribute().Replace(tag, x =>
				$"URI=\"{Local(new Uri(parent, x.Groups[1].Value), tag.Contains("TYPE=SUBTITLES", StringComparison.Ordinal) ? "subtitles.m3u8" : "playlist.m3u8").AbsoluteUri}\""),
			var uri => Local(new Uri(parent, uri), name).AbsoluteUri,
		}));
	}

	// Tags' URIs (EXT-X-MEDIA renditions, here) are playlists.
	[GeneratedRegex("URI=\"([^\"]+)\"")]
	private static partial Regex UriAttribute();
}

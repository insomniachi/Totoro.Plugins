using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace Totoro.Plugins.AniPM;

/// <summary>
/// Serves Settlar's streams on localhost, fetched with <see cref="Curl"/>: its CDN turns away players' own requests (see
/// there). Stateless: each URL carries its upstream one in its query (u), and playlists have every URL in them made one
/// of the proxy's, so it serves any number of episodes at once. Every request is answered whole and the connection closed.
/// </summary>
internal static partial class AniPMProxy
{
	private static readonly Lock s_lock = new();
	private static TcpListener? s_listener;

	/// <summary>The local URL of <paramref name="url"/>.</summary>
	/// <param name="name">A file name, so players can tell the format from it (e.g. playlist.m3u8, subtitle.vtt).</param>
	public static Uri Local(Uri url, string name) =>
		new($"http://127.0.0.1:{Port()}/{Uri.EscapeDataString(name)}?u={Uri.EscapeDataString(url.AbsoluteUri)}");

	private static int Port()
	{
		lock (s_lock)
		{
			if (s_listener is null)
			{
				s_listener = new TcpListener(IPAddress.Loopback, 0);
				s_listener.Start();
				_ = AcceptAsync(s_listener);
			}

			return ((IPEndPoint)s_listener.LocalEndpoint).Port;
		}
	}

	private static async Task AcceptAsync(TcpListener listener)
	{
		while (true)
		{
			try
			{
				_ = HandleAsync(await listener.AcceptTcpClientAsync());
			}
			catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
			{
				return;
			}
		}
	}

	private static async Task HandleAsync(TcpClient client)
	{
		using var connection = client;
		var stream = client.GetStream();
		try
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
			var target = await ReadTargetAsync(stream, timeout.Token);
			// Only Settlar's hosts: the proxy listens on loopback, but needn't fetch anything for anyone.
			if (HttpUtility.ParseQueryString(target?.Query ?? "")["u"] is not { } u || !Uri.TryCreate(u, UriKind.Absolute, out var url) ||
				url.Scheme != Uri.UriSchemeHttps || !url.Host.EndsWith(".settlar.io", StringComparison.OrdinalIgnoreCase))
			{
				await WriteAsync(stream, HttpStatusCode.BadRequest, "text/plain", "Bad request"u8.ToArray(), timeout.Token);
				return;
			}

			var (status, type, body) = await Curl.GetAsync(url, null, timeout.Token);
			if (status is < 200 or > 299)
			{
				await WriteAsync(stream, (HttpStatusCode)(status == 0 ? 502 : status), "text/plain", [], timeout.Token);
				return;
			}

			if (body.AsSpan().StartsWith("#EXTM3U"u8))
			{
				var rewritten = RewritePlaylist(Encoding.UTF8.GetString(body), url, subtitles: target!.AbsolutePath == "/subtitles.m3u8");
				await WriteAsync(stream, HttpStatusCode.OK, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(rewritten), timeout.Token);
				return;
			}

			// shortcut: no ranges, every file is sent whole (HLS players fetch segments whole); add them if one seeks within a file.
			await WriteAsync(stream, HttpStatusCode.OK, type ?? "application/octet-stream", body, timeout.Token);
		}
		catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or OperationCanceledException)
		{
			// The player went away or the CDN failed; it asks again if it still wants it.
		}
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

	/// <summary>The request's path and query; null if it isn't a GET or HEAD.</summary>
	private static async Task<Uri?> ReadTargetAsync(NetworkStream stream, CancellationToken ct)
	{
		var buffer = new byte[16 * 1024];
		var length = 0;
		while (length < buffer.Length)
		{
			var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
			if (read == 0)
			{
				break;
			}

			length += read;
			if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
			{
				break;
			}
		}

		var requestLine = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
		return requestLine is ["GET" or "HEAD", var path, ..] && Uri.TryCreate($"http://127.0.0.1{path}", UriKind.Absolute, out var target) ? target : null;
	}

	private static async Task WriteAsync(NetworkStream stream, HttpStatusCode status, string type, byte[] body, CancellationToken ct)
	{
		var head = $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
		await stream.WriteAsync(body, ct);
	}

	// Tags' URIs (EXT-X-MEDIA renditions, here) are playlists.
	[GeneratedRegex("URI=\"([^\"]+)\"")]
	private static partial Regex UriAttribute();
}

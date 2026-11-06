using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace Totoro.Plugins.Gotaku;

/// <summary>
/// Serves Gotaku's streams on localhost, decrypted: its playlists are encrypted whole and its segments' first bytes, so
/// no player can open them as they are. Stateless: each URL carries what it needs in its query (u: the upstream URL,
/// k: the manifest's keySeed, sb: how many of a segment's bytes are encrypted, m=1: a playlist), so it serves any
/// number of episodes at once. Every request is answered whole and the connection closed.
/// </summary>
internal static partial class GotakuProxy
{
	private static readonly HttpClient s_client = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
	{
		Timeout = TimeSpan.FromSeconds(30),
	};

	private static readonly Lock s_lock = new();
	private static TcpListener? s_listener;
	private static string s_referer = "https://gotaku.to/";

	/// <summary>The local URL of a stream's playlist.</summary>
	/// <param name="referer">The site's address; its CDN turns away requests from elsewhere.</param>
	public static Uri Playlist(Uri url, string keySeed, int segmentBytes, string referer)
	{
		s_referer = referer;
		return Local(url, keySeed, segmentBytes, playlist: true);
	}

	/// <summary>The local URL of a file fetched as it is (a subtitle file, an HLS key).</summary>
	public static Uri Plain(Uri url) => Local(url, "", 0, playlist: false);

	private static Uri Local(Uri url, string keySeed, int segmentBytes, bool playlist)
	{
		var query = $"u={Uri.EscapeDataString(url.AbsoluteUri)}";
		if (keySeed.Length > 0)
		{
			query += $"&k={Uri.EscapeDataString(keySeed)}&sb={segmentBytes}";
		}

		// The file name stays at the end of the path, so players can tell the format from it.
		var name = playlist ? "playlist.m3u8" : Path.GetFileName(url.AbsolutePath) is { Length: > 0 } file ? file : "file";
		return new Uri($"http://127.0.0.1:{Port()}/{Uri.EscapeDataString(name)}?{query}{(playlist ? "&m=1" : "")}");
	}

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
			var query = target is null ? null : HttpUtility.ParseQueryString(target.Query);
			if (query?["u"] is not { } u || !Uri.TryCreate(u, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
			{
				await WriteAsync(stream, HttpStatusCode.BadRequest, "text/plain", "Bad request"u8.ToArray(), timeout.Token);
				return;
			}

			var keySeed = query["k"] is { } k ? GotakuCrypto.TryBase64Url(k) : null;
			_ = int.TryParse(query["sb"], out var segmentBytes);

			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.TryAddWithoutValidation("User-Agent", GotakuProvider.UserAgent);
			request.Headers.TryAddWithoutValidation("Referer", s_referer);
			using var response = await s_client.SendAsync(request, timeout.Token);
			if (!response.IsSuccessStatusCode)
			{
				await WriteAsync(stream, response.StatusCode, "text/plain", [], timeout.Token);
				return;
			}

			var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
			if (query["m"] == "1")
			{
				var text = keySeed is null ? null : GotakuCrypto.DecryptPlaylist(body, keySeed, url);
				text ??= Encoding.UTF8.GetString(body);
				if (!text.TrimStart('﻿', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal))
				{
					await WriteAsync(stream, HttpStatusCode.BadGateway, "text/plain", "The playlist didn't decrypt"u8.ToArray(), timeout.Token);
					return;
				}

				var rewritten = RewritePlaylist(text, response.RequestMessage?.RequestUri ?? url, query["k"] ?? "", segmentBytes);
				await WriteAsync(stream, HttpStatusCode.OK, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(rewritten), timeout.Token);
				return;
			}

			if (keySeed is not null)
			{
				GotakuCrypto.DecryptSegment(body, keySeed, url.AbsoluteUri, segmentBytes);
			}

			// shortcut: no ranges, every file is sent whole (HLS players fetch segments whole); add them if one seeks within a file.
			await WriteAsync(stream, HttpStatusCode.OK, response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", body, timeout.Token);
		}
		catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or OperationCanceledException)
		{
			// The player went away or the CDN failed; it asks again if it still wants it.
		}
	}

	/// <summary>Every URL in a playlist made one of the proxy's: playlists as playlists, keys and subtitles as they are, the rest as segments.</summary>
	internal static string RewritePlaylist(string text, Uri parent, string keySeed, int segmentBytes)
	{
		var isMaster = text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);
		return string.Join('\n', text.Split('\n').Select(line =>
		{
			var trimmed = line.Trim();
			if (trimmed.Length == 0)
			{
				return line;
			}

			if (!trimmed.StartsWith('#'))
			{
				return Local(new Uri(parent, trimmed), keySeed, segmentBytes, playlist: isMaster).AbsoluteUri;
			}

			return UriAttribute().Replace(trimmed, match =>
			{
				var url = new Uri(parent, match.Groups[1].Value);
				var local = trimmed switch
				{
					_ when trimmed.StartsWith("#EXT-X-KEY", StringComparison.Ordinal) || trimmed.StartsWith("#EXT-X-SESSION-KEY", StringComparison.Ordinal) => Plain(url),
					_ when trimmed.StartsWith("#EXT-X-MEDIA", StringComparison.Ordinal) && trimmed.Contains("TYPE=SUBTITLES", StringComparison.Ordinal) => Local(url, "", 0, playlist: true),
					_ when trimmed.StartsWith("#EXT-X-MEDIA", StringComparison.Ordinal) || trimmed.StartsWith("#EXT-X-I-FRAME-STREAM-INF", StringComparison.Ordinal) => Local(url, keySeed, segmentBytes, playlist: true),
					_ => Local(url, keySeed, segmentBytes, playlist: false),
				};
				return $"URI=\"{local.AbsoluteUri}\"";
			});
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

	[GeneratedRegex("URI=\"([^\"]+)\"")]
	private static partial Regex UriAttribute();
}

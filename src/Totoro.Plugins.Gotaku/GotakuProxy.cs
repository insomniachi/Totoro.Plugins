using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Totoro.Plugins.Shared;

namespace Totoro.Plugins.Gotaku;

/// <summary>
/// Serves Gotaku's streams on localhost, decrypted: its playlists are encrypted whole and its segments' first bytes, so
/// no player can open them as they are. Stateless: each URL carries what it needs in its query (u: the upstream URL,
/// k: the manifest's keySeed, sb: how many of a segment's bytes are encrypted, m=1: a playlist, s: u's signature), so it serves any
/// number of episodes at once. Every request is answered whole and the connection closed.
/// </summary>
internal static partial class GotakuProxy
{
	private static readonly HttpClient s_client = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
	{
		Timeout = TimeSpan.FromSeconds(30),
	};

	private static readonly LoopbackServer s_server = new(HandleAsync);
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
		return new Uri($"http://127.0.0.1:{s_server.Port}/{Uri.EscapeDataString(name)}?{query}{(playlist ? "&m=1" : "")}&s={Sign(url.AbsoluteUri)}");
	}

	// Only URLs the proxy made itself are fetched (signed with a key of this run's), so a web page can't use it to reach
	// anything else from this machine.
	private static readonly byte[] s_signingKey = RandomNumberGenerator.GetBytes(32);

	private static string Sign(string url) => Convert.ToHexString(HMACSHA256.HashData(s_signingKey, Encoding.UTF8.GetBytes(url)));

	private static bool IsSigned(string url, string? signature) =>
		signature is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(url)), Encoding.ASCII.GetBytes(signature));

	private static async Task HandleAsync(NetworkStream stream, Uri? target, CancellationToken ct)
	{
		var query = target is null ? null : HttpUtility.ParseQueryString(target.Query);
		if (query?["u"] is not { } u || !IsSigned(u, query["s"]) || !Uri.TryCreate(u, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
		{
			await LoopbackServer.WriteAsync(stream, HttpStatusCode.BadRequest, "text/plain", "Bad request"u8.ToArray(), ct);
			return;
		}

		var keySeed = query["k"] is { } k ? GotakuCrypto.TryBase64Url(k) : null;
		_ = int.TryParse(query["sb"], out var segmentBytes);

		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.TryAddWithoutValidation("User-Agent", BrowserHeaders.UserAgent);
		request.Headers.TryAddWithoutValidation("Referer", s_referer);
		using var response = await s_client.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode)
		{
			await LoopbackServer.WriteAsync(stream, response.StatusCode, "text/plain", [], ct);
			return;
		}

		var body = await response.Content.ReadAsByteArrayAsync(ct);
		if (query["m"] == "1")
		{
			var text = keySeed is null ? null : GotakuCrypto.DecryptPlaylist(body, keySeed, url);
			text ??= Encoding.UTF8.GetString(body);
			if (!text.TrimStart('﻿', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal))
			{
				await LoopbackServer.WriteAsync(stream, HttpStatusCode.BadGateway, "text/plain", "The playlist didn't decrypt"u8.ToArray(), ct);
				return;
			}

			var rewritten = RewritePlaylist(text, response.RequestMessage?.RequestUri ?? url, query["k"] ?? "", segmentBytes);
			await LoopbackServer.WriteAsync(stream, HttpStatusCode.OK, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(rewritten), ct);
			return;
		}

		if (keySeed is not null)
		{
			GotakuCrypto.DecryptSegment(body, keySeed, url.AbsoluteUri, segmentBytes);
		}

		// shortcut: no ranges, every file is sent whole (HLS players fetch segments whole); add them if one seeks within a file.
		await LoopbackServer.WriteAsync(stream, HttpStatusCode.OK, response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", body, ct);
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

	[GeneratedRegex("URI=\"([^\"]+)\"")]
	private static partial Regex UriAttribute();
}

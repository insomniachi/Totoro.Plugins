using System.Text.Json;
using System.Text.Json.Serialization;
using Flurl;
using Flurl.Http;
using Microsoft.Extensions.Logging;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.RealDebrid;

/// <summary>
/// Streams torrents with Real-Debrid (https://api.real-debrid.com), a debrid service: the torrent is added to the user's
/// account, only the chosen file is selected, Real-Debrid downloads it on its servers (at once, if it has it cached), and
/// the player streams the file from its unrestricted link. When the stream ends the torrent is removed again, unless the
/// settings keep it.
/// </summary>
public sealed class RealDebridStreamer(IModuleSettings<Settings> settings, ILogger<RealDebridStreamer> logger) : ITorrentStreamer
{
	private const string Api = "https://api.real-debrid.com/rest/1.0";
	private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(2);
	private static readonly string[] s_failed = ["magnet_error", "error", "virus", "dead"];

	public Guid Id => RealDebridModule.Id;

	public string Name => "Real-Debrid";

	public bool IsAvailable() => !string.IsNullOrWhiteSpace(settings.Value.ApiKey);

	public async Task<TorrentStream> StreamAsync(Uri torrent, Func<IReadOnlyList<TorrentFile>, TorrentFile?> selectFile, IProgress<string>? progress,
												 CancellationToken ct)
	{
		progress?.Report("Adding it to Real-Debrid…");
		// Each add makes a new torrent in the account, even for one the user has already, so the stream owns it.
		var added = torrent.Scheme is "http" or "https"
			? await SendAsync<Added>(Request("torrents", "addTorrent").PutAsync(new ByteArrayContent(await GetTorrentFileAsync(torrent, ct)), cancellationToken: ct))
			: await SendAsync<Added>(Request("torrents", "addMagnet").PostUrlEncodedAsync(new { magnet = torrent.AbsoluteUri }, cancellationToken: ct));
		var remove = !settings.Value.KeepTorrents;

		try
		{
			var (selected, link) = await WaitForFileAsync(added.Id, selectFile, progress, ct);
			var unrestricted = await SendAsync<Unrestricted>(Request("unrestrict", "link").PostUrlEncodedAsync(new { link }, cancellationToken: ct));
			return new TorrentStream(new Uri(unrestricted.Download), selected, remove ? () => new ValueTask(RemoveAsync(added.Id)) : null);
		}
		catch when (remove)
		{
			await RemoveAsync(added.Id);
			throw;
		}
	}

	/// <summary>
	/// Selects the file of the torrent that <paramref name="selectFile"/> picks, and returns it with its Real-Debrid link
	/// once Real-Debrid has all of it.
	/// </summary>
	private async Task<(TorrentFile File, string Link)> WaitForFileAsync(string id, Func<IReadOnlyList<TorrentFile>, TorrentFile?> selectFile,
																		 IProgress<string>? progress, CancellationToken ct)
	{
		TorrentFile? selected = null;
		while (true)
		{
			var info = await SendAsync<Torrent>(Request("torrents", "info", id).GetAsync(cancellationToken: ct));
			if (s_failed.Contains(info.Status))
			{
				throw new TorrentStreamException($"Real-Debrid couldn't download it: {info.Status}");
			}

			// Its files are known once Real-Debrid has the torrent's metadata; it downloads nothing until some are selected.
			if (selected is null && info.Status == "waiting_files_selection")
			{
				selected = selectFile([.. (info.Files ?? []).Select(x => new TorrentFile(x.Id, x.Path.TrimStart('/'), x.Bytes))])
						   ?? throw new TorrentStreamException("The torrent has no video to play.");
				await SendAsync<JsonElement?>(Request("torrents", "selectFiles", id).PostUrlEncodedAsync(new { files = selected.Index }, cancellationToken: ct));
				continue;
			}

			// One link per selected file, and only one is.
			if (selected is not null && info.Status == "downloaded" && info.Links is [var link, ..])
			{
				return (selected, link);
			}

			progress?.Report(info.Progress > 0 ? $"Real-Debrid is downloading it… {info.Progress / 100:P0}" : "Real-Debrid is getting the torrent…");
			await Task.Delay(s_pollInterval, ct);
		}
	}

	/// <summary>Ends a stream: removes its torrent from Real-Debrid.</summary>
	private async Task RemoveAsync(string id)
	{
		try
		{
			await SendAsync<JsonElement?>(Request("torrents", "delete", id).DeleteAsync());
		}
		catch (TorrentStreamException ex)
		{
			logger.LogWarning(ex, "Couldn't remove torrent {Id} from Real-Debrid", id);
		}
	}

	private IFlurlRequest Request(params object[] segments)
	{
		return Api.AppendPathSegments(segments).AllowAnyHttpStatus().WithOAuthBearerToken(settings.Value.ApiKey);
	}

	private static async Task<byte[]> GetTorrentFileAsync(Uri torrent, CancellationToken ct)
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

	/// <summary>Calls Real-Debrid's API; its errors become <see cref="TorrentStreamException"/>s with Real-Debrid's reason.</summary>
	private static async Task<T> SendAsync<T>(Task<IFlurlResponse> call)
	{
		try
		{
			var response = await call;
			if (response.StatusCode is >= 200 and < 300)
			{
				// 204: nothing to read.
				return response.StatusCode == 204 ? default! : await response.GetJsonAsync<T>();
			}

			var error = await response.GetJsonAsync<Error?>();
			throw new TorrentStreamException(error?.Message is { Length: > 0 } message
				? $"Real-Debrid: {message.Replace('_', ' ')}"
				: $"Real-Debrid couldn't stream it (HTTP {response.StatusCode}).");
		}
		catch (Exception ex) when (ex is FlurlHttpException or JsonException)
		{
			throw new TorrentStreamException($"Couldn't reach Real-Debrid: {ex.Message}", ex);
		}
	}

	// Real-Debrid's error response: "error" is a code like "bad_token", sent with a 4xx or 5xx status.
	private sealed record Error([property: JsonPropertyName("error")] string? Message);

	private sealed record Added([property: JsonPropertyName("id")] string Id);

	private sealed record Unrestricted([property: JsonPropertyName("download")] string Download);

	private sealed record Torrent(
		[property: JsonPropertyName("status")] string Status,
		[property: JsonPropertyName("progress")] double Progress,
		[property: JsonPropertyName("files")] List<RealDebridFile>? Files,
		[property: JsonPropertyName("links")] List<string>? Links);

	private sealed record RealDebridFile(
		[property: JsonPropertyName("id")] int Id,
		[property: JsonPropertyName("path")] string Path,
		[property: JsonPropertyName("bytes")] long Bytes);
}

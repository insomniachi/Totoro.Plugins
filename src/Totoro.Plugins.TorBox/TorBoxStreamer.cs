using System.Text.Json;
using System.Text.Json.Serialization;
using Flurl;
using Flurl.Http;
using Microsoft.Extensions.Logging;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.TorBox;

/// <summary>
/// Streams torrents with TorBox (https://api-docs.torbox.app), a debrid service: the torrent is added to the user's
/// TorBox account, TorBox downloads it on its servers (at once, if it has it cached), and the player streams the file
/// from TorBox's download link. When the stream ends the torrent is removed again, unless the settings keep it or the
/// user had it already.
/// </summary>
public sealed class TorBoxStreamer(IModuleSettings<Settings> settings, ILogger<TorBoxStreamer> logger) : ITorrentStreamer
{
	private const string Api = "https://api.torbox.app/v1/api";
	private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(2);

	// How many streams use each torrent this added (by TorBox's id), so ending one doesn't remove a torrent another still
	// plays. Static: the streamer is made anew for each use.
	private static readonly Lock s_streamsLock = new();
	private static readonly Dictionary<int, int> s_streams = [];

	public Guid Id => TorBoxModule.Id;

	public string Name => "TorBox";

	public bool IsAvailable() => !string.IsNullOrWhiteSpace(settings.Value.ApiKey);

	public async Task<TorrentStream> StreamAsync(Uri torrent, Func<IReadOnlyList<TorrentFile>, TorrentFile?> selectFile, IProgress<string>? progress,
												 CancellationToken ct)
	{
		progress?.Report("Adding it to TorBox…");
		// A .torrent is downloaded here and uploaded, as rqbit does, rather than left to TorBox to fetch.
		var file = torrent.Scheme is "http" or "https" ? await GetTorrentFileAsync(torrent, ct) : null;
		// What the user has already, which isn't removed: adding a torrent that's there may return it.
		// shortcut: only mylist's first page (TorBox's default size); page through it if users have more torrents than that.
		var existing = settings.Value.KeepTorrents
			? null
			: ((await SendAsync<List<Listed>?>(Request("torrents", "mylist").SetQueryParam("bypass_cache", "true").GetAsync(cancellationToken: ct), requireData: false)) ?? [])
			  .Select(x => x.Id).ToHashSet();
		var added = await SendAsync<Added>(Request("torrents", "createtorrent").PostMultipartAsync(content =>
		{
			if (file is null)
			{
				content.AddString("magnet", torrent.AbsoluteUri);
			}
			else
			{
				content.AddFile("file", new MemoryStream(file), "file.torrent");
			}

			if (settings.Value.OnlyCached)
			{
				content.AddString("add_only_if_cached", "true");
			}
		}, cancellationToken: ct));

		bool remove;
		lock (s_streamsLock)
		{
			remove = s_streams.ContainsKey(added.Id) || existing?.Contains(added.Id) == false;
			if (remove)
			{
				s_streams[added.Id] = s_streams.GetValueOrDefault(added.Id) + 1;
			}
		}

		try
		{
			var selected = await WaitForFileAsync(added.Id, selectFile, progress, ct);
			var url = await SendAsync<string>(Request("torrents", "requestdl").SetQueryParams(new { token = settings.Value.ApiKey, torrent_id = added.Id, file_id = selected.Index })
																			 .GetAsync(cancellationToken: ct));
			return new TorrentStream(new Uri(url), selected, remove ? () => new ValueTask(StopAsync(added.Id)) : null);
		}
		catch when (remove)
		{
			await StopAsync(added.Id);
			throw;
		}
	}

	/// <summary>The file of the torrent that <paramref name="selectFile"/> picks, once TorBox has all of it.</summary>
	private async Task<TorrentFile> WaitForFileAsync(int id, Func<IReadOnlyList<TorrentFile>, TorrentFile?> selectFile, IProgress<string>? progress,
													 CancellationToken ct)
	{
		TorrentFile? selected = null;
		while (true)
		{
			var info = await SendAsync<Torrent>(Request("torrents", "mylist").SetQueryParams(new { id, bypass_cache = "true" }).GetAsync(cancellationToken: ct));
			// Its files are known once TorBox has the torrent's metadata.
			if (selected is null && info.Files is { Count: > 0 } files)
			{
				selected = selectFile([.. files.Select(x => new TorrentFile(x.Id, x.Name, x.Size))])
						   ?? throw new TorrentStreamException("The torrent has no video to play.");
			}

			if (selected is not null && info.DownloadFinished && info.DownloadPresent)
			{
				return selected;
			}

			if (info.DownloadState.Contains("error", StringComparison.OrdinalIgnoreCase) ||
				info.DownloadState.Contains("failed", StringComparison.OrdinalIgnoreCase))
			{
				throw new TorrentStreamException($"TorBox couldn't download it: {info.DownloadState}");
			}

			progress?.Report(info.Progress > 0 ? $"TorBox is downloading it… {info.Progress:P0}" : "TorBox is getting the torrent…");
			await Task.Delay(s_pollInterval, ct);
		}
	}

	/// <summary>Ends a stream: removes the torrent from TorBox once no stream uses it.</summary>
	private async Task StopAsync(int id)
	{
		lock (s_streamsLock)
		{
			var count = s_streams.GetValueOrDefault(id) - 1;
			if (count > 0)
			{
				s_streams[id] = count;
				return;
			}

			s_streams.Remove(id);
		}

		try
		{
			await SendAsync<JsonElement?>(Request("torrents", "controltorrent").PostJsonAsync(new { torrent_id = id, operation = "delete" }), requireData: false);
		}
		catch (TorrentStreamException ex)
		{
			logger.LogWarning(ex, "Couldn't remove torrent {Id} from TorBox", id);
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

	/// <summary>Calls TorBox's API; its errors become <see cref="TorrentStreamException"/>s with TorBox's explanation.</summary>
	private static async Task<T> SendAsync<T>(Task<IFlurlResponse> call, bool requireData = true)
	{
		Response<T> response;
		try
		{
			response = await (await call).GetJsonAsync<Response<T>>();
		}
		catch (Exception ex) when (ex is FlurlHttpException or JsonException)
		{
			throw new TorrentStreamException($"Couldn't reach TorBox: {ex.Message}", ex);
		}

		if (response is { Success: true } && (response.Data is not null || !requireData))
		{
			return response.Data!;
		}

		throw new TorrentStreamException(response.Detail is { Length: > 0 } detail ? $"TorBox: {detail}" : "TorBox couldn't stream it.");
	}

	// TorBox's standard response: "detail" is a message meant for users, on success or failure.
	private sealed record Response<T>(
		[property: JsonPropertyName("success")] bool Success,
		[property: JsonPropertyName("detail")] string? Detail,
		[property: JsonPropertyName("data")] T? Data);

	private sealed record Added([property: JsonPropertyName("torrent_id")] int Id);

	private sealed record Listed([property: JsonPropertyName("id")] int Id);

	private sealed record Torrent(
		[property: JsonPropertyName("download_state")] string DownloadState,
		[property: JsonPropertyName("download_finished")] bool DownloadFinished,
		[property: JsonPropertyName("download_present")] bool DownloadPresent,
		[property: JsonPropertyName("progress")] double Progress,
		[property: JsonPropertyName("files")] List<TorBoxFile>? Files);

	private sealed record TorBoxFile(
		[property: JsonPropertyName("id")] int Id,
		[property: JsonPropertyName("name")] string Name,
		[property: JsonPropertyName("size")] long Size);
}

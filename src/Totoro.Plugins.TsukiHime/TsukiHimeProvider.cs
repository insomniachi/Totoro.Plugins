using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Flurl;
using Flurl.Http;
using Microsoft.Extensions.Logging;
using Totoro.Core.Modules.Anime;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.TsukiHime;

/// <summary>
/// TsukiHime (api.tsukihime.org): an index of anime torrents, each matched to a show (with its AniList/MyAnimeList ids)
/// and an episode. Videos are those torrents, which the player streams itself.
/// </summary>
public sealed partial class TsukiHimeProvider(
	IModuleSettings<Settings> moduleSettings,
	ILogger<TsukiHimeProvider> logger) : IAnimeProvider
{
	private const string Api = "https://api.tsukihime.org/v1";

	public Guid Id => TsukiHimeModule.Id;

	public string Name => "TsukiHime";

	private Settings _settings = moduleSettings.Value;

	public void UseSettings(DataContainerConfig settings)
	{
		_settings = (Settings)settings;
	}

	// There's no search for shows, only for torrents, each of which names its show.
	public async Task<List<ProviderShow>> SearchAsync(string query, CancellationToken ct)
	{
		if (query.Trim().Length < 2)
		{
			return [];
		}

		try
		{
			var response = await Api.AppendPathSegments("search", "torrents")
									.SetQueryParams(new { q = query, limit = 100 })
									.GetJsonAsync<TorrentPage>(cancellationToken: ct);
			return
			[
				.. response.Results
						   .Select(x => x.Anime)
						   .OfType<Anime>()
						   .DistinctBy(x => x.Id)
						   .Select(x => ToShow(x, Math.Max(TitleMatch.Score(query, x.Title), TitleMatch.Score(query, x.EnglishTitle ?? ""))))
						   .OrderByDescending(x => x.Score)
			];
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "TsukiHime search for \"{Query}\" failed", query);
			return [];
		}
	}

	public async Task<ProviderShow?> FindShowAsync(AnimeId ids, CancellationToken ct)
	{
		try
		{
			var anime = (ids.Anilist > 0 ? await GetAnimeAsync("anilist", ids.Anilist, ct) : null)
						?? (ids.MyAnimeList > 0 ? await GetAnimeAsync("mal", ids.MyAnimeList, ct) : null);
			return anime is null ? null : ToShow(anime, 1);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "TsukiHime lookup of AniList id {Anilist} / MyAnimeList id {Mal} failed", ids.Anilist, ids.MyAnimeList);
			return null;
		}
	}

	public async Task<List<ProviderEpisode>> GetEpisodesAsync(string showId, CancellationToken ct)
	{
		try
		{
			var episodes = await Api.AppendPathSegments("animes", showId, "episodes")
									.GetJsonAsync<List<Episode>>(cancellationToken: ct);
			return [.. episodes.Select(x => new ProviderEpisode(showId, x.Number.ToString(CultureInfo.InvariantCulture), x.Number))];
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Couldn't fetch episodes for show {ShowId}", showId);
			return [];
		}
	}

	public async Task<List<VideoServer>> GetServersAsync(ProviderEpisode episode, CancellationToken ct)
	{
		try
		{
			var response = await Api.AppendPathSegments("animes", episode.ShowId, "episodes", episode.Id)
									.SetQueryParam("limit", 100)
									.GetJsonAsync<TorrentPage>(cancellationToken: ct);
			return ToServers(response.Results, _settings);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Couldn't fetch servers for {ShowId} ep {Episode}", episode.ShowId, episode.Id);
			return [];
		}
	}

	/// <summary>The episode's torrents, the preferred audio first, then the preferred quality, then the best of the rest.</summary>
	internal static List<VideoServer> ToServers(IEnumerable<Torrent> torrents, Settings settings)
	{
		var preferred = settings.Quality.ToResolution();
		return
		[
			.. torrents
			   // shortcut: drops batches (several files), since the streamer plays the largest video, which needn't be this episode; pick the episode's file by name to keep them.
			   .Where(x => x.FileCount <= 1 && x.Btih.Length == 40)
			   .Select(x => (Torrent: x, Resolution: Resolution().Match(x.Name) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0))
			   .OrderByDescending(x => settings.Audio == AudioType.Dub ? x.Torrent.IsDub : x.Torrent.HasOriginalAudio)
			   .ThenByDescending(x => x.Resolution == preferred)
			   .ThenByDescending(x => x.Resolution)
			   .Select(x => new VideoServer(ServerName(x.Torrent, x.Resolution), TorrentUrl(x.Torrent), VideoServerKind.Torrent))
		];
	}

	private static string ServerName(Torrent torrent, int resolution)
	{
		var name = torrent.Group?.Name is { Length: > 0 } group ? group : "Unknown";
		if (resolution > 0)
		{
			name += $" {resolution}p";
		}
		return torrent.IsDub ? $"{name} (dub)" : name;
	}

	// Nyaa's .torrent lists trackers; a bare magnet link relies on DHT.
	private static Uri TorrentUrl(Torrent torrent) => torrent.NyaaId > 0
		? new Uri($"https://nyaa.si/download/{torrent.NyaaId}.torrent")
		: new Uri($"magnet:?xt=urn:btih:{torrent.Btih}&dn={Uri.EscapeDataString(torrent.Name)}");

	private static async Task<Anime?> GetAnimeAsync(string database, long id, CancellationToken ct)
	{
		using var response = await Api.AppendPathSegments("animes", database, id)
									  .AllowHttpStatus(404)
									  .GetAsync(cancellationToken: ct);
		return response.StatusCode == 404 ? null : await response.GetJsonAsync<Anime>();
	}

	private static ProviderShow ToShow(Anime anime, double score) => new(anime.Id.ToString(CultureInfo.InvariantCulture), anime.Title, score)
	{
		Image = anime.Thumbnail ?? "",
		// Search results don't carry the ids.
		Ids = anime.Anilist > 0 || anime.Mal > 0 ? new AnimeId { Anilist = anime.Anilist ?? 0, MyAnimeList = anime.Mal ?? 0 } : null,
	};

	[GeneratedRegex(@"\b(\d{3,4})p\b", RegexOptions.IgnoreCase)]
	private static partial Regex Resolution();

	internal sealed class TorrentPage
	{
		[JsonPropertyName("results")] public List<Torrent> Results { get; set; } = [];
	}

	internal sealed class Torrent
	{
		[JsonPropertyName("name")] public string Name { get; set; } = "";
		[JsonPropertyName("btih")] public string Btih { get; set; } = "";
		[JsonPropertyName("nyaa_id")] public long NyaaId { get; set; }
		[JsonPropertyName("filecount")] public int FileCount { get; set; }
		[JsonPropertyName("audiolangs")] public List<string> AudioLanguages { get; set; } = [];
		[JsonPropertyName("group")] public Group? Group { get; set; }
		[JsonPropertyName("anime")] public Anime? Anime { get; set; }

		public bool IsDub => AudioLanguages.Any(x => x.StartsWith("en", StringComparison.OrdinalIgnoreCase));

		// Unlisted languages are usually the original audio.
		public bool HasOriginalAudio => AudioLanguages.Count == 0 || AudioLanguages.Any(x => !x.StartsWith("en", StringComparison.OrdinalIgnoreCase));
	}

	internal sealed class Group
	{
		[JsonPropertyName("name")] public string Name { get; set; } = "";
	}

	internal sealed class Anime
	{
		[JsonPropertyName("id")] public long Id { get; set; }
		[JsonPropertyName("title")] public string Title { get; set; } = "";
		[JsonPropertyName("english_title")] public string? EnglishTitle { get; set; }
		[JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }
		[JsonPropertyName("anilist")] public long? Anilist { get; set; }
		[JsonPropertyName("mal")] public long? Mal { get; set; }
	}

	private sealed class Episode
	{
		[JsonPropertyName("episode_num")] public float Number { get; set; }
	}
}

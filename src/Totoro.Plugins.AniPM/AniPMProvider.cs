using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using Totoro.Core.Modules.Anime;
using Totoro.Core.Modules.Media;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;
using Totoro.Core.Web;

namespace Totoro.Plugins.AniPM;

/// <summary>
/// AniPM (after the Android extension at github.com/yuzono/anime-extensions, src/en/anipm): a JSON API for searching and a
/// show's episodes; an episode's playback bootstrap gives a selection, which buys a Settlar embed token, whose session has
/// the signed HLS stream and its subtitles. Settlar's Cloudflare turns away .NET and players (see <see cref="Curl"/>), so
/// the session is loaded in a browser (<see cref="IWebPageLoader"/>) and the stream played through <see cref="AniPMProxy"/>.
/// </summary>
/// <remarks>
/// A show's id here is <c>"{source}/{id}"</c> as the site has them (e.g. settlar/6351, or anilist/154587 for one found by
/// its AniList id), an episode's its number.
/// </remarks>
public sealed class AniPMProvider(
	IHttpClientFactory httpClientFactory,
	IModuleSettings<Settings> moduleSettings,
	IWebPageLoader pages,
	ILogger<AniPMProvider> logger) : IAnimeProvider
{
	internal const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

	private const string Embed = "https://embed.settlar.io";

	/// <summary>The site makes up AniList ids from here on for shows AniList hasn't.</summary>
	private const long SyntheticAniList = 800_000_000;

	private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

	public Guid Id => AniPMModule.Id;

	public string Name => "AniPM";

	// The module's settings, unless an anime's own are used instead.
	private Settings _settings = moduleSettings.Value;

	public void UseSettings(DataContainerConfig settings)
	{
		_settings = (Settings)settings;
	}

	private string Site => Uri.TryCreate(_settings.Site.Trim().TrimEnd('/'), UriKind.Absolute, out var site)
		? site.ToString().TrimEnd('/')
		: "https://ani.pm";

	public async Task<List<ProviderShow>> SearchAsync(string query, CancellationToken ct)
	{
		var results = await GetJsonAsync<Catalog>($"{Site}/api/anime/catalog?page=1&limit=30&q={Uri.EscapeDataString(query.Trim())}", ct);
		return
		[
			.. (results?.Items ?? [])
				.Where(x => x.Id > 0 && x.Source is { Length: > 0 } && x.Title is { Length: > 0 })
				.Select(x => new ProviderShow($"{x.Source}/{x.Id}", x.Title!, TitleMatch.Score(query, x.Title!))
				{
					Image = Cover(x.AnilistId, x.MalId, x.Poster),
					Ids = Ids(x.AnilistId, x.MalId),
				})
				.OrderByDescending(x => x.Score)
		];
	}

	public async Task<ProviderShow?> FindShowAsync(AnimeId ids, CancellationToken ct)
	{
		if (ids.Anilist is <= 0 or >= SyntheticAniList)
		{
			return null;
		}

		return await GetJsonAsync<Series>($"{Site}/api/anime/ani/{ids.Anilist}", ct) is { Episodes.Count: > 0 } series
			? new ProviderShow($"anilist/{ids.Anilist}", series.Title ?? "", 1) { Image = Cover(series.AnilistId, series.MalId, series.Poster), Ids = Ids(series.AnilistId, series.MalId) }
			: null;
	}

	public async Task<List<ProviderEpisode>> GetEpisodesAsync(string showId, CancellationToken ct)
	{
		var (source, id) = Split(showId);
		var series = await GetJsonAsync<Series>($"{Site}/api/anime/{(source == "anilist" ? "ani" : "series")}/{Uri.EscapeDataString(id)}", ct);
		return
		[
			.. (series?.Episodes ?? [])
				.Where(x => x.Sub || x.Dub || x.Subhard || x.Dubhard)
				.Select(x => new ProviderEpisode(showId, Number(x.Number), (float)x.Number))
		];
	}

	/// <summary>The episode's stream in each audio it has (the preferred one first), each with its subtitles and intro and outro.</summary>
	public async Task<List<VideoServer>> GetServersAsync(ProviderEpisode episode, CancellationToken ct)
	{
		var (source, id) = Split(episode.ShowId);
		var bootstrap = $"{Site}/api/anime/playback-bootstrap/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}?ep={Uri.EscapeDataString(episode.Id)}&lang=";
		if (await GetJsonAsync<Bootstrap>(bootstrap + "sub", ct) is not { } sub)
		{
			return [];
		}

		// The bootstrap's selection differs by audio (hard subs share the sub one); the dub's is only fetched if needed.
		Bootstrap? dub = null;
		var package = sub.AnipmPackages?.Episodes?.GetValueOrDefault(episode.Id);
		var channels = new[]
			{
				(Channel: "sub", Name: "Sub", Has: package?.Sub ?? sub.Availability?.Sub ?? false),
				(Channel: "subhard", Name: "Hard Sub", Has: package?.Subhard ?? false),
				(Channel: "dub", Name: "Dub", Has: package?.Dub ?? sub.Availability?.Dub ?? false),
				(Channel: "dubhard", Name: "Hard Dub", Has: package?.Dubhard ?? false),
			}
			.Where(x => x.Has)
			.OrderByDescending(x => _settings.Audio == AudioType.Dub && x.Channel.StartsWith("dub"));

		List<VideoServer> servers = [];
		foreach (var (channel, name, _) in channels)
		{
			try
			{
				var boot = channel.StartsWith("dub") ? dub ??= await GetJsonAsync<Bootstrap>(bootstrap + "dub", ct) : sub;
				if (boot?.SettlarSelection is { Length: > 0 } selection && await GetStreamAsync(name, selection, episode.Id, channel, ct) is { } server)
				{
					servers.Add(server with { Segments = Segments(boot.Skip) });
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
			{
				logger.LogWarning(ex, "Couldn't read AniPM's {Channel} stream of {Show} episode {Episode}", channel, episode.ShowId, episode.Id);
			}
		}

		return servers;
	}

	/// <summary>The selection's Settlar embed token, and with it the embed session's stream and subtitles.</summary>
	private async Task<VideoServer?> GetStreamAsync(string name, string selection, string episode, string channel, CancellationToken ct)
	{
		var session = await GetJsonAsync<SettlarSession>(
			$"{Site}/api/anime/settlar/session?selection={Uri.EscapeDataString(selection)}&provider=anipm&ep={Uri.EscapeDataString(episode)}&channel={channel}&telemetry=0", ct);
		if (!Uri.TryCreate(session?.EmbedUrl, UriKind.Absolute, out var embed) || HttpUtility.ParseQueryString(embed.Query)["t"] is not { Length: > 0 } token)
		{
			logger.LogWarning("AniPM gave no {Channel} embed token for episode {Episode}", channel, episode);
			return null;
		}

		var page = await pages.LoadAsync(new Uri($"{Embed}/api/embed/session?t={Uri.EscapeDataString(token)}"), new Uri($"{Site}/"), ct);
		if (JsonSerializer.Deserialize<EmbedSession>(page.Content, s_json) is not { Source: { Length: > 0 } source } stream || !Uri.TryCreate(source, UriKind.Absolute, out var url))
		{
			logger.LogWarning("Settlar has no {Channel} stream of episode {Episode}", channel, episode);
			return null;
		}

		return new VideoServer(name, AniPMProxy.Local(url, "playlist.m3u8"))
		{
			Subtitles =
			[
				.. (stream.Subtitles ?? [])
					.Where(x => Uri.IsWellFormedUriString(x.Url, UriKind.Absolute))
					.OrderByDescending(x => x.Default)
					.ThenByDescending(x => x.Srclang == "en")
					.Select(x => new SubtitleTrack(x.Label ?? x.Srclang ?? "Subtitle", AniPMProxy.Local(new Uri(x.Url!), "subtitle.vtt"), x.Srclang))
			],
		};
	}

	internal static List<MediaSegment> Segments(Skip? skip) =>
	[
		.. new[] { (MediaSegmentType.Opening, skip?.Op), (MediaSegmentType.Ending, skip?.Ed) }
			.Where(x => x.Item2 is { } range && range.End > range.Start)
			.Select(x => new MediaSegment(x.Item1, TimeSpan.FromSeconds(x.Item2!.Start), TimeSpan.FromSeconds(x.Item2.End)))
	];

	/// <summary>An episode number as the site writes it: 1, 12.5.</summary>
	internal static string Number(double number) => number.ToString(CultureInfo.InvariantCulture);

	private static (string Source, string Id) Split(string showId) =>
		showId.IndexOf('/') is > 0 and var slash ? (showId[..slash], showId[(slash + 1)..]) : ("settlar", showId);

	private static long Long(JsonElement? value) => value switch
	{
		{ ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var x) => x,
		{ ValueKind: JsonValueKind.String } text when long.TryParse(text.GetString(), CultureInfo.InvariantCulture, out var x) => x,
		_ => 0,
	};

	private static AnimeId? Ids(JsonElement? anilist, JsonElement? mal) =>
		(Long(anilist), Long(mal)) is var (a, m) && (a is > 0 and < SyntheticAniList || m > 0)
			? new AnimeId { Anilist = a < SyntheticAniList ? a : 0, MyAnimeList = m }
			: null;

	/// <summary>The cover by its ids, which keeps working; the poster's own path has a key the site rotates.</summary>
	private string Cover(JsonElement? anilist, JsonElement? mal, string? poster) =>
		(Long(anilist), Long(mal)) is var (a, m) && a is > 0 and < SyntheticAniList
			? $"{Site}/api/anime/cover?anilistId={a}{(m > 0 ? $"&malId={m}" : "")}"
			: poster is { Length: > 0 } ? (poster.StartsWith('/') ? Site + poster : poster) : "";

	/// <summary>An API call's JSON; null when it fails (a show it doesn't have, say).</summary>
	private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
	{
		using var client = httpClientFactory.CreateClient("AniPM");
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Referrer = new Uri($"{Site}/");
		using var response = await client.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode)
		{
			logger.LogWarning("AniPM answered {Url} with {Status}", url, response.StatusCode);
			return default;
		}

		return await response.Content.ReadFromJsonAsync<T>(s_json, ct);
	}

	private sealed record Catalog(List<Show>? Items);

	// The ids are numbers in some answers, strings in others.
	private sealed record Show(long Id, string? Source, string? Title, string? Poster, JsonElement? AnilistId, JsonElement? MalId);

	private sealed record Series(string? Title, string? Poster, JsonElement? AnilistId, JsonElement? MalId, List<SeriesEpisode>? Episodes);

	private sealed record SeriesEpisode(double Number, bool Sub, bool Dub, bool Subhard, bool Dubhard);

	private sealed record Bootstrap(Availability? Availability, Skip? Skip, string? SettlarSelection, Packages? AnipmPackages);

	private sealed record Availability(bool Sub, bool Dub);

	/// <param name="Episodes">By episode number, as <see cref="Number"/> writes it.</param>
	private sealed record Packages(Dictionary<string, Package>? Episodes);

	private sealed record Package(bool? Sub, bool? Dub, bool? Subhard, bool? Dubhard);

	internal sealed record Skip(Range? Op, Range? Ed);

	/// <param name="Start">In seconds.</param>
	internal sealed record Range(double Start, double End);

	/// <param name="EmbedUrl">e.g. https://embed.settlar.io/embed/v1?t={token}</param>
	private sealed record SettlarSession(string? EmbedUrl);

	private sealed record EmbedSession(string? Source, List<EmbedSubtitle>? Subtitles);

	private sealed record EmbedSubtitle(string? Url, string? Label, string? Srclang, bool Default);
}

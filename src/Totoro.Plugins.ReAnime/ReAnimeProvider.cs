using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Totoro.Core.Modules.Anime;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.ReAnime;

/// <summary>
/// Re:ANIME (after the Android extension at github.com/yuzono/anime-extensions, src/en/reanime): its JSON API for
/// searching, episodes and an episode's servers, which are FlixCloud embeds. Of those, it plays FlixCloud's download
/// of the original file, an MKV a player opens and seeks directly. The embeds' HLS streams aren't used: their segments
/// are scrambled and their addresses encrypted, which takes a local proxy and a third-party decryption service.
/// </summary>
/// <remarks>A show's id here is <c>"{Re:ANIME's slug}:{AniList id}"</c>: episodes go by the one, servers by the other.</remarks>
public sealed partial class ReAnimeProvider(
	IHttpClientFactory httpClientFactory,
	IModuleSettings<Settings> moduleSettings,
	ILogger<ReAnimeProvider> logger) : IAnimeProvider
{
	private const string FlixCloud = "https://flixcloud.cc";

	public Guid Id => ReAnimeModule.Id;

	public string Name => "Re:ANIME";

	// The module's settings, unless an anime's own are used instead.
	private Settings _settings = moduleSettings.Value;

	public void UseSettings(DataContainerConfig settings)
	{
		_settings = (Settings)settings;
	}

	private string Site => Uri.TryCreate(_settings.Site.Trim().TrimEnd('/'), UriKind.Absolute, out var site)
		? site.ToString().TrimEnd('/')
		: "https://reanime.to";

	public async Task<List<ProviderShow>> SearchAsync(string query, CancellationToken ct)
	{
		var response = await GetJsonAsync<SearchResponse>($"{Site}/api/v1/search?q={Uri.EscapeDataString(query)}&limit=36&offset=0", $"{Site}/search", ct);
		return
		[
			.. (response?.Results ?? [])
				// It also lists shows with nothing to watch yet (unreleased, shorts it hasn't got), which its site hides.
				.Where(x => x.AnilistId > 0 && x.Title?.Name is { Length: > 0 } && (x.Subbed > 0 || x.Dubbed > 0))
				.Select(x => new ProviderShow(
					$"{x.AnimeId}:{x.AnilistId}",
					x.Title!.Name!,
					x.Title.All.Max(title => TitleMatch.Score(query, title)))
					{ Image = x.Cover?.ExtraLarge ?? x.Cover?.Large ?? "", Ids = new AnimeId { Anilist = x.AnilistId } })
				.OrderByDescending(x => x.Score)
		];
	}

	public async Task<List<ProviderEpisode>> GetEpisodesAsync(string showId, CancellationToken ct)
	{
		var (slug, _) = ParseShowId(showId);
		var response = await GetJsonAsync<EpisodeList>($"{Site}/api/v1/anime/{Uri.EscapeDataString(slug)}/episodes?limit=2000", $"{Site}/anime/{slug}", ct);
		return
		[
			.. (response?.Data ?? [])
				.Where(x => x.Playable)
				.Select(x => new ProviderEpisode(
					showId,
					x.EpisodeId?.Replace("ep-", "") ?? x.Number.ToString(CultureInfo.InvariantCulture),
					(float)x.Number))
		];
	}

	/// <summary>
	/// The episode's file from each of its FlixCloud videos (often one, with both audio tracks), the preferred audio's
	/// first. Each is ready to play: FlixCloud may first have to prepare the file.
	/// </summary>
	public async Task<List<VideoServer>> GetServersAsync(ProviderEpisode episode, CancellationToken ct)
	{
		var (slug, anilistId) = ParseShowId(episode.ShowId);
		var response = await GetJsonAsync<ServerList>($"{Site}/api/flix/{anilistId}/{episode.Id}", $"{Site}/watch/{slug}?ep={episode.Id}", ct);
		var audio = _settings.Audio == AudioType.Dub ? "dub" : "sub";

		// Servers (HD-1, HD-2) and audio types are ways to play the same video, whose id is in the embed's address.
		var videos = (response?.Servers ?? [])
					 .Select(x => (Video: EmbedId().Match(x.DataLink ?? "") is { Success: true } match ? match.Groups["id"].Value : null, x.DataType))
					 .Where(x => x.Video is not null)
					 .OrderByDescending(x => x.DataType == audio)
					 .GroupBy(x => x.Video!, x => x.DataType ?? "");

		List<VideoServer> servers = [];
		foreach (var video in videos)
		{
			try
			{
				if (await GetDownloadAsync(video.Key, ct) is { } download)
				{
					var audios = string.Join(" & ", video.Distinct().Select(CultureInfo.InvariantCulture.TextInfo.ToTitleCase));
					servers.Add(new VideoServer($"{download.Resolution} {audios}".Trim(), download.Url));
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
			{
				logger.LogWarning(ex, "Couldn't get FlixCloud's file for video {Video}", video.Key);
			}
		}

		return servers;
	}

	/// <summary>The video's file once FlixCloud has it ready; null if it has no download.</summary>
	private async Task<(Uri Url, string Resolution)?> GetDownloadAsync(string video, CancellationToken ct)
	{
		using var client = httpClientFactory.CreateClient("ReAnime");

		// The download page's SvelteKit data: the file's id, a token for it, and the server it's on.
		var data = await client.GetStringAsync($"{FlixCloud}/d/{video}/__data.json", ct);
		var fileId = FileId().Match(data);
		var token = Jwt().Match(data);
		if (!fileId.Success || !token.Success)
		{
			return null;
		}

		var server = FetchServer().Match(data) is { Success: true } match ? match.Value : FlixCloud;
		var query = $"{fileId.Value}?token={token.Value}";

		// Progress events (Server-Sent Events) until it's ready; they don't end on their own.
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));
		using var progress = await client.GetStreamAsync($"{server}/download/{fileId.Value}/progress?token={token.Value}", timeout.Token);
		using var reader = new StreamReader(progress);
		while (await reader.ReadLineAsync(timeout.Token) is { } line)
		{
			switch (ProgressStatus().Match(line).Groups["status"].Value)
			{
				case "ready":
					// It redirects to the file, on another host that needs no headers.
					return (new Uri($"{server}/download/{query}"), Resolution().Match(data).Value);
				case "failed":
					throw new InvalidOperationException("FlixCloud couldn't prepare the file.");
			}
		}

		return null;
	}

	private static (string Slug, string AnilistId) ParseShowId(string showId)
	{
		var separator = showId.LastIndexOf(':');
		return separator < 0
			? throw new InvalidOperationException("That's not a Re:ANIME show. Pick its show again.")
			: (showId[..separator], showId[(separator + 1)..]);
	}

	/// <summary>An API call's JSON; null when it fails (a show it doesn't have, say).</summary>
	private async Task<T?> GetJsonAsync<T>(string url, string referrer, CancellationToken ct)
	{
		using var client = httpClientFactory.CreateClient("ReAnime");
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Referrer = new Uri(referrer);
		using var response = await client.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode)
		{
			logger.LogWarning("Re:ANIME answered {Url} with {Status}", url, response.StatusCode);
			return default;
		}

		return await response.Content.ReadFromJsonAsync<T>(ct);
	}

	private sealed record SearchResponse(List<Anime>? Results);

	private sealed record Anime(
		[property: JsonPropertyName("anime_id")] string AnimeId,
		[property: JsonPropertyName("anilist_id")] int AnilistId,
		Title? Title,
		[property: JsonPropertyName("cover_image")] CoverImage? Cover,
		int Subbed,
		int Dubbed);

	// AniList's covers: "extra_large" is AniList's large.
	private sealed record CoverImage([property: JsonPropertyName("extra_large")] string? ExtraLarge, string? Large);

	private sealed record Title(string? English, string? Romaji, string? Native)
	{
		public string? Name => new[] { Romaji, English, Native }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

		public IEnumerable<string> All => new[] { Romaji, English, Native }.Where(x => !string.IsNullOrWhiteSpace(x))!;
	}

	private sealed record EpisodeList(List<Episode>? Data);

	private sealed record Episode(
		string? EpisodeId,
		[property: JsonPropertyName("episode_number")] double Number,
		bool Playable = true);

	private sealed record ServerList(List<Server>? Servers);

	/// <param name="DataLink">The FlixCloud embed, e.g. https://flixcloud.cc/e/rjyccuaq8h5n?v=1.</param>
	/// <param name="DataType">"sub" or "dub".</param>
	private sealed record Server(string? DataLink, string? DataType);

	[GeneratedRegex(@"/e/(?<id>[a-z0-9]+)")]
	private static partial Regex EmbedId();

	[GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
	private static partial Regex FileId();

	[GeneratedRegex(@"eyJ[\w-]+\.[\w-]+\.[\w-]+")]
	private static partial Regex Jwt();

	[GeneratedRegex(@"https://fetch\d*\.flixcloud\.cc")]
	private static partial Regex FetchServer();

	[GeneratedRegex(@"\d{3,4}p")]
	private static partial Regex Resolution();

	[GeneratedRegex(@"""status"":\s*""(?<status>\w+)""")]
	private static partial Regex ProgressStatus();
}

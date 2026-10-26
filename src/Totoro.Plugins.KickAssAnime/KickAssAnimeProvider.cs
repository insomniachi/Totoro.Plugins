using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.KickAssAnime;

/// <summary>
/// KickAssAnime (after the Android extension at github.com/yuzono/anime-extensions, src/en/kickassanime): its JSON API for
/// searching, a show's episodes in each language and an episode's servers, which are players on another host. A
/// player's page has its HLS or DASH manifest (with every audio track) and its subtitle files in plain sight.
/// </summary>
/// <remarks>A show's id here is its slug, an episode's <c>"ep-{number}-{slug}"</c>, both as the site has them.</remarks>
public sealed partial class KickAssAnimeProvider(
	IHttpClientFactory httpClientFactory,
	IModuleSettings<Settings> moduleSettings,
	ILogger<KickAssAnimeProvider> logger) : IAnimeProvider
{
	public Guid Id => KickAssAnimeModule.Id;

	public string Name => "KickAssAnime";

	// The module's settings, unless an anime's own are used instead.
	private Settings _settings = moduleSettings.Value;

	public void UseSettings(DataContainerConfig settings)
	{
		_settings = (Settings)settings;
	}

	private string Site => Uri.TryCreate(_settings.Site.Trim().TrimEnd('/'), UriKind.Absolute, out var site)
		? site.ToString().TrimEnd('/')
		: "https://kaa.lt";

	public async Task<List<ProviderShow>> SearchAsync(string query, CancellationToken ct)
	{
		using var client = httpClientFactory.CreateClient("KickAssAnime");
		using var response = await client.PostAsJsonAsync($"{Site}/api/fsearch", new { page = 1, query }, ct);
		if (!response.IsSuccessStatusCode)
		{
			logger.LogWarning("KickAssAnime answered a search with {Status}", response.StatusCode);
			return [];
		}

		var results = (await response.Content.ReadFromJsonAsync<SearchResponse>(ct))?.Result ?? [];
		return
		[
			// Shows with nothing to watch yet have no watch_uri.
			.. results.Where(x => x.WatchUri is not null && x.Title is { Length: > 0 })
					  .Select(x => new ProviderShow(
						  x.Slug,
						  x.Title,
						  new[] { x.Title, x.TitleEn }.Where(title => !string.IsNullOrWhiteSpace(title)).Max(title => TitleMatch.Score(query, title!)))
						  { Image = x.Poster?.Hq is { Length: > 0 } poster ? $"{Site}/image/poster/{poster}.webp" : "" })
					  .OrderByDescending(x => x.Score)
		];
	}

	/// <summary>The episodes in the preferred audio's language, else in the first other language that has them.</summary>
	public async Task<List<ProviderEpisode>> GetEpisodesAsync(string showId, CancellationToken ct)
	{
		var show = $"{Site}/api/show/{Uri.EscapeDataString(showId)}";
		var preferred = _settings.Audio == AudioType.Dub ? "en-US" : "ja-JP";
		var languages = (await GetJsonAsync<LanguageList>($"{show}/language", ct))?.Result ?? [];
		foreach (var language in languages.OrderByDescending(x => x == preferred))
		{
			var url = $"{show}/episodes?lang={Uri.EscapeDataString(language)}&page=";
			if (await GetJsonAsync<EpisodePage>(url + 1, ct) is not { Result.Count: > 0 } first)
			{
				continue;
			}

			var rest = await Task.WhenAll(Enumerable.Range(2, Math.Max(0, (first.Pages?.Count ?? 1) - 1))
														   .Select(page => GetJsonAsync<EpisodePage>(url + page, ct)));
			return
			[
				.. first.Result.Concat(rest.SelectMany(x => x?.Result ?? []))
					 .Select(x => new ProviderEpisode(
						 showId,
						 $"ep-{x.EpisodeString}-{x.Slug}",
						 float.TryParse(x.EpisodeString, CultureInfo.InvariantCulture, out var number) ? number : x.EpisodeNumber))
			];
		}

		return [];
	}

	/// <summary>Each of the episode's players' streams (all its audio in one), with their subtitle files, English first.</summary>
	public async Task<List<VideoServer>> GetServersAsync(ProviderEpisode episode, CancellationToken ct)
	{
		var response = await GetJsonAsync<ServerList>($"{Site}/api/show/{Uri.EscapeDataString(episode.ShowId)}/episode/{Uri.EscapeDataString(episode.Id)}", ct);
		List<VideoServer> servers = [];
		foreach (var server in response?.Servers ?? [])
		{
			try
			{
				if (Uri.TryCreate(server.Src, UriKind.Absolute, out var player) && await GetServerAsync(server.Name ?? player.Host, player, ct) is { } found)
				{
					servers.Add(found);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
			{
				logger.LogWarning(ex, "Couldn't read KickAssAnime's player {Player}", server.Src);
			}
		}

		return servers;
	}

	private async Task<VideoServer?> GetServerAsync(string name, Uri player, CancellationToken ct)
	{
		using var client = httpClientFactory.CreateClient("KickAssAnime");
		return ParsePlayer(name, player, await client.GetStringAsync(player, ct));
	}

	/// <summary>The stream in a player's page (its Astro props: "manifest":[0,"https://.../master.m3u8"]), and its subtitles; null if it has none.</summary>
	internal static VideoServer? ParsePlayer(string name, Uri player, string html)
	{
		html = html.Replace("&quot;", "\"");
		if (Manifest().Match(html) is not { Success: true } manifest || !Uri.TryCreate(FixUrl(manifest.Groups["url"].Value), UriKind.Absolute, out var url))
		{
			return null;
		}

		List<SubtitleTrack> subtitles =
		[
			.. Subtitle().Matches(html)
						 .Select(x => (Language: x.Groups["language"].Value, Name: x.Groups["name"].Value, Url: FixUrl(x.Groups["src"].Value.Replace("\\/", "/"))))
						 .Where(x => Uri.IsWellFormedUriString(x.Url, UriKind.Absolute))
						 .DistinctBy(x => x.Url)
						 .OrderByDescending(x => x.Language == "en")
						 .Select(x => new SubtitleTrack(x.Name, new Uri(x.Url), x.Language))
		];

		return new VideoServer(name, url)
		{
			// The stream's host turns away requests that aren't from its player's page.
			Headers = new Dictionary<string, string> { ["Origin"] = $"{player.Scheme}://{player.Authority}" },
			Subtitles = subtitles,
		};
	}

	/// <summary>An absolute URL from the page's: some have too many slashes (https:////host/...), some none (//host/...).</summary>
	private static string FixUrl(string url) => ExtraSlashes().Replace(url.Trim(), "https://");

	/// <summary>An API call's JSON; null when it fails (a show it doesn't have, say).</summary>
	private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
	{
		using var client = httpClientFactory.CreateClient("KickAssAnime");
		using var response = await client.GetAsync(url, ct);
		if (!response.IsSuccessStatusCode)
		{
			logger.LogWarning("KickAssAnime answered {Url} with {Status}", url, response.StatusCode);
			return default;
		}

		return await response.Content.ReadFromJsonAsync<T>(ct);
	}

	private sealed record SearchResponse(List<Show>? Result);

	private sealed record Show(
		string Slug,
		string Title,
		[property: JsonPropertyName("title_en")] string? TitleEn,
		Poster? Poster,
		[property: JsonPropertyName("watch_uri")] string? WatchUri);

	/// <param name="Hq">The poster's name, e.g. sousou-no-frieren-f6d4-hq, at /image/poster/{name}.webp.</param>
	private sealed record Poster(string? Hq);

	private sealed record LanguageList(List<string>? Result);

	/// <param name="Pages">One per page of episodes; only their count matters.</param>
	private sealed record EpisodePage(List<object>? Pages, List<Episode>? Result);

	private sealed record Episode(
		string Slug,
		[property: JsonPropertyName("episode_string")] string EpisodeString,
		[property: JsonPropertyName("episode_number")] float EpisodeNumber);

	private sealed record ServerList(List<Server>? Servers);

	/// <param name="Src">The player, e.g. https://krussdomi.com/cat-player/player?id=...</param>
	private sealed record Server(string? Name, string? Src);

	[GeneratedRegex(@"manifest"":\[0,""(?<url>[^""]+)""\]")]
	private static partial Regex Manifest();

	// Other properties (e.g. "filename") may come between them.
	[GeneratedRegex(@"""language"":\[\d+,""(?<language>[^""]+)""\][^}]+?""name"":\[\d+,""(?<name>[^""]+)""\][^}]+?""src"":\[\d+,""(?<src>[^""]+)""\]")]
	private static partial Regex Subtitle();

	[GeneratedRegex(@"^(?:https?:)?/{2,}")]
	private static partial Regex ExtraSlashes();
}

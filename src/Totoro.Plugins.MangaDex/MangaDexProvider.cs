using System.Globalization;
using System.Net;
using System.Text.Json;
using Flurl.Http;
using Totoro.Core.Modules.Manga;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.MangaDex;

/// <summary>
/// MangaDex (api.mangadex.org), through its JSON API. Its manga link to AniList and MyAnimeList, which matches database
/// manga exactly. Licensed series often have no chapters (taken down) or only links to official sites, which are left
/// out. Ported from keiyoushi's extension.
/// </summary>
public sealed class MangaDexProvider(IModuleSettings<Settings> settings) : IMangaProvider
{
	private const string Api = "https://api.mangadex.org";
	private const string UserAgent = "Totoro (https://github.com/insomniachi/Totoro)";

	// Chapters per request: the most the feed gives.
	private const int FeedPageSize = 500;

	// Safe and suggestive, as MangaDex shows by default.
	private const string ContentRatings = "&contentRating[]=safe&contentRating[]=suggestive";

	public Guid Id => MangaDexModule.Id;

	public string Name => "MangaDex";

	public async Task<List<ProviderManga>> SearchAsync(string query, CancellationToken ct)
	{
		using var json = await GetAsync($"{Api}/manga?title={Uri.EscapeDataString(query)}&limit=20&includes[]=cover_art&order[relevance]=desc{ContentRatings}", ct);
		return [.. json.RootElement.GetProperty("data").EnumerateArray().Select(x => ToManga(x, query))];
	}

	/// <summary>A search result whose AniList or MyAnimeList link is the manga's; else the best title match.</summary>
	public async Task<ProviderManga?> FindMangaAsync(MangaModel manga, CancellationToken ct)
	{
		foreach (var title in manga.AllTitles.Take(2))
		{
			using var json = await GetAsync($"{Api}/manga?title={Uri.EscapeDataString(title)}&limit=20&includes[]=cover_art&order[relevance]=desc{ContentRatings}", ct);
			if (json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(x => LinksTo(x, manga.ExternalIds)) is { ValueKind: JsonValueKind.Object } match)
			{
				return ToManga(match, title);
			}
		}

		return await IMangaProvider.MatchByTitleAsync(this, manga, ct);
	}

	public static bool LinksTo(JsonElement manga, MangaId ids)
	{
		if (!manga.GetProperty("attributes").TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		return ids.Anilist > 0 && links.TryGetProperty("al", out var al) && al.GetString() == ids.Anilist.ToString(CultureInfo.InvariantCulture)
			   || ids.MyAnimeList > 0 && links.TryGetProperty("mal", out var mal) && mal.GetString() == ids.MyAnimeList.ToString(CultureInfo.InvariantCulture);
	}

	private static ProviderManga ToManga(JsonElement manga, string query)
	{
		var id = manga.GetProperty("id").GetString()!;
		var titles = manga.GetProperty("attributes").GetProperty("title");
		// Titles are by language; English if there's one.
		var title = titles.TryGetProperty("en", out var en) ? en.GetString()! : titles.EnumerateObject().FirstOrDefault().Value.GetString() ?? id;
		var cover = manga.GetProperty("relationships").EnumerateArray()
			.FirstOrDefault(x => x.GetProperty("type").GetString() == "cover_art" && x.TryGetProperty("attributes", out _));
		return new ProviderManga(id, title, TitleMatch.Score(query, title))
		{
			Image = cover.ValueKind == JsonValueKind.Object
				? $"https://uploads.mangadex.org/covers/{id}/{cover.GetProperty("attributes").GetProperty("fileName").GetString()}.256.jpg"
				: "",
		};
	}

	public async Task<List<ProviderChapter>> GetChaptersAsync(string mangaId, CancellationToken ct)
	{
		var language = Uri.EscapeDataString(settings.Value.Language);
		List<JsonElement> chapters = [];
		for (var offset = 0; ; offset += FeedPageSize)
		{
			using var json = await GetAsync($"{Api}/manga/{mangaId}/feed?translatedLanguage[]={language}&order[chapter]=asc&limit={FeedPageSize}&offset={offset}" +
											$"&includeExternalUrl=0&includeEmptyPages=0&includeFuturePublishAt=0{ContentRatings}", ct);
			chapters.AddRange(json.RootElement.GetProperty("data").EnumerateArray().Select(x => x.Clone()));
			if (offset + FeedPageSize >= json.RootElement.GetProperty("total").GetInt32())
			{
				break;
			}
		}

		return ToChapters(chapters, mangaId);
	}

	/// <summary>One per number (several groups often translate the same chapter: the first is kept), in reading order.</summary>
	public static List<ProviderChapter> ToChapters(IEnumerable<JsonElement> chapters, string mangaId)
	{
		return
		[
			.. chapters
				.Select(x => (Id: x.GetProperty("id").GetString()!, Attributes: x.GetProperty("attributes")))
				// Hosted elsewhere (an official site), or with no pages: nothing to read here.
				.Where(x => !(x.Attributes.TryGetProperty("externalUrl", out var url) && url.ValueKind == JsonValueKind.String)
							&& (!x.Attributes.TryGetProperty("pages", out var pages) || pages.GetInt32() > 0))
				.Select(x =>
				{
					// A oneshot has no number.
					var number = x.Attributes.GetProperty("chapter").GetString() is { } text && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
					var name = x.Attributes.TryGetProperty("title", out var title) ? title.GetString() : null;
					return new ProviderChapter(mangaId, x.Id, number)
					{
						Title = string.IsNullOrWhiteSpace(name) ? "" : name,
						Volume = x.Attributes.TryGetProperty("volume", out var volume) ? volume.GetString() : null,
						Language = x.Attributes.GetProperty("translatedLanguage").GetString(),
					};
				})
				.DistinctBy(x => x.Number)
				.OrderBy(x => x.Number)
		];
	}

	/// <summary>The pages on the MangaDex@Home server MangaDex hands out for the chapter (its addresses expire after a while).</summary>
	public async Task<List<MangaPage>> GetPagesAsync(ProviderChapter chapter, CancellationToken ct)
	{
		using var json = await GetAsync($"{Api}/at-home/server/{chapter.Id}", ct);
		var root = json.RootElement;
		var (folder, files) = settings.Value.DataSaver ? ("data-saver", "dataSaver") : ("data", "data");
		var baseUrl = root.GetProperty("baseUrl").GetString();
		var hash = root.GetProperty("chapter").GetProperty("hash").GetString();
		return [.. root.GetProperty("chapter").GetProperty(files).EnumerateArray().Select(x => new MangaPage(new Uri($"{baseUrl}/{folder}/{hash}/{x.GetString()}")))];
	}

	/// <summary>A request to the API, waiting and trying again when it's asked to slow down (it allows about 5 a second).</summary>
	private static async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
	{
		for (var attempt = 1; ; attempt++)
		{
			var response = await url.WithHeader("User-Agent", UserAgent).AllowHttpStatus(429).GetAsync(cancellationToken: ct);
			if (response.StatusCode != (int)HttpStatusCode.TooManyRequests || attempt == 3)
			{
				if (response.StatusCode == (int)HttpStatusCode.TooManyRequests)
				{
					throw new HttpRequestException("MangaDex is limiting requests; try again in a minute.", null, HttpStatusCode.TooManyRequests);
				}

				return JsonDocument.Parse(await response.GetStringAsync());
			}

			var wait = response.ResponseMessage.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt);
			await Task.Delay(wait, ct);
		}
	}
}

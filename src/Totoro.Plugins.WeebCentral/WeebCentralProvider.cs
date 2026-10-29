using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Flurl.Http;
using HtmlAgilityPack;
using Totoro.Core.Modules.Manga;
using Totoro.Core.Modules.Streaming;

namespace Totoro.Plugins.WeebCentral;

/// <summary>
/// Weeb Central (weebcentral.com): HTML pages, fetched as a browser would (it turns away other user agents). Its series
/// pages link to AniList and MyAnimeList, which matches database manga exactly. Ported from keiyoushi's extension.
/// </summary>
public sealed partial class WeebCentralProvider : IMangaProvider
{
	private const string BaseUrl = "https://weebcentral.com";
	private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36";

	// Search results whose series pages are checked for database links.
	private const int CandidatesToCheck = 3;

	public Guid Id => WeebCentralModule.Id;

	public string Name => "Weeb Central";

	public async Task<List<ProviderManga>> SearchAsync(string query, CancellationToken ct)
	{
		// Its search finds nothing for punctuation it doesn't expect, even in its own titles ("Onepunch-Man"): words only.
		var html = await GetAsync($"{BaseUrl}/search/data?text={Uri.EscapeDataString(TitleMatch.Normalize(query))}&sort=Best%20Match&order=Ascending&official=Any&anime=Any&adult=Any&display_mode=Full%20Display&limit=32&offset=0", ct);
		return ParseSearch(html, query);
	}

	/// <summary>Results in the site's order (best match first), each once: a result's markup links to its series more than once.</summary>
	public static List<ProviderManga> ParseSearch(string html, string query)
	{
		var doc = new HtmlDocument();
		doc.LoadHtml(html);
		return
		[
			.. (doc.DocumentNode.SelectNodes("//a[contains(@href, '/series/')]") ?? Enumerable.Empty<HtmlNode>())
				.Select(a => (Match: SeriesId().Match(a.GetAttributeValue("href", "")), Image: a.SelectSingleNode(".//img")))
				.Where(x => x.Match.Success && x.Image is not null)
				.DistinctBy(x => x.Match.Groups[1].Value)
				.Select(x =>
				{
					var title = WebUtility.HtmlDecode(x.Image!.GetAttributeValue("alt", "")).Replace(" cover", "").Trim();
					return new ProviderManga(x.Match.Groups[1].Value, title, TitleMatch.Score(query, title))
					{
						Image = x.Image.GetAttributeValue("src", ""),
					};
				})
		];
	}

	/// <summary>Its search's top results whose series page links to the manga's AniList or MyAnimeList entry; else the best title match.</summary>
	public async Task<ProviderManga?> FindMangaAsync(MangaModel manga, CancellationToken ct)
	{
		foreach (var title in manga.AllTitles.Take(2))
		{
			foreach (var candidate in (await SearchAsync(title, ct)).Take(CandidatesToCheck))
			{
				var page = await GetAsync($"{BaseUrl}/series/{candidate.Id}", ct);
				if (LinksTo(page, manga.ExternalIds))
				{
					return candidate;
				}
			}
		}

		// No links to go by: the title match, as other sources do.
		return await IMangaProvider.MatchByTitleAsync(this, manga, ct);
	}

	/// <summary>Whether a series page links to the manga's AniList or MyAnimeList entry (and not one whose id starts the same).</summary>
	public static bool LinksTo(string seriesPage, MangaId ids)
	{
		return ids.Anilist > 0 && Regex.IsMatch(seriesPage, $@"anilist\.co/manga/{ids.Anilist}(?!\d)")
			   || ids.MyAnimeList > 0 && Regex.IsMatch(seriesPage, $@"myanimelist\.net/manga/{ids.MyAnimeList}(?!\d)");
	}

	public async Task<List<ProviderChapter>> GetChaptersAsync(string mangaId, CancellationToken ct)
	{
		return ParseChapters(await GetAsync($"{BaseUrl}/series/{mangaId}/full-chapter-list", ct), mangaId);
	}

	/// <summary>The list is newest first, labelled e.g. "Chapter 242" or "Mag Version 242"; one chapter per number, in reading order.</summary>
	public static List<ProviderChapter> ParseChapters(string html, string mangaId)
	{
		var doc = new HtmlDocument();
		doc.LoadHtml(html);
		return
		[
			.. (doc.DocumentNode.SelectNodes("//a[contains(@href, '/chapters/')]") ?? Enumerable.Empty<HtmlNode>())
				.Select(a =>
				{
					var id = ChapterId().Match(a.GetAttributeValue("href", ""));
					var label = WebUtility.HtmlDecode(a.SelectSingleNode(".//span[contains(@class, 'grow')]/span")?.InnerText ?? "").Trim();
					var number = Numbers().Matches(label).LastOrDefault();
					return id.Success && number is not null
						? new ProviderChapter(mangaId, id.Groups[1].Value, float.Parse(number.Value, CultureInfo.InvariantCulture)) { Title = label, Language = "en" }
						: null;
				})
				.OfType<ProviderChapter>()
				.Reverse()
				.DistinctBy(x => x.Number)
				.OrderBy(x => x.Number)
		];
	}

	public async Task<List<MangaPage>> GetPagesAsync(ProviderChapter chapter, CancellationToken ct)
	{
		var html = await GetAsync($"{BaseUrl}/chapters/{chapter.Id}/images?is_prev=False&current_page=1&reading_style=long_strip", ct);
		return ParsePages(html);
	}

	public static List<MangaPage> ParsePages(string html)
	{
		var doc = new HtmlDocument();
		doc.LoadHtml(html);
		return
		[
			.. (doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
				.Select(x => x.GetAttributeValue("src", ""))
				.Where(x => x.StartsWith("http", StringComparison.Ordinal) && !x.Contains("/static/", StringComparison.Ordinal))
				.Select(x => new MangaPage(new Uri(x)))
		];
	}

	private static Task<string> GetAsync(string url, CancellationToken ct) =>
		url.WithHeader("User-Agent", UserAgent).WithHeader("Referer", BaseUrl + "/").GetStringAsync(cancellationToken: ct);

	[GeneratedRegex(@"/series/([0-9A-Z]{26})")]
	private static partial Regex SeriesId();

	[GeneratedRegex(@"/chapters/([0-9A-Z]{26})")]
	private static partial Regex ChapterId();

	[GeneratedRegex(@"\d+(?:\.\d+)?")]
	private static partial Regex Numbers();
}

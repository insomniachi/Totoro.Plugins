using System.Net;
using System.Text;
using System.Text.Json;
using Flurl.Http;
using Totoro.Core.Modules.Manga;
using Totoro.Core.Modules.Streaming;

namespace Totoro.Plugins.MangaFire;

/// <summary>
/// MangaFire (mangafire.to): a JSON API whose calls must carry a signature of their path and query (vrf). Its titles
/// carry their AniList and MyAnimeList ids, which match database manga exactly. Ported from keiyoushi's extension.
/// </summary>
public sealed class MangaFireProvider : IMangaProvider
{
	private const string BaseUrl = "https://mangafire.to";
	private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36";

	// Search results whose titles are checked for database ids.
	private const int CandidatesToCheck = 3;

	private const int ChaptersPerPage = 200;

	public Guid Id => MangaFireModule.Id;

	public string Name => "MangaFire";

	public async Task<List<ProviderManga>> SearchAsync(string query, CancellationToken ct)
	{
		var response = await GetAsync<ListResponse<Series>>("/titles", [("keyword", query), ("languages[]", "en"), ("page", "1"), ("limit", "30")], ct);
		return
		[
			.. response.Items.Select(x => new ProviderManga(x.Hid, x.Title, TitleMatch.Score(query, x.Title))
			{
				Image = x.Poster?.Medium ?? "",
			})
		];
	}

	/// <summary>Its search's top results whose AniList or MyAnimeList id is the manga's; else the best title match.</summary>
	public async Task<ProviderManga?> FindMangaAsync(MangaModel manga, CancellationToken ct)
	{
		foreach (var title in manga.AllTitles.Take(2))
		{
			foreach (var candidate in (await SearchAsync(title, ct)).Take(CandidatesToCheck))
			{
				var details = (await GetAsync<DataResponse<Series>>($"/titles/{candidate.Id}", [], ct)).Data;
				if (manga.ExternalIds.Anilist > 0 && details.AnilistId == manga.ExternalIds.Anilist
					|| manga.ExternalIds.MyAnimeList > 0 && details.MalId == manga.ExternalIds.MyAnimeList)
				{
					return candidate;
				}
			}
		}

		// No ids to go by: the title match, as other sources do.
		return await IMangaProvider.MatchByTitleAsync(this, manga, ct);
	}

	public async Task<List<ProviderChapter>> GetChaptersAsync(string mangaId, CancellationToken ct)
	{
		Task<ListResponse<Chapter>> Page(int page) => GetAsync<ListResponse<Chapter>>($"/titles/{mangaId}/chapters",
			[("language", "en"), ("sort", "number"), ("order", "desc"), ("page", $"{page}"), ("limit", $"{ChaptersPerPage}")], ct);

		var first = await Page(1);
		var rest = await Task.WhenAll(Enumerable.Range(2, Math.Max(0, (first.Meta?.LastPage ?? 1) - 1)).Select(Page));
		return ToChapters(first.Items.Concat(rest.SelectMany(x => x.Items)), mangaId);
	}

	/// <summary>One chapter per number, in reading order; an official release beats a fan translation of the same number.</summary>
	public static List<ProviderChapter> ToChapters(IEnumerable<Chapter> chapters, string mangaId)
	{
		return
		[
			.. chapters
				.OrderBy(x => x.Number)
				.ThenBy(x => x.Type == "official" ? 0 : 1)
				.DistinctBy(x => x.Number)
				.Select(x => new ProviderChapter(mangaId, $"{x.Id}", x.Number)
				{
					Title = string.IsNullOrWhiteSpace(x.Name) ? $"Chapter {x.Number}" : $"Chapter {x.Number}: {x.Name}",
					Language = "en",
				})
		];
	}

	public async Task<List<MangaPage>> GetPagesAsync(ProviderChapter chapter, CancellationToken ct)
	{
		var response = await GetAsync<DataResponse<ChapterPages>>($"/chapters/{chapter.Id}", [], ct);
		// Its image host turns away requests that don't come from the site.
		return [.. response.Data.Pages.Select(x => new MangaPage(new Uri(x.Url)) { Headers = new Dictionary<string, string> { ["Referer"] = BaseUrl + "/" } })];
	}

	/// <summary>
	/// The query the site signs: the path after /api, then its parameters sorted by name (values unescaped), the
	/// repeats of an array parameter numbered (languages[0], languages[1], ...).
	/// </summary>
	public static string Canonical(string path, IEnumerable<(string Key, string Value)> query)
	{
		var sorted = query.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
		if (sorted.Count == 0)
		{
			return path;
		}

		var parts = sorted.GroupBy(x => x.Key).SelectMany(g => g.Select((x, i) => $"{(g.Key.EndsWith("[]", StringComparison.Ordinal) ? g.Key.Replace("[]", $"[{i}]") : g.Key)}={x.Value}"));
		return $"{path}?{string.Join('&', parts)}";
	}

	/// <summary>The signature (vrf): three rounds of a table cipher with feedback, base64url without padding.</summary>
	public static string Sign(string canonical)
	{
		var data = Encoding.UTF8.GetBytes(canonical);
		foreach (var (table, key, iv) in Stages)
		{
			var prev = iv;
			for (var i = 0; i < data.Length; i++)
			{
				prev = table[(data[i] ^ key[i % key.Length] ^ prev) & 0xFF];
				data[i] = (byte)prev;
			}
		}

		return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	private static readonly (byte[] Table, byte[] Key, int Iv)[] Stages =
	[
		(Convert.FromBase64String("yINlmUNho8VYJT+ibTIP+9ESiULpVEtMOoD6U6lRE0R/xwXo/Xp9NrUgC4cw/Lmo33vUyjUE40kUoEWIr/fxfNNcq2s79ShQ5NhNrFnJ4hXPwOu/SuXzIbuTQKGFvfm08E9jvCfqAtoDqvQq3dVWPQFmJjgvkISBeXY3BgANR+yVnjGbcxZ47d6kLNfZPIayTq3/YGySb1KuVZodWp/WGNAO5pfMcpaK53Hhs0allBszaMaxuouOwdxbwgxIw6YunSsXjI05Yi0j9j4eHKfSXR8Ifo/Od+8iamRfCXTyvm7NGRGYdcQ0ywcK/u6RXhrbcCm4t2eCtrDgQVecJGkQ+A=="),
			Convert.FromBase64String("0Ec58JOY3uBzJK9m3zqIOpdlF7UFiax9DmA="), 0x5A),
		(Convert.FromBase64String("IUFltCxD3Oc2cwCgkJffthaOg9cgPUb0LgW6H/VtfcF0kc5F25t+aWj6JH9VOhOaY0rAFdUxlDnl5BLNvwEJvQtP5qcw7vdb/K+chnbwnspSHT8mz5lqwz41TezG0hkO06FTjJZhsyNuFLDpD2ZZxQj/QIRcF90zpmQ7Byu483WsQqUE0C342HL+JXngRB6fRzxRyVTaKu83h7UYTJ0QMt6ixFh6S3F8gqkKwrGTL3jHNBsD45UnifK8+RGtishQV2K3rujLKEkiZxpr2dYcudFW4oFsDKhad3CLBvuyTqsCo4B7mL5IKQ1vXo/MOOvq1I1d8ar9X6Ttu5KF4fZgiA=="),
			Convert.FromBase64String("AAdjb1iPY8CiDmq9H34tKTBF8a3oDQ=="), 0x35),
		(Convert.FromBase64String("NQHlu1/wVO5EmkwQymF810qqY2xG1k2obcas4Z9mCsPEIFl9pRIjFxbJ7ybMHbBckT5Ton85E0FOeHezbh/mjlEYpmpnlXOS8dgrqeq2KfxImTh1YK9y0PeMNhzA1OQzSY9brYOJq/l2QnE/hwOeZIhPixVSKIUlDb5vLcH6RWKxkIEMuP0bDwIqQ71AJJaEaMJL7A6YtyIwoRT+L5v4aZzodN/0+3nOGsfblFjgxSfPzVDjNFeNl5P26+kEC/8AHgdrpAbt3hHz3HrRN1Y6e+JHgF7ncFWnoF0y3THL1S71WgWGCa6KtSzTCCG58n68nTyj2T3Sshk7utqCtMi/ZQ=="),
			Convert.FromBase64String("DELOJgPsVaCcblDtTGMdHzM="), 0xBA),
	];

	private static async Task<T> GetAsync<T>(string path, (string Key, string Value)[] query, CancellationToken ct)
	{
		var url = $"{BaseUrl}/api{path}?" + string.Join('&', query.Append((Key: "vrf", Value: Sign(Canonical(path, query)))).Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
		using var response = await url.WithHeader("User-Agent", UserAgent).WithHeader("Referer", BaseUrl + "/").AllowAnyHttpStatus().GetAsync(cancellationToken: ct);
		var body = await response.GetStringAsync();

		// shortcut: its shape-picking captcha isn't solved here (the extension needs a browser and OpenCV for it); upgrade if it starts showing often.
		if (response.StatusCode == (int)HttpStatusCode.Forbidden && body.Contains("captcha_required", StringComparison.Ordinal))
		{
			throw new InvalidOperationException("MangaFire wants a captcha solved: open mangafire.to in a browser, solve it, and try again.");
		}

		if (response.StatusCode is < 200 or > 299)
		{
			throw new HttpRequestException($"MangaFire answered {path} with {response.StatusCode}", null, (HttpStatusCode)response.StatusCode);
		}

		return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
	}

	public sealed record ListResponse<T>(List<T> Items, Meta? Meta);

	public sealed record DataResponse<T>(T Data);

	public sealed record Meta(int LastPage);

	/// <param name="Hid">Its id in the site's URLs, e.g. "dkw" in /title/dkw-one-piece.</param>
	public sealed record Series(string Hid, string Title, Poster? Poster, long? MalId, long? AnilistId);

	public sealed record Poster(string? Medium);

	/// <param name="Type">"official" or "unofficial" (a fan translation).</param>
	public sealed record Chapter(long Id, float Number, string? Name, string? Type);

	public sealed record ChapterPages(List<Page> Pages);

	public sealed record Page(string Url);
}

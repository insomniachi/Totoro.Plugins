using System.Text.Json.Nodes;
using Flurl.Http;
using Microsoft.Extensions.Logging;
using Totoro.Core.Modules.Anime;

namespace Totoro.Plugins.Kitsu;

/// <summary>
/// Anime information from Kitsu. Episodes come from ani.zip, by Kitsu id, and characters from AniList, by the AniList
/// id Kitsu maps it to: Kitsu's API no longer returns any.
/// </summary>
internal class KitsuMetadataProvider(KitsuSession session, ILogger<KitsuMetadataProvider> logger) : IAnimeMetadataProvider
{
	// The most anime Kitsu returns in one page.
	private const int PageSize = 20;

	// Kitsu's categories (its genres) by title, to filter by their slugs; loaded on first use.
	private static Dictionary<string, string>? s_categories;

	public Guid Id => KitsuModule.Id;

	public string Name => nameof(AnimeId.Kitsu);

	public async Task<AnimeModel> GetAnimeAsync(long id)
	{
		var request = (await session.RequestAsync($"anime/{id}")).WithAnimeFields("mediaRelationships.destination")
																   .SetQueryParam("fields[mediaRelationships]", "role,destination");
		var document = await session.GetAsync(request);
		return document.Data.FirstOrDefault() is { } anime
			? KitsuConverter.ConvertModel(anime, document)
			: throw new InvalidOperationException($"Kitsu has no anime {id}");
	}

	public async Task<List<AnimeModel>> SearchAnimeAsync(string term)
	{
		try
		{
			return await FindAsync(x => x.SetQueryParam("filter[text]", term));
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Kitsu search failed");
			return [];
		}
	}

	public async Task<List<AnimeModel>> SearchAnimeAsync(AdvancedSearchRequest request)
	{
		try
		{
			var categories = await LoadCategoriesAsync();
			var included = (request.IncludedGenres ?? []).Select(x => categories.GetValueOrDefault(x)).OfType<string>().ToList();
			var result = await FindAsync(x =>
			{
				if (!string.IsNullOrWhiteSpace(request.Title))
				{
					x.SetQueryParam("filter[text]", request.Title);
				}
				else
				{
					// Kitsu orders text searches by relevance and can't sort them.
					x.SetQueryParam("sort", "popularityRank");
				}

				if (request.SeasonName is { } season)
				{
					x.SetQueryParam("filter[season]", season.ToString().ToLowerInvariant());
				}

				if (request.MinYear is > 1950 || request.MaxYear is > 1950)
				{
					x.SetQueryParam("filter[seasonYear]", $"{(request.MinYear is > 1950 and var min ? min : "")}..{(request.MaxYear is > 1950 and var max ? max : "")}");
				}

				// Kitsu's ratings are out of 100; the request's out of 10.
				if (request.MinimumScore is not null || request.MaximumScore is not null)
				{
					x.SetQueryParam("filter[averageRating]", FormattableString.Invariant($"{request.MinimumScore * 10}..{request.MaximumScore * 10}"));
				}

				if (included.Count > 0)
				{
					x.SetQueryParam("filter[categories]", string.Join(",", included));
				}
			});

			// Kitsu can't leave categories out.
			return request.ExcludedGenres is { Count: > 0 } excluded ? [.. result.Where(x => !x.Genres.Intersect(excluded).Any())] : result;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Kitsu search failed");
			return [];
		}
	}

	public Task<List<EpisodeInfo>> GetEpisodesAsync(AnimeModel anime)
	{
		return anime.GetEpisodesAsync();
	}

	public async Task<List<CharacterModel>> GetCharactersAsync(long animeId)
	{
		var anime = new AnimeModel { Id = animeId, ServiceName = nameof(AnimeId.Kitsu) };
		if (await anime.FindIdAsync(nameof(AnimeId.Anilist)) is not { } anilistId)
		{
			return [];
		}

		const string query = "query ($id: Int) { Media(id: $id, type: ANIME) { characters(sort: ROLE) { nodes { name { full } image { large } description(asHtml: true) gender } } } }";
		var response = await "https://graphql.anilist.co".PostJsonAsync(new { query, variables = new { id = anilistId } }).ReceiveJson<JsonObject>();
		var characters = response["data"]?["Media"]?["characters"]?["nodes"]?.AsArray() ?? [];

		return
		[
			.. characters.OfType<JsonNode>().Select(x => new CharacterModel
			{
				Name = (string?)x["name"]?["full"] ?? "",
				Image = Uri.TryCreate((string?)x["image"]?["large"], UriKind.Absolute, out var uri) ? uri : null,
				Description = (string?)x["description"] ?? "",
				Gender = (string?)x["gender"] ?? "",
			})
		];
	}

	public async Task<List<string>> GetGenresAsync()
	{
		return [.. (await LoadCategoriesAsync()).Keys.Order()];
	}

	public Task<List<AnimeModel>> GetPopularAnimeAsync(CancellationToken ct)
	{
		return FindOrEmptyAsync(x => x.SetQueryParam("filter[status]", "current").SetQueryParam("sort", "popularityRank"), ct);
	}

	public Task<List<AnimeModel>> GetUpcomingAnimeAsync(CancellationToken ct)
	{
		// Not-yet-aired anime have no popularity rank; how many users have them is the next best thing.
		return FindOrEmptyAsync(x => x.SetQueryParam("filter[status]", "upcoming,unreleased").SetQueryParam("sort", "-userCount"), ct);
	}

	public async Task<List<AnimeModel>> GetAiringTodayAsync(CancellationToken ct)
	{
		// Kitsu has no schedule; its airing anime whose next episode is today (the user's day) are the closest.
		// shortcut: only the most popular pages of airing anime, so a rarely watched one can be missed; page through if that matters.
		var airing = new List<AnimeModel>();
		for (var page = 0; page < 3; page++)
		{
			var offset = page * PageSize;
			airing.AddRange(await FindOrEmptyAsync(x => x.SetQueryParam("filter[status]", "current").SetQueryParam("sort", "-userCount").SetQueryParam("page[offset]", offset), ct));
		}

		return [.. airing.Where(x => x.NextEpisodeAt?.Date == DateTime.Today).DistinctBy(x => x.Id)];
	}

	public async Task<List<AnimeModel>> GetSeasonAnimeAsync(Season season, CancellationToken ct)
	{
		var result = new List<AnimeModel>();
		// shortcut: the five most popular pages, so a season's least watched anime can be left out.
		for (var page = 0; page < 5; page++)
		{
			var offset = page * PageSize;
			var anime = await FindAsync(x => x.SetQueryParam("filter[season]", season.SeasonName.ToString().ToLowerInvariant())
											  .SetQueryParam("filter[seasonYear]", season.Year)
											  .SetQueryParam("sort", "-userCount")
											  .SetQueryParam("page[offset]", offset)
											  .WithAnimeFields("mediaRelationships.destination")
											  .SetQueryParam("fields[mediaRelationships]", "role,destination"), ct);
			result.AddRange(anime);
			if (anime.Count < PageSize)
			{
				break;
			}
		}

		return result;
	}

	private async Task<List<AnimeModel>> FindOrEmptyAsync(Action<IFlurlRequest> filter, CancellationToken ct)
	{
		try
		{
			return await FindAsync(filter, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Kitsu request failed");
			return [];
		}
	}

	/// <summary>A page of anime, filtered by <paramref name="filter"/>, which can also ask for more fields.</summary>
	private async Task<List<AnimeModel>> FindAsync(Action<IFlurlRequest> filter, CancellationToken ct = default)
	{
		var request = (await session.RequestAsync("anime")).WithAnimeFields().SetQueryParam("page[limit]", PageSize);
		filter(request);
		var document = await session.GetAsync(request, ct);
		// R18 is the rating Kitsu gives hentai. Not filtered by Kitsu: its filter also drops the many anime with no rating.
		return
		[
			.. document.Data.Where(x => session.IncludeNsfw || (string?)x["attributes"]?["ageRating"] != "R18")
					   .Select(x => KitsuConverter.ConvertModel(x, document))
		];
	}

	private async Task<Dictionary<string, string>> LoadCategoriesAsync()
	{
		if (s_categories is { } categories)
		{
			return categories;
		}

		// Kitsu has hundreds of categories (settings, themes...); the most used are the ones that work as genres.
		var request = (await session.RequestAsync("categories")).SetQueryParam("sort", "-totalMediaCount")
																 .SetQueryParam("page[limit]", 40)
																 .SetQueryParam("fields[categories]", "title,slug");
		var document = await session.GetAsync(request);
		return s_categories = document.Data.Select(x => x["attributes"])
								   .Where(x => (string?)x?["title"] is not null && (string?)x?["slug"] is not null)
								   .DistinctBy(x => (string)x!["title"]!)
								   .ToDictionary(x => (string)x!["title"]!, x => (string)x!["slug"]!);
	}
}

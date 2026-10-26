using Microsoft.Extensions.Logging;
using Totoro.Core.Modules.Anime;
using static Totoro.Plugins.Shikimori.ShikimoriConverter;

namespace Totoro.Plugins.Shikimori;

/// <summary>Anime information from Shikimori's GraphQL API. Episodes come from ani.zip, by MyAnimeList id.</summary>
internal class ShikimoriMetadataProvider(ShikimoriSession session, ILogger<ShikimoriMetadataProvider> logger) : IAnimeMetadataProvider
{
	// The most anime Shikimori returns in one page.
	private const int PageSize = 50;

	// Shikimori filters genres by id; loaded on first use.
	private static List<ShikiGenre>? s_genres;

	public Guid Id => ShikimoriModule.Id;

	public string Name => nameof(AnimeId.Shikimori);

	public async Task<AnimeModel> GetAnimeAsync(long id)
	{
		var anime = await FindAsync($"ids: \"{id}\", limit: 1", ListFields + " " + RelatedFields);
		return anime.FirstOrDefault() ?? throw new InvalidOperationException($"Shikimori has no anime {id}");
	}

	public async Task<List<AnimeModel>> SearchAnimeAsync(string term)
	{
		try
		{
			return await FindAsync($"search: {Literal(term)}, limit: 20");
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Shikimori search failed");
			return [];
		}
	}

	public async Task<List<AnimeModel>> SearchAnimeAsync(AdvancedSearchRequest request)
	{
		try
		{
			var filters = new List<string> { $"limit: {PageSize}", "order: popularity" };
			if (!string.IsNullOrWhiteSpace(request.Title))
			{
				filters.Add($"search: {Literal(request.Title)}");
			}

			// Shikimori's seasons: "fall_2020", "2020" or a range of years, "2019_2021".
			var minYear = request.MinYear is > 1950 and var min ? min : (int?)null;
			var maxYear = request.MaxYear is > 1950 and var max ? max : (int?)null;
			if (request.SeasonName is { } seasonName && minYear is { } year && (maxYear ?? year) == year)
			{
				filters.Add($"season: \"{seasonName.ToString().ToLowerInvariant()}_{year}\"");
			}
			else if (minYear is not null || maxYear is not null)
			{
				filters.Add($"season: \"{minYear ?? 1950}_{maxYear ?? DateTime.Today.Year + 1}\"");
			}

			// Shikimori's score filter is a minimum, out of 10 like the request's.
			if (request.MinimumScore is { } minScore)
			{
				filters.Add($"score: {(int)Math.Floor(minScore)}");
			}

			var genres = await LoadGenresAsync();
			var genreFilter = (request.IncludedGenres ?? []).Select(x => genres.FirstOrDefault(g => g.Name == x)?.Id)
															.Concat((request.ExcludedGenres ?? []).Select(x => genres.FirstOrDefault(g => g.Name == x)?.Id is { } id ? $"!{id}" : null))
															.OfType<string>()
															.ToList();
			if (genreFilter.Count > 0)
			{
				filters.Add($"genre: {Literal(string.Join(",", genreFilter))}");
			}

			var result = await FindAsync(string.Join(", ", filters));
			return request.MaximumScore is { } maxScore ? [.. result.Where(x => x.MeanScore is null || x.MeanScore <= maxScore * 10)] : result;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Shikimori search failed");
			return [];
		}
	}

	public Task<List<EpisodeInfo>> GetEpisodesAsync(AnimeModel anime)
	{
		return anime.GetEpisodesAsync();
	}

	public async Task<List<CharacterModel>> GetCharactersAsync(long animeId)
	{
		var anime = await session.QueryAsync<List<ShikiAnime>>(
			$"{{ animes(ids: \"{animeId}\", limit: 1) {{ id name characterRoles {{ character {{ name poster {{ originalUrl }} descriptionHtml }} }} }} }}", "animes");

		return
		[
			.. (anime.FirstOrDefault()?.CharacterRoles ?? []).Select(x => x.Character).OfType<ShikiCharacter>().Select(x => new CharacterModel
			{
				Name = x.Name,
				Image = Uri.TryCreate(x.Poster?.OriginalUrl, UriKind.Absolute, out var uri) ? uri : null,
				Description = x.DescriptionHtml ?? "",
			})
		];
	}

	public async Task<List<string>> GetGenresAsync()
	{
		return [.. (await LoadGenresAsync()).Select(x => x.Name).Distinct().Order()];
	}

	public Task<List<AnimeModel>> GetPopularAnimeAsync(CancellationToken ct)
	{
		return FindOrEmptyAsync($"status: \"ongoing\", order: popularity, limit: {PageSize}", ct);
	}

	public Task<List<AnimeModel>> GetUpcomingAnimeAsync(CancellationToken ct)
	{
		return FindOrEmptyAsync($"status: \"anons\", order: popularity, limit: {PageSize}", ct);
	}

	public async Task<List<AnimeModel>> GetAiringTodayAsync(CancellationToken ct)
	{
		// Shikimori has no schedule; its airing anime whose next episode is today (the user's day) are the closest.
		// shortcut: only the most popular page of airing anime, so a rarely watched one can be missed; page through if that matters.
		var airing = await FindOrEmptyAsync($"status: \"ongoing\", order: popularity, limit: {PageSize}", ct);
		return [.. airing.Where(x => x.NextEpisodeAt?.Date == DateTime.Today)];
	}

	public async Task<List<AnimeModel>> GetSeasonAnimeAsync(Season season, CancellationToken ct)
	{
		var result = new List<AnimeModel>();
		// shortcut: the first few pages (Shikimori allows 90 requests a minute), so a season's least popular anime can be left out.
		for (var page = 1; page <= 4; page++)
		{
			var anime = await FindAsync($"season: \"{season.SeasonName.ToString().ToLowerInvariant()}_{season.Year}\", order: popularity, limit: {PageSize}, page: {page}",
										ListFields + " " + RelatedFields, ct);
			result.AddRange(anime);
			if (anime.Count < PageSize)
			{
				break;
			}
		}

		return result;
	}

	private async Task<List<AnimeModel>> FindOrEmptyAsync(string filters, CancellationToken ct)
	{
		try
		{
			return await FindAsync(filters, ct: ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Shikimori request failed");
			return [];
		}
	}

	private async Task<List<AnimeModel>> FindAsync(string filters, string fields = ListFields, CancellationToken ct = default)
	{
		// censored leaves out hentai; not when asking for an anime by id, which may be on the user's list.
		var censored = !session.IncludeNsfw && !filters.StartsWith("ids:", StringComparison.Ordinal);
		var anime = await session.QueryAsync<List<ShikiAnime>>($"{{ animes({filters}, censored: {(censored ? "true" : "false")}) {{ {fields} }} }}", "animes", ct);
		return [.. anime.Select(ConvertModel)];
	}

	private async Task<List<ShikiGenre>> LoadGenresAsync()
	{
		return s_genres ??= await session.QueryAsync<List<ShikiGenre>>("{ genres(entryType: Anime) { id name } }", "genres");
	}

	private sealed record ShikiGenre(string Id, string Name);
}

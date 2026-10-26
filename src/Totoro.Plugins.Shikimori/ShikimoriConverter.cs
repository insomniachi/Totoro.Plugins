using Totoro.Core.Modules.Anime;

namespace Totoro.Plugins.Shikimori;

internal sealed record ShikiUser(long Id, string Nickname, string? AvatarUrl);

internal sealed record ShikiName(string Name);

internal sealed record ShikiDate(DateTime? Date);

internal sealed record ShikiPoster(string? OriginalUrl);

internal sealed record ShikiRelated(string RelationKind, ShikiAnime? Anime);

internal sealed record ShikiCharacter(string Name, ShikiPoster? Poster, string? DescriptionHtml);

internal sealed record ShikiCharacterRole(ShikiCharacter? Character);

internal sealed record ShikiUserRate(long Id, string Status, int Score, int Episodes, int Rewatches, string? Text, DateTimeOffset? UpdatedAt, ShikiAnime? Anime = null);

internal sealed record ShikiAnime(
	long Id,
	string Name,
	string? English,
	string? Japanese,
	string[]? Synonyms,
	string? Kind,
	string? Status,
	int Episodes,
	int EpisodesAired,
	double? Score,
	string? Season,
	ShikiDate? ReleasedOn,
	DateTimeOffset? NextEpisodeAt,
	ShikiPoster? Poster,
	string? DescriptionHtml,
	ShikiName[]? Genres,
	ShikiName[]? Studios,
	string? Url,
	ShikiUserRate? UserRate,
	ShikiRelated[]? Related,
	ShikiCharacterRole[]? CharacterRoles);

internal static class ShikimoriConverter
{
	/// <summary>The fields asked for of anime in lists (search results, the user's list...).</summary>
	public const string ListFields =
		"id name english japanese synonyms kind status episodes episodesAired score season releasedOn { date } nextEpisodeAt " +
		"poster { originalUrl } descriptionHtml genres { name } studios { name } url userRate { id status score episodes rewatches text updatedAt }";

	/// <summary>The fields of related anime: Shikimori's queries can only be five levels deep.</summary>
	public const string RelatedFields = $"related {{ relationKind anime {{ id name english kind status episodes score poster {{ originalUrl }} }} }}";

	public static AnimeModel ConvertModel(ShikiAnime anime)
	{
		var model = new AnimeModel
		{
			Id = anime.Id,
			// Shikimori's ids are MyAnimeList's.
			ExternalIds = new AnimeId { MyAnimeList = anime.Id },
			Title = anime.Name,
			EngTitle = string.IsNullOrEmpty(anime.English) ? anime.Name : anime.English,
			RomajiTitle = anime.Name,
			Image = anime.Poster?.OriginalUrl ?? "",
			BannerImage = anime.Poster?.OriginalUrl ?? "",
			ServiceId = ShikimoriModule.Id,
			ServiceName = nameof(AnimeId.Shikimori),
			Description = anime.DescriptionHtml ?? "",
			Url = anime.Url ?? $"{ShikimoriSession.Site}/animes/{anime.Id}",
			MediaFormat = anime.Kind switch
			{
				"tv" => AnimeMediaFormat.Tv,
				"movie" => AnimeMediaFormat.Movie,
				"ova" => AnimeMediaFormat.Ova,
				"ona" => AnimeMediaFormat.Ona,
				"special" or "tv_special" => AnimeMediaFormat.Special,
				"music" => AnimeMediaFormat.Music,
				_ => AnimeMediaFormat.Unknown,
			},
			Genres = anime.Genres is { } genres ? [.. genres.Select(x => x.Name)] : [],
			Studios = anime.Studios is { } studios ? [.. studios.Select(x => x.Name)] : [],
			AlternateTitles = [.. new[] { anime.English, anime.Japanese }.Concat(anime.Synonyms ?? []).OfType<string>().Where(x => x.Length > 0 && x != anime.Name).Distinct()],
			Tracking = anime.UserRate is { } rate ? ConvertTracking(rate) : null,
			AiringStatus = anime.Status switch
			{
				"ongoing" => AiringStatus.CurrentlyAiring,
				"released" => AiringStatus.FinishedAiring,
				_ => AiringStatus.NotYetAired,
			},
			TotalEpisodes = anime.Episodes > 0 ? anime.Episodes : null,
			AiredEpisodes = anime.EpisodesAired,
			// Out of 100, like AniList's (see AnimeModel.MeanScore); Shikimori's is out of 10.
			MeanScore = anime.Score is > 0 and var score ? (float)(score * 10) : null,
			NextEpisodeAt = anime.NextEpisodeAt?.LocalDateTime,
			EndDate = anime.ReleasedOn?.Date,
			Season = ParseSeason(anime.Season),
		};

		if (anime.Related is { Length: > 0 } related)
		{
			model.Related =
			[
				.. related.Where(x => x.Anime is not null).Select(x =>
				{
					var relatedModel = ConvertModel(x.Anime!);
					relatedModel.Relation = x.RelationKind switch
					{
						"sequel" => AnimeRelationType.Sequel,
						"prequel" => AnimeRelationType.Prequel,
						"side_story" => AnimeRelationType.SideStory,
						"parent_story" or "full_story" => AnimeRelationType.Parent,
						"alternative_setting" or "alternative_version" => AnimeRelationType.Alternative,
						"summary" => AnimeRelationType.Summary,
						"spin_off" => AnimeRelationType.SpinOff,
						_ => AnimeRelationType.Other,
					};
					return relatedModel;
				}),
			];
		}

		return model;
	}

	public static Tracking ConvertTracking(ShikiUserRate rate)
	{
		return new Tracking
		{
			Status = ConvertStatus(rate.Status),
			Score = rate.Score > 0 ? rate.Score : null,
			WatchedEpisodes = rate.Episodes,
			UpdatedAt = rate.UpdatedAt?.LocalDateTime,
		};
	}

	public static ListItemStatus? ConvertStatus(string status) => status switch
	{
		"watching" => ListItemStatus.Watching,
		"completed" => ListItemStatus.Completed,
		"on_hold" => ListItemStatus.OnHold,
		"planned" => ListItemStatus.PlanToWatch,
		"dropped" => ListItemStatus.Dropped,
		"rewatching" => ListItemStatus.Rewatching,
		_ => null,
	};

	public static string ConvertStatus(ListItemStatus status) => status switch
	{
		ListItemStatus.Watching => "watching",
		ListItemStatus.Completed => "completed",
		ListItemStatus.OnHold => "on_hold",
		ListItemStatus.Dropped => "dropped",
		ListItemStatus.Rewatching => "rewatching",
		_ => "planned",
	};

	/// <summary>Shikimori's season, e.g. "fall_2020"; null for anything else (it also has "2020" or "2019_2021").</summary>
	public static Season? ParseSeason(string? season)
	{
		return season?.Split('_') is [var name, var year] && Enum.TryParse<AnimeSeason>(name, true, out var seasonName) && int.TryParse(year, out var y)
			? new Season(seasonName, y)
			: null;
	}

	/// <summary>A GraphQL string literal: JSON's escaping is GraphQL's.</summary>
	public static string Literal(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}

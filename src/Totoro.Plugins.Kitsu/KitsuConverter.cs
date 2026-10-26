using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Flurl.Http;
using Totoro.Core.Modules.Anime;

namespace Totoro.Plugins.Kitsu;

internal static class KitsuConverter
{
	/// <summary>The query parameters asking for an anime's fields, and its ids elsewhere and categories.</summary>
	public static IFlurlRequest WithAnimeFields(this IFlurlRequest request, string? relationships = null) => request
		.SetQueryParam("include", relationships is null ? "mappings,categories" : $"mappings,categories,{relationships}")
		.SetQueryParam("fields[anime]", "canonicalTitle,titles,abbreviatedTitles,synopsis,averageRating,popularityRank,episodeCount,status,subtype," +
										"startDate,endDate,nextRelease,posterImage,coverImage,youtubeVideoId,slug,ageRating,mappings,categories,mediaRelationships")
		.SetQueryParam("fields[mappings]", "externalSite,externalId")
		.SetQueryParam("fields[categories]", "title");

	/// <param name="withRelated">Whether to convert its related anime: not theirs, which can lead back to it.</param>
	public static AnimeModel ConvertModel(JsonNode anime, KitsuDocument document, bool withRelated = true)
	{
		var attributes = anime["attributes"]!;
		var id = long.Parse((string)anime["id"]!, CultureInfo.InvariantCulture);
		var title = (string?)attributes["canonicalTitle"] ?? "";
		var titles = attributes["titles"];
		var english = (string?)titles?["en"];
		var status = (string?)attributes["status"];
		var start = ParseDate(attributes["startDate"]);
		var episodes = (int?)attributes["episodeCount"];

		var model = new AnimeModel
		{
			Id = id,
			ExternalIds = ConvertIds(id, document.Related(anime, "mappings")),
			Title = title,
			EngTitle = string.IsNullOrEmpty(english) ? title : english,
			RomajiTitle = (string?)titles?["en_jp"] ?? title,
			Image = (string?)attributes["posterImage"]?["large"] ?? (string?)attributes["posterImage"]?["original"] ?? "",
			BannerImage = (string?)attributes["coverImage"]?["original"] ?? (string?)attributes["posterImage"]?["large"] ?? "",
			ServiceId = KitsuModule.Id,
			ServiceName = nameof(AnimeId.Kitsu),
			Description = ToHtml((string?)attributes["synopsis"]),
			Url = $"{KitsuSession.Site}/anime/{(string?)attributes["slug"] ?? id.ToString(CultureInfo.InvariantCulture)}",
			MediaFormat = (string?)attributes["subtype"] switch
			{
				"TV" => AnimeMediaFormat.Tv,
				"movie" => AnimeMediaFormat.Movie,
				"OVA" => AnimeMediaFormat.Ova,
				"ONA" => AnimeMediaFormat.Ona,
				"special" => AnimeMediaFormat.Special,
				"music" => AnimeMediaFormat.Music,
				_ => AnimeMediaFormat.Unknown,
			},
			Genres = [.. document.Related(anime, "categories").Select(x => (string?)x["attributes"]?["title"]).OfType<string>()],
			Trailers = (string?)attributes["youtubeVideoId"] is { Length: > 0 } video
				? [new TrailerVideo { Url = $"https://www.youtube.com/watch?v={video}", Thumbnail = $"https://i.ytimg.com/vi/{video}/hqdefault.jpg" }]
				: [],
			AlternateTitles = [.. (titles?.AsObject().Select(x => (string?)x.Value) ?? [])
								  .Concat(attributes["abbreviatedTitles"]?.AsArray().Select(x => (string?)x) ?? [])
								  .OfType<string>().Where(x => x.Length > 0 && x != title).Distinct()],
			AiringStatus = status switch
			{
				"current" => AiringStatus.CurrentlyAiring,
				"finished" => AiringStatus.FinishedAiring,
				_ => AiringStatus.NotYetAired,
			},
			TotalEpisodes = episodes is > 0 ? episodes : null,
			// Kitsu's is out of 100, like AniList's (see AnimeModel.MeanScore).
			MeanScore = float.TryParse((string?)attributes["averageRating"], CultureInfo.InvariantCulture, out var score) ? score : null,
			Popularity = (int?)attributes["popularityRank"] ?? 0,
			NextEpisodeAt = DateTimeOffset.TryParse((string?)attributes["nextRelease"], CultureInfo.InvariantCulture, out var next) ? next.LocalDateTime : null,
			EndDate = ParseDate(attributes["endDate"]),
			Season = start is { } date ? Season.Of(date) : null,
		};

		// Kitsu doesn't say how many have aired.
		// shortcut: counts one a week from the start, which is off for breaks and double episodes; good enough for a "new" badge.
		model.AiredEpisodes = status switch
		{
			"finished" => episodes ?? 0,
			"current" when start is { } started => Math.Min(episodes is > 0 ? episodes.Value : int.MaxValue, Math.Max(0, (DateTime.Today - started).Days / 7 + 1)),
			_ => 0,
		};

		var relationships = withRelated ? document.Related(anime, "mediaRelationships").ToList() : [];
		if (relationships.Count > 0)
		{
			model.Related =
			[
				.. relationships.Select(x => (Role: (string?)x["attributes"]?["role"], Anime: document.Related(x, "destination").FirstOrDefault()))
								.Where(x => (string?)x.Anime?["type"] == "anime")
								.Select(x =>
								{
									var related = ConvertModel(x.Anime!, document, withRelated: false);
									related.Relation = x.Role switch
									{
										"sequel" => AnimeRelationType.Sequel,
										"prequel" => AnimeRelationType.Prequel,
										"side_story" => AnimeRelationType.SideStory,
										"parent_story" or "full_story" => AnimeRelationType.Parent,
										"alternative_setting" or "alternative_version" => AnimeRelationType.Alternative,
										"summary" => AnimeRelationType.Summary,
										"spinoff" => AnimeRelationType.SpinOff,
										_ => AnimeRelationType.Other,
									};
									return related;
								}),
			];
		}

		return model;
	}

	public static Tracking ConvertTracking(JsonNode entry)
	{
		var attributes = entry["attributes"]!;
		var rating = (int?)attributes["ratingTwenty"];
		return new Tracking
		{
			Status = ((string?)attributes["status"], (bool?)attributes["reconsuming"]) switch
			{
				("current", true) => ListItemStatus.Rewatching,
				("current", _) => ListItemStatus.Watching,
				("completed", _) => ListItemStatus.Completed,
				("on_hold", _) => ListItemStatus.OnHold,
				("dropped", _) => ListItemStatus.Dropped,
				("planned", _) => ListItemStatus.PlanToWatch,
				_ => null,
			},
			// Kitsu rates out of 20 (shown as 1 to 10 in halves).
			Score = rating is > 0 ? rating / 2.0 : null,
			WatchedEpisodes = (int?)attributes["progress"],
			StartDate = ParseDateTime(attributes["startedAt"]),
			FinishDate = ParseDateTime(attributes["finishedAt"]),
			UpdatedAt = ParseDateTime(attributes["updatedAt"]),
		};
	}

	public static string ConvertStatus(ListItemStatus status) => status switch
	{
		ListItemStatus.Watching or ListItemStatus.Rewatching => "current",
		ListItemStatus.Completed => "completed",
		ListItemStatus.OnHold => "on_hold",
		ListItemStatus.Dropped => "dropped",
		_ => "planned",
	};

	private static AnimeId ConvertIds(long id, IEnumerable<JsonNode> mappings)
	{
		var ids = new AnimeId { Kitsu = id };
		foreach (var mapping in mappings)
		{
			if (!long.TryParse((string?)mapping["attributes"]?["externalId"], CultureInfo.InvariantCulture, out var externalId))
			{
				continue;
			}

			switch ((string?)mapping["attributes"]?["externalSite"])
			{
				case "myanimelist/anime":
					ids.MyAnimeList = externalId;
					break;
				case "anilist/anime":
					ids.Anilist = externalId;
					break;
				case "anidb":
					ids.AniDb = externalId;
					break;
			}
		}

		return ids;
	}

	private static DateTime? ParseDate(JsonNode? node)
	{
		return DateTime.TryParseExact((string?)node, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
	}

	private static DateTime? ParseDateTime(JsonNode? node)
	{
		return DateTimeOffset.TryParse((string?)node, CultureInfo.InvariantCulture, out var date) ? date.LocalDateTime : null;
	}

	// Kitsu's synopses are plain text; AnimeModel.Description is HTML.
	private static string ToHtml(string? text)
	{
		return string.IsNullOrEmpty(text) ? "" : WebUtility.HtmlEncode(text).ReplaceLineEndings("<br>");
	}
}

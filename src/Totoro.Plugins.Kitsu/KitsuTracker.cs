using System.Text.Json.Nodes;
using Flurl.Http;
using Totoro.Core.Modules.Anime;

namespace Totoro.Plugins.Kitsu;

internal class KitsuTracker(KitsuSession session) : IAnimeTracker
{
	// The most library entries Kitsu returns in one page.
	private const int PageSize = 500;

	public Guid Id => KitsuModule.Id;

	public string Name => nameof(AnimeId.Kitsu);

	public async Task<List<AnimeModel>> GetUserListAsync(CancellationToken ct)
	{
		if (!session.IsSignedIn)
		{
			return [];
		}

		var request = (await LibraryAsync(ct))
					  .SetQueryParam("filter[kind]", "anime")
					  .SetQueryParam("page[limit]", PageSize)
					  .WithAnimeFields()
					  // The entry's anime, and the anime's ids elsewhere and categories.
					  .SetQueryParam("include", "anime,anime.mappings,anime.categories");

		var result = new List<AnimeModel>();
		foreach (var page in await session.GetAllAsync(request, ct))
		{
			foreach (var entry in page.Data)
			{
				if (page.Related(entry, "anime").FirstOrDefault() is not { } anime)
				{
					continue;
				}

				var model = KitsuConverter.ConvertModel(anime, page);
				model.Tracking = KitsuConverter.ConvertTracking(entry);
				result.Add(model);
			}
		}

		return result;
	}

	public async Task<TrackingForm> GetFormAsync(long id, CancellationToken ct)
	{
		var attributes = (await FindEntryAsync(id, ct))?["attributes"];
		// The scale the user rates on, in their Kitsu settings; every one is saved out of 20. Numbers, not stars: the
		// dialog's stars are whole, and saving would round a half-star rating. Kitsu's simple scale has four faces to
		// Totoro's three (AniList's), so it gets Kitsu's finest scale, which loses nothing.
		var scoreMax = (string?)(await session.GetUserAsync(ct))?["attributes"]?["ratingSystem"] == "regular" ? 5 : 10;
		return new TrackingForm(scoreMax, 0.5,
		[
			new NumberField("rewatches", "Total times rewatched", (int?)attributes?["reconsumeCount"] ?? 0),
			new ToggleField("private", "Private", (bool?)attributes?["private"] ?? false),
			new TextField("notes", "Notes", (string?)attributes?["notes"]) { IsMultiline = true },
		]);
	}

	/// <summary>Saves the tracking. Throws if Kitsu rejects it, so callers don't show a change that didn't happen.</summary>
	public async Task<Tracking> UpdateAsync(long id, Tracking tracking)
	{
		var attributes = new JsonObject();
		if (tracking.Status is { } status)
		{
			attributes["status"] = KitsuConverter.ConvertStatus(status);
			// Kitsu's rewatching is a flag on a current entry.
			attributes["reconsuming"] = status == ListItemStatus.Rewatching;
		}

		if (tracking.Score is { } score)
		{
			// Out of 20, from 2; null clears it.
			attributes["ratingTwenty"] = score > 0 ? Math.Clamp((int)Math.Round(score * 2), 2, 20) : null;
		}

		if (tracking.WatchedEpisodes is { } episodes)
		{
			attributes["progress"] = episodes;
		}

		if (tracking.StartDate is { } startDate)
		{
			attributes["startedAt"] = startDate.ToUniversalTime().ToString("O");
		}

		if (tracking.FinishDate is { } finishDate)
		{
			attributes["finishedAt"] = finishDate.ToUniversalTime().ToString("O");
		}

		if (tracking.Changed<NumberField>("rewatches") is { } rewatches)
		{
			attributes["reconsumeCount"] = (int)rewatches.Value;
		}

		if (tracking.Changed<ToggleField>("private") is { } isPrivate)
		{
			attributes["private"] = isPrivate.Value;
		}

		if (tracking.Changed<TextField>("notes") is { } notes)
		{
			attributes["notes"] = notes.Value;
		}

		IFlurlResponse response;
		if (await FindEntryAsync(id) is { } entry)
		{
			var entryId = (string)entry["id"]!;
			var body = new JsonObject { ["data"] = new JsonObject { ["type"] = "libraryEntries", ["id"] = entryId, ["attributes"] = attributes } };
			response = await (await session.RequestAsync($"library-entries/{entryId}")).PatchStringAsync(body.ToJsonString());
		}
		else
		{
			var userId = await session.GetUserIdAsync();
			var body = new JsonObject
			{
				["data"] = new JsonObject
				{
					["type"] = "libraryEntries",
					["attributes"] = attributes,
					["relationships"] = new JsonObject
					{
						["anime"] = new JsonObject { ["data"] = new JsonObject { ["type"] = "anime", ["id"] = id.ToString() } },
						["user"] = new JsonObject { ["data"] = new JsonObject { ["type"] = "users", ["id"] = userId } },
					},
				},
			};
			response = await (await session.RequestAsync("library-entries")).PostStringAsync(body.ToJsonString());
		}

		var saved = new KitsuDocument(JsonNode.Parse(await response.GetStringAsync())!).Data.FirstOrDefault();
		return saved is null ? tracking : KitsuConverter.ConvertTracking(saved);
	}

	public async Task<bool> RemoveAsync(long id)
	{
		if (await FindEntryAsync(id) is not { } entry)
		{
			return false;
		}

		await (await session.RequestAsync($"library-entries/{(string)entry["id"]!}")).DeleteAsync();
		return true;
	}

	/// <summary>The user's entry for the anime; null if it isn't in their library.</summary>
	private async Task<JsonNode?> FindEntryAsync(long id, CancellationToken ct = default)
	{
		var request = (await LibraryAsync(ct)).SetQueryParam("filter[animeId]", id);
		return (await session.GetAsync(request, ct)).Data.FirstOrDefault();
	}

	private async Task<IFlurlRequest> LibraryAsync(CancellationToken ct)
	{
		var userId = await session.GetUserIdAsync(ct);
		return (await session.RequestAsync("library-entries")).SetQueryParam("filter[userId]", userId);
	}
}

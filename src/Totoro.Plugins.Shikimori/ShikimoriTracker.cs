using Flurl.Http;
using Totoro.Core.Modules.Anime;

namespace Totoro.Plugins.Shikimori;

internal class ShikimoriTracker(ShikimoriSession session) : IAnimeTracker
{
	// The most entries Shikimori returns in one page of a user's list.
	private const int PageSize = 50;

	public Guid Id => ShikimoriModule.Id;

	public string Name => nameof(AnimeId.Shikimori);

	public async Task<List<AnimeModel>> GetUserListAsync(CancellationToken ct)
	{
		if (!session.IsSignedIn)
		{
			return [];
		}

		var userId = await session.GetUserIdAsync(ct);
		var result = new List<AnimeModel>();
		for (var page = 1; ; page++)
		{
			var rates = await session.QueryAsync<List<ShikiUserRate>>(
				$"{{ userRates(userId: {userId}, targetType: Anime, limit: {PageSize}, page: {page}) {{ id status score episodes rewatches text updatedAt anime {{ {ShikimoriConverter.ListFields} }} }} }}",
				"userRates", ct);

			foreach (var rate in rates.Where(x => x.Anime is not null))
			{
				var anime = ShikimoriConverter.ConvertModel(rate.Anime!);
				anime.Tracking = ShikimoriConverter.ConvertTracking(rate);
				result.Add(anime);
			}

			if (rates.Count < PageSize)
			{
				return result;
			}
		}
	}

	public async Task<TrackingForm> GetFormAsync(long id, CancellationToken ct)
	{
		var rate = await FindRateAsync(id, ct);
		return new TrackingForm(10, 1,
		[
			new NumberField("rewatches", "Total times rewatched", rate?.Rewatches ?? 0),
			new TextField("notes", "Notes", rate?.Text) { IsMultiline = true },
		]);
	}

	/// <summary>Saves the tracking. Throws if Shikimori rejects it, so callers don't show a change that didn't happen.</summary>
	public async Task<Tracking> UpdateAsync(long id, Tracking tracking)
	{
		var changes = new Dictionary<string, object>();
		if (tracking.Status is { } status)
		{
			changes["status"] = ShikimoriConverter.ConvertStatus(status);
		}

		if (tracking.Score is { } score)
		{
			changes["score"] = Math.Clamp((int)Math.Round(score), 0, 10);
		}

		if (tracking.WatchedEpisodes is { } episodes)
		{
			changes["episodes"] = episodes;
		}

		if (tracking.Changed<NumberField>("rewatches") is { } rewatches)
		{
			changes["rewatches"] = (int)rewatches.Value;
		}

		if (tracking.Changed<TextField>("notes") is { } notes)
		{
			changes["text"] = notes.Value;
		}

		// Shikimori has no start or finish dates.
		ShikiUserRate? saved;
		if (await FindRateAsync(id) is { } rate)
		{
			saved = await session.SendRateAsync(rate.Id, x => x.PatchJsonAsync(new { user_rate = changes }));
		}
		else
		{
			changes["user_id"] = await session.GetUserIdAsync();
			changes["target_id"] = id;
			changes["target_type"] = "Anime";
			saved = await session.SendRateAsync(null, x => x.PostJsonAsync(new { user_rate = changes }));
		}

		return saved is null ? tracking : ShikimoriConverter.ConvertTracking(saved);
	}

	public async Task<bool> RemoveAsync(long id)
	{
		if (await FindRateAsync(id) is not { } rate)
		{
			return false;
		}

		await session.SendRateAsync(rate.Id, x => x.DeleteAsync());
		return true;
	}

	/// <summary>The user's entry for the anime; null if it isn't on their list.</summary>
	private async Task<ShikiUserRate?> FindRateAsync(long id, CancellationToken ct = default)
	{
		var anime = await session.QueryAsync<List<ShikiAnime>>(
			$"{{ animes(ids: \"{id}\", limit: 1) {{ id name userRate {{ id status score episodes rewatches text updatedAt }} }} }}", "animes", ct);
		return anime.FirstOrDefault()?.UserRate;
	}
}

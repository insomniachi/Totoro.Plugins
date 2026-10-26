using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Anime;
using Totoro.Core.Settings;
using Totoro.Plugins.Kitsu;
using Totoro.Plugins.Shikimori;

namespace Totoro.Plugins.Tests;

/// <summary>Shikimori and Kitsu against their live APIs, signed out.</summary>
public class AnimeDatabasePluginTests
{
	private const long JujutsuKaisenMal = 40748;
	private const long DemonSlayerKitsu = 41370;

	[Fact]
	public void Modules_RegisterTrackerAndMetadataProviderNamedAfterTheirIds()
	{
		// Trackers are named after the AnimeId property their ids are, which syncing other lists relies on.
		Assert.Equal(nameof(AnimeId.Shikimori), Shikimori().GetRequiredKeyedService<IAnimeTracker>(ShikimoriModule.Id).Name);
		Assert.Equal(nameof(AnimeId.Kitsu), Kitsu().GetRequiredKeyedService<IAnimeTracker>(KitsuModule.Id).Name);
		Assert.NotNull(Shikimori().GetKeyedService<IAnimeMetadataProvider>(ShikimoriModule.Id));
		Assert.NotNull(Kitsu().GetKeyedService<IAnimeMetadataProvider>(KitsuModule.Id));
	}

	[Fact]
	public async Task Trackers_SignedOut_HaveEmptyLists()
	{
		Assert.Empty(await Shikimori().GetRequiredKeyedService<IAnimeTracker>(ShikimoriModule.Id).GetUserListAsync(CancellationToken.None));
		Assert.Empty(await Kitsu().GetRequiredKeyedService<IAnimeTracker>(KitsuModule.Id).GetUserListAsync(CancellationToken.None));
	}

	[Fact]
	public async Task Shikimori_Live_AnimeHasMyAnimeListIdRelationsCharactersAndEpisodes()
	{
		var provider = Shikimori().GetRequiredKeyedService<IAnimeMetadataProvider>(ShikimoriModule.Id);

		var anime = await provider.GetAnimeAsync(JujutsuKaisenMal);
		Assert.Equal("Jujutsu Kaisen", anime.Title);
		Assert.Equal(JujutsuKaisenMal, anime.ExternalIds.MyAnimeList);
		Assert.Equal(JujutsuKaisenMal, await anime.FindIdAsync(nameof(AnimeId.Shikimori)));
		Assert.Equal(24, anime.TotalEpisodes);
		Assert.Equal(new Season(AnimeSeason.Fall, 2020), anime.Season);
		Assert.Contains(anime.Related, x => x.Relation == AnimeRelationType.Sequel);
		Assert.NotEmpty(anime.Genres);

		Assert.NotEmpty(await provider.GetCharactersAsync(JujutsuKaisenMal));
		// ani.zip, by MyAnimeList id; it also lists a special.
		Assert.True((await provider.GetEpisodesAsync(anime)).Count >= 24);
	}

	[Fact]
	public async Task Shikimori_Live_SearchesAndListsSeasons()
	{
		var provider = Shikimori().GetRequiredKeyedService<IAnimeMetadataProvider>(ShikimoriModule.Id);

		Assert.Contains(await provider.SearchAnimeAsync("Frieren"), x => x.Id == 52991);

		var genres = await provider.GetGenresAsync();
		Assert.Contains("Action", genres);
		var advanced = await provider.SearchAnimeAsync(new AdvancedSearchRequest { IncludedGenres = ["Action"], MinYear = 2019, MaxYear = 2020, MinimumScore = 8 });
		Assert.NotEmpty(advanced);
		Assert.All(advanced, x => Assert.Contains("Action", x.Genres));

		var season = await provider.GetSeasonAnimeAsync(new Season(AnimeSeason.Fall, 2020), CancellationToken.None);
		Assert.Contains(season, x => x.Id == JujutsuKaisenMal);
		Assert.Contains(season, x => x.Related.Any());
		Assert.NotEmpty(await provider.GetPopularAnimeAsync(CancellationToken.None));
	}

	[Fact]
	public async Task Kitsu_Live_AnimeHasMappedIdsRelationsCharactersAndEpisodes()
	{
		var provider = Kitsu().GetRequiredKeyedService<IAnimeMetadataProvider>(KitsuModule.Id);

		var anime = await provider.GetAnimeAsync(DemonSlayerKitsu);
		Assert.Equal("Kimetsu no Yaiba", anime.Title);
		Assert.Equal(DemonSlayerKitsu, anime.ExternalIds.Kitsu);
		Assert.Equal(38000, anime.ExternalIds.MyAnimeList);
		Assert.Equal(101922, anime.ExternalIds.Anilist);
		Assert.Equal(26, anime.TotalEpisodes);
		Assert.Contains("Action", anime.Genres);
		Assert.Contains(anime.Related, x => x.Relation == AnimeRelationType.Sequel);

		// From AniList, by the AniList id ani.zip maps the Kitsu id to.
		Assert.NotEmpty(await provider.GetCharactersAsync(DemonSlayerKitsu));
		// From ani.zip, by Kitsu id.
		Assert.Equal(26, (await provider.GetEpisodesAsync(anime)).Count);
	}

	[Fact]
	public async Task Kitsu_Live_SearchesAndListsSeasons()
	{
		var provider = Kitsu().GetRequiredKeyedService<IAnimeMetadataProvider>(KitsuModule.Id);

		Assert.Contains(await provider.SearchAnimeAsync("Kimetsu no Yaiba"), x => x.Id == DemonSlayerKitsu);

		Assert.Contains("Action", await provider.GetGenresAsync());
		var advanced = await provider.SearchAnimeAsync(new AdvancedSearchRequest { IncludedGenres = ["Action"], MinYear = 2019, MaxYear = 2020, MinimumScore = 8 });
		Assert.NotEmpty(advanced);
		Assert.All(advanced, x => Assert.True(x.MeanScore >= 80));

		var season = await provider.GetSeasonAnimeAsync(new Season(AnimeSeason.Spring, 2019), CancellationToken.None);
		Assert.Contains(season, x => x.Id == DemonSlayerKitsu);
		Assert.Contains(season, x => x.Related.Any());
		Assert.NotEmpty(await provider.GetPopularAnimeAsync(CancellationToken.None));
	}

	[Fact]
	public async Task Kitsu_Live_SignInReachesKitsuPastCloudflare()
	{
		var session = Kitsu().GetRequiredService<KitsuSession>();

		// A wrong login: Kitsu itself answers 400, where Cloudflare's bot check would be a 403 page.
		var ex = await Assert.ThrowsAsync<Flurl.Http.FlurlHttpException>(() => session.SignInAsync("nobody@example.com", "not-a-password"));
		Assert.Equal(400, ex.StatusCode);
		Assert.False(session.IsSignedIn);
	}

	private static ServiceProvider Shikimori() => Services<Totoro.Plugins.Shikimori.Settings>(new ShikimoriModule());

	private static ServiceProvider Kitsu() => Services<Totoro.Plugins.Kitsu.Settings>(new KitsuModule());

	/// <summary>The module's services, with blank settings kept in memory: never the user's (their login), and never saved.</summary>
	private static ServiceProvider Services<TSettings>(IModule module) where TSettings : DataContainerConfig, new()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		module.ConfigureServices(services);
		services.AddSingleton<IModuleSettings<TSettings>>(new InMemorySettings<TSettings>(module.Descriptor));
		return services.BuildServiceProvider();
	}

	private sealed class InMemorySettings<TSettings>(Descriptor descriptor) : IModuleSettings<TSettings> where TSettings : DataContainerConfig, new()
	{
		public TSettings Value { get; } = new();
		public Descriptor Descriptor => descriptor;
		public DataContainerConfig Config => Value;
		public DataContainerConfig CreateCopy(JsonObject? changes = null) => new TSettings();
		public JsonObject ToJson(DataContainerConfig config) => [];
		public void Save() { }
	}
}

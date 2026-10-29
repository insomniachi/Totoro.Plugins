using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Modules.Manga;
using Totoro.Plugins.MangaDex;
using Totoro.Plugins.WeebCentral;

namespace Totoro.Plugins.Tests;

public class MangaSourcePluginTests
{
	private static IMangaProvider Create(Core.Modules.IModule module, Guid id)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		module.ConfigureServices(services);
		return services.BuildServiceProvider().GetRequiredKeyedService<IMangaProvider>(id);
	}

	[Fact]
	public void Modules_RegisterKeyedMangaProviders()
	{
		Assert.Equal("Weeb Central", Create(new WeebCentralModule(), WeebCentralModule.Id).Name);
		Assert.Equal("MangaDex", Create(new MangaDexModule(), MangaDexModule.Id).Name);
	}

	[Fact]
	public void WeebCentral_ParseChapters_OnePerNumberInReadingOrder()
	{
		const string html = """
			<a href="/chapters/01M5KHCBE9Y56XTJBCHZX46KEN"><span class="grow flex"><span class="">Mag Version 242</span></span></a>
			<a href="/chapters/01M57YNSGCXXRK2CA0H2FJC1XW"><span class="grow flex"><span class="">Chapter 242</span></span></a>
			<a href="/chapters/01M57YNSGCXXRK2CA0H2FJC1XV"><span class="grow flex"><span class="">Episode 3.5</span></span></a>
			""";

		var chapters = WeebCentralProvider.ParseChapters(html, "S");

		Assert.Equal([3.5f, 242f], chapters.Select(x => x.Number));
		// The oldest of a number is kept: the list is newest first.
		Assert.Equal("Chapter 242", chapters[1].Title);
	}

	[Fact]
	public void WeebCentral_LinksTo_MatchesWholeIds()
	{
		const string page = """<a href="https://anilist.co/manga/74347">AniList</a>""";
		Assert.True(WeebCentralProvider.LinksTo(page, new MangaId { Anilist = 74347 }));
		Assert.False(WeebCentralProvider.LinksTo(page, new MangaId { Anilist = 7434 }));
	}

	[Fact]
	public void MangaDex_ToChapters_LeavesOutExternalAndDuplicates()
	{
		using var json = JsonDocument.Parse("""
			[
			  {"id":"a","attributes":{"chapter":"1","title":"Dog & Chainsaw","volume":"1","translatedLanguage":"en","pages":53,"externalUrl":null}},
			  {"id":"b","attributes":{"chapter":"1","title":null,"volume":"1","translatedLanguage":"en","pages":50,"externalUrl":null}},
			  {"id":"c","attributes":{"chapter":"2","title":"","volume":null,"translatedLanguage":"en","pages":1,"externalUrl":"https://mangaplus.shueisha.co.jp/"}},
			  {"id":"d","attributes":{"chapter":"3","title":"","volume":null,"translatedLanguage":"en","pages":0,"externalUrl":null}}
			]
			""");

		var chapters = MangaDexProvider.ToChapters(json.RootElement.EnumerateArray(), "m");

		var chapter = Assert.Single(chapters);
		Assert.Equal("a", chapter.Id);
		Assert.Equal("Dog & Chainsaw", chapter.Title);
	}

	// Live: these reach the sites, as the anime providers' live tests do.

	[Fact]
	public async Task WeebCentral_Live_FindsByAniListIdAndReadsPages()
	{
		var source = Create(new WeebCentralModule(), WeebCentralModule.Id);

		var match = await source.FindMangaAsync(new MangaModel { Title = "One Punch-Man", ExternalIds = new MangaId { Anilist = 74347 } }, CancellationToken.None);

		Assert.Equal("01J76XY7KT7J224EBK6J816Y1Q", match?.Id);
		var chapters = await source.GetChaptersAsync(match!.Id, CancellationToken.None);
		Assert.True(chapters.Count > 200);
		Assert.NotEmpty(await source.GetPagesAsync(chapters[0], CancellationToken.None));
	}

	[Fact]
	public async Task MangaDex_Live_FindsByAniListIdAndReadsPages()
	{
		var source = Create(new MangaDexModule(), MangaDexModule.Id);

		var match = await source.FindMangaAsync(new MangaModel { Title = "Chainsaw Man", ExternalIds = new MangaId { Anilist = 105778 } }, CancellationToken.None);

		Assert.Equal("a77742b1-befd-49a4-bff5-1ad4e6b0ef7b", match?.Id);
		var chapters = await source.GetChaptersAsync(match!.Id, CancellationToken.None);
		Assert.True(chapters.Count > 50);
		Assert.NotEmpty(await source.GetPagesAsync(chapters[0], CancellationToken.None));
	}
}

using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Modules.Manga;
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
}

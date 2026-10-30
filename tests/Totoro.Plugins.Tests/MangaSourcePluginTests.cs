using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Modules.Manga;
using Totoro.Plugins.MangaFire;
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
		Assert.Equal("MangaFire", Create(new MangaFireModule(), MangaFireModule.Id).Name);
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
	public void MangaFire_Sign_MatchesTheSite()
	{
		// Signatures the site accepted.
		Assert.Equal("8sK3xtqdFds7Xfo", MangaFireProvider.Sign(MangaFireProvider.Canonical("/titles/dkw", [])));
		var canonical = MangaFireProvider.Canonical("/titles", [("page", "1"), ("keyword", "one piece"), ("languages[]", "en"), ("limit", "3")]);
		Assert.Equal("/titles?keyword=one piece&languages[0]=en&limit=3&page=1", canonical);
		Assert.Equal("8sK3xtqdFZfetBhus6bRApNr5zMeEWBTZ95f5YlQeQ2zTkcLLz77bewH4IB-V9RBYc7VE6KQR-8", MangaFireProvider.Sign(canonical));
	}

	[Fact]
	public void MangaFire_ToChapters_OnePerNumberOfficialFirst()
	{
		var chapters = MangaFireProvider.ToChapters(
		[
			new(3, 2, "", "unofficial"),
			new(2, 2, "Two", "official"),
			new(1, 1.5f, null, "unofficial"),
		], "dkw");

		Assert.Equal([1.5f, 2f], chapters.Select(x => x.Number));
		Assert.Equal("2", chapters[1].Id);
		Assert.Equal("Chapter 2: Two", chapters[1].Title);
	}

	// Live: these reach the sites, as the anime providers' live tests do.

	[Fact]
	public async Task MangaFire_Live_FindsByAniListIdAndReadsPages()
	{
		var source = Create(new MangaFireModule(), MangaFireModule.Id);

		var match = await source.FindMangaAsync(new MangaModel { Title = "One Piece", ExternalIds = new MangaId { Anilist = 30013 } }, CancellationToken.None);

		Assert.Equal("dkw", match?.Id);
		var chapters = await source.GetChaptersAsync(match!.Id, CancellationToken.None);
		Assert.True(chapters.Count > 1000);
		var pages = await source.GetPagesAsync(chapters[^1], CancellationToken.None);
		Assert.NotEmpty(pages);
		using var client = new HttpClient();
		using var image = new HttpRequestMessage(HttpMethod.Get, pages[0].Url);
		foreach (var (name, value) in pages[0].Headers)
		{
			image.Headers.Add(name, value);
		}

		Assert.True((await client.SendAsync(image)).IsSuccessStatusCode);
	}

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

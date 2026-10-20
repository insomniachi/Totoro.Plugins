using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Modules.Anime;
using Totoro.Core.Modules.Streaming;
using Totoro.Plugins.AnimePahe;
using Totoro.Plugins.ReAnime;
using Totoro.Plugins.Senshi;
using Totoro.Plugins.SubsPlease;

namespace Totoro.Plugins.Tests;

public class PluginVerificationTests
{
	private sealed record PluginManifestDto(
		[property: JsonPropertyName("id")] Guid Id,
		[property: JsonPropertyName("name")] string Name,
		[property: JsonPropertyName("version")] string Version,
		[property: JsonPropertyName("entryPoint")] string EntryPoint);

	[Fact]
	public void AllPluginManifests_AreValidAndParseable()
	{
		var repoRoot = FindPluginsRepoRoot();
		var manifestFiles = Directory.GetFiles(Path.Combine(repoRoot, "src"), "plugin.json", SearchOption.AllDirectories)
			.Where(f => !f.Contains("bin", StringComparison.OrdinalIgnoreCase) && !f.Contains("obj", StringComparison.OrdinalIgnoreCase))
			.ToList();

		Assert.Equal(4, manifestFiles.Count);

		foreach (var file in manifestFiles)
		{
			var json = File.ReadAllText(file);
			var manifest = JsonSerializer.Deserialize<PluginManifestDto>(json);

			Assert.NotNull(manifest);
			Assert.NotEqual(Guid.Empty, manifest.Id);
			Assert.False(string.IsNullOrWhiteSpace(manifest.Name));
			Assert.False(string.IsNullOrWhiteSpace(manifest.Version));
			Assert.False(string.IsNullOrWhiteSpace(manifest.EntryPoint));
		}
	}

	[Fact]
	public void SenshiModule_RegistersKeyedAnimeProvider()
	{
		var module = new SenshiModule();
		var services = new ServiceCollection();
		services.AddLogging();
		module.ConfigureServices(services);

		var sp = services.BuildServiceProvider();
		var provider = sp.GetKeyedService<IAnimeProvider>(SenshiModule.Id);

		Assert.NotNull(provider);
		Assert.Equal("Senshi", provider.Name);
	}

	[Fact]
	public void SubsPleaseModule_RegistersKeyedAnimeProvider()
	{
		var module = new SubsPleaseModule();
		var services = new ServiceCollection();
		services.AddLogging();
		module.ConfigureServices(services);

		var sp = services.BuildServiceProvider();
		var provider = sp.GetKeyedService<IAnimeProvider>(SubsPleaseModule.Id);

		Assert.NotNull(provider);
		Assert.Equal("SubsPlease", provider.Name);
	}

	[Fact]
	public void AnimePaheModule_RegistersKeyedAnimeProvider()
	{
		var module = new AnimePaheModule();
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<Totoro.Core.Web.IWebPageLoader, DummyWebPageLoader>();
		module.ConfigureServices(services);

		var sp = services.BuildServiceProvider();
		var provider = sp.GetKeyedService<IAnimeProvider>(AnimePaheModule.Id);

		Assert.NotNull(provider);
		Assert.Equal("AnimePahe", provider.Name);
	}

	[Fact]
	public async Task SenshiProvider_LiveSearch_ReturnsResultsFromSenshiTo()
	{
		var module = new SenshiModule();
		var services = new ServiceCollection();
		services.AddLogging();
		module.ConfigureServices(services);

		var sp = services.BuildServiceProvider();
		var provider = sp.GetRequiredKeyedService<IAnimeProvider>(SenshiModule.Id);

		var results = await provider.SearchAsync("Frieren", CancellationToken.None);
		Assert.NotEmpty(results);
		Assert.Contains(results, s => s.Title.Contains("Frieren", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task SenshiProvider_LiveFindShow_ByMyAnimeListId()
	{
		var module = new SenshiModule();
		var services = new ServiceCollection();
		services.AddLogging();
		module.ConfigureServices(services);

		var provider = services.BuildServiceProvider().GetRequiredKeyedService<IAnimeProvider>(SenshiModule.Id);

		var show = await provider.FindShowAsync(new AnimeId { MyAnimeList = 52991 }, CancellationToken.None);
		Assert.NotNull(show);
		Assert.Contains("Frieren", show.Title, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(154587, show.Ids?.Anilist);
		Assert.NotEmpty(await provider.GetEpisodesAsync(show.Id, CancellationToken.None));
		Assert.Null(await provider.FindShowAsync(new AnimeId { MyAnimeList = 999999999 }, CancellationToken.None));
	}

	[Fact]
	public async Task ReAnimeProvider_Live_SearchesListsAndResolvesAFile()
	{
		var module = new ReAnimeModule();
		var services = new ServiceCollection();
		services.AddLogging();
		module.ConfigureServices(services);

		var sp = services.BuildServiceProvider();
		var provider = sp.GetRequiredKeyedService<IAnimeProvider>(ReAnimeModule.Id);
		Assert.Equal("Re:ANIME", provider.Name);

		var shows = await provider.SearchAsync("Frieren", CancellationToken.None);
		Assert.Contains(shows, s => s.Title.Contains("Frieren", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(shows, s => s.Ids?.Anilist == 154587);

		var episodes = await provider.GetEpisodesAsync(shows[0].Id, CancellationToken.None);
		Assert.NotEmpty(episodes);

		var servers = await provider.GetServersAsync(episodes.MinBy(x => x.Number)!, CancellationToken.None);
		Assert.NotEmpty(servers);
		Assert.All(servers, x => Assert.True(x.Url.IsAbsoluteUri));
	}

	private sealed class DummyWebPageLoader : Totoro.Core.Web.IWebPageLoader
	{
		public Task<Totoro.Core.Web.WebPage> LoadAsync(Uri url, Uri? referrer, CancellationToken ct) =>
			Task.FromResult(new Totoro.Core.Web.WebPage(url, "text/html", ""));
	}

	private static string FindPluginsRepoRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Totoro.Plugins.slnx")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		return dir ?? throw new InvalidOperationException("Could not find Totoro.Plugins repository root.");
	}
}

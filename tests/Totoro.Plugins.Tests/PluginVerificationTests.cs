using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Modules.Streaming;
using Totoro.Plugins.AnimePahe;
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

		Assert.Equal(3, manifestFiles.Count);

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

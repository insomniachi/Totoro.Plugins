using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Modules.Anime;
using Totoro.Core.Modules.Media;
using Totoro.Core.Modules.Streaming;
using Totoro.Plugins.AnimePahe;
using Totoro.Plugins.KickAssAnime;
using Totoro.Plugins.ReAnime;
using Totoro.Plugins.RealDebrid;
using Totoro.Plugins.Senshi;
using Totoro.Plugins.SubsPlease;
using Totoro.Plugins.TorBox;
using Totoro.Plugins.TsukiHime;

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

		Assert.Equal(11, manifestFiles.Count);

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
	public void KickAssAnimeProvider_ParsePlayer_ReadsManifestAndSubtitles()
	{
		var player = new Uri("https://krussdomi.com/cat-player/player?id=1&type=dash");
		const string html = """
			<astro-island props="{&quot;manifest&quot;:[0,&quot;https:////bl.krussdomi.com/mpd/1/master.mpd&quot;],&quot;subtitles&quot;:[1,[[0,{&quot;language&quot;:[0,&quot;id&quot;],&quot;filename&quot;:[0,&quot;a_id.vtt&quot;],&quot;name&quot;:[0,&quot;Bahasa Indonesia&quot;],&quot;src&quot;:[0,&quot;https://krussdomi.com/subs/a_id.vtt&quot;]}],[0,{&quot;language&quot;:[0,&quot;en&quot;],&quot;name&quot;:[0,&quot;English&quot;],&quot;src&quot;:[0,&quot;//krussdomi.com/subs/a_en.vtt&quot;]}]]]}">
			""";

		var server = KickAssAnimeProvider.ParsePlayer("BirdStream", player, html);

		Assert.NotNull(server);
		Assert.Equal("https://bl.krussdomi.com/mpd/1/master.mpd", server.Url.AbsoluteUri);
		Assert.Equal("https://krussdomi.com", server.Headers["Origin"]);
		Assert.Equal(
			[("English", "en", "https://krussdomi.com/subs/a_en.vtt"), ("Bahasa Indonesia", "id", "https://krussdomi.com/subs/a_id.vtt")],
			server.Subtitles.Select(x => (x.Name, x.Language, x.Url.AbsoluteUri)));
		Assert.Null(KickAssAnimeProvider.ParsePlayer("Vid", player, "<html></html>"));
	}

	[Fact]
	public async Task KickAssAnimeProvider_Live_SearchesListsAndFindsStreams()
	{
		var module = new KickAssAnimeModule();
		var services = new ServiceCollection();
		services.AddLogging();
		module.ConfigureServices(services);

		var sp = services.BuildServiceProvider();
		var provider = sp.GetRequiredKeyedService<IAnimeProvider>(KickAssAnimeModule.Id);
		Assert.Equal("KickAssAnime", provider.Name);

		var shows = await provider.SearchAsync("Sousou no Frieren", CancellationToken.None);
		Assert.Equal("sousou-no-frieren-2d15", shows[0].Id);

		var episodes = await provider.GetEpisodesAsync(shows[0].Id, CancellationToken.None);
		Assert.Equal(28, episodes.Count);

		var servers = await provider.GetServersAsync(episodes.MinBy(x => x.Number)!, CancellationToken.None);
		Assert.NotEmpty(servers);
		Assert.Contains(servers, x => x.Subtitles.FirstOrDefault()?.Language == "en");
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
		Assert.Contains(servers, x => x.Segments.Any(s => s.Type == MediaSegmentType.Opening));
	}

	[Fact]
	public void ReAnimeProvider_ParseSegments_ReadsChaptersFromSvelteKitData()
	{
		// devalue: values are indexes into "data"; -1 is undefined.
		const string json = """
			{"type":"data","nodes":[null,{"type":"data","data":[
				{"intro_chapter":1,"outro_chapter":5,"video_title":4},
				{"start":2,"end":3,"title":4},0,90,"OP",
				{"start":6,"end":7},1332,1422.5]}]}
			""";

		Assert.Equal(
			[new(MediaSegmentType.Opening, TimeSpan.Zero, TimeSpan.FromSeconds(90)), new(MediaSegmentType.Ending, TimeSpan.FromSeconds(1332), TimeSpan.FromSeconds(1422.5))],
			ReAnimeProvider.ParseSegments(json));
		Assert.Empty(ReAnimeProvider.ParseSegments("""{"nodes":[{"type":"data","data":[{"intro_chapter":-1}]}]}"""));
	}

	[Fact]
	public void SenshiStream_Segments_SkipsUnknownTimes()
	{
		var stream = JsonSerializer.Deserialize<SenshiStream>("""{"intro_start_ms":117033,"intro_end_ms":206997,"outro_start_ms":null,"outro_end_ms":null}""")!;

		Assert.Equal([new(MediaSegmentType.Opening, TimeSpan.FromMilliseconds(117033), TimeSpan.FromMilliseconds(206997))], stream.Segments());
	}

	[Fact]
	public void TsukiHimeModule_RegistersKeyedAnimeProvider()
	{
		var module = new TsukiHimeModule();
		var services = new ServiceCollection();
		services.AddLogging();
		module.ConfigureServices(services);

		var provider = services.BuildServiceProvider().GetKeyedService<IAnimeProvider>(TsukiHimeModule.Id);

		Assert.NotNull(provider);
		Assert.Equal("TsukiHime", provider.Name);
	}

	[Fact]
	public void TsukiHimeProvider_ToServers_DropsBatchesAndOrdersByAudioThenQuality()
	{
		var json = """
			[
			  {"name": "[A] Show - 01 (720p)", "btih": "1111111111111111111111111111111111111111", "nyaa_id": 5, "filecount": 1, "audiolangs": ["ja"], "group": {"name": "A"}},
			  {"name": "[B] Show - 01 (1080p)", "btih": "2222222222222222222222222222222222222222", "nyaa_id": 0, "filecount": 1, "audiolangs": ["en"], "group": {"name": "B"}},
			  {"name": "[C] Show - 01 (1080p)", "btih": "3333333333333333333333333333333333333333", "nyaa_id": 0, "filecount": 1, "audiolangs": [], "group": {"name": "C"}},
			  {"name": "[D] Show - Batch (1080p)", "btih": "4444444444444444444444444444444444444444", "nyaa_id": 0, "filecount": 12, "audiolangs": ["ja"], "group": {"name": "D"}}
			]
			""";
		var torrents = JsonSerializer.Deserialize<List<TsukiHimeProvider.Torrent>>(json)!;

		var sub = TsukiHimeProvider.ToServers(torrents, new Totoro.Plugins.TsukiHime.Settings());
		Assert.Equal(["C 1080p", "A 720p", "B 1080p (dub)"], sub.Select(x => x.Name));
		Assert.All(sub, x => Assert.Equal(VideoServerKind.Torrent, x.Kind));
		Assert.Equal("https://nyaa.si/download/5.torrent", sub[1].Url.ToString());
		Assert.StartsWith("magnet:?xt=urn:btih:3333", sub[0].Url.ToString());

		var dub = TsukiHimeProvider.ToServers(torrents, new Totoro.Plugins.TsukiHime.Settings { Audio = Totoro.Core.Modules.Streaming.AudioType.Dub, Quality = Totoro.Core.Modules.Streaming.VideoQuality.Hd });
		Assert.Equal("B 1080p (dub)", dub[0].Name);
	}

	[Fact]
	public async Task TsukiHimeProvider_Live_FindsShowByAnilistIdAndListsTorrents()
	{
		var module = new TsukiHimeModule();
		var services = new ServiceCollection();
		services.AddLogging();
		module.ConfigureServices(services);

		var provider = services.BuildServiceProvider().GetRequiredKeyedService<IAnimeProvider>(TsukiHimeModule.Id);

		// Link Click Season 3.
		var show = await provider.FindShowAsync(new AnimeId { Anilist = 191832 }, CancellationToken.None);
		Assert.NotNull(show);
		Assert.Equal(191832, show.Ids?.Anilist);

		var episodes = await provider.GetEpisodesAsync(show.Id, CancellationToken.None);
		Assert.NotEmpty(episodes);

		var servers = await provider.GetServersAsync(episodes.MinBy(x => x.Number)!, CancellationToken.None);
		Assert.NotEmpty(servers);

		Assert.Contains(await provider.SearchAsync("Frieren", CancellationToken.None), s => s.Title.Contains("Frieren", StringComparison.OrdinalIgnoreCase));
		Assert.Null(await provider.FindShowAsync(new AnimeId { MyAnimeList = 999999999 }, CancellationToken.None));
	}

	[Fact]
	public async Task TorBoxStreamer_AddsWaitsForDownloadAndLinksTheSelectedFile()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		new TorBoxModule().ConfigureServices(services);
		var sp = services.BuildServiceProvider();
		var streamer = sp.GetRequiredKeyedService<ITorrentStreamer>(TorBoxModule.Id);
		Assert.Equal("TorBox", streamer.Name);

		// Changed in memory only: module settings are written to disk by Save.
		var settings = sp.GetRequiredService<Totoro.Core.Settings.IModuleSettings<Totoro.Plugins.TorBox.Settings>>().Value;
		settings.ApiKey = "";
		Assert.False(streamer.IsAvailable());
		settings.ApiKey = "key";
		settings.KeepTorrents = false;
		settings.OnlyCached = false;

		var files = new[] { new { id = 0, name = "Show/sample.mkv", size = 10L }, new { id = 1, name = "Show/Show - 01.mkv", size = 100L } };
		var magnet = new Uri("magnet:?xt=urn:btih:1111111111111111111111111111111111111111");
		using var http = new Flurl.Http.Testing.HttpTest();
		http.RespondWithJson(new { success = true, data = new[] { new { id = 3 } } })
			.RespondWithJson(new { success = true, data = new { torrent_id = 7 } })
			.RespondWithJson(new { success = true, data = new { download_state = "downloading", download_finished = false, download_present = false, progress = 0.5, files } })
			.RespondWithJson(new { success = true, data = new { download_state = "cached", download_finished = true, download_present = true, progress = 1.0, files } })
			.RespondWithJson(new { success = true, data = "https://cdn.torbox.app/7/1" })
			.RespondWithJson(new { success = true, data = (object?)null });

		var stream = await streamer.StreamAsync(magnet, TorrentFile.LargestVideo, null, CancellationToken.None);

		Assert.Equal("https://cdn.torbox.app/7/1", stream.Url.ToString());
		Assert.Equal(1, stream.File.Index);
		http.ShouldHaveCalled("https://api.torbox.app/v1/api/torrents/requestdl").WithQueryParam("torrent_id", 7).WithQueryParam("file_id", 1)
			.WithOAuthBearerToken("key");

		// Added by the stream, so removed when it ends.
		await stream.DisposeAsync();
		http.ShouldHaveCalled("https://api.torbox.app/v1/api/torrents/controltorrent").WithRequestJson(new { torrent_id = 7, operation = "delete" });

		// The user's own torrent (3) is left in TorBox; only cached ones are asked for.
		settings.OnlyCached = true;
		http.RespondWithJson(new { success = true, data = new[] { new { id = 3 } } })
			.RespondWithJson(new { success = true, data = new { torrent_id = 3 } })
			.RespondWithJson(new { success = true, data = new { download_state = "cached", download_finished = true, download_present = true, progress = 1.0, files } })
			.RespondWithJson(new { success = true, data = "https://cdn.torbox.app/3/1" });
		await (await streamer.StreamAsync(magnet, TorrentFile.LargestVideo, null, CancellationToken.None)).DisposeAsync();
		http.ShouldHaveCalled("https://api.torbox.app/v1/api/torrents/createtorrent")
			.With(c => c.HttpRequestMessage.Content is System.Net.Http.MultipartContent parts &&
					   parts.Any(x => x.Headers.ContentDisposition?.Name?.Trim('"') == "add_only_if_cached"));
		http.ShouldHaveCalled("https://api.torbox.app/v1/api/torrents/controltorrent").Times(1);

		http.RespondWithJson(new { success = false, detail = "Your API key is invalid." });
		var error = await Assert.ThrowsAsync<TorrentStreamException>(() => streamer.StreamAsync(new Uri("magnet:?xt=urn:btih:2"), TorrentFile.LargestVideo, null,
																									  CancellationToken.None));
		Assert.Equal("TorBox: Your API key is invalid.", error.Message);
	}

	[Fact]
	public async Task RealDebridStreamer_SelectsTheFileWaitsAndUnrestrictsItsLink()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		new RealDebridModule().ConfigureServices(services);
		var sp = services.BuildServiceProvider();
		var streamer = sp.GetRequiredKeyedService<ITorrentStreamer>(RealDebridModule.Id);
		Assert.Equal("Real-Debrid", streamer.Name);

		// Changed in memory only: module settings are written to disk by Save.
		var settings = sp.GetRequiredService<Totoro.Core.Settings.IModuleSettings<Totoro.Plugins.RealDebrid.Settings>>().Value;
		settings.ApiKey = "";
		Assert.False(streamer.IsAvailable());
		settings.ApiKey = "key";
		settings.KeepTorrents = false;

		var files = new[] { new { id = 1, path = "/Show/sample.mkv", bytes = 10L }, new { id = 2, path = "/Show/Show - 01.mkv", bytes = 100L } };
		var magnet = new Uri("magnet:?xt=urn:btih:1111111111111111111111111111111111111111");
		using var http = new Flurl.Http.Testing.HttpTest();
		http.RespondWithJson(new { id = "ABC", uri = "https://api.real-debrid.com/rest/1.0/torrents/info/ABC" }, 201)
			.RespondWithJson(new { status = "waiting_files_selection", progress = 0.0, files, links = Array.Empty<string>() })
			.RespondWith("", 204)
			.RespondWithJson(new { status = "downloading", progress = 50.0, files, links = Array.Empty<string>() })
			.RespondWithJson(new { status = "downloaded", progress = 100.0, files, links = new[] { "https://real-debrid.com/d/XYZ" } })
			.RespondWithJson(new { download = "https://cdn.real-debrid.com/d/XYZ/Show%20-%2001.mkv" })
			.RespondWith("", 204);

		var stream = await streamer.StreamAsync(magnet, TorrentFile.LargestVideo, null, CancellationToken.None);

		Assert.Equal("https://cdn.real-debrid.com/d/XYZ/Show%20-%2001.mkv", stream.Url.AbsoluteUri);
		Assert.Equal(2, stream.File.Index);
		Assert.Equal("Show/Show - 01.mkv", stream.File.Path);
		http.ShouldHaveCalled("https://api.real-debrid.com/rest/1.0/torrents/selectFiles/ABC").WithRequestUrlEncoded(new { files = 2 })
			.WithOAuthBearerToken("key");
		http.ShouldHaveCalled("https://api.real-debrid.com/rest/1.0/unrestrict/link").WithRequestUrlEncoded(new { link = "https://real-debrid.com/d/XYZ" });

		// Added by the stream, so removed when it ends.
		await stream.DisposeAsync();
		http.ShouldHaveCalled("https://api.real-debrid.com/rest/1.0/torrents/delete/ABC").WithVerb(HttpMethod.Delete);

		http.RespondWithJson(new { error = "bad_token", error_code = 8 }, 401);
		var error = await Assert.ThrowsAsync<TorrentStreamException>(() => streamer.StreamAsync(new Uri("magnet:?xt=urn:btih:2"), TorrentFile.LargestVideo, null,
																									  CancellationToken.None));
		Assert.Equal("Real-Debrid: bad token", error.Message);
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

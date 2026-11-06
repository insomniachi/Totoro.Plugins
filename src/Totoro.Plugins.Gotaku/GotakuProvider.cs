using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Totoro.Core.Modules.Media;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;
using Totoro.Core.Web;

namespace Totoro.Plugins.Gotaku;

/// <summary>
/// Gotaku (after the Android extension at github.com/yuzono/anime-extensions, src/en/gotaku): a JSON API for searching,
/// and encrypted answers for a show's episodes, an episode's audio and its player. The player's page leads to an
/// encrypted manifest, which has the HLS stream; that is encrypted too, so it's played through <see cref="GotakuProxy"/>.
/// </summary>
/// <remarks>A show's id here is its id on the site (e.g. vbhgad), an episode's too.</remarks>
public sealed partial class GotakuProvider(
	IHttpClientFactory httpClientFactory,
	IModuleSettings<Settings> moduleSettings,
	ILogger<GotakuProvider> logger,
	IWebPageLoader? pages = null) : IAnimeProvider
{
	internal const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

	private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

	public Guid Id => GotakuModule.Id;

	public string Name => "Gotaku";

	// The module's settings, unless an anime's own are used instead.
	private Settings _settings = moduleSettings.Value;

	public void UseSettings(DataContainerConfig settings)
	{
		_settings = (Settings)settings;
	}

	private string Site => Uri.TryCreate(_settings.Site.Trim().TrimEnd('/'), UriKind.Absolute, out var site)
		? site.ToString().TrimEnd('/')
		: "https://gotaku.to";

	public async Task<List<ProviderShow>> SearchAsync(string query, CancellationToken ct)
	{
		using var client = httpClientFactory.CreateClient("Gotaku");
		using var request = Request($"{Site}/api/v1/titles?q={Uri.EscapeDataString(query)}&limit=28&page=1", $"{Site}/browse?q={Uri.EscapeDataString(query)}");
		using var response = await client.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode)
		{
			logger.LogWarning("Gotaku answered a search with {Status}", response.StatusCode);
			return [];
		}

		var titles = (await response.Content.ReadFromJsonAsync<Envelope<List<Title>>>(s_json, ct))?.Data ?? [];
		return
		[
			.. titles.Where(x => x.Id is { Length: > 0 } && x.Name is { Length: > 0 })
					 .Select(x => new ProviderShow(x.Id, x.Name, TitleMatch.Score(query, x.Name)) { Image = x.PosterUrl ?? "" })
					 .OrderByDescending(x => x.Score)
		];
	}

	public async Task<List<ProviderEpisode>> GetEpisodesAsync(string showId, CancellationToken ct)
	{
		var episodes = await GetEncryptedAsync<List<Episode>>($"/titles/{Uri.EscapeDataString(showId)}/episodes", $"{Site}/title/{Uri.EscapeDataString(showId)}", ct) ?? [];
		return [.. episodes.Select((x, i) => new ProviderEpisode(showId, x.Id, (float)(x.Number ?? i + 1)))];
	}

	/// <summary>The episode's stream in each audio it has (the preferred one first), each with its subtitles and intro and outro.</summary>
	public async Task<List<VideoServer>> GetServersAsync(ProviderEpisode episode, CancellationToken ct)
	{
		var watch = $"{Site}/title/{Uri.EscapeDataString(episode.ShowId)}?ep={Uri.EscapeDataString(episode.Id)}";
		var path = $"/episodes/{Uri.EscapeDataString(episode.Id)}";
		if (await GetEncryptedAsync<AudioFlags>(path, watch, ct) is not { } flags)
		{
			return [];
		}

		var types = new[] { ("soft_sub", "Soft Sub", flags.SoftSub), ("hard_sub", "Hard Sub", flags.HardSub), ("dub", "Dub", flags.Dub) }
			.Where(x => x.Item3)
			.OrderByDescending(x => _settings.Audio == AudioType.Dub && x.Item1 == "dub");

		List<VideoServer> servers = [];
		foreach (var (type, name, _) in types)
		{
			try
			{
				if (await GetEncryptedAsync<Embed>($"{path}/embed?type={type}", watch, ct) is { Url: { Length: > 0 } embed } found &&
					await GetStreamAsync(name, new Uri(new Uri(Site), embed), watch, ct) is { } server)
				{
					servers.Add(server with { Segments = Segments(found.Skip) });
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
			{
				logger.LogWarning(ex, "Couldn't read Gotaku's {Type} stream of {Episode}", type, episode.Id);
			}
		}

		return servers;
	}

	/// <summary>The player page's manifest (fetched with the handshake its data-manifest attributes make), as a local stream.</summary>
	private async Task<VideoServer?> GetStreamAsync(string name, Uri embed, string watch, CancellationToken ct)
	{
		using var client = httpClientFactory.CreateClient("Gotaku");
		using var page = Request(embed.AbsoluteUri, watch);
		using var pageResponse = await client.SendAsync(page, ct);
		if (ManifestAttributes().Match(await pageResponse.Content.ReadAsStringAsync(ct)) is not { Success: true } attributes ||
			!Uri.TryCreate(WebUtility.HtmlDecode(attributes.Groups["base"].Value), UriKind.Absolute, out var manifestBase) || manifestBase.Scheme != Uri.UriSchemeHttps)
		{
			logger.LogWarning("Gotaku's player {Embed} has no manifest", embed);
			return null;
		}

		var handshake = new GotakuCrypto.ManifestHandshake(WebUtility.HtmlDecode(attributes.Groups["stamp"].Value));
		using var request = Request($"{manifestBase.AbsoluteUri.TrimEnd('/')}/{handshake.RequestPath()}", embed.AbsoluteUri);
		request.Headers.Accept.Clear();
		request.Headers.Accept.ParseAdd("text/plain");
		using var response = await client.SendAsync(request, ct);
		if (handshake.DecryptResponse(await response.Content.ReadAsStringAsync(ct)) is not { } json ||
			JsonSerializer.Deserialize<Manifest>(json, s_json) is not { Source: { Length: > 0 } source } manifest ||
			!Uri.TryCreate(source, UriKind.Absolute, out var stream))
		{
			logger.LogWarning("Gotaku's manifest for {Embed} didn't decrypt ({Status})", embed, response.StatusCode);
			return null;
		}

		List<SubtitleTrack> subtitles =
		[
			.. (manifest.Subtitles ?? [])
				.Where(x => Uri.TryCreate(x.Url, UriKind.Absolute, out _))
				.Select(x => (Name: x.Label ?? x.Name ?? x.Language ?? x.Lang ?? "Unknown", Language: x.Lang ?? x.Language, Url: new Uri(x.Url!)))
				.OrderByDescending(x => x.Language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true || x.Name.StartsWith("English", StringComparison.OrdinalIgnoreCase))
				.Select(x => new SubtitleTrack(x.Name, GotakuProxy.Plain(x.Url), x.Language))
		];

		return new VideoServer(name, GotakuProxy.Playlist(stream, manifest.KeySeed ?? "", manifest.Obf?.SegmentBytes ?? 0, $"{Site}/"))
		{
			Subtitles = subtitles,
		};
	}

	private static List<MediaSegment> Segments(Skip? skip) =>
	[
		.. new[] { (MediaSegmentType.Opening, skip?.Intro), (MediaSegmentType.Ending, skip?.Outro) }
			.Where(x => x.Item2 is [var start, var end, ..] && end > start)
			.Select(x => new MediaSegment(x.Item1, TimeSpan.FromSeconds(x.Item2![0]), TimeSpan.FromSeconds(x.Item2[1])))
	];

	/// <summary>
	/// An encrypted API call's <c>data</c>; null when it fails or doesn't decrypt. When the site has moved on from the
	/// keys' build (it answers 426, or with what they don't decrypt), they're learned again and the call made once more.
	/// </summary>
	private async Task<T?> GetEncryptedAsync<T>(string path, string referer, CancellationToken ct)
	{
		var keys = GotakuKeyStore.Current;
		var (status, json) = await CallEncryptedAsync(path, referer, keys, ct);
		if (json is null && status is HttpStatusCode.UpgradeRequired or HttpStatusCode.OK &&
			await GotakuKeyStore.RelearnAsync(keys, pages, Site, async learned => (await CallEncryptedAsync("/auth/me", $"{Site}/browse", learned, ct)).Json is not null, logger, ct) is { } relearned)
		{
			(status, json) = await CallEncryptedAsync(path, referer, relearned, ct);
		}

		if (json is null)
		{
			logger.LogWarning("Gotaku's answer to {Path} was {Status}, or didn't decrypt", path, status);
			return default;
		}

		return JsonSerializer.Deserialize<Envelope<T>>(json, s_json) is { } envelope ? envelope.Data : default;
	}

	/// <summary>The API's answer to <paramref name="path"/>, decrypted with <paramref name="keys"/> (null if it fails or doesn't).</summary>
	private async Task<(HttpStatusCode Status, string? Json)> CallEncryptedAsync(string path, string referer, GotakuKeys keys, CancellationToken ct)
	{
		using var client = httpClientFactory.CreateClient("Gotaku");
		using var request = Request($"{Site}/api/v1{path}{(path.Contains('?') ? '&' : '?')}k={keys.Version}", referer);
		using var response = await client.SendAsync(request, ct);
		return (response.StatusCode, response.IsSuccessStatusCode ? GotakuCrypto.DecryptApi(await response.Content.ReadAsByteArrayAsync(ct), keys) : null);
	}

	private static HttpRequestMessage Request(string url, string referer)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Accept.ParseAdd("application/json");
		request.Headers.Referrer = new Uri(referer);
		return request;
	}

	private sealed record Envelope<T>(T? Data);

	private sealed record Title(string Id, string Name, [property: JsonPropertyName("poster_url")] string? PosterUrl);

	private sealed record Episode(string Id, double? Number);

	private sealed record AudioFlags(
		[property: JsonPropertyName("soft_sub")] bool SoftSub,
		[property: JsonPropertyName("hard_sub")] bool HardSub,
		bool Dub);

	/// <param name="Url">The player, e.g. /embed/..., on the site.</param>
	private sealed record Embed(string? Url, Skip? Skip);

	/// <param name="Intro">[start, end] in seconds.</param>
	private sealed record Skip(double[]? Intro, double[]? Outro);

	private sealed record Manifest(string? Source, string? KeySeed, Obfuscation? Obf, List<ManifestSubtitle>? Subtitles);

	/// <param name="SegmentBytes">How many of each segment's first bytes are encrypted; 0 for none.</param>
	private sealed record Obfuscation(int SegmentBytes);

	private sealed record ManifestSubtitle(string? Url, string? Language, string? Lang, string? Label, string? Name);

	[GeneratedRegex(@"data-manifest-base=""(?<base>[^""]+)""\s+data-manifest-stamp=""(?<stamp>[^""]+)""")]
	private static partial Regex ManifestAttributes();
}

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Totoro.Core.Helpers;
using Totoro.Core.Web;

namespace Totoro.Plugins.Gotaku;

/// <summary>What the site's API answers are encrypted with, which changes with the site's builds.</summary>
/// <param name="Version">The build, sent as <c>?k=</c> and the first byte of each answer.</param>
/// <param name="Seed">The 32 bytes the AES key is derived from (HKDF-SHA256, no salt).</param>
/// <param name="Info">The derivation's label.</param>
internal sealed record GotakuKeys(string Version, byte[] Seed, string Info)
{
	/// <summary>The site's as of November 2026.</summary>
	public static GotakuKeys Default { get; } = new("10",
		[30, 99, 206, 236, 176, 26, 228, 147, 55, 35, 152, 123, 6, 17, 126, 179, 40, 181, 197, 210, 194, 90, 232, 35, 247, 20, 130, 43, 71, 117, 123, 82],
		"api-seal|v1");

	public byte[] ApiKey { get; } = HKDF.DeriveKey(HashAlgorithmName.SHA256, Seed, 32, [], System.Text.Encoding.UTF8.GetBytes(Info));
}

/// <summary>
/// The keys in use, shared by every provider instance and kept in the plugin's folder: <see cref="GotakuKeys.Default"/>
/// until the site moves on, when they're learned again from the site's own code, run in the app's browser.
/// </summary>
internal static class GotakuKeyStore
{
	// The site's scripts keep their own references to fetch and crypto.subtle, so these wrap them before the scripts
	// run: the browse page's first API call (auth/me) then shows the build, and its decryption the seed and label.
	internal const string CaptureScript = """
		(() => {
			if (window !== window.top) return;
			const seen = {};
			const send = () => {
				if (seen.sent || !seen.seed || !seen.info || !seen.version) return;
				seen.sent = true;
				window.chrome.webview.postMessage(JSON.stringify({ version: seen.version, seed: seen.seed, info: seen.info }));
			};
			const version = url => {
				const k = String(url && url.url || url).match(/\/api\/.*[?&]k=(\d+)/);
				if (k && (!seen.version || +k[1] > +seen.version)) { seen.version = k[1]; send(); }
			};
			const subtle = crypto.subtle, importKey = subtle.importKey, deriveKey = subtle.deriveKey;
			subtle.importKey = function (format, data, algorithm, extractable, usages) {
				if (format === 'raw' && usages && usages.includes('deriveKey') && !seen.seed) {
					const bytes = ArrayBuffer.isView(data) ? new Uint8Array(data.buffer, data.byteOffset, data.byteLength) : new Uint8Array(data);
					if (bytes.length === 32) { seen.seed = Array.from(bytes); send(); }
				}
				return importKey.apply(this, arguments);
			};
			subtle.deriveKey = function (algorithm) {
				if (algorithm && algorithm.name === 'HKDF' && !seen.info) {
					seen.info = algorithm.info ? new TextDecoder().decode(algorithm.info) : '';
					send();
				}
				return deriveKey.apply(this, arguments);
			};
			const fetch = window.fetch, open = XMLHttpRequest.prototype.open;
			window.fetch = function (url) { version(url); return fetch.apply(this, arguments); };
			XMLHttpRequest.prototype.open = function (method, url) { version(url); return open.apply(this, arguments); };
		})();
		""";

	private static readonly SemaphoreSlim s_gate = new(1, 1);
	private static GotakuKeys? s_current;
	private static DateTime s_lastAttempt = DateTime.MinValue;

	private static string FilePath => FileHelper.GetModulePath(GotakuModule.Descriptor, "keys.json");

	public static GotakuKeys Current => s_current ??= Load() ?? GotakuKeys.Default;

	/// <summary>
	/// The site's keys, learned anew because <paramref name="failed"/> stopped working; null if they couldn't be (no
	/// browser to run its code in, or what it caught doesn't decrypt the site's answers). Another call's newer keys are
	/// returned as they are; and the site isn't asked more than every few minutes.
	/// </summary>
	/// <param name="works">Whether keys decrypt one of the site's answers.</param>
	public static async Task<GotakuKeys?> RelearnAsync(GotakuKeys failed, IWebPageLoader? pages, string site, Func<GotakuKeys, Task<bool>> works, ILogger logger, CancellationToken ct)
	{
		if (pages is null)
		{
			return null;
		}

		await s_gate.WaitAsync(ct);
		try
		{
			if (!ReferenceEquals(Current, failed))
			{
				return Current;
			}

			if (DateTime.UtcNow - s_lastAttempt < TimeSpan.FromMinutes(5))
			{
				return null;
			}

			s_lastAttempt = DateTime.UtcNow;
			logger.LogInformation("Gotaku's keys (build {Version}) stopped working; learning them again from the site", failed.Version);
			string? message;
			try
			{
				message = await pages.RunAsync(new Uri($"{site}/browse"), CaptureScript, TimeSpan.FromSeconds(20), ct);
			}
			catch (Exception ex) when (ex is NotSupportedException or WebPageException or TimeoutException)
			{
				logger.LogWarning(ex, "Couldn't run Gotaku's site to learn its keys");
				return null;
			}

			if (Parse(message) is not { } keys || !await works(keys))
			{
				logger.LogWarning("What was caught from Gotaku's site ({Message}) doesn't decrypt its answers", message is null ? "nothing" : "keys");
				return null;
			}

			logger.LogInformation("Learned Gotaku's keys for build {Version}", keys.Version);
			s_current = keys;
			Save(keys);
			return keys;
		}
		finally
		{
			s_gate.Release();
		}
	}

	/// <summary>The capture script's message as keys; null if it isn't a whole set.</summary>
	internal static GotakuKeys? Parse(string? message)
	{
		try
		{
			return message is not null && JsonSerializer.Deserialize<Stored>(message) is { Version: { Length: > 0 } version, Seed.Length: 32, Info: { } info } stored && stored.Seed.All(x => x is >= 0 and <= 255) && version.All(char.IsAsciiDigit) && int.Parse(version) < 256
				? new GotakuKeys(version, [.. stored.Seed.Select(x => (byte)x)], info)
				: null;
		}
		catch (Exception ex) when (ex is JsonException or OverflowException)
		{
			return null;
		}
	}

	private static GotakuKeys? Load()
	{
		try
		{
			return File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) : null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	private static void Save(GotakuKeys keys)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			File.WriteAllText(FilePath, JsonSerializer.Serialize(new Stored(keys.Version, [.. keys.Seed.Select(x => (int)x)], keys.Info)));
		}
		catch (IOException)
		{
			// Kept for this run only; learned again next time.
		}
	}

	private sealed record Stored(
		[property: System.Text.Json.Serialization.JsonPropertyName("version")] string? Version,
		[property: System.Text.Json.Serialization.JsonPropertyName("seed")] int[]? Seed,
		[property: System.Text.Json.Serialization.JsonPropertyName("info")] string? Info);
}

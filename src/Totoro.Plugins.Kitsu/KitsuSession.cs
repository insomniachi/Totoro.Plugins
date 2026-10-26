using System.Text.Json.Nodes;
using Flurl;
using Flurl.Http;
using Microsoft.Extensions.Logging;
using Totoro.Core.Settings;

namespace Totoro.Plugins.Kitsu;

/// <summary>
/// Requests to Kitsu's JSON:API and the user's login. Logins last a month and are refreshed (and saved) on first use
/// after they've expired. A singleton, so everything shares one login.
/// </summary>
internal sealed class KitsuSession(IModuleSettings<Settings> settings, ILogger<KitsuSession> logger)
{
	/// <summary>Kitsu's website, for links.</summary>
	public const string Site = "https://kitsu.app";

	// The API at its old address: at kitsu.app, Cloudflare answers .NET's requests to sign in with a bot check, which
	// an app can't pass. The same API, without the check, at kitsu.io.
	private const string Api = "https://kitsu.io";
	private const string JsonApi = "application/vnd.api+json";

	private readonly SemaphoreSlim _lock = new(1, 1);
	private string? _userId;

	public bool IsSignedIn => settings.Value.Auth is not null;

	public bool IncludeNsfw => settings.Value.IncludeNsfw;

	/// <summary>A request to the API's <paramref name="path"/>, with the user's (current) token if they're signed in.</summary>
	public async Task<IFlurlRequest> RequestAsync(string path)
	{
		var request = new Url(Api).AppendPathSegments("api", "edge").AppendPathSegment(path)
								   .WithHeader("Accept", JsonApi)
								   .WithHeader("Content-Type", JsonApi);
		return await GetTokenAsync() is { } token ? request.WithOAuthBearerToken(token) : request;
	}

	public async Task<KitsuDocument> GetAsync(IFlurlRequest request, CancellationToken ct = default)
	{
		return new KitsuDocument(JsonNode.Parse(await request.GetStringAsync(cancellationToken: ct))!);
	}

	/// <summary>Every page of a collection, following its next links. Throws if a page fails.</summary>
	public async Task<List<KitsuDocument>> GetAllAsync(IFlurlRequest request, CancellationToken ct = default)
	{
		var pages = new List<KitsuDocument> { await GetAsync(request, ct) };
		while (pages[^1].Next is { } next)
		{
			ct.ThrowIfCancellationRequested();
			// Same headers (the token), next page's address.
			request.Url = next;
			pages.Add(await GetAsync(request, ct));
		}

		return pages;
	}

	public async Task<string> GetUserIdAsync(CancellationToken ct = default)
	{
		return _userId ??= (await GetUserAsync(ct))?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Not signed in to Kitsu");
	}

	/// <summary>The signed-in user's resource; null if not signed in.</summary>
	public async Task<JsonNode?> GetUserAsync(CancellationToken ct = default)
	{
		var request = (await RequestAsync("users")).SetQueryParam("filter[self]", true).SetQueryParam("fields[users]", "name,avatar,ratingSystem");
		return (await GetAsync(request, ct)).Data.FirstOrDefault();
	}

	/// <summary>Signs in with the user's Kitsu email (or name) and password. Kitsu's API has no other way to.</summary>
	public async Task SignInAsync(string username, string password)
	{
		var token = await RequestTokenAsync(new { grant_type = "password", username, password });

		await _lock.WaitAsync();
		try
		{
			settings.Value.Auth = token;
			settings.Save();
			_userId = null;
		}
		finally
		{
			_lock.Release();
		}
	}

	public void SignOut()
	{
		_lock.Wait();
		try
		{
			settings.Value.Auth = null;
			settings.Save();
			_userId = null;
		}
		finally
		{
			_lock.Release();
		}
	}

	private async Task<string?> GetTokenAsync()
	{
		await _lock.WaitAsync();
		try
		{
			if (settings.Value.Auth is not { } auth)
			{
				return null;
			}

			if (auth.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
			{
				try
				{
					auth = await RequestTokenAsync(new { grant_type = "refresh_token", refresh_token = auth.RefreshToken });
					settings.Value.Auth = auth;
					settings.Save();
				}
				catch (Exception ex)
				{
					// Carry on with the old token; requests that need the login will fail and say so.
					logger.LogWarning(ex, "Couldn't refresh the Kitsu login");
				}
			}

			return auth.AccessToken;
		}
		finally
		{
			_lock.Release();
		}
	}

	private static async Task<KitsuToken> RequestTokenAsync(object body)
	{
		var response = await new Url(Api).AppendPathSegments("api", "oauth", "token")
										  .PostUrlEncodedAsync(body)
										  .ReceiveJson<JsonObject>();

		return new KitsuToken
		{
			AccessToken = (string?)response["access_token"] ?? throw new InvalidOperationException("Kitsu returned no token"),
			RefreshToken = (string?)response["refresh_token"] ?? "",
			ExpiresAt = DateTimeOffset.UtcNow.AddSeconds((double?)response["expires_in"] ?? 2592000),
		};
	}
}

/// <summary>A JSON:API response: its resources, and the ones included with them, found by their relationships.</summary>
internal sealed class KitsuDocument(JsonNode root)
{
	private readonly Dictionary<(string, string), JsonNode> _included =
		(root["included"]?.AsArray() ?? []).OfType<JsonNode>()
										   .DistinctBy(Key)
										   .ToDictionary(Key);

	/// <summary>The resources, whether the response has one or a list.</summary>
	public IReadOnlyList<JsonNode> Data => root["data"] switch
	{
		JsonArray array => [.. array.OfType<JsonNode>()],
		JsonObject resource => [resource],
		_ => [],
	};

	public string? Next => (string?)root["links"]?["next"];

	/// <summary>The included resources a resource's relationship points to.</summary>
	public IEnumerable<JsonNode> Related(JsonNode resource, string relationship)
	{
		var data = resource["relationships"]?[relationship]?["data"];
		var links = data switch
		{
			JsonArray array => array.OfType<JsonNode>(),
			JsonObject link => [link],
			_ => [],
		};

		return links.Select(x => _included.GetValueOrDefault(Key(x))).OfType<JsonNode>();
	}

	private static (string, string) Key(JsonNode resource) => ((string?)resource["type"] ?? "", (string?)resource["id"] ?? "");
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flurl;
using Flurl.Http;
using Microsoft.Extensions.Logging;
using Totoro.Core.Settings;

namespace Totoro.Plugins.Shikimori;

/// <summary>
/// Requests to Shikimori and the user's login. Logins last a day, so the token is refreshed (and saved) on first use
/// after it has expired. A singleton, so everything shares one login.
/// </summary>
internal sealed class ShikimoriSession(IModuleSettings<Settings> settings, ILogger<ShikimoriSession> logger)
{
	public const string Site = "https://shikimori.io";

	// The OAuth application's, from shikimori.io/oauth/applications, with Callback as its redirect URI. Shikimori bans
	// clients whose User-Agent doesn't name their application. The id and secret are baked in at build time (see the
	// csproj); a build without them signs in to nothing and says so, and the rest works signed out.
	public const string AppName = "Totoro";
	public static readonly string ClientId = BuildMetadata("ShikimoriClientId");
	public static readonly string ClientSecret = BuildMetadata("ShikimoriClientSecret");
	public static readonly Uri Callback = new("http://localhost:2222/callback");

	private static readonly JsonSerializerOptions s_graphQl = new(JsonSerializerDefaults.Web);
	private static readonly JsonSerializerOptions s_rest = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private readonly SemaphoreSlim _lock = new(1, 1);
	private long? _userId;

	public bool IsSignedIn => settings.Value.Auth is not null;

	public bool IncludeNsfw => settings.Value.IncludeNsfw;

	public static bool IsRegistered => ClientId.Length > 0 && ClientSecret.Length > 0;

	private static string BuildMetadata(string key) =>
		typeof(ShikimoriSession).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "";

	/// <summary>A request to <paramref name="path"/>, with the user's (current) token if they're signed in.</summary>
	public async Task<IFlurlRequest> RequestAsync(string path)
	{
		var request = new Url(Site).AppendPathSegment(path).WithHeader("User-Agent", AppName);
		return await GetTokenAsync() is { } token ? request.WithOAuthBearerToken(token) : request;
	}

	/// <summary>Runs a GraphQL query and reads its <paramref name="field"/>. Throws if Shikimori reports an error.</summary>
	public async Task<T> QueryAsync<T>(string query, string field, CancellationToken ct = default)
	{
		var request = await RequestAsync("api/graphql");
		var response = await request.PostJsonAsync(new { query }, cancellationToken: ct).ReceiveString();
		var json = JsonNode.Parse(response);
		if (json?["errors"] is JsonArray { Count: > 0 } errors)
		{
			throw new InvalidOperationException($"Shikimori: {errors[0]?["message"]}");
		}

		// Null too when signed out and asking for the current user.
		var data = json?["data"]?[field] ?? throw new InvalidOperationException($"Shikimori returned no {field}");
		return data.Deserialize<T>(s_graphQl)!;
	}

	/// <summary>Sends a list change to Shikimori's REST API (GraphQL can't change lists) and reads the entry it returns.</summary>
	/// <param name="rateId">The entry to change; null to add one.</param>
	public async Task<ShikiUserRate?> SendRateAsync(long? rateId, Func<IFlurlRequest, Task<IFlurlResponse>> send)
	{
		var request = await RequestAsync(rateId is { } id ? $"api/v2/user_rates/{id}" : "api/v2/user_rates");
		var json = await (await send(request)).GetStringAsync();
		return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ShikiUserRate>(json, s_rest);
	}

	public async Task<long> GetUserIdAsync(CancellationToken ct = default)
	{
		return _userId ??= (await QueryAsync<ShikiUser?>("{ currentUser { id nickname avatarUrl } }", "currentUser", ct))?.Id
						   ?? throw new InvalidOperationException("Not signed in to Shikimori");
	}

	public async Task<ShikiUser?> GetUserAsync()
	{
		return await QueryAsync<ShikiUser?>("{ currentUser { id nickname avatarUrl } }", "currentUser");
	}

	/// <summary>Finishes signing in with the code Shikimori's sign-in page redirected with.</summary>
	public async Task SignInAsync(string code)
	{
		var token = await RequestTokenAsync(new
		{
			grant_type = "authorization_code",
			client_id = ClientId,
			client_secret = ClientSecret,
			code,
			redirect_uri = Callback.ToString(),
		});

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
					auth = await RequestTokenAsync(new
					{
						grant_type = "refresh_token",
						client_id = ClientId,
						client_secret = ClientSecret,
						refresh_token = auth.RefreshToken,
					});
					settings.Value.Auth = auth;
					settings.Save();
				}
				catch (Exception ex)
				{
					// Carry on with the old token; requests that need the login will fail and say so.
					logger.LogWarning(ex, "Couldn't refresh the Shikimori login");
				}
			}

			return auth.AccessToken;
		}
		finally
		{
			_lock.Release();
		}
	}

	private static async Task<ShikimoriToken> RequestTokenAsync(object body)
	{
		var response = await new Url(Site).AppendPathSegments("oauth", "token")
										  .WithHeader("User-Agent", AppName)
										  .PostUrlEncodedAsync(body)
										  .ReceiveJson<JsonObject>();

		return new ShikimoriToken
		{
			AccessToken = (string?)response["access_token"] ?? throw new InvalidOperationException("Shikimori returned no token"),
			RefreshToken = (string?)response["refresh_token"] ?? "",
			ExpiresAt = DateTimeOffset.UtcNow.AddSeconds((double?)response["expires_in"] ?? 86400),
		};
	}
}

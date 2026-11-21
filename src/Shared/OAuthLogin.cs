using System.Text.Json.Nodes;
using Flurl.Http;
using Microsoft.Extensions.Logging;

namespace Totoro.Plugins.Shared;

/// <summary>An OAuth login as a plugin's settings save it.</summary>
internal interface IOAuthToken
{
	string AccessToken { get; set; }
	string RefreshToken { get; set; }
	DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// A plugin's saved OAuth login: refreshed (and saved) on first use after it has expired, one use at a time. Shared
/// source, linked into the plugins that sign in with OAuth.
/// </summary>
/// <param name="load">The saved login; null when signed out.</param>
/// <param name="save">Saves the login (null: signed out).</param>
/// <param name="refresh">Gets a new login with the refresh token.</param>
/// <param name="service">The service's name, for the log.</param>
internal sealed class OAuthLogin<TToken>(Func<TToken?> load, Action<TToken?> save, Func<string, Task<TToken>> refresh, ILogger logger, string service)
	where TToken : class, IOAuthToken, new()
{
	private readonly SemaphoreSlim _lock = new(1, 1);

	public async Task SetAsync(TToken? token)
	{
		await _lock.WaitAsync();
		try
		{
			save(token);
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <summary>The current access token, refreshed first if it has expired; null if signed out.</summary>
	public async Task<string?> GetAccessTokenAsync()
	{
		await _lock.WaitAsync();
		try
		{
			if (load() is not { } auth)
			{
				return null;
			}

			if (auth.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
			{
				try
				{
					auth = await refresh(auth.RefreshToken);
					save(auth);
				}
				catch (Exception ex)
				{
					// Carry on with the old token; requests that need the login will fail and say so.
					logger.LogWarning(ex, "Couldn't refresh the {Service} login", service);
				}
			}

			return auth.AccessToken;
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <summary>Posts <paramref name="body"/> (a grant) to the token endpoint and reads the login it returns.</summary>
	/// <param name="lifetime">How long the login lasts when the response doesn't say.</param>
	public static async Task<TToken> RequestAsync(IFlurlRequest endpoint, object body, TimeSpan lifetime, string service)
	{
		var response = await endpoint.PostUrlEncodedAsync(body).ReceiveJson<JsonObject>();
		return new TToken
		{
			AccessToken = (string?)response["access_token"] ?? throw new InvalidOperationException($"{service} returned no token"),
			RefreshToken = (string?)response["refresh_token"] ?? "",
			ExpiresAt = DateTimeOffset.UtcNow.AddSeconds((double?)response["expires_in"] ?? lifetime.TotalSeconds),
		};
	}
}

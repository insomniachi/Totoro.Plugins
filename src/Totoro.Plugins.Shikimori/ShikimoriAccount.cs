using System.Web;
using Flurl;
using Microsoft.Extensions.Logging;
using Totoro.Core.Authentication;
using Totoro.Core.Modules;

namespace Totoro.Plugins.Shikimori;

/// <summary>Signs in to Shikimori with its OAuth code flow.</summary>
internal sealed class ShikimoriAccount(
	ShikimoriSession session,
	IWebAuthenticator authenticator,
	ILogger<ShikimoriAccount> logger) : IAccount
{
	public Guid Id => ShikimoriModule.Id;

	public string Name => "Shikimori";

	public bool IsSignedIn => session.IsSignedIn;

	public async Task<AccountUser?> GetUserAsync()
	{
		if (!IsSignedIn)
		{
			return null;
		}

		try
		{
			return await session.GetUserAsync() is { } user ? new AccountUser(user.Nickname, user.AvatarUrl) : null;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Couldn't fetch the Shikimori user");
			return null;
		}
	}

	public async Task<bool> SignInAsync()
	{
		if (!ShikimoriSession.IsRegistered)
		{
			logger.LogWarning("Can't sign in to Shikimori: this build has no Shikimori application (client id and secret)");
			return false;
		}

		var start = new Url(ShikimoriSession.Site).AppendPathSegments("oauth", "authorize")
												  .SetQueryParam("client_id", ShikimoriSession.ClientId)
												  .SetQueryParam("redirect_uri", ShikimoriSession.Callback.ToString())
												  .SetQueryParam("response_type", "code")
												  .SetQueryParam("scope", "user_rates")
												  .ToUri();
		if (await authenticator.AuthenticateAsync(start, ShikimoriSession.Callback) is not { } result)
		{
			return false;
		}

		var code = HttpUtility.ParseQueryString(result.Query)["code"];
		if (string.IsNullOrEmpty(code))
		{
			// E.g. the user declined.
			return false;
		}

		try
		{
			await session.SignInAsync(code);
			return true;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Couldn't finish signing in to Shikimori");
			return false;
		}
	}

	public void SignOut()
	{
		session.SignOut();
	}
}

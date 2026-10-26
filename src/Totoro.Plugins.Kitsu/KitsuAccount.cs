using Microsoft.Extensions.Logging;
using Totoro.Core.Dialogs;
using Totoro.Core.Modules;
using Totoro.Core.Settings;

namespace Totoro.Plugins.Kitsu;

/// <summary>
/// Signs in to Kitsu with the user's email and password, asked for in a dialog: Kitsu's API has no sign-in page to send
/// them to. Only the token it returns is kept, never the password.
/// </summary>
internal sealed class KitsuAccount(KitsuSession session, IDialogService dialogs, ILogger<KitsuAccount> logger) : IAccount
{
	public Guid Id => KitsuModule.Id;

	public string Name => "Kitsu";

	public bool IsSignedIn => session.IsSignedIn;

	public async Task<AccountUser?> GetUserAsync()
	{
		if (!IsSignedIn)
		{
			return null;
		}

		try
		{
			var attributes = (await session.GetUserAsync())?["attributes"];
			return attributes is null ? null : new AccountUser((string?)attributes["name"] ?? "", (string?)attributes["avatar"]?["large"]);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Couldn't fetch the Kitsu user");
			return null;
		}
	}

	public async Task<bool> SignInAsync()
	{
		DataContainerProperty[] form =
		[
			// Icons by Totoro.UI.Icons' names; a Totoro without them shows none.
			new() { Name = "username", DisplayName = "Email", IconKey = "Person", Value = "" },
			new() { Name = "password", DisplayName = "Password", EditorType = DataContainerEditors.Password, IconKey = "Password", Value = "" },
		];
		if (!await dialogs.EditAsync("Sign in to Kitsu", form))
		{
			return false;
		}

		var username = form[0].Value as string;
		var password = form[1].Value as string;
		if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
		{
			return false;
		}

		try
		{
			await session.SignInAsync(username.Trim(), password);
			return true;
		}
		catch (Exception ex)
		{
			// E.g. a wrong password.
			logger.LogWarning(ex, "Couldn't sign in to Kitsu");
			return false;
		}
	}

	public void SignOut()
	{
		session.SignOut();
	}
}

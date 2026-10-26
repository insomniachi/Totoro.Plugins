using System.ComponentModel;
using System.Runtime.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Anime;
using Totoro.Core.Settings;

namespace Totoro.Plugins.Shikimori;

public class ShikimoriModule : IModule<Settings>
{
	public static Guid Id { get; } = new("4833500d-bf61-4600-9f2d-0890c63c38c4");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "Shikimori",
		Description = "Shikimori is an anime database and list tracker. It numbers its anime with MyAnimeList's ids, so what's saved about an anime carries over between them.",
		DataFolder = "Shikimori",
		Image = "icon.png"
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddSingleton<ShikimoriSession>();
		services.AddKeyedTransient<IAnimeTracker, ShikimoriTracker>(Id);
		services.AddKeyedTransient<IAnimeMetadataProvider, ShikimoriMetadataProvider>(Id);
		services.AddTransient<IAccount, ShikimoriAccount>();
	}
}

public sealed class Settings : DataContainerConfig
{
	/// <summary>The user's login; refreshed by <see cref="ShikimoriSession"/> when it expires.</summary>
	[IgnoreDataMember]
	public ShikimoriToken? Auth { get; set; }

	[DisplayName("Include NSFW")]
	public bool IncludeNsfw { get; set; }
}

public sealed class ShikimoriToken
{
	public string AccessToken { get; set; } = "";
	public string RefreshToken { get; set; } = "";
	public DateTimeOffset ExpiresAt { get; set; }
}

using System.ComponentModel;
using System.Runtime.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Anime;
using Totoro.Core.Settings;

namespace Totoro.Plugins.Kitsu;

public class KitsuModule : IModule<Settings>
{
	public static Guid Id { get; } = new("d8ae6ad9-3e24-40fe-b79a-8c5d4212dc6e");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "Kitsu",
		Description = "Kitsu is an anime database and list tracker.",
		DataFolder = "Kitsu",
		Image = "icon.png"
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddSingleton<KitsuSession>();
		services.AddKeyedTransient<IAnimeTracker, KitsuTracker>(Id);
		services.AddKeyedTransient<IAnimeMetadataProvider, KitsuMetadataProvider>(Id);
		services.AddTransient<IAccount, KitsuAccount>();
	}
}

public sealed class Settings : DataContainerConfig
{
	/// <summary>The user's login; refreshed by <see cref="KitsuSession"/> when it expires.</summary>
	[IgnoreDataMember]
	public KitsuToken? Auth { get; set; }

	[DisplayName("Include NSFW")]
	public bool IncludeNsfw { get; set; }
}

public sealed class KitsuToken
{
	public string AccessToken { get; set; } = "";
	public string RefreshToken { get; set; } = "";
	public DateTimeOffset ExpiresAt { get; set; }
}

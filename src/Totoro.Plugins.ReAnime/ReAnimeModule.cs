using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.ReAnime;

public class ReAnimeModule : IModule<Settings>
{
	public static Guid Id { get; } = new("1d572b0b-9185-4679-86d7-5e6dfa1bcdcd");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "Re:ANIME",
		Description = "Re:ANIME streams anime, subtitled and dubbed, as the original release files (with their own audio and subtitle tracks) from FlixCloud.",
		EntryPoint = "Totoro.Plugins.ReAnime.dll"
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddKeyedTransient<IAnimeProvider, ReAnimeProvider>(Id);
		services.AddHttpClient("ReAnime", client =>
		{
			client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
			client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
		});
	}
}

public sealed class Settings : DataContainerConfig
{
	[DisplayName("Site")]
	[Description("Re:ANIME's address (reanime.to, reanime.cz or reanime.wtf). Change it if the site has moved.")]
	public string Site { get; set; } = "https://reanime.to";

	[DisplayName("Audio")]
	[Description("Played first when the episode has separate files for it. Most files have both: pick the track in the player.")]
	public AudioType Audio { get; set; } = AudioType.Sub;
}

public enum AudioType
{
	[Description("Sub (Japanese, with subtitles)")] Sub,
	[Description("Dub (English dubbed)")] Dub,
}

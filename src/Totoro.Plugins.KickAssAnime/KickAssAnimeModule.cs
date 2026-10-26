using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.KickAssAnime;

public class KickAssAnimeModule : IModule<Settings>
{
	public static Guid Id { get; } = new("6da3925f-e703-4f69-9999-c17d6fc7942f");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "KickAssAnime",
		Description = "KickAssAnime streams anime, subtitled and dubbed, with subtitles in many languages.",
		DataFolder = "KickAssAnime",
		Image = "icon.png"
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddKeyedTransient<IAnimeProvider, KickAssAnimeProvider>(Id);
		services.AddHttpClient("KickAssAnime", client =>
		{
			client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
			client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
		});
	}
}

public sealed class Settings : DataContainerConfig
{
	[DisplayName("Site")]
	[Description("KickAssAnime's address. Change it if the site has moved (its other addresses only redirect to it, and can't search).")]
	public string Site { get; set; } = "https://kaa.lt";

	[DisplayName("Audio")]
	[Description("The episodes listed and played first. A show without the dub has the original.")]
	public AudioType Audio { get; set; } = AudioType.Sub;
}

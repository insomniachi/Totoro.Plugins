using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.AniPM;

public class AniPMModule : IModule<Settings>
{
	public static Guid Id { get; } = new("d3180556-f773-4265-ac9d-b3f3be98ce6a");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "AniPM",
		Description = "AniPM streams anime, subtitled, hard-subbed and dubbed, with intro and outro times.",
		DataFolder = "AniPM",
		Image = "icon.png"
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddKeyedTransient<IAnimeProvider, AniPMProvider>(Id);
		services.AddHttpClient("AniPM", client =>
		{
			// Cloudflare turns away the API, and the video, without a browser's User-Agent.
			client.DefaultRequestHeaders.UserAgent.ParseAdd(AniPMProvider.UserAgent);
			client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
		});
	}
}

public sealed class Settings : DataContainerConfig
{
	[DisplayName("Site")]
	[Description("AniPM's address. Change it if the site has moved.")]
	public string Site { get; set; } = "https://ani.pm";

	[DisplayName("Audio")]
	[Description("The audio played first: Sub tries soft subtitles, then burned-in ones. A show without the dub has the original.")]
	public AudioType Audio { get; set; } = AudioType.Sub;
}

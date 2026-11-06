using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.Gotaku;

public class GotakuModule : IModule<Settings>
{
	public static Guid Id { get; } = new("45389725-d0a0-4426-87b0-255e86901fc8");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "Gotaku",
		Description = "Gotaku streams anime, soft-subbed, hard-subbed and dubbed, with intro and outro times.",
		DataFolder = "Gotaku",
		Image = "icon.png"
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddKeyedTransient<IAnimeProvider, GotakuProvider>(Id);
		services.AddHttpClient("Gotaku", client =>
		{
			client.DefaultRequestHeaders.UserAgent.ParseAdd(GotakuProvider.UserAgent);
			client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
		});
	}
}

public sealed class Settings : DataContainerConfig
{
	[DisplayName("Site")]
	[Description("Gotaku's address. Change it if the site has moved.")]
	public string Site { get; set; } = "https://gotaku.to";

	[DisplayName("Audio")]
	[Description("The audio played first: Sub tries soft subtitles, then burned-in ones. A show without the dub has the original.")]
	public AudioType Audio { get; set; } = AudioType.Sub;
}

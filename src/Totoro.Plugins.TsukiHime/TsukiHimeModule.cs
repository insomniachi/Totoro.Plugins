using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.TsukiHime;

public class TsukiHimeModule : IModule<Settings>
{
	public static Guid Id { get; } = new("e3b5d0a4-6f21-4c8e-9a7d-2b14c9f06e58");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "TsukiHime",
		Description = "TsukiHime indexes anime torrents from Nyaa, NekoBT and TokyoTosho, matched to shows and episodes.",
		EntryPoint = "Totoro.Plugins.TsukiHime.dll",
		Image = "icon.png"
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddKeyedTransient<IAnimeProvider, TsukiHimeProvider>(Id);
	}
}

public sealed class Settings : DataContainerConfig
{
	[DisplayName("Preferred quality")]
	[Description("Used when the episode has it; otherwise the best there is.")]
	public VideoQuality Quality { get; set; } = VideoQuality.FullHd;

	[DisplayName("Audio")]
	[Description("Preferred audio type: subbed or dubbed.")]
	public AudioType Audio { get; set; } = AudioType.Sub;
}

public enum VideoQuality
{
	[Description("1080p")] FullHd,
	[Description("720p")] Hd,
	[Description("480p")] Sd,
}

public enum AudioType
{
	[Description("Sub (original audio, with subtitles)")] Sub,
	[Description("Dub (English dubbed)")] Dub,
}

internal static class VideoQualityExtensions
{
	public static int ToResolution(this VideoQuality quality) => quality switch
	{
		VideoQuality.Hd => 720,
		VideoQuality.Sd => 480,
		_ => 1080,
	};
}

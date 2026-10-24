using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.TorBox;

public class TorBoxModule : IModule<Settings>
{
	public static Guid Id { get; } = new("33c86adc-7dc0-4fa4-a2bf-993acc66700b");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "TorBox",
		Description = "Streams torrents through TorBox, a debrid service: it downloads them on its servers and the player streams its copy.",
		DataFolder = "TorBox",
		Image = "icon.png"
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddKeyedTransient<ITorrentStreamer, TorBoxStreamer>(Id);
	}
}

public sealed class Settings : DataContainerConfig
{
	[DisplayName("API key")]
	[Description("From torbox.app/settings")]
	[Editor(DataContainerEditors.Password, "")]
	public string ApiKey { get; set; } = "";

	[DisplayName("Keep torrents in TorBox")]
	[Description("So they play at once next time. Otherwise they're removed when you stop watching; torrents you added yourself never are.")]
	public bool KeepTorrents { get; set; }

	[DisplayName("Only play what TorBox has cached")]
	[Description("They start at once, and don't count toward TorBox's limit of 60 new torrents an hour.")]
	public bool OnlyCached { get; set; }
}

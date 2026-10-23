using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.RealDebrid;

public class RealDebridModule : IModule<Settings>
{
	public static Guid Id { get; } = new("8fa7a097-dba5-4177-8f06-de7caa2ac580");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "Real-Debrid",
		Description = "Streams torrents through Real-Debrid, a debrid service: it downloads them on its servers and the player streams its copy.",
		EntryPoint = "Totoro.Plugins.RealDebrid.dll",
		Image = "icon.png"
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddKeyedTransient<ITorrentStreamer, RealDebridStreamer>(Id);
	}
}

public sealed class Settings : DataContainerConfig
{
	[DisplayName("API token")]
	[Description("From real-debrid.com/apitoken")]
	[Editor(DataContainerEditors.Password, "")]
	public string ApiKey { get; set; } = "";

	[DisplayName("Keep torrents in Real-Debrid")]
	[Description("So they play at once next time. Otherwise they're removed when you stop watching.")]
	public bool KeepTorrents { get; set; }
}

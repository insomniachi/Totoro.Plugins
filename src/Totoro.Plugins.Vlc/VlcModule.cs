using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Media;
using Totoro.UI.Player;

namespace Totoro.Plugins.Vlc;

public class VlcModule : IModule<Settings>
{
	// The one it had built into the app, so a choice of it and its settings carry over.
	public static Guid Id { get; } = new("5e0a7c1f-3b9d-4e62-8a4f-2d7c9b1e6f30");

	public Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "VLC",
		Description = "Plays videos in Totoro's own window, with libvlc (VLC's engine): skipping the opening and the next episode are offered over the video.",
		DataFolder = "Vlc",
		Image = "icon.png"
	};

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		// One player app-wide: it owns the window, so a second Play replaces what's playing.
		services.AddSingleton<VlcMediaPlayer>();
		services.AddKeyedSingleton<IMediaPlayer>(Id, (sp, _) => sp.GetRequiredService<VlcMediaPlayer>());
	}
}

public sealed class Settings : PlayerSettings;

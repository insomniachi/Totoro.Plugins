using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Manga;
using Totoro.Core.Settings;

namespace Totoro.Plugins.MangaFire;

public class MangaFireModule : IModule<Settings>
{
	public static Guid Id { get; } = new("f122281d-eadd-43ff-8bf6-96f7c904515e");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "MangaFire",
		Description = "Read manga from MangaFire, a large library with official and fan translations.",
		DataFolder = "MangaFire",
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddKeyedTransient<IMangaProvider, MangaFireProvider>(Id);
	}
}

// No settings yet; the module's settings type is required.
public sealed class Settings : DataContainerConfig;

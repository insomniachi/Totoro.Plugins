using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Manga;
using Totoro.Core.Settings;

namespace Totoro.Plugins.WeebCentral;

public class WeebCentralModule : IModule<Settings>
{
	public static Guid Id { get; } = new("5f2ae848-fafe-41bb-a851-9e22ecfb1e21");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "Weeb Central",
		Description = "Read manga from Weeb Central, a large English library that keeps up with new chapters.",
		DataFolder = "WeebCentral",
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddKeyedTransient<IMangaProvider, WeebCentralProvider>(Id);
	}
}

// No settings yet; the module's settings type is required.
public sealed class Settings : DataContainerConfig;

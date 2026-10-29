using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Manga;
using Totoro.Core.Settings;

namespace Totoro.Plugins.MangaDex;

public class MangaDexModule : IModule<Settings>
{
	public static Guid Id { get; } = new("0f7d9328-566f-4206-9a3e-3c2a94030f7c");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "MangaDex",
		Description = "Read manga from MangaDex, scanlations in many languages through its official API. Licensed series often have no chapters there.",
		DataFolder = "MangaDex",
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddKeyedTransient<IMangaProvider, MangaDexProvider>(Id);
	}
}

public sealed class Settings : DataContainerConfig
{
	[DisplayName("Language")]
	[Description("The language of the chapters to read, as a code: en, es-la, pt-br, fr...")]
	public string Language { get; set; } = "en";

	[DisplayName("Data saver")]
	[Description("Smaller, compressed page images.")]
	public bool DataSaver { get; set; }
}

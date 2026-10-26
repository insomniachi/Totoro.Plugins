using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Totoro.Core.Extensions;
using Totoro.Core.Modules;
using Totoro.Core.Modules.Streaming;
using Totoro.Core.Settings;

namespace Totoro.Plugins.ReAnime;

public class ReAnimeModule : IModule<Settings>
{
	public static Guid Id { get; } = new("1d572b0b-9185-4679-86d7-5e6dfa1bcdcd");

	public static Descriptor Descriptor { get; } = new()
	{
		Id = Id,
		Name = "Re:ANIME",
		Description = "Re:ANIME streams anime, subtitled and dubbed, as the original release files (with their own audio and subtitle tracks) from FlixCloud.",
		DataFolder = "ReAnime",
		Image = "icon.png"
	};

	Descriptor IModule.Descriptor => Descriptor;

	public void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient(_ => Descriptor);
		services.AddModuleSettings(this);
		services.AddKeyedTransient<IAnimeProvider, ReAnimeProvider>(Id);
		services.AddHttpClient("ReAnime", client =>
		{
			client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
			client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
		})
		// FlixCloud's file links only work from the IP address that asked for them, so connect the way the player will
		// (IPv6 when it works), not over IPv4 as the app's clients do.
		.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectCallback = PreferIPv6Async });
	}

	// shortcut: a copy of Totoro.Core's HttpConnect.PreferIPv6Async, which the Totoro.Core package doesn't have yet; use that once it does.
	/// <summary>Over IPv6 when it works, falling back to IPv4 after a few seconds rather than the OS timeout (~21 s an address).</summary>
	private static async ValueTask<Stream> PreferIPv6Async(SocketsHttpConnectionContext context, CancellationToken ct)
	{
		var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
		var ipv4 = addresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork).ToArray();
		if (ipv4.Length > 0 && addresses.Any(x => x.AddressFamily == AddressFamily.InterNetworkV6) && Socket.OSSupportsIPv6)
		{
			using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
			attempt.CancelAfter(TimeSpan.FromSeconds(3));
			try
			{
				return await ConnectAsync(addresses.Where(x => x.AddressFamily == AddressFamily.InterNetworkV6), context.DnsEndPoint.Port, attempt.Token);
			}
			catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
			{
				addresses = ipv4;
			}
		}

		return await ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
	}

	private static async Task<Stream> ConnectAsync(IEnumerable<IPAddress> addresses, int port, CancellationToken ct)
	{
		var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
		try
		{
			await socket.ConnectAsync([.. addresses], port, ct);
			return new NetworkStream(socket, ownsSocket: true);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}
}

public sealed class Settings : DataContainerConfig
{
	[DisplayName("Site")]
	[Description("Re:ANIME's address (reanime.to, reanime.cz or reanime.wtf). Change it if the site has moved.")]
	public string Site { get; set; } = "https://reanime.to";

	[DisplayName("Audio")]
	[Description("Played first when the episode has separate files for it. Most files have both: pick the track in the player.")]
	public AudioType Audio { get; set; } = AudioType.Sub;
}

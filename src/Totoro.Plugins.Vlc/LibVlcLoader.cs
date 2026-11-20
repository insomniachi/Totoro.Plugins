using LibVLCSharp.Shared;

namespace Totoro.Plugins.Vlc;

/// <summary>
/// Loads libvlc (from libvlc\win-x64 in the plugin's folder) ahead of the player window, so the window opens straight
/// away. The first load after an install or update can take many seconds, mostly Windows reading its ~400 plugins; later
/// ones are quick, quicker still with the plugin cache (plugins.dat) written by the first.
/// </summary>
internal static class LibVlcLoader
{
	private static readonly Lock s_lock = new();
	private const long RetryAfterMs = 60_000;
	private static Task? s_loading;
	private static long s_startedAt;

	/// <summary>libvlc's folder, in the plugin's.</summary>
	public static string Directory { get; } = Path.Combine(Path.GetDirectoryName(typeof(LibVlcLoader).Assembly.Location)!, "libvlc", "win-x64");

	public static bool IsLoaded => s_loading is { IsCompletedSuccessfully: true };

	/// <summary>Starts loading libvlc in the background, if it isn't already.</summary>
	public static void Preload() => _ = Loading();

	/// <summary>Loads libvlc, waiting for it; on a background thread.</summary>
	public static void Load() => Loading().GetAwaiter().GetResult();

	// A load that failed is tried again (the failure is logged by whoever waited for it), not kept for the session; but
	// not until a while after the last try, so a lasting failure (a broken install) isn't a seconds-long scan every play.
	private static Task Loading()
	{
		lock (s_lock)
		{
			if (s_loading is null || (s_loading.IsFaulted && Environment.TickCount64 - s_startedAt > RetryAfterMs))
			{
				s_startedAt = Environment.TickCount64;
				s_loading = Task.Run(LoadPlugins);
			}

			return s_loading;
		}
	}

	private static void LoadPlugins()
	{
		// Not beside the app, where LibVLCSharp looks by default.
		LibVLCSharp.Shared.Core.Initialize(Directory);
		var cache = Path.Combine(Directory, "plugins", "plugins.dat");
		// Scans the plugins once; with no cache yet, saves one (the plugin's folder can be written to).
		using var libVlc = File.Exists(cache) ? new LibVLC(enableDebugLogs: false) : new LibVLC(enableDebugLogs: false, "--reset-plugins-cache");
	}
}

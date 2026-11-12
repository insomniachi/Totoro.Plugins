using System.Diagnostics;
using System.Text;

namespace Totoro.Plugins.AniPM;

/// <summary>
/// GETs through Windows' own curl.exe. Settlar's Cloudflare turns away .NET's TLS handshake (whatever the headers or HTTP
/// version) and any request whose Host header isn't first (as ffmpeg, so mpv, sends it); curl.exe gets through.
/// </summary>
internal static class Curl
{
	// In System32 since Windows 10 1803.
	private static string Exe => Path.Combine(Environment.SystemDirectory, "curl.exe");

	/// <summary>The answer after redirects; curl failing to connect at all throws.</summary>
	public static async Task<(int Status, string? ContentType, byte[] Body)> GetAsync(Uri url, string? referer, CancellationToken ct)
	{
		var start = new ProcessStartInfo(Exe)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		// -D - puts each response's head (one per redirect) before the body on stdout.
		foreach (var argument in (string[])["-sS", "-L", "--max-time", "60", "-D", "-", "-A", AniPMProvider.UserAgent])
		{
			start.ArgumentList.Add(argument);
		}

		if (referer is not null)
		{
			start.ArgumentList.Add("-e");
			start.ArgumentList.Add(referer);
		}

		start.ArgumentList.Add(url.AbsoluteUri);

		using var process = Process.Start(start) ?? throw new HttpRequestException("curl.exe didn't start");
		await using var kill = ct.Register(() =>
		{
			try
			{
				process.Kill();
			}
			catch (InvalidOperationException)
			{
				// It had exited.
			}
		});

		var error = process.StandardError.ReadToEndAsync(ct);
		using var output = new MemoryStream();
		await process.StandardOutput.BaseStream.CopyToAsync(output, ct);
		await process.WaitForExitAsync(ct);
		if (process.ExitCode != 0)
		{
			throw new HttpRequestException($"curl.exe failed ({process.ExitCode}): {(await error).Trim()}");
		}

		return Parse(output.ToArray());
	}

	/// <summary>The last response head's status and type, and the body after it.</summary>
	internal static (int Status, string? ContentType, byte[] Body) Parse(byte[] output)
	{
		var (status, type, offset) = (0, (string?)null, 0);
		while (output.AsSpan(offset).StartsWith("HTTP/"u8) && output.AsSpan(offset).IndexOf("\r\n\r\n"u8) is >= 0 and var end)
		{
			var lines = Encoding.ASCII.GetString(output, offset, end).Split("\r\n");
			status = int.TryParse(lines[0].Split(' ').ElementAtOrDefault(1), out var code) ? code : 0;
			type = lines.Skip(1).FirstOrDefault(x => x.StartsWith("content-type:", StringComparison.OrdinalIgnoreCase))?[13..].Trim();
			offset += end + 4;
		}

		return (status, type, output[offset..]);
	}
}

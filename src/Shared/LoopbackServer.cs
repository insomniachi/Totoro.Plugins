using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Totoro.Plugins.Shared;

/// <summary>
/// A minimal HTTP server on localhost, for plugins that serve players streams they can't open themselves: started on
/// first use, on a free port; each GET or HEAD request is passed to the handler, which answers it whole, and the
/// connection is closed. Shared source, linked into the plugins that proxy streams.
/// </summary>
/// <param name="handle">Answers a request (with <see cref="WriteAsync"/>), given its path and query; null if it isn't a GET or HEAD.</param>
internal sealed class LoopbackServer(Func<NetworkStream, Uri?, CancellationToken, Task> handle)
{
	private readonly Lock _lock = new();
	private TcpListener? _listener;

	/// <summary>The port it listens on; starts it if it isn't yet.</summary>
	public int Port
	{
		get
		{
			lock (_lock)
			{
				if (_listener is null)
				{
					_listener = new TcpListener(IPAddress.Loopback, 0);
					_listener.Start();
					_ = AcceptAsync(_listener);
				}

				return ((IPEndPoint)_listener.LocalEndpoint).Port;
			}
		}
	}

	public static async Task WriteAsync(NetworkStream stream, HttpStatusCode status, string type, byte[] body, CancellationToken ct)
	{
		var head = $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
		await stream.WriteAsync(body, ct);
	}

	private async Task AcceptAsync(TcpListener listener)
	{
		while (true)
		{
			try
			{
				_ = HandleAsync(await listener.AcceptTcpClientAsync());
			}
			catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
			{
				return;
			}
		}
	}

	private async Task HandleAsync(TcpClient client)
	{
		using var connection = client;
		var stream = client.GetStream();
		try
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
			await handle(stream, await ReadTargetAsync(stream, timeout.Token), timeout.Token);
		}
		catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or OperationCanceledException)
		{
			// The player went away or the upstream server failed; it asks again if it still wants it.
		}
		catch (Exception)
		{
			// What it fetched didn't parse (or decrypt): answered, not dropped.
			try
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
				await WriteAsync(stream, HttpStatusCode.BadGateway, "text/plain", [], timeout.Token);
			}
			catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
			{
			}
		}
	}

	/// <summary>The request's path and query; null if it isn't a GET or HEAD.</summary>
	private static async Task<Uri?> ReadTargetAsync(NetworkStream stream, CancellationToken ct)
	{
		var buffer = new byte[16 * 1024];
		var length = 0;
		while (length < buffer.Length)
		{
			var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
			if (read == 0)
			{
				break;
			}

			length += read;
			if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
			{
				break;
			}
		}

		var requestLine = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
		return requestLine is ["GET" or "HEAD", var path, ..] && Uri.TryCreate($"http://127.0.0.1{path}", UriKind.Absolute, out var target) ? target : null;
	}
}

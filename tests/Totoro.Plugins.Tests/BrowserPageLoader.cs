using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Totoro.Core.Web;

namespace Totoro.Plugins.Tests;

/// <summary>
/// Totoro's page loader for tests: a hidden WebView2 (Edge's engine), as the app uses, on its own UI thread, so live tests
/// of plugins that load pages in a browser reach the sites as the app does. Unlike the app, there's no one to do a
/// site's bot check by hand: one that doesn't pass by itself throws a <see cref="WebPageException"/> naming it a bot check.
/// </summary>
internal sealed class BrowserPageLoader : IWebPageLoader
{
	public static BrowserPageLoader Shared { get; } = new();

	/// <summary>In the message a load fails with when a site's bot check doesn't pass by itself.</summary>
	public const string BotCheck = "bot check didn't pass";

	private static readonly TimeSpan s_automaticCheck = TimeSpan.FromSeconds(15);
	private static readonly TimeSpan s_loadTimeout = TimeSpan.FromSeconds(30);

	// As the app's WebPageLoader reads a page.
	private const string ReadScript = """
		JSON.stringify({
			url: location.href,
			type: document.contentType,
			check: document.title === 'Just a moment...' || !!document.getElementById('challenge-form'),
			blocked: document.title.startsWith('Attention Required!'),
			content: document.contentType === 'text/html' ? document.documentElement.outerHTML : (document.body ? document.body.innerText : '')
		})
		""";

	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly Task<(Form Ui, CoreWebView2 Browser)> _started;

	// The page being opened and as from where: its request gets that Referer (see the WebResourceRequested handler).
	private (Uri Url, Uri? Referrer)? _opening;

	private BrowserPageLoader()
	{
		var started = new TaskCompletionSource<(Form, CoreWebView2)>(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			// Never shown: it's only the window WebView2 needs, and its thread's message loop.
			var ui = new Form { ShowInTaskbar = false };
			_ = ui.Handle;
			ui.BeginInvoke(async () =>
			{
				try
				{
					var folder = Path.Combine(Path.GetTempPath(), "Totoro.Plugins.Tests", "WebView2");
					var environment = await CoreWebView2Environment.CreateAsync(null, folder);
					var controller = await environment.CreateCoreWebView2ControllerAsync(ui.Handle);
					controller.IsVisible = false;
					var browser = controller.CoreWebView2;
					browser.IsMuted = true;
					browser.WebResourceRequested += (_, args) =>
					{
						if (_opening is { Referrer: { } referrer } page && args.Request.Method == "GET" &&
							Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var requested) && requested == page.Url)
						{
							args.Request.Headers.SetHeader("Referer", referrer.AbsoluteUri);
						}
					};
					started.SetResult((ui, browser));
				}
				catch (Exception ex)
				{
					started.SetException(ex);
				}
			});
			Application.Run();
		})
		{ IsBackground = true };
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		_started = started.Task;
	}

	public Task<WebPage> LoadAsync(Uri url, Uri? referrer, CancellationToken ct) => OnUiAsync(async browser =>
	{
		var page = await NavigateAsync(browser, url, referrer, ct);
		if (page.Check)
		{
			// Cloudflare's automatic check reloads the page when it passes (by posting to it), so it's opened again then.
			for (var deadline = DateTime.UtcNow + s_automaticCheck; page.Check && DateTime.UtcNow < deadline;)
			{
				await Task.Delay(500, ct);
				page = await ReadAsync(browser);
			}

			if (page.Check)
			{
				throw new WebPageException($"{url.Host}'s {BotCheck} by itself.");
			}

			page = await NavigateAsync(browser, url, referrer, ct);
		}

		return page.Blocked
			? throw new WebPageException($"{url.Host} refused the request (Cloudflare blocked it).")
			: new WebPage(new Uri(page.Url), page.Type, page.Content);
	}, ct);

	public Task<string?> RunAsync(Uri url, string script, TimeSpan timeout, CancellationToken ct) => OnUiAsync(async browser =>
	{
		var message = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
		{
			if (Uri.TryCreate(args.Source, UriKind.Absolute, out var source) && source.Host == url.Host)
			{
				try
				{
					message.TrySetResult(args.TryGetWebMessageAsString());
				}
				catch (ArgumentException)
				{
					// Sent as JSON, not a string: not the script's.
				}
			}
		}

		var scriptId = await browser.AddScriptToExecuteOnDocumentCreatedAsync(script);
		browser.WebMessageReceived += OnMessage;
		try
		{
			await NavigateAsync(browser, url, null, ct);
			return await message.Task.WaitAsync(timeout, ct);
		}
		catch (TimeoutException)
		{
			return null;
		}
		finally
		{
			browser.WebMessageReceived -= OnMessage;
			browser.RemoveScriptToExecuteOnDocumentCreated(scriptId);
		}
	}, ct);

	/// <summary>Runs <paramref name="action"/> on the browser's thread (WebView2 only runs there), one at a time.</summary>
	private async Task<T> OnUiAsync<T>(Func<CoreWebView2, Task<T>> action, CancellationToken ct)
	{
		var (ui, browser) = await _started;
		await _gate.WaitAsync(ct);
		try
		{
			var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			ui.BeginInvoke(async () =>
			{
				try
				{
					result.SetResult(await action(browser));
				}
				catch (Exception ex)
				{
					result.SetException(ex);
				}
				finally
				{
					// Stops the page running in the background, e.g. a video player loading the video.
					browser.Navigate("about:blank");
				}
			});
			return await result.Task;
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>Opens the page, as from a link on <paramref name="referrer"/>, once its HTML has loaded.</summary>
	private async Task<PageState> NavigateAsync(CoreWebView2 browser, Uri url, Uri? referrer, CancellationToken ct)
	{
		var navigations = new HashSet<ulong>();
		var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
		{
			if (!args.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
			{
				navigations.Add(args.NavigationId);
			}
		}
		void OnLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs args)
		{
			if (navigations.Contains(args.NavigationId))
			{
				loaded.TrySetResult();
			}
		}
		void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
		{
			// An HTTP error still loads a page (Cloudflare's check is a 403); only failing to connect ends here.
			if (navigations.Contains(args.NavigationId) && !args.IsSuccess && args.HttpStatusCode == 0 &&
				args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
			{
				loaded.TrySetException(new WebPageException($"Couldn't load {url} ({args.WebErrorStatus})."));
			}
		}

		browser.NavigationStarting += OnStarting;
		browser.DOMContentLoaded += OnLoaded;
		browser.NavigationCompleted += OnCompleted;
		_opening = (url, referrer);
		if (referrer is not null)
		{
			browser.AddWebResourceRequestedFilter(url.AbsoluteUri, CoreWebView2WebResourceContext.Document);
		}

		try
		{
			browser.Navigate(url.AbsoluteUri);
			await loaded.Task.WaitAsync(s_loadTimeout, ct);
		}
		finally
		{
			browser.NavigationStarting -= OnStarting;
			browser.DOMContentLoaded -= OnLoaded;
			browser.NavigationCompleted -= OnCompleted;
			if (referrer is not null)
			{
				browser.RemoveWebResourceRequestedFilter(url.AbsoluteUri, CoreWebView2WebResourceContext.Document);
			}
		}

		return await ReadAsync(browser);
	}

	private static async Task<PageState> ReadAsync(CoreWebView2 browser)
	{
		// The script returns a JSON string, which ExecuteScriptAsync returns as JSON again; null while a page is loading.
		var json = JsonSerializer.Deserialize<string?>(await browser.ExecuteScriptAsync(ReadScript));
		if (json is null)
		{
			return new PageState("", "", Check: true, Blocked: false, "");
		}

		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		return new PageState(
			root.GetProperty("url").GetString() ?? "",
			root.GetProperty("type").GetString() ?? "",
			root.GetProperty("check").GetBoolean(),
			root.GetProperty("blocked").GetBoolean(),
			root.GetProperty("content").GetString() ?? "");
	}

	private sealed record PageState(string Url, string Type, bool Check, bool Blocked, string Content);
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Readdit;

/// <summary>
/// Talks to Reddit through an embedded WebView2 so requests carry the user's own logged-in
/// session. Reddit no longer serves anonymous API requests or hands out self-service API keys.
/// </summary>
public sealed class RedditSession(WebView2 webView, string profile)
{
    public const string Home = "https://www.reddit.com/";
    public const string LoginPage = "https://www.reddit.com/login/";

    // Tiny same-origin page to sit on while hidden: fetches stay same-origin (so cookies apply)
    // without running Reddit's full web UI in the background.
    const string ParkingPage = "https://www.reddit.com/robots.txt";

    public event EventHandler? NavigationCompleted;

    /// <summary>Shared with the detail view so both use the same browser profile.</summary>
    public CoreWebView2Environment Environment { get; private set; } = null!;

    CoreWebView2 Core => webView.CoreWebView2;

    bool OnRedditOrigin =>
        Uri.TryCreate(Core.Source, UriKind.Absolute, out var uri) && uri.Host == "www.reddit.com";

    public async Task InitializeAsync()
    {
        // Video inside a WPF-hosted WebView froze at ~1 fps. Edge plays HD video through Media
        // Foundation and presents it on a separate DirectComposition layer, which doesn't present
        // reliably when hosted in WPF, so keep video on the normal Chromium decode/composite path.
        // Occlusion tracking is off too: it can misread the hosted window as covered and throttle it.
        // (All feature names go in one --disable-features switch; a repeated switch replaces the first.)
        var args = "--disable-features=CalculateNativeWinOcclusion,MediaFoundationClearPlayback " +
                   "--disable-direct-composition-video-overlays --disable-backgrounding-occluded-windows";
        var options = new CoreWebView2EnvironmentOptions(args);
        Environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.Local, "WebView2"), options);
        await webView.EnsureCoreWebView2Async(Environment, CreateControllerOptions());

        Core.NavigationCompleted += (_, _) => NavigationCompleted?.Invoke(this, EventArgs.Empty);
        Core.NewWindowRequested += (_, e) =>
        {
            // Google/Apple sign-in runs in a popup that must stay attached to the Reddit page
            // that opened it, so let WebView2 host those. Other pop-up links go to the real browser.
            if (IsSignInPopup(e.Uri)) return;
            e.Handled = true;
            // Only web links go to the shell; never arbitrary protocol handlers.
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return;
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // No default browser registered; nothing sensible to do.
            }
        };
    }

    /// <summary>
    /// Options selecting this account's browser profile (cookies, logins). Each Readdit account
    /// is a separate WebView2 profile; "" keeps the original default profile.
    /// </summary>
    public CoreWebView2ControllerOptions CreateControllerOptions()
    {
        var options = Environment.CreateCoreWebView2ControllerOptions();
        if (profile.Length > 0) options.ProfileName = profile;
        return options;
    }

    static bool IsSignInPopup(string url)
    {
        if (string.IsNullOrEmpty(url) || url.StartsWith("about:")) return true; // popups often start blank
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        return host == "reddit.com" || host.EndsWith(".reddit.com") ||
               host == "google.com" || host.EndsWith(".google.com") ||
               host == "apple.com" || host.EndsWith(".apple.com");
    }

    /// <summary>
    /// Navigates and completes when *this* navigation finishes. Matching on the navigation id
    /// matters: an earlier navigation that this one cancels also raises NavigationCompleted.
    /// </summary>
    public Task NavigateAsync(string url)
    {
        var done = new TaskCompletionSource();
        ulong? navigationId = null;

        void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (navigationId == null && SameUrl(e.Uri, url)) navigationId = e.NavigationId;
        }
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (navigationId != e.NavigationId) return;
            Finish();
        }
        void Finish()
        {
            Core.NavigationStarting -= OnStarting;
            Core.NavigationCompleted -= OnCompleted;
            done.TrySetResult();
        }

        Core.NavigationStarting += OnStarting;
        Core.NavigationCompleted += OnCompleted;
        Core.Navigate(url);
        // Never hang forever if the navigation is swallowed.
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => webView.Dispatcher.Invoke(Finish));
        return done.Task;
    }

    static bool SameUrl(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y) &&
        Uri.Compare(x, y, UriComponents.HttpRequestUrl, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    Task? _parking;

    /// <summary>Concurrent callers share one in-flight navigation instead of cancelling each other's.</summary>
    public Task ParkAsync() => _parking is { IsCompleted: false } ? _parking : _parking = NavigateAsync(ParkingPage);

    public void ApplyTheme(string theme) => Appearance.ApplyToWebView(webView.CoreWebView2, theme);

    /// <summary>Returns the signed-in username, or null if signed out (or the page isn't on reddit.com yet).</summary>
    public async Task<string?> GetUserNameAsync()
    {
        try
        {
            if (!OnRedditOrigin) return null;
            var (status, body) = await SendAsync("GET", "https://www.reddit.com/api/me.json", null, null, default, park: false);
            if (status != 200) return null;
            using var doc = JsonDocument.Parse(body);
            var name = doc.RootElement.TryGetProperty("data", out var data) ? data.Str("name") : "";
            return name.Length > 0 ? name : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetCookieAsync(string name)
    {
        var cookies = await Core.CookieManager.GetCookiesAsync("https://www.reddit.com");
        return cookies.FirstOrDefault(c => c.Name == name)?.Value;
    }

    public Task<(int Status, string Body)> GetAsync(string url, CancellationToken ct) =>
        SendAsync("GET", url, null, null, ct);

    /// <summary>Runs fetch() inside the page so the request uses the browser's cookies.</summary>
    public async Task<(int Status, string Body)> SendAsync(
        string method, string url, IDictionary<string, string>? form, string? modhash, CancellationToken ct,
        bool park = true)
    {
        if (park && !OnRedditOrigin) await ParkAsync();

        var options = JsonSerializer.Serialize(new
        {
            method,
            form,
            headers = modhash == null ? new Dictionary<string, string>() : new() { ["X-Modhash"] = modhash },
        });
        var expression =
            "(async () => { const o = " + options + ";" +
            " const init = { method: o.method, credentials: 'include', headers: o.headers };" +
            " if (o.form) init.body = new URLSearchParams(o.form);" +
            " const r = await fetch(" + JsonSerializer.Serialize(url) + ", init);" +
            " return JSON.stringify({ status: r.status, body: await r.text() }); })()";
        var args = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true });

        // DevTools' Runtime.evaluate can await the promise, which ExecuteScriptAsync can't.
        var raw = await Core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
        ct.ThrowIfCancellationRequested();

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("exceptionDetails", out var error))
        {
            var message = error.TryGetProperty("exception", out var ex) ? ex.Str("description") : error.Str("text");
            throw new InvalidOperationException($"Request to Reddit failed: {message}");
        }

        using var result = JsonDocument.Parse(root.GetProperty("result").Str("value"));
        return ((int)result.RootElement.Long("status"), result.RootElement.Str("body"));
    }
}

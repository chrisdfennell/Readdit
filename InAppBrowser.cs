using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Readdit;

/// <summary>Opens links inside Readdit, in a WebView2 that shares the signed-in browser profile.</summary>
public sealed class InAppBrowser(WebView2 view) : ObservableObject
{
    // Pages are arbitrary sites, so the only thing they can tell us is "Esc was pressed".
    const string EscapeScript =
        "document.addEventListener('keydown', e => { if (e.key === 'Escape' && !document.fullscreenElement) " +
        "window.chrome.webview.postMessage('escape'); }, true);";

    string? _pending;
    bool _initialized;

    public event Action? EscapePressed;

    CoreWebView2? Core => view.CoreWebView2;

    string _url = "";
    public string Url { get => _url; private set => Set(ref _url, value); }

    string _title = "";
    public string Title { get => _title; private set => Set(ref _title, value); }

    bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    public bool CanGoBack => Core?.CanGoBack == true;
    public bool CanGoForward => Core?.CanGoForward == true;

    /// <param name="options">The signed-in account's profile, so reddit.com pages open logged in.</param>
    public async Task InitializeAsync(CoreWebView2Environment environment, CoreWebView2ControllerOptions options)
    {
        await view.EnsureCoreWebView2Async(environment, options);
        var core = view.CoreWebView2;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(EscapeScript);
        core.WebMessageReceived += (_, e) =>
        {
            if (e.TryGetWebMessageAsString() == "escape") EscapePressed?.Invoke();
        };
        core.SourceChanged += (_, _) => Url = core.Source;
        core.DocumentTitleChanged += (_, _) => Title = core.DocumentTitle;
        core.NavigationStarting += (_, _) => IsLoading = true;
        core.NavigationCompleted += (_, _) =>
        {
            IsLoading = false;
            Raise(nameof(CanGoBack));
            Raise(nameof(CanGoForward));
        };
        core.NewWindowRequested += (_, e) =>
        {
            // Script pop-ups (window.open() with no URL yet) and sign-in windows need a real
            // window that stays linked to this page, so let WebView2 open one.
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
                IsSignInHost(uri.Host))
                return;
            // Ordinary target=_blank links open in this pane rather than a bare window.
            e.Handled = true;
            core.Navigate(e.Uri);
        };

        _initialized = true;
        if (_pending != null) Navigate(_pending);
    }

    public void Navigate(string url)
    {
        if (!_initialized)
        {
            _pending = url;
            return;
        }
        _pending = null;
        Url = url;
        Title = "Loading…";
        Core!.IsMuted = false;
        Core.Navigate(url);
    }

    static bool IsSignInHost(string host) =>
        host is "accounts.google.com" or "appleid.apple.com" or "login.microsoftonline.com" or "login.live.com" ||
        host.EndsWith(".facebook.com") || host == "github.com";

    public void ApplyTheme(string theme) => Appearance.ApplyToWebView(Core, theme);

    public void GoBack() { if (CanGoBack) Core!.GoBack(); }
    public void GoForward() { if (CanGoForward) Core!.GoForward(); }
    public void Reload() => Core?.Reload();

    /// <summary>
    /// Silences the page when the pane closes. (Navigating to about:blank instead added a blank
    /// entry to Back history.)
    /// </summary>
    public void Unload()
    {
        if (!_initialized) return;
        Core!.IsMuted = true;
        _ = Core.ExecuteScriptAsync("document.querySelectorAll('video,audio').forEach(m => m.pause())");
    }
}

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
        // Keep target=_blank links in this pane instead of spawning bare windows.
        core.NewWindowRequested += (_, e) =>
        {
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
        Core!.Navigate(url);
    }

    public void ApplyTheme(string theme) => Appearance.ApplyToWebView(Core, theme);

    public void GoBack() { if (CanGoBack) Core!.GoBack(); }
    public void GoForward() { if (CanGoForward) Core!.GoForward(); }
    public void Reload() => Core?.Reload();

    /// <summary>Stops media and frees the page when the pane closes.</summary>
    public void Unload()
    {
        if (_initialized) Core!.Navigate("about:blank");
    }
}

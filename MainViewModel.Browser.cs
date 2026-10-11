using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Skein;

public sealed partial class MainViewModel
{
    // ---- In-app browser -------------------------------------------------------------------------

    bool _showBrowser;
    public bool ShowBrowser
    {
        get => _showBrowser;
        set
        {
            if (!Set(ref _showBrowser, value)) return;
            if (!value) _browser.Unload();
        }
    }

    public string BrowserUrl => _browser.Url;
    public string BrowserTitle => _browser.Title;
    public bool BrowserLoading => _browser.IsLoading;

    /// <summary>Opens a web link in the in-app browser pane (or the default browser, if preferred).</summary>
    void OpenLink(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return;
        if (!Settings.OpenLinksInApp)
        {
            OpenExternal(url);
            return;
        }
        _browser.Navigate(uri.AbsoluteUri);
        ShowBrowser = true;
    }

    public ICommand ToggleReadingModeCommand { get; }
    public ICommand HideCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand SpamCommand { get; }

    // ---- Links ------------------------------------------------------------------------------

    /// <summary>Opens Reddit links inside Skein and everything else in the default browser.</summary>
    public void OpenUrl(string url)
    {
        if (url.StartsWith(AppCommands.FilesPrefix, StringComparison.Ordinal))
        {
            if (AppCommands.FilesFrom(url) is { Count: > 0 } files) ShowSubmit(new SubmitPrefill("", "", "", files));
            return;
        }
        if (AppCommands.Parse(url) is { } command)
        {
            RunAppCommand(command.Command, command.Args);
            return;
        }
        switch (RedditLinks.Classify(url))
        {
            case ThreadLink thread:
                ShowBrowser = false;
                _ = _detail.OpenThreadAsync(thread.PostId, thread.CommentId);
                break;
            case FeedLink feed:
                OpenFeed(feed.Spec, newTab: true);
                break;
            case WebLink web:
                OpenLink(web.Url);
                break;
        }
    }

    /// <summary>The jump list's and the "Post to Skein" bookmark's commands.</summary>
    void RunAppCommand(string command, Dictionary<string, string> args)
    {
        switch (command)
        {
            case "submit": ShowSubmit(AppCommands.PrefillFrom(args)); break;
            case "home": OpenFeed("home", newTab: false); break;
            default: OpenFeed(command, newTab: false); break; // inbox, later, activity, modmail
        }
    }

    void OpenExternal(string? url)
    {
        // Only hand web links to the shell; never arbitrary schemes from page content.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"Couldn't open browser: {ex.Message}";
        }
    }
}

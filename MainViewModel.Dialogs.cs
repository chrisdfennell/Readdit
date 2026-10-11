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
    // ---- User notes and tags ----------------------------------------------------------------------

    void EditUserNote(string user)
    {
        var window = new UserNoteWindow(user, _services.UserNotes.Get(user)) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        _services.UserNotes.Set(user, window.TagValue, window.NoteValue);
        OnUserNoteChanged(user);
        _detail.RefreshSettings();
        Status = window.TagValue.Length + window.NoteValue.Length == 0 ? $"Cleared your note on u/{user}." : $"Saved your note on u/{user}.";
    }

    /// <summary>Shows a user's new tag on every loaded post of theirs.</summary>
    void OnUserNoteChanged(string user)
    {
        var tag = _services.UserNotes.Get(user)?.Tag ?? "";
        foreach (var post in Tabs.SelectMany(t => t.Posts).Where(p => p.Author.Equals(user, StringComparison.OrdinalIgnoreCase)))
            post.AuthorTag = tag;
    }

    // ---- Copied reddit links ------------------------------------------------------------------------

    string? _copiedLink;
    string? _lastClipboard;

    /// <summary>A reddit link on the clipboard that Skein offers to open.</summary>
    public string? CopiedLink
    {
        get => _copiedLink;
        private set
        {
            if (!Set(ref _copiedLink, value)) return;
            Raise(nameof(HasCopiedLink));
            Raise(nameof(CopiedLinkLabel));
        }
    }

    public bool HasCopiedLink => CopiedLink != null;

    public string CopiedLinkLabel =>
        CopiedLink == null ? "" : "📋 Open the reddit link you copied? " + (CopiedLink.Length > 70 ? CopiedLink[..70] + "…" : CopiedLink);

    /// <summary>Called when Skein comes to the front: offer a newly copied reddit link.</summary>
    public void CheckClipboard(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text == _lastClipboard) return;
        _lastClipboard = text;
        if (!_isReady || text.Length > 2000 || text.Contains(' ') || text.Contains('\n')) return;
        if (!LinkHandler.IsRedditUrl(text) || RedditLinks.Classify(text) is WebLink) return;
        // Not the post that's already open (you probably copied it from here).
        if (SelectedPost != null && text.Contains(SelectedPost.PermalinkUrl.Replace("https://www.reddit.com", ""), StringComparison.OrdinalIgnoreCase))
            return;
        CopiedLink = text;
    }

    // ---- Dialogs ----------------------------------------------------------------------------

    void ShowCompose(string to)
    {
        var window = new ComposeMessageWindow(_services.Api, to)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow,
        };
        if (window.ShowDialog() != true) return;
        Status = $"Message sent to u/{window.Recipient}.";
        // It shows up under Inbox → sent.
        foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.Inbox && t.Sort == "sent")) tab.MarkStale();
    }

    void ShowReportProblem() =>
        new ReportProblemWindow(_updates.CurrentVersion, _updates.Kind, Settings.UpdateUrl, _services.Api.UserName)
        {
            // Over Preferences when opened from there.
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow,
        }.ShowDialog();

    void ShowPreferences()
    {
        var (syncWas, syncFolderWas) = (Settings.SyncEnabled, Settings.SyncFolder);
        var window = new PreferencesWindow(Settings, _updates, SyncStatus) { Owner = Application.Current.MainWindow };
        window.ReportProblemRequested += () => ShowReportProblem();
        var saved = window.ShowDialog() == true;
        if (window.RestartToRestore)
        {
            RestartRequested?.Invoke(); // the backup is put in place as Skein starts again
            return;
        }
        UpdateReady = _updates.IsUpdateReady;
        Raise(nameof(UpdateButtonLabel));
        if (!saved) return;
        Settings.Save();
        ApplyAppearance();
        _detail.SetDisplay(ReadingFontSize, IsReadingMode);
        _detail.RefreshSettings(); // translation on or off
        Raise(nameof(ReadingFontSize));
        // Filters changed: reload this tab now, and the others when they're next opened.
        foreach (var tab in Tabs.Where(t => t != SelectedTab)) tab.MarkStale();
        _ = SelectedTab?.ReloadAsync();
        if (Settings.SyncEnabled != syncWas || Settings.SyncFolder != syncFolderWas) OnSyncSettingsChanged(syncWas, syncFolderWas);
    }

    /// <summary>
    /// Skein went out of sight (minimized or to the tray), or came back. Out of sight, the web
    /// views give memory back and background tabs sleep straight away.
    /// </summary>
    public void SetInBackground(bool background)
    {
        _detail.SetMemoryLow(background);
        _services.Session.SetMemoryLow(background);
        if (!background || Settings.SleepTabsAfterMinutes <= 0) return;
        foreach (var tab in Tabs.Where(t => t != SelectedTab)) tab.Sleep();
    }

    /// <summary>Raised after theme/scale changes so the window can restyle itself.</summary>
    public event Action? AppearanceChanged;

    public double UiScale => Math.Clamp(Settings.UiScale, 0.8, 1.5);
    public bool IsBlackTheme => Settings.Theme is "Black" or "High contrast";

    void ApplyAppearance()
    {
        Appearance.ApplyToApp(Settings);
        _services.Session.ApplyTheme(Settings.Theme);
        _browser.ApplyTheme(Settings.Theme);
        _detail.SetTheme(Settings.Theme, Settings.Accent);
        AppearanceChanged?.Invoke();
    }

    /// <param name="share">Something shared to Skein to start the post with (a link, text or files).</param>
    void ShowSubmit(SubmitPrefill? share = null)
    {
        var defaultSub = SelectedTab?.IsSubreddit == true && !FeedViewModel.IsAggregateName(SelectedTab.Target)
            ? SelectedTab.Target
            : "";
        var window = new SubmitWindow(_services.Api, defaultSub, _services, share) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        if (window.Scheduled is { } scheduled)
        {
            Status = $"Scheduled for {LaterTimes.Describe(DateTimeOffset.FromUnixTimeSeconds(scheduled.Due).LocalDateTime, DateTime.Now)}. " +
                     "Skein posts it then, as long as it's running (the tray counts). See ⏰ Reminders.";
            _detail.RefreshLater();
            return;
        }
        if (window.PostUrl is { Length: > 0 } url)
        {
            Status = "Posted!";
            OpenUrl(url);
        }
        else
        {
            // Media posts finish in the background on Reddit's side.
            Status = "Posted! Reddit is still processing your upload; it'll appear on your profile in a moment.";
        }
    }
}

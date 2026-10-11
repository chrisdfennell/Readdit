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
    // ---- Sleeping tabs ------------------------------------------------------------------------------

    readonly DispatcherTimer _sleepTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    /// <summary>Background tabs unused for a while let go of their posts (they reload when opened).</summary>
    void SleepIdleTabs()
    {
        var after = Settings.SleepTabsAfterMinutes;
        if (after <= 0) return;
        var cutoff = DateTime.Now.AddMinutes(-after);
        foreach (var tab in Tabs.Where(t => t != SelectedTab && t.LastShown < cutoff)) tab.Sleep();
    }

    // ---- Sync between PCs -----------------------------------------------------------------------

    readonly SettingsSync _sync;
    readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    System.IO.FileSystemWatcher? _syncWatcher;
    DispatcherTimer? _syncSoon;

    /// <summary>For Preferences: whether sync is working.</summary>
    public string SyncStatus =>
        !Settings.SyncEnabled ? "Off."
        : _sync.LastError != null ? $"Couldn't use the sync folder just now ({_sync.LastError}). Trying again shortly."
        : _sync.LastSynced is { } at ? $"✓ In sync. Last checked at {at:t}."
        : "Waiting to sync…";

    /// <summary>Starts (or restarts) syncing: a round now, then every so often and whenever the file changes.</summary>
    void StartSync()
    {
        _syncTimer.Stop();
        _syncWatcher?.Dispose();
        _syncWatcher = null;
        if (!Settings.SyncEnabled || Settings.SyncFolder.Length == 0) return;
        RunSync();
        _syncTimer.Start();
        try
        {
            System.IO.Directory.CreateDirectory(Settings.SyncFolder);
            _syncWatcher = new System.IO.FileSystemWatcher(Settings.SyncFolder, SettingsSync.FileName)
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            // Another PC's changes arrive as the file changes; wait a moment for OneDrive to finish writing it.
            void Soon(object? _, System.IO.FileSystemEventArgs __) => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _syncSoon ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _syncSoon.Tick -= OnSyncSoon;
                _syncSoon.Tick += OnSyncSoon;
                _syncSoon.Stop();
                _syncSoon.Start();
            });
            _syncWatcher.Changed += Soon;
            _syncWatcher.Created += Soon;
            _syncWatcher.Renamed += (s, e) => Soon(s, e);
        }
        catch
        {
            // The timer still syncs.
        }
    }

    void OnSyncSoon(object? sender, EventArgs e)
    {
        _syncSoon?.Stop();
        RunSync();
    }

    void RunSync()
    {
        var applied = _sync.RunOnce();
        if (applied is { Count: > 0 }) OnSyncApplied(applied);
    }

    /// <summary>Settings arrived from another PC: show them.</summary>
    void OnSyncApplied(IReadOnlyList<string> sections)
    {
        if (sections.Contains("folders") || sections.Contains("searches"))
        {
            RefreshSidebar();
            foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.Folder)) tab.MarkStale();
        }
        if (sections.Contains("alerts"))
        {
            _alertTimer.Interval = AlertInterval;
            RefreshAlertTabs();
        }
        if (sections.Contains("notes"))
            foreach (var post in Tabs.SelectMany(t => t.Posts))
                post.AuthorTag = _services.UserNotes.Get(post.Author)?.Tag ?? "";
        if (sections.Contains("filters"))
            foreach (var tab in Tabs.Where(t => t != SelectedTab)) tab.MarkStale();
        if (sections.Contains("notes") || sections.Contains("filters") || sections.Contains("replies") || sections.Contains("reminders"))
            _detail.RefreshSettings();
        if (sections.Contains("reminders")) _detail.RefreshLater();
        if (sections.Contains("appearance"))
        {
            ApplyAppearance();
            Raise(nameof(LayoutMode));
            Raise(nameof(ReadingFontSize));
            _detail.SetDisplay(ReadingFontSize, IsReadingMode);
        }
        Status = $"Synced {string.Join(", ", sections)} from your other PC.";
    }

    /// <summary>After Preferences: sync was turned on or pointed somewhere else.</summary>
    void OnSyncSettingsChanged(bool wasEnabled, string oldFolder)
    {
        if (!Settings.SyncEnabled)
        {
            StartSync(); // stops it
            return;
        }
        if (wasEnabled && oldFolder.Equals(Settings.SyncFolder, StringComparison.OrdinalIgnoreCase)) return;
        // Starting fresh with this folder. If another PC already syncs there, ask which copy to keep.
        var preferFile = _sync.FileExists && MessageBox.Show(Application.Current.MainWindow,
            "Another PC already syncs Skein's settings in this folder.\n\n" +
            "Yes: use those (folders, alerts, notes, filters and appearance on this PC are replaced).\n" +
            "No: use this PC's settings everywhere instead.",
            "Sync between PCs", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        _sync.Reset(preferFile);
        StartSync();
    }
}

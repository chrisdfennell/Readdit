using System.IO;
using System.Text.Json;

namespace Skein;

static class Paths
{
    /// <summary>Small user settings (roams with the profile).</summary>
    public static readonly string Roaming =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skein");

    /// <summary>
    /// Machine-local data: browser profile (sign-in), read history, offline library.
    /// Not %LocalAppData%\Skein: that's where the installer puts the app, and it replaces
    /// that folder on install and deletes it on uninstall.
    /// </summary>
    public static readonly string Local =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkeinData");

    // Skein used to be called Readdit. Its data lived here:
    static readonly string LegacyRoaming =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Readdit");
    static readonly string LegacyLocal =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReadditData");
    // …and before that, inside Readdit's install folder.
    static readonly string LegacyInstallFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Readdit");

    /// <summary>The data the oldest versions kept inside their install folder (the rest there is the app).</summary>
    static readonly string[] InstallFolderData = ["WebView2", "offline", "read.txt", "visits.json", "api.log", "crash.log"];

    /// <summary>
    /// Moves data from earlier locations (Readdit's folders) to <see cref="Local"/> and
    /// <see cref="Roaming"/>. Runs at startup before anything opens these files.
    /// </summary>
    public static void MigrateLocalData()
    {
        var retry = TimeSpan.FromMilliseconds(500);
        MigrateLocalData(LegacyInstallFolder, Local, retry);
        MigrateFolder(LegacyLocal, Local, null, retry);
        MigrateFolder(LegacyRoaming, Roaming, null, retry);
    }

    /// <summary>The oldest migration, out of an install folder (tests use temporary folders).</summary>
    public static void MigrateLocalData(string oldLocal, string newLocal, TimeSpan retryDelay) =>
        MigrateFolder(oldLocal, newLocal, InstallFolderData, retryDelay);

    /// <summary>
    /// Moves the named entries (or, with null, everything) from one folder to another, skipping
    /// anything already there. A folder emptied by moving everything is removed.
    /// </summary>
    public static void MigrateFolder(string oldDir, string newDir, IReadOnlyList<string>? only, TimeSpan retryDelay)
    {
        if (!Directory.Exists(oldDir)) return;
        IReadOnlyList<string> entries;
        try
        {
            Directory.CreateDirectory(newDir);
            entries = only ?? Directory.EnumerateFileSystemEntries(oldDir).Select(e => Path.GetFileName(e)).ToList();
        }
        catch
        {
            return;
        }

        foreach (var name in entries)
        {
            var from = Path.Combine(oldDir, name);
            var to = Path.Combine(newDir, name);
            if (!Path.Exists(from) || Path.Exists(to)) continue;

            // Each entry on its own, so one locked item doesn't strand the rest. Right after an
            // update the previous version's browser processes can hold the WebView2 folder for a
            // few seconds, so retry briefly.
            for (var attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    if (Directory.Exists(from)) Directory.Move(from, to);
                    else File.Move(from, to);
                    break;
                }
                catch (IOException) when (attempt < 11)
                {
                    Thread.Sleep(retryDelay);
                }
                catch (UnauthorizedAccessException) when (attempt < 11)
                {
                    Thread.Sleep(retryDelay);
                }
                catch
                {
                    break; // Give up on this entry; it's retried on the next launch.
                }
            }
        }

        if (only != null) return;
        try
        {
            if (!Directory.EnumerateFileSystemEntries(oldDir).Any()) Directory.Delete(oldDir);
        }
        catch
        {
            // Something still in use; it's moved on a later launch.
        }
    }
}

public sealed class AccountInfo
{
    public string Profile { get; set; } = "";
    public string UserName { get; set; } = "";
}

public sealed class AppSettings
{
    static readonly string FilePath = Path.Combine(Paths.Roaming, "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<string> OpenTabs { get; set; } = ["home"];
    public int SelectedTab { get; set; }

    /// <summary>Sidebar sections you've collapsed: feeds, moderation, favorites, subscriptions.</summary>
    public List<string> CollapsedSidebarSections { get; set; } = [];

    /// <summary>Cache of your Reddit favorites (starred communities), refreshed after sign-in.</summary>
    public List<string> Favorites { get; set; } = [];

    /// <summary>Your own groups of communities (kept on this PC, not on Reddit).</summary>
    public List<SubscriptionFolder> SubscriptionFolders { get; set; } = [];

    /// <summary>Order of the Subscriptions list: Name (A–Z) or Visits (most opened first).</summary>
    public string SubscriptionSort { get; set; } = "Name";

    /// <summary>Sync folders, alerts, notes, filters and appearance through <see cref="SyncFolder"/>.</summary>
    public bool SyncEnabled { get; set; }

    /// <summary>A folder other PCs also see (OneDrive\Skein by default).</summary>
    public string SyncFolder { get; set; } = SettingsSync.DefaultFolder() ?? "";

    /// <summary>For each synced section: the sync file's "updated" time as of the last sync (this PC's bookkeeping).</summary>
    public Dictionary<string, long> SyncStamps { get; set; } = [];

    /// <summary>For each synced section: its hash when last synced, to notice changes made here.</summary>
    public Dictionary<string, string> SyncHashes { get; set; } = [];

    /// <summary>Mini mode window position and size: left, top, width, height.</summary>
    public double[] MiniBounds { get; set; } = [];

    /// <summary>Keep the mini mode window on top of other windows.</summary>
    public bool MiniTopmost { get; set; } = true;

    /// <summary>In mixed feeds, show a link posted in several communities once (noting the others).</summary>
    public bool HideDuplicates { get; set; } = true;

    /// <summary>Background tabs let go of their posts after this many minutes (0 = never).</summary>
    public int SleepTabsAfterMinutes { get; set; } = 15;

    /// <summary>Searches kept in the sidebar.</summary>
    public List<SavedSearch> SavedSearches { get; set; } = [];

    /// <summary>Threads you're watching for new comments.</summary>
    public List<WatchedThread> WatchedThreads { get; set; } = [];

    /// <summary>People whose new posts you're notified about.</summary>
    public List<FollowedUser> FollowedUsers { get; set; } = [];

    /// <summary>Posts you asked to be reminded about.</summary>
    public List<Reminder> Reminders { get; set; } = [];

    /// <summary>Posts waiting to go out at a set time (this PC only: it posts them while running).</summary>
    public List<ScheduledPost> ScheduledPosts { get; set; } = [];

    /// <summary>Snippets to drop into replies and posts.</summary>
    public List<SavedReply> SavedReplies { get; set; } = [];

    /// <summary>Offer to translate posts and comments (sends their text to <see cref="TranslateServer"/>).</summary>
    public bool TranslateEnabled { get; set; }

    /// <summary>Language to translate into (two letters; empty = Windows' language).</summary>
    public string TranslateTo { get; set; } = "";

    /// <summary>A LibreTranslate server to use instead of MyMemory (empty = MyMemory).</summary>
    public string TranslateServer { get; set; } = "";

    /// <summary>The LibreTranslate server's API key, if it needs one.</summary>
    public string TranslateKey { get; set; } = "";

    /// <summary>Your karma each day Skein ran (for the activity page's chart), per account.</summary>
    public Dictionary<string, List<KarmaPoint>> KarmaHistory { get; set; } = [];

    /// <summary>Keyword alerts, checked in the background.</summary>
    public List<AlertRule> AlertRules { get; set; } = [];

    /// <summary>How often alerts are checked, in minutes.</summary>
    public int AlertIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Skein is set up as Windows' handler for web links: it opens reddit links itself and
    /// passes everything else to <see cref="FallbackBrowserCommand"/>.
    /// </summary>
    public bool LinkHandlerEnabled { get; set; }

    /// <summary>The browser that had web links before Skein (its shell command, with %1 for the URL).</summary>
    public string FallbackBrowserCommand { get; set; } = "";

    /// <summary>That browser's name, for display.</summary>
    public string FallbackBrowserName { get; set; } = "";

    /// <summary>Post list layout: Compact, Cards or Grid.</summary>
    public string LayoutMode { get; set; } = "Compact";

    /// <summary>Base text size (px) in the post view.</summary>
    public int ReadingFontSize { get; set; } = 15;

    /// <summary>System, Light, Dark or Black (true-black dark mode for OLED screens).</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Accent colour as #RRGGBB.</summary>
    public string Accent { get; set; } = "#FF4500";

    /// <summary>Scale for the app's own interface (sidebar, list, toolbar); 1.0 = 100%.</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>GitHub repository URL that publishes Skein releases (enables auto-update).</summary>
    public string UpdateUrl { get; set; } = "https://github.com/chrisdfennell/Skein";

    /// <summary>Check for updates regularly, download them, and install when Skein closes.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Open web links in Skein's own browser pane instead of the default browser.</summary>
    public bool OpenLinksInApp { get; set; } = true;

    /// <summary>Check the open feed for new posts in the background and offer to show them.</summary>
    public bool CheckForNewPosts { get; set; } = true;

    public bool HideNsfw { get; set; }
    public bool BlurNsfw { get; set; } = true;
    public bool HideRead { get; set; }
    public bool SaveOffline { get; set; } = true;
    public bool InboxNotifications { get; set; } = true;

    /// <summary>What closing the window does: Ask, Tray (keep running in the tray) or Exit.</summary>
    public string CloseAction { get; set; } = "Ask";
    public bool TrayHintShown { get; set; }

    /// <summary>Hide posts below this score in Home, Popular, All and search (null = off).</summary>
    public int? MinScore { get; set; }

    /// <summary>Auto-collapse comments at or below this score (null = off).</summary>
    public int? CollapseBelowScore { get; set; } = -5;

    /// <summary>Users whose posts are hidden and comments collapsed.</summary>
    public List<string> MutedUsers { get; set; } = [];

    /// <summary>Users whose posts and comments stand out.</summary>
    public List<string> HighlightedUsers { get; set; } = [];

    /// <summary>Where the Download button saves images and videos.</summary>
    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Skein");

    /// <summary>Signed-in accounts; each has its own browser profile.</summary>
    public List<AccountInfo> Accounts { get; set; } = [];

    /// <summary>Browser profile of the active account ("" = the original default profile).</summary>
    public string ActiveProfile { get; set; } = "";

    /// <summary>The profile we last switched away from.</summary>
    public string PreviousProfile { get; set; } = "";

    public List<string> KeywordFilters { get; set; } = [];
    public List<string> DomainFilters { get; set; } = [];
    public List<string> SubredditFilters { get; set; } = [];

    /// <summary>Where releases came from before the rename (the repository was called Readdit).</summary>
    const string LegacyUpdateUrl = "https://github.com/chrisdfennell/Readdit";

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
        if (settings.UpdateUrl.TrimEnd('/').Equals(LegacyUpdateUrl, StringComparison.OrdinalIgnoreCase))
            settings.UpdateUrl = new AppSettings().UpdateUrl;
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Settings are a convenience; never crash over them.
        }
    }
}

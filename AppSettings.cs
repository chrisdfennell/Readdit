using System.IO;
using System.Text.Json;

namespace Readdit;

static class Paths
{
    /// <summary>Small user settings (roams with the profile).</summary>
    public static readonly string Roaming =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Readdit");

    /// <summary>
    /// Machine-local data: browser profile (sign-in), read history, offline library.
    /// Not %LocalAppData%\Readdit: that's where the installer puts the app, and it replaces
    /// that folder on install and deletes it on uninstall.
    /// </summary>
    public static readonly string Local =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReadditData");

    static readonly string OldLocal =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Readdit");

    /// <summary>
    /// Moves data from the old location (shared with the install folder) to <see cref="Local"/>.
    /// Runs at startup before anything opens these files.
    /// </summary>
    public static void MigrateLocalData()
    {
        string[] entries = ["WebView2", "offline", "read.txt", "visits.json", "api.log", "crash.log"];
        try
        {
            if (!Directory.Exists(OldLocal)) return;
            Directory.CreateDirectory(Local);
            foreach (var name in entries)
            {
                var from = Path.Combine(OldLocal, name);
                var to = Path.Combine(Local, name);
                if (Path.Exists(to)) continue;
                if (Directory.Exists(from)) Directory.Move(from, to);
                else if (File.Exists(from)) File.Move(from, to);
            }
        }
        catch
        {
            // Worst case Readdit starts fresh (sign in again); never block startup over it.
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

    /// <summary>Cache of your Reddit favorites (starred communities), refreshed after sign-in.</summary>
    public List<string> Favorites { get; set; } = [];

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

    /// <summary>GitHub repository URL that publishes Readdit releases (enables auto-update).</summary>
    public string UpdateUrl { get; set; } = "https://github.com/chrisdfennell/Readdit";

    /// <summary>Check for updates regularly, download them, and install when Readdit closes.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Open web links in Readdit's own browser pane instead of the default browser.</summary>
    public bool OpenLinksInApp { get; set; } = true;

    public bool HideNsfw { get; set; }
    public bool BlurNsfw { get; set; } = true;
    public bool HideRead { get; set; }
    public bool SaveOffline { get; set; } = true;
    public bool InboxNotifications { get; set; } = true;

    /// <summary>Keep running in the system tray when the window is closed.</summary>
    public bool CloseToTray { get; set; } = true;
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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Readdit");

    /// <summary>Signed-in accounts; each has its own browser profile.</summary>
    public List<AccountInfo> Accounts { get; set; } = [];

    /// <summary>Browser profile of the active account ("" = the original default profile).</summary>
    public string ActiveProfile { get; set; } = "";

    public List<string> KeywordFilters { get; set; } = [];
    public List<string> DomainFilters { get; set; } = [];
    public List<string> SubredditFilters { get; set; } = [];

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
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

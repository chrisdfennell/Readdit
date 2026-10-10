using System.Diagnostics;
using Microsoft.Win32;

namespace Readdit;

/// <summary>
/// Lets other apps hand links to Readdit. Windows has no "open reddit.com links with…" setting,
/// so Readdit offers to be the app for web links in general: it opens reddit links itself and
/// passes every other link straight on to the browser that had them before. It also registers a
/// readdit:// link type (readdit://r/pics, readdit://https://www.reddit.com/…).
/// </summary>
static class LinkHandler
{
    const string Protocol = "readdit";
    const string ProgId = "ReadditURL";
    const string ClientKey = @"Software\Clients\StartMenuInternet\Readdit";
    const string CapabilitiesKey = ClientKey + @"\Capabilities";

    static string ExePath => Environment.ProcessPath ?? "Readdit.exe";
    static string OpenCommand => $"\"{ExePath}\" \"%1\"";

    // ---- Incoming links -------------------------------------------------------------------------

    /// <summary>
    /// A command-line argument as a web URL: readdit:// links become reddit.com ones; other
    /// http(s) links pass through. Null for anything else (flags like --restarted).
    /// </summary>
    public static string? NormalizeIncoming(string arg)
    {
        arg = arg.Trim().Trim('"');
        if (arg.StartsWith(Protocol + ":", StringComparison.OrdinalIgnoreCase))
        {
            var rest = arg[(Protocol.Length + 1)..].TrimStart('/');
            if (rest.Length == 0) return "https://www.reddit.com/";
            if (rest.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                // "readdit://https://…" arrives as "https:/www…" once slashes are collapsed.
                var colon = rest.IndexOf(':');
                rest = rest[..(colon + 1)] + "//" + rest[(colon + 1)..].TrimStart('/');
                return Uri.TryCreate(rest, UriKind.Absolute, out var full) ? full.AbsoluteUri : null;
            }
            return Uri.TryCreate("https://www.reddit.com/" + rest, UriKind.Absolute, out var path) ? path.AbsoluteUri : null;
        }
        return Uri.TryCreate(arg, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http")
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>reddit.com and its short links (redd.it).</summary>
    public static bool IsRedditUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("reddit.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".reddit.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("redd.it", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Passes a non-reddit link on to the user's real browser. Never via the shell's "open this
    /// URL", which would come straight back to Readdit while it's the web link handler.
    /// </summary>
    public static void OpenInFallbackBrowser(string url, string command)
    {
        try
        {
            if (TrySplitCommand(command, url) is { } start && !start.Exe.Equals(ExePath, StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(start.Exe, start.Args) { UseShellExecute = false });
                return;
            }
        }
        catch
        {
            // Fall through to Edge, which every Windows has.
        }
        Process.Start(new ProcessStartInfo("microsoft-edge:" + url) { UseShellExecute = true });
    }

    /// <summary>"\"C:\…\chrome.exe\" --single-argument %1" with a URL → (exe, arguments).</summary>
    public static (string Exe, string Args)? TrySplitCommand(string command, string url)
    {
        command = command.Trim();
        if (command.Length == 0) return null;
        string exe, args;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end < 0) return null;
            exe = command[1..end];
            args = command[(end + 1)..].Trim();
        }
        else
        {
            var space = command.IndexOf(' ');
            exe = space < 0 ? command : command[..space];
            args = space < 0 ? "" : command[(space + 1)..].Trim();
        }
        // The URL replaces %1 (keeping any quotes around it), or goes on the end.
        var quoted = url.Replace("\"", "%22");
        args = args.Contains("%1") ? args.Replace("%1", quoted) : (args + " \"" + quoted + "\"").Trim();
        return (exe, args);
    }

    // ---- Registration ---------------------------------------------------------------------------

    /// <summary>The readdit:// link type; harmless to repeat, and keeps the exe path current.</summary>
    public static void RegisterProtocol()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Protocol}");
            key.SetValue("", "URL:Readdit link");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{ExePath}\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", OpenCommand);
        }
        catch
        {
            // A convenience; Readdit works without it.
        }
    }

    /// <summary>
    /// Registers Readdit as an app that can open web links, so it can be picked in Windows'
    /// default apps settings. Returns the current default browser (to pass other links to).
    /// </summary>
    public static (string Command, string Name)? RegisterAsBrowser()
    {
        var previous = CurrentBrowser();
        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue("", "Readdit link");
            prog.SetValue("URL Protocol", "");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{ExePath}\",0");
            using var command = prog.CreateSubKey(@"shell\open\command");
            command.SetValue("", OpenCommand);
        }
        using (var client = Registry.CurrentUser.CreateSubKey(ClientKey))
        {
            client.SetValue("", "Readdit");
            using (var icon = client.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{ExePath}\",0");
            using var command = client.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{ExePath}\"");
        }
        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            caps.SetValue("ApplicationName", "Readdit");
            caps.SetValue("ApplicationDescription",
                "Opens Reddit links in Readdit and passes every other link to your usual browser.");
            caps.SetValue("ApplicationIcon", $"\"{ExePath}\",0");
            using var urls = caps.CreateSubKey("URLAssociations");
            urls.SetValue("http", ProgId);
            urls.SetValue("https", ProgId);
        }
        using (var apps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            apps.SetValue("Readdit", CapabilitiesKey);
        return previous;
    }

    public static void UnregisterAsBrowser()
    {
        try
        {
            using (var apps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
                apps?.DeleteValue("Readdit", throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree(ClientKey, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        }
        catch
        {
            // Nothing else to do; Windows ignores registrations whose app is gone.
        }
    }

    /// <summary>Removes everything Readdit registered (on uninstall).</summary>
    public static void UnregisterAll()
    {
        UnregisterAsBrowser();
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Protocol}", throwOnMissingSubKey: false);
        }
        catch
        {
            // As above.
        }
    }

    /// <summary>Whether Windows currently sends https links to Readdit.</summary>
    public static bool IsDefaultForWebLinks() => UserChoiceProgId("https") == ProgId;

    /// <summary>The app Windows sends https links to now, unless that's Readdit: its command and name.</summary>
    public static (string Command, string Name)? CurrentBrowser()
    {
        var progId = UserChoiceProgId("https");
        if (progId == null || progId == ProgId) return null;
        using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
        var line = command?.GetValue("") as string;
        if (string.IsNullOrWhiteSpace(line)) return null;
        using var app = Registry.ClassesRoot.OpenSubKey($@"{progId}\Application");
        var name = app?.GetValue("ApplicationName") as string;
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('@'))
            name = TrySplitCommand(line, "") is { } split ? System.IO.Path.GetFileNameWithoutExtension(split.Exe) : progId;
        return (line, name);
    }

    static string? UserChoiceProgId(string scheme)
    {
        using var choice = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\UserChoice");
        return choice?.GetValue("ProgId") as string;
    }

    /// <summary>Opens Windows' default apps page for Readdit, where web links can be given to it.</summary>
    public static void OpenDefaultAppsSettings(bool forReaddit = true)
    {
        try
        {
            Process.Start(new ProcessStartInfo(forReaddit
                ? "ms-settings:defaultapps?registeredAppUser=Readdit"
                : "ms-settings:defaultapps")
            { UseShellExecute = true });
        }
        catch
        {
            // Settings app unavailable; the user can find it themselves.
        }
    }
}

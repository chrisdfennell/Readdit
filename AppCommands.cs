using System.IO;

namespace Skein;

/// <summary>What a shared link, text or files start a new post with.</summary>
public sealed record SubmitPrefill(string Url, string Title, string Text, IReadOnlyList<string> Files);

/// <summary>
/// Commands Skein can be started (or poked) with: the taskbar jump list, the "Post to Skein"
/// bookmark and Explorer's Send to menu.
/// skein://_skein/inbox (or home, submit, later, activity, modmail) arrives as
/// https://www.reddit.com/_skein/inbox; submit takes url, title and text. Files from Send to
/// arrive as "skein-share-files:" and their paths, one per line. Only Skein's own launcher
/// makes that form, so a web page can't attach files from your PC.
/// </summary>
public static class AppCommands
{
    public const string FilesPrefix = "skein-share-files:";
    const string CommandPath = "/_skein/";

    public static readonly string[] Commands = ["inbox", "home", "submit", "later", "activity", "modmail"];

    public static string Link(string command) => "skein://_skein/" + command;

    /// <summary>A command link → its name and arguments; null for ordinary links.</summary>
    public static (string Command, Dictionary<string, string> Args)? Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !LinkHandler.IsRedditUrl(url) ||
            !uri.AbsolutePath.StartsWith(CommandPath, StringComparison.OrdinalIgnoreCase))
            return null;
        var command = uri.AbsolutePath[CommandPath.Length..].Trim('/').ToLowerInvariant();
        if (!Commands.Contains(command)) return null;
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (key.Length > 0 && value.Length <= 10000) args[key] = value;
        }
        return (command, args);
    }

    /// <summary>A submit command's link, title and text (only a web link is kept as the link).</summary>
    public static SubmitPrefill PrefillFrom(Dictionary<string, string> args)
    {
        var url = args.GetValueOrDefault("url", "");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var link) || (link.Scheme != "https" && link.Scheme != "http")) url = "";
        var title = args.GetValueOrDefault("title", "").Trim();
        return new SubmitPrefill(url, title.Length > 300 ? title[..300] : title, args.GetValueOrDefault("text", ""), []);
    }

    /// <summary>Command-line arguments that are pictures or videos → a files message (null if there are none).</summary>
    public static string? ForFiles(IEnumerable<string> args)
    {
        var files = args.Select(a => a.Trim().Trim('"'))
            .Where(a => a.Length > 3 && Path.IsPathFullyQualified(a) && File.Exists(a) && RedditApi.MediaTypeFor(a) != null)
            .Take(20)
            .ToList();
        return files.Count == 0 ? null : FilesPrefix + string.Join("\n", files);
    }

    /// <summary>The files in a files message that still exist.</summary>
    public static List<string> FilesFrom(string message) =>
        message.StartsWith(FilesPrefix, StringComparison.Ordinal)
            ? message[FilesPrefix.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(f => Path.IsPathFullyQualified(f) && File.Exists(f) && RedditApi.MediaTypeFor(f) != null)
                .Take(20)
                .ToList()
            : [];

    /// <summary>The bookmark that sends the page you're on to Skein as a new post.</summary>
    public const string Bookmarklet =
        "javascript:location.href='skein://_skein/submit?url='+encodeURIComponent(location.href)+'&title='+encodeURIComponent(document.title)";
}

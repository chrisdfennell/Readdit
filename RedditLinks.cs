using System.Text.RegularExpressions;

namespace Readdit;

/// <summary>Where a link should go inside Readdit.</summary>
public abstract record LinkTarget;

/// <summary>A Reddit thread, optionally focused on one comment.</summary>
public sealed record ThreadLink(string PostId, string? CommentId) : LinkTarget;

/// <summary>A subreddit or profile, as a feed spec ("r/name", "u/name").</summary>
public sealed record FeedLink(string Spec) : LinkTarget;

/// <summary>Anything else: opened as a web page.</summary>
public sealed record WebLink(string Url) : LinkTarget;

/// <summary>Decides how Reddit URLs and typed shortcuts open in Readdit.</summary>
public static partial class RedditLinks
{
    /// <summary>Classifies an absolute URL; null if it isn't one.</summary>
    public static LinkTarget? Classify(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();

        // redd.it/abc123 short links.
        if (host == "redd.it" && uri.AbsolutePath.Trim('/') is { Length: > 0 } shortId && ThreadId().IsMatch(shortId))
            return new ThreadLink(shortId, null);

        if (host == "reddit.com" || host.EndsWith(".reddit.com"))
        {
            var path = uri.AbsolutePath;
            var thread = RedditThread().Match(path);
            if (thread.Success)
                return new ThreadLink(thread.Groups[1].Value, thread.Groups[2].Success ? thread.Groups[2].Value : null);
            if (FeedFromPath(path, allowSubPages: true) is { } spec) return new FeedLink(spec);
        }

        return new WebLink(url);
    }

    /// <summary>
    /// What the search box treats as navigation rather than a search: "r/name", "/u/name",
    /// "user/name". Returns the feed spec, or null to search for the text instead.
    /// </summary>
    public static string? ParseFeedInput(string input) => FeedFromPath(input.Trim(), allowSubPages: false);

    static string? FeedFromPath(string path, bool allowSubPages)
    {
        var match = (allowSubPages ? FeedPathWithSubPages() : FeedPath()).Match(path);
        if (!match.Success) return null;
        var prefix = match.Groups[1].Value.Equals("r", StringComparison.OrdinalIgnoreCase) ? "r/" : "u/";
        return prefix + match.Groups[2].Value;
    }

    [GeneratedRegex(@"/comments/([a-z0-9]+)(?:/[^/]*/([a-z0-9]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex RedditThread();

    [GeneratedRegex("^[a-z0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ThreadId();

    [GeneratedRegex(@"^/?(r|u|user)/([A-Za-z0-9_+-]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex FeedPath();

    /// <summary>Subreddit or profile pages, including sorted/sub-pages like /r/x/top or /user/x/submitted.</summary>
    [GeneratedRegex(@"^/(r|u|user)/([A-Za-z0-9_+-]+)(?:/(?:hot|new|top|rising|controversial|best|submitted|comments|overview|about)/?)?/?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex FeedPathWithSubPages();
}

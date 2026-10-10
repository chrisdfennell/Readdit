using System.IO;
using System.Text.Json;

namespace Readdit;

/// <summary>
/// "Tell me when a post matching this appears": keywords, optionally limited to communities.
/// Kept in settings; Readdit checks them in the background while it runs (tray included).
/// </summary>
public sealed class AlertRule
{
    public string Name { get; set; } = "";

    /// <summary>Comma-separated words or phrases; any one matches. A leading "-" excludes ("-broken").</summary>
    public string Keywords { get; set; } = "";

    /// <summary>Communities to watch, separated by commas or spaces. Empty = search all of Reddit.</summary>
    public string Subreddits { get; set; } = "";

    /// <summary>Match the post's text as well as its title.</summary>
    public bool SearchText { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Newest post time (unix seconds) already looked at; 0 = never checked.</summary>
    public long LastChecked { get; set; }

    public AlertRule Clone() => (AlertRule)MemberwiseClone();
}

/// <summary>Matching rules against posts, and where to look for them.</summary>
public static class AlertMatcher
{
    /// <summary>"RTX, 4090, -broken" → wanted [RTX, 4090], excluded [broken].</summary>
    public static (List<string> Wanted, List<string> Excluded) ParseTerms(string keywords)
    {
        var wanted = new List<string>();
        var excluded = new List<string>();
        foreach (var raw in keywords.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var term = raw.Trim().Trim('"');
            if (term.StartsWith('-'))
            {
                var without = term[1..].Trim().Trim('"');
                if (without.Length > 0) excluded.Add(without);
            }
            else if (term.Length > 0)
            {
                wanted.Add(term);
            }
        }
        return (wanted, excluded);
    }

    public static List<string> ParseSubreddits(string text) => FolderWindow.ParseMembers(text);

    /// <summary>A rule needs something to watch: words, or communities (every new post there).</summary>
    public static bool IsUsable(AlertRule rule) =>
        ParseTerms(rule.Keywords).Wanted.Count > 0 || ParseSubreddits(rule.Subreddits).Count > 0;

    public static bool Matches(AlertRule rule, string title, string text)
    {
        var (wanted, excluded) = ParseTerms(rule.Keywords);
        var haystack = rule.SearchText ? title + "\n" + text : title;
        if (excluded.Any(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase))) return false;
        return wanted.Count == 0 || wanted.Any(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The listing to check: the newest posts in the rule's communities, or (with no communities)
    /// a Reddit-wide search for its words, newest first.
    /// </summary>
    public static string ListingPath(AlertRule rule)
    {
        var subs = ParseSubreddits(rule.Subreddits);
        if (subs.Count > 0) return $"/r/{string.Join("+", subs.Take(FeedViewModel.MaxFolderSize))}/new.json?";
        var query = string.Join(" OR ", ParseTerms(rule.Keywords).Wanted.Select(t => t.Contains(' ') ? $"\"{t}\"" : t));
        return $"/search.json?q={Uri.EscapeDataString(query)}&sort=new&t=week&";
    }
}

/// <summary>Posts that matched an alert, newest first, for the Alerts feed.</summary>
public sealed class AlertStore
{
    const int MaxHits = 300;
    readonly string _file;
    readonly List<Hit> _hits;

    public sealed record Hit(string Rule, string FullName, long At, string Raw);

    public AlertStore() : this(Path.Combine(Paths.Local, "alerts.json")) { }

    /// <summary>Uses <paramref name="file"/> for storage (tests use a temporary one).</summary>
    public AlertStore(string file)
    {
        _file = file;
        try
        {
            _hits = File.Exists(_file) ? JsonSerializer.Deserialize<List<Hit>>(File.ReadAllText(_file)) ?? [] : [];
        }
        catch
        {
            _hits = [];
        }
    }

    public int Count => _hits.Count;

    /// <summary>Records a match; false if that post was already recorded.</summary>
    public bool Add(string rule, Post post)
    {
        if (_hits.Any(h => h.FullName == post.FullName)) return false;
        _hits.Insert(0, new Hit(rule, post.FullName, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), post.RawJson));
        if (_hits.Count > MaxHits) _hits.RemoveRange(MaxHits, _hits.Count - MaxHits);
        return true;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_hits));
        }
        catch
        {
            // Best-effort.
        }
    }

    public void Clear()
    {
        _hits.Clear();
        Save();
    }

    public List<Post> List()
    {
        var posts = new List<Post>();
        foreach (var hit in _hits)
        {
            try
            {
                using var doc = JsonDocument.Parse(hit.Raw);
                var post = Post.FromLink(doc.RootElement);
                post.AlertName = hit.Rule;
                posts.Add(post);
            }
            catch
            {
                // Skip anything unreadable.
            }
        }
        return posts;
    }
}

/// <summary>Checks alert rules against Reddit and records new matches.</summary>
public sealed class AlertMonitor(Services services)
{
    bool _running;

    /// <summary>New matches from one check: the rule names involved and the posts.</summary>
    public event Action<List<(string Rule, Post Post)>>? Matched;

    public async Task CheckAsync()
    {
        if (_running) return;
        _running = true;
        var found = new List<(string, Post)>();
        try
        {
            foreach (var rule in services.Settings.AlertRules.Where(r => r.Enabled && AlertMatcher.IsUsable(r)).ToList())
            {
                List<Post> items;
                try
                {
                    (items, _) = await services.Api.GetListingAsync(AlertMatcher.ListingPath(rule), null, default);
                }
                catch
                {
                    continue; // try again next round
                }

                var links = items.Where(p => p.Kind == ItemKind.Link).ToList();
                var since = rule.LastChecked;
                var newest = links.Count > 0 ? links.Max(p => p.Created.ToUnixTimeSeconds()) : since;
                // First check of a new rule: show what already matches in the Alerts feed, but
                // only notify about posts that arrive from now on.
                var first = since == 0;
                foreach (var post in links.Where(p => (first || p.Created.ToUnixTimeSeconds() > since) &&
                                                      AlertMatcher.Matches(rule, p.Title, p.SelfText)))
                {
                    if (services.Alerts.Add(rule.Name, post) && !first) found.Add((rule.Name, post));
                }
                // Remember the newest post seen (not the clock: Reddit can list a post a little after
                // its timestamp). A first check that found nothing starts from now.
                rule.LastChecked = Math.Max(since, first && links.Count == 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : newest);
            }
            services.Alerts.Save();
            services.Settings.Save();
        }
        finally
        {
            _running = false;
        }
        if (found.Count > 0) Matched?.Invoke(found);
    }
}

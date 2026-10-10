using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Readdit;

/// <summary>Remembers which posts have been opened, so feeds can dim or hide them.</summary>
public sealed class ReadTracker
{
    const int MaxEntries = 20_000;
    readonly string _file = Path.Combine(Paths.Local, "read.txt");
    readonly HashSet<string> _ids = [];

    public ReadTracker()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var lines = File.ReadAllLines(_file);
            var keep = lines.Length > MaxEntries ? lines[^MaxEntries..] : lines;
            _ids.UnionWith(keep);
            if (keep.Length != lines.Length) File.WriteAllLines(_file, keep);
        }
        catch
        {
            // Read history is best-effort.
        }
    }

    public bool Contains(string fullName) => _ids.Contains(fullName);

    public void Mark(string fullName)
    {
        if (!_ids.Add(fullName)) return;
        try
        {
            Directory.CreateDirectory(Paths.Local);
            File.AppendAllText(_file, fullName + "\n");
        }
        catch
        {
            // Best-effort.
        }
    }
}

/// <summary>
/// Saved threads kept on disk for offline reading: post + comments JSON, the post's media,
/// and an index of tags.
/// </summary>
public sealed partial class OfflineStore
{
    readonly string _dir = Path.Combine(Paths.Local, "offline");
    readonly Dictionary<string, Entry> _index;
    readonly Dictionary<string, string> _searchText = [];

    public sealed class Entry
    {
        public List<string> Tags { get; set; } = [];
        public long SavedAt { get; set; }
    }

    public OfflineStore()
    {
        try
        {
            _index = File.Exists(IndexFile)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(IndexFile)) ?? []
                : [];
        }
        catch
        {
            _index = [];
        }
    }

    /// <summary>Folder served to the post view as https://offline.readdit/…</summary>
    public string MediaRoot => Path.Combine(_dir, "media");

    string IndexFile => Path.Combine(_dir, "index.json");

    string? FileFor(string postId) =>
        SafeId().IsMatch(postId) ? Path.Combine(_dir, postId + ".json") : null;

    string? MediaDirFor(string postId) =>
        SafeId().IsMatch(postId) ? Path.Combine(MediaRoot, postId) : null;

    void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(IndexFile, JsonSerializer.Serialize(_index));
        }
        catch
        {
            // Best-effort.
        }
    }

    public bool Has(string postId) => FileFor(postId) is { } f && File.Exists(f);

    // ---- Tags ---------------------------------------------------------------------------------

    public IReadOnlyList<string> TagsFor(string postId) => _index.GetValueOrDefault(postId)?.Tags ?? [];

    public void SetTags(string postId, IEnumerable<string> tags)
    {
        if (!_index.TryGetValue(postId, out var entry))
            _index[postId] = entry = new Entry { SavedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        entry.Tags = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _searchText.Remove(postId); // cached search text includes tags
        SaveIndex();
    }

    public List<string> AllTags() =>
        _index.Values.SelectMany(e => e.Tags).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

    // ---- Media --------------------------------------------------------------------------------

    /// <summary>Downloads the post's images/video (and preview stills) next to the saved thread.</summary>
    public async Task SaveMediaAsync(string postId, string rawThread, CancellationToken ct = default)
    {
        if (MediaDirFor(postId) is not { } dir) return;
        List<string> urls;
        using (var doc = JsonDocument.Parse(rawThread))
        {
            var post = doc.RootElement[0].GetProperty("data").GetProperty("children")[0].GetProperty("data");
            urls = MediaExtractor.Extract(post).Select(m => m.Url).Concat(MediaExtractor.Stills(post)).Distinct().ToList();
        }
        if (urls.Count == 0) return;

        Directory.CreateDirectory(dir);
        var map = new Dictionary<string, string>();
        for (var i = 0; i < urls.Count; i++)
        {
            var file = await MediaDownloader.DownloadToAsync(urls[i], Path.Combine(dir, i.ToString()), ct);
            if (file != null) map[urls[i]] = Path.GetFileName(file);
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "map.json"), JsonSerializer.Serialize(map), ct);
    }

    /// <summary>Original media URL → local URL on <paramref name="host"/>, or null if nothing is stored.</summary>
    public Dictionary<string, string>? LocalMedia(string postId, string host)
    {
        try
        {
            if (MediaDirFor(postId) is not { } dir || !File.Exists(Path.Combine(dir, "map.json"))) return null;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "map.json")));
            return map?.ToDictionary(p => p.Key, p => $"https://{host}/{postId}/{Uri.EscapeDataString(p.Value)}");
        }
        catch
        {
            return null;
        }
    }

    // ---- Search -------------------------------------------------------------------------------

    /// <summary>Lower-cased title, text, community, author and every comment body, for searching.</summary>
    string SearchTextFor(string postId, JsonElement root)
    {
        if (_searchText.TryGetValue(postId, out var cached)) return cached;
        var text = new System.Text.StringBuilder();
        var post = root[0].GetProperty("data").GetProperty("children")[0].GetProperty("data");
        foreach (var field in new[] { "title", "selftext", "subreddit", "author", "domain" })
            text.Append(post.Str(field)).Append('\n');
        AppendComments(root[1], text);
        foreach (var tag in TagsFor(postId)) text.Append(tag).Append('\n');
        return _searchText[postId] = text.ToString().ToLowerInvariant();

        static void AppendComments(JsonElement listing, System.Text.StringBuilder sb)
        {
            if (listing.ValueKind != JsonValueKind.Object || !listing.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("children", out var children))
                return;
            foreach (var child in children.EnumerateArray())
            {
                if (child.Str("kind") != "t1") continue;
                var d = child.GetProperty("data");
                sb.Append(d.Str("author")).Append(' ').Append(d.Str("body")).Append('\n');
                if (d.TryGetProperty("replies", out var replies)) AppendComments(replies, sb);
            }
        }
    }

    public string? Load(string postId)
    {
        try
        {
            return FileFor(postId) is { } f && File.Exists(f) ? File.ReadAllText(f) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string postId, string rawThread)
    {
        if (FileFor(postId) is not { } f) return;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(f, rawThread);
        if (!_index.ContainsKey(postId))
        {
            _index[postId] = new Entry { SavedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            SaveIndex();
        }
        _searchText.Remove(postId);
    }

    public void Remove(string postId)
    {
        // Index first, so a locked media folder can't leave a stale entry behind.
        if (_index.Remove(postId)) SaveIndex();
        _searchText.Remove(postId);
        if (FileFor(postId) is { } f && File.Exists(f)) File.Delete(f);
        if (MediaDirFor(postId) is { } dir && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Saved posts, newest first, optionally filtered by search words and a tag.</summary>
    public List<Post> List(string query = "", string? tag = null)
    {
        var posts = new List<Post>();
        if (!Directory.Exists(_dir)) return posts;
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var file in new DirectoryInfo(_dir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            var postId = Path.GetFileNameWithoutExtension(file.Name);
            if (!SafeId().IsMatch(postId)) continue;
            if (tag != null && !TagsFor(postId).Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file.FullName));
                if (words.Length > 0)
                {
                    var text = SearchTextFor(postId, doc.RootElement);
                    if (!words.All(w => text.Contains(w))) continue;
                }
                var data = doc.RootElement[0].GetProperty("data").GetProperty("children")[0].GetProperty("data");
                var post = Post.FromLink(data);
                post.LibraryTags = string.Join(", ", TagsFor(postId));
                posts.Add(post);
            }
            catch
            {
                // Skip unreadable entries.
            }
        }
        return posts;
    }

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex SafeId();
}

/// <summary>When each thread was last opened and how many comments it had then.</summary>
public sealed class VisitStore
{
    const int MaxEntries = 3000;
    readonly string _file = Path.Combine(Paths.Local, "visits.json");
    readonly Dictionary<string, Visit> _visits;

    public sealed record Visit(long At, long CommentCount);

    public VisitStore()
    {
        try
        {
            _visits = File.Exists(_file)
                ? JsonSerializer.Deserialize<Dictionary<string, Visit>>(File.ReadAllText(_file)) ?? []
                : [];
        }
        catch
        {
            _visits = [];
        }
    }

    public Visit? Get(string postId) => _visits.GetValueOrDefault(postId);

    public void Record(string postId, long commentCount)
    {
        _visits[postId] = new Visit(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), commentCount);
        if (_visits.Count > MaxEntries)
            foreach (var old in _visits.OrderBy(v => v.Value.At).Take(_visits.Count - MaxEntries).ToList())
                _visits.Remove(old.Key);
        try
        {
            Directory.CreateDirectory(Paths.Local);
            File.WriteAllText(_file, JsonSerializer.Serialize(_visits));
        }
        catch
        {
            // Best-effort.
        }
    }
}

/// <summary>Diagnostic log of write requests (no credentials), for debugging Reddit's responses.</summary>
static class ApiLog
{
    static readonly string FilePath = Path.Combine(Paths.Local, "api.log");

    public static void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(Paths.Local);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 512 * 1024) info.Delete();
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
        }
        catch
        {
            // Diagnostics only.
        }
    }

    public static string Clip(string body)
    {
        var flat = body.ReplaceLineEndings(" ");
        return flat.Length > 300 ? flat[..300] + "…" : flat;
    }
}

/// <summary>Everything feeds and the detail view share.</summary>
public sealed class Services
{
    public Services(Microsoft.Web.WebView2.Wpf.WebView2 sessionView)
    {
        Settings = AppSettings.Load();
        Session = new RedditSession(sessionView, Settings.ActiveProfile);
        Api = new RedditApi(Session);
    }

    public RedditSession Session { get; }
    public RedditApi Api { get; }
    public AppSettings Settings { get; }
    public ReadTracker Read { get; } = new();
    public OfflineStore Offline { get; } = new();
    public VisitStore Visits { get; } = new();

    public bool IsMuted(string user) =>
        Settings.MutedUsers.Contains(user, StringComparer.OrdinalIgnoreCase);

    public bool IsHighlighted(string user) =>
        Settings.HighlightedUsers.Contains(user, StringComparer.OrdinalIgnoreCase);
}

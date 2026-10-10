using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Skein;

/// <summary>Remembers which posts have been opened, so feeds can dim or hide them.</summary>
public sealed class ReadTracker
{
    const int MaxEntries = 20_000;
    readonly string _file;
    readonly HashSet<string> _ids = [];

    public ReadTracker() : this(Path.Combine(Paths.Local, "read.txt")) { }

    /// <summary>Uses <paramref name="file"/> for storage (tests use a temporary one).</summary>
    public ReadTracker(string file)
    {
        _file = file;
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
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
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
    readonly string _dir;
    readonly Dictionary<string, Entry> _index;
    readonly Dictionary<string, string> _searchText = [];

    public sealed class Entry
    {
        public List<string> Tags { get; set; } = [];
        public long SavedAt { get; set; }
    }

    public OfflineStore() : this(Path.Combine(Paths.Local, "offline")) { }

    /// <summary>Uses <paramref name="dir"/> for storage (tests use a temporary one).</summary>
    public OfflineStore(string dir)
    {
        _dir = dir;
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

    /// <summary>Folder served to the post view as https://offline.skein/…</summary>
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

    // One background media download per post; re-saving or unsaving cancels the previous one so
    // two downloads never race in the same folder (or write into a folder that's being deleted).
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _mediaJobs = new();

    // Syncing a whole saved list starts hundreds of these; a few at a time is plenty.
    readonly SemaphoreSlim _mediaSlots = new(3);

    public void StartMediaDownload(string postId, string rawThread)
    {
        CancelMediaDownload(postId);
        var cts = new CancellationTokenSource();
        _mediaJobs[postId] = cts;
        _ = Task.Run(async () =>
        {
            var slot = false;
            try
            {
                await _mediaSlots.WaitAsync(cts.Token);
                slot = true;
                await SaveMediaAsync(postId, rawThread, cts.Token);
            }
            catch
            {
                // Text and comments are already saved; media is a bonus.
            }
            finally
            {
                if (slot) _mediaSlots.Release();
                _mediaJobs.TryRemove(new KeyValuePair<string, CancellationTokenSource>(postId, cts));
            }
        });
    }

    void CancelMediaDownload(string postId)
    {
        if (_mediaJobs.TryRemove(postId, out var running)) running.Cancel();
    }

    /// <summary>Downloads the post's images/video (and preview stills) next to the saved thread.</summary>
    async Task SaveMediaAsync(string postId, string rawThread, CancellationToken ct)
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
        ct.ThrowIfCancellationRequested(); // unsaved meanwhile: don't recreate its map
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
        CancelMediaDownload(postId);
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
    readonly string _file;
    readonly Dictionary<string, Visit> _visits;

    public sealed record Visit(long At, long CommentCount);

    public VisitStore() : this(Path.Combine(Paths.Local, "visits.json")) { }

    /// <summary>Uses <paramref name="file"/> for storage (tests use a temporary one).</summary>
    public VisitStore(string file)
    {
        _file = file;
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
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_visits));
        }
        catch
        {
            // Best-effort.
        }
    }
}

/// <summary>
/// The last subscription list per account, so the sidebar fills instantly at launch instead of
/// waiting for Reddit (thousands of subscriptions take a while to page through).
/// </summary>
public sealed class SubscriptionCache(string dir)
{
    public SubscriptionCache() : this(Paths.Local) { }

    string FileFor(string profile) =>
        Path.Combine(dir, profile.Length == 0 ? "subscriptions.json" : $"subscriptions-{SafeName(profile)}.json");

    static string SafeName(string profile) => string.Concat(profile.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    public List<SubredditRef> Load(string profile)
    {
        try
        {
            var file = FileFor(profile);
            return File.Exists(file) ? JsonSerializer.Deserialize<List<SubredditRef>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(string profile, List<SubredditRef> subscriptions)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(FileFor(profile), JsonSerializer.Serialize(subscriptions));
        }
        catch
        {
            // A cache; the next launch just waits for Reddit.
        }
    }
}

/// <summary>How often each community has been opened, for the "most visited" sidebar order.</summary>
public sealed class CommunityVisits
{
    readonly string _file;
    readonly Dictionary<string, int> _counts;

    public CommunityVisits() : this(Path.Combine(Paths.Local, "community-visits.json")) { }

    /// <summary>Uses <paramref name="file"/> for storage (tests use a temporary one).</summary>
    public CommunityVisits(string file)
    {
        _file = file;
        try
        {
            var loaded = File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_file)) : null;
            _counts = new Dictionary<string, int>(loaded ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _counts = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public int CountFor(string subreddit) => _counts.GetValueOrDefault(subreddit);

    public void Record(string subreddit)
    {
        _counts[subreddit] = CountFor(subreddit) + 1;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_counts));
        }
        catch
        {
            // Best-effort.
        }
    }
}

/// <summary>An unfinished new post, kept so closing the window (or Skein) doesn't lose it.</summary>
public sealed record PostDraft(
    string Subreddit, string Title, string Kind, string Body, bool Nsfw, bool Spoiler, List<PostDraft.MediaFile> Media)
{
    public sealed record MediaFile(string Path, string Caption);

    /// <summary>Nothing worth keeping (the community alone doesn't count).</summary>
    public bool IsEmpty => Title.Trim().Length == 0 && Body.Trim().Length == 0 && Media.Count == 0;
}

/// <summary>Keeps the one draft post on disk.</summary>
public sealed class PostDraftStore(string file)
{
    public PostDraftStore() : this(Path.Combine(Paths.Local, "post-draft.json")) { }

    public PostDraft? Load()
    {
        try
        {
            var draft = File.Exists(file) ? JsonSerializer.Deserialize<PostDraft>(File.ReadAllText(file)) : null;
            if (draft == null || draft.IsEmpty) return null;
            // Files that have since been moved or deleted can't be posted.
            return draft with { Media = draft.Media.Where(m => File.Exists(m.Path)).ToList() };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Saves the draft, or removes the file when there's nothing worth keeping.</summary>
    public void Save(PostDraft draft)
    {
        try
        {
            if (draft.IsEmpty)
            {
                Clear();
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(draft));
        }
        catch
        {
            // Best-effort.
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // Best-effort.
        }
    }
}

/// <summary>Your private notes and tags on Reddit users (kept on this PC only).</summary>
public sealed class UserNotes
{
    readonly string _file;
    readonly Dictionary<string, UserNote> _notes;

    public sealed record UserNote(string Tag, string Note);

    public UserNotes() : this(Path.Combine(Paths.Local, "user-notes.json")) { }

    /// <summary>Uses <paramref name="file"/> for storage (tests use a temporary one).</summary>
    public UserNotes(string file)
    {
        _file = file;
        try
        {
            var loaded = File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, UserNote>>(File.ReadAllText(_file)) : null;
            _notes = new Dictionary<string, UserNote>(loaded ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _notes = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyDictionary<string, UserNote> All => _notes;

    public UserNote? Get(string user) => _notes.GetValueOrDefault(user);

    /// <summary>Replaces every note (from another PC, through sync).</summary>
    public void ReplaceAll(IReadOnlyDictionary<string, UserNote> notes)
    {
        _notes.Clear();
        foreach (var (user, note) in notes) _notes[user] = note;
        Save();
    }

    /// <summary>Sets a user's tag and note; both empty removes the entry.</summary>
    public void Set(string user, string tag, string note)
    {
        tag = tag.Trim();
        note = note.Trim();
        if (user.Length == 0) return;
        if (tag.Length == 0 && note.Length == 0) _notes.Remove(user);
        else _notes[user] = new UserNote(tag.Length > 40 ? tag[..40] : tag, note);
        Save();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_notes));
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
        Translator = new Translator(Settings);
    }

    public Translator Translator { get; }

    public RedditSession Session { get; }
    public RedditApi Api { get; }
    public AppSettings Settings { get; }
    public ReadTracker Read { get; } = new();
    public OfflineStore Offline { get; } = new();
    public VisitStore Visits { get; } = new();
    public SubscriptionCache SubscriptionCache { get; } = new();
    public CommunityVisits CommunityVisits { get; } = new();
    public AlertStore Alerts { get; } = new();
    public UserNotes UserNotes { get; } = new();

    /// <summary>Communities you moderate (loaded after sign-in).</summary>
    public HashSet<string> Moderated { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsModeratorOf(string subreddit) => Moderated.Contains(subreddit);

    public bool IsMuted(string user) =>
        Settings.MutedUsers.Contains(user, StringComparer.OrdinalIgnoreCase);

    public bool IsHighlighted(string user) =>
        Settings.HighlightedUsers.Contains(user, StringComparer.OrdinalIgnoreCase);
}

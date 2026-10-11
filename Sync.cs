using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Skein;

/// <summary>The shared sync file: each section with when and where it last changed.</summary>
public sealed class SyncDocument
{
    public int Version { get; set; } = 1;
    public Dictionary<string, SyncEntry> Sections { get; set; } = [];
}

public sealed class SyncEntry
{
    /// <summary>When this section was last changed (unix milliseconds).</summary>
    public long Updated { get; set; }

    /// <summary>The PC that changed it.</summary>
    public string By { get; set; } = "";

    public JsonNode? Data { get; set; }
}

/// <summary>What one sync round does: sections to take from the file, and sections to write to it.</summary>
public sealed record SyncPlan(IReadOnlyList<string> Apply, IReadOnlyList<string> Push);

/// <summary>
/// Decides, section by section, which way to sync. A section changed here since the last sync is
/// written to the file (the newest edit wins); otherwise a newer copy in the file is taken.
/// </summary>
public static class SyncEngine
{
    public static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    /// <param name="local">Each section's current JSON on this PC.</param>
    /// <param name="syncedHashes">Each section's hash when it was last synced on this PC.</param>
    /// <param name="stamps">The file's "updated" time for each section as of the last sync.</param>
    public static SyncPlan Plan(IReadOnlyDictionary<string, string> local, IReadOnlyDictionary<string, string> syncedHashes,
        IReadOnlyDictionary<string, long> stamps, SyncDocument? remote)
    {
        var apply = new List<string>();
        var push = new List<string>();
        foreach (var (section, json) in local)
        {
            var changedHere = !syncedHashes.TryGetValue(section, out var synced) || synced != Hash(json);
            var entry = remote?.Sections.GetValueOrDefault(section);
            var newerThere = entry != null && entry.Updated > stamps.GetValueOrDefault(section);
            if (changedHere) push.Add(section);
            else if (newerThere) apply.Add(section);
            else if (entry == null) push.Add(section); // missing from the file (first sync, or it was deleted)
        }
        return new SyncPlan(apply, push);
    }
}

/// <summary>
/// Keeps folders, alerts, reminders, saved replies, user notes, filters and appearance in step between PCs through a file
/// in a synced folder (OneDrive, by default).
/// </summary>
public sealed class SettingsSync(AppSettings settings, UserNotes notes, Action? save = null)
{
    public const string FileName = "skein-sync.json";

    /// <summary>Saves the settings (tests pass their own, so they never touch the real settings file).</summary>
    readonly Action _save = save ?? settings.Save;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>OneDrive\Skein, if OneDrive is set up on this PC.</summary>
    public static string? DefaultFolder()
    {
        var oneDrive = new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) && Directory.Exists(p));
        return oneDrive == null ? null : Path.Combine(oneDrive, "Skein");
    }

    public string FilePath => Path.Combine(settings.SyncFolder, FileName);

    public bool FileExists => settings.SyncFolder.Length > 0 && File.Exists(FilePath);

    // ---- What's synced ------------------------------------------------------------------------

    /// <summary>Each section as JSON. Things that change by themselves (when alerts last ran) are left out.</summary>
    public Dictionary<string, string> Collect() => new()
    {
        ["folders"] = JsonSerializer.Serialize(settings.SubscriptionFolders),
        ["searches"] = JsonSerializer.Serialize(settings.SavedSearches),
        ["reminders"] = JsonSerializer.Serialize(settings.Reminders.OrderBy(r => r.Id, StringComparer.Ordinal)),
        ["replies"] = JsonSerializer.Serialize(settings.SavedReplies),
        ["sorts"] = JsonSerializer.Serialize(settings.CommunitySorts.OrderBy(s => s.Key, StringComparer.Ordinal).ToDictionary(s => s.Key, s => s.Value)),
        ["watching"] = JsonSerializer.Serialize(new
        {
            threads = settings.WatchedThreads.Select(w => new { w.PostId, w.Title, w.Subreddit }),
            users = settings.FollowedUsers.Select(f => f.Name),
        }),
        ["alerts"] = JsonSerializer.Serialize(new
        {
            interval = settings.AlertIntervalMinutes,
            rules = settings.AlertRules.Select(r => new { r.Name, r.Keywords, r.Subreddits, r.SearchText, r.Enabled }),
        }),
        ["notes"] = JsonSerializer.Serialize(notes.All.OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(n => n.Key, n => n.Value)),
        ["filters"] = JsonSerializer.Serialize(new
        {
            settings.KeywordFilters,
            settings.DomainFilters,
            settings.SubredditFilters,
            settings.MutedUsers,
            settings.HighlightedUsers,
            settings.MinScore,
            settings.CollapseBelowScore,
            settings.HideNsfw,
            settings.BlurNsfw,
            settings.HideRead,
        }),
        ["appearance"] = JsonSerializer.Serialize(new
        {
            settings.Theme,
            settings.Accent,
            settings.LayoutMode,
            settings.ReadingFontSize,
            settings.ReadingFont,
            settings.CustomCss,
        }),
    };

    void Apply(string section, JsonNode data)
    {
        switch (section)
        {
            case "folders":
                settings.SubscriptionFolders = data.Deserialize<List<SubscriptionFolder>>() ?? [];
                break;
            case "watching":
                // Keep this PC's "seen up to" for threads and people that are still there.
                var oldThreads = settings.WatchedThreads;
                settings.WatchedThreads = (data["threads"]?.Deserialize<List<WatchedThread>>() ?? []).Select(w =>
                {
                    // -1: not counted on this PC yet (the next check counts, without announcing).
                    w.SeenComments = oldThreads.FirstOrDefault(o => o.PostId == w.PostId)?.SeenComments ?? -1;
                    return w;
                }).ToList();
                var oldUsers = settings.FollowedUsers;
                settings.FollowedUsers = (data["users"]?.Deserialize<List<string>>() ?? []).Select(name => new FollowedUser
                {
                    Name = name,
                    LastChecked = oldUsers.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.LastChecked ?? 0,
                }).ToList();
                break;
            case "searches":
                settings.SavedSearches = data.Deserialize<List<SavedSearch>>() ?? [];
                break;
            case "reminders":
                settings.Reminders = data.Deserialize<List<Reminder>>() ?? [];
                break;
            case "sorts":
                settings.CommunitySorts = data.Deserialize<Dictionary<string, string>>() ?? [];
                break;
            case "replies":
                settings.SavedReplies = data.Deserialize<List<SavedReply>>() ?? [];
                break;
            case "alerts":
                settings.AlertIntervalMinutes = data["interval"]?.GetValue<int>() ?? settings.AlertIntervalMinutes;
                var old = settings.AlertRules;
                settings.AlertRules = (data["rules"]?.Deserialize<List<AlertRule>>() ?? []).Select(r =>
                {
                    // Keep this PC's "checked up to" for rules that still watch the same thing.
                    r.LastChecked = old.FirstOrDefault(o => o.Keywords == r.Keywords && o.Subreddits == r.Subreddits &&
                                                            o.SearchText == r.SearchText)?.LastChecked ?? 0;
                    return r;
                }).ToList();
                break;
            case "notes":
                notes.ReplaceAll(data.Deserialize<Dictionary<string, UserNotes.UserNote>>() ?? []);
                break;
            case "filters":
                settings.KeywordFilters = data["KeywordFilters"]?.Deserialize<List<string>>() ?? [];
                settings.DomainFilters = data["DomainFilters"]?.Deserialize<List<string>>() ?? [];
                settings.SubredditFilters = data["SubredditFilters"]?.Deserialize<List<string>>() ?? [];
                settings.MutedUsers = data["MutedUsers"]?.Deserialize<List<string>>() ?? [];
                settings.HighlightedUsers = data["HighlightedUsers"]?.Deserialize<List<string>>() ?? [];
                settings.MinScore = data["MinScore"]?.GetValue<int>();
                settings.CollapseBelowScore = data["CollapseBelowScore"]?.GetValue<int>();
                settings.HideNsfw = data["HideNsfw"]?.GetValue<bool>() ?? settings.HideNsfw;
                settings.BlurNsfw = data["BlurNsfw"]?.GetValue<bool>() ?? settings.BlurNsfw;
                settings.HideRead = data["HideRead"]?.GetValue<bool>() ?? settings.HideRead;
                break;
            case "appearance":
                settings.Theme = data["Theme"]?.GetValue<string>() ?? settings.Theme;
                settings.Accent = data["Accent"]?.GetValue<string>() ?? settings.Accent;
                settings.LayoutMode = data["LayoutMode"]?.GetValue<string>() ?? settings.LayoutMode;
                settings.ReadingFontSize = data["ReadingFontSize"]?.GetValue<int>() ?? settings.ReadingFontSize;
                settings.ReadingFont = data["ReadingFont"]?.GetValue<string>() ?? settings.ReadingFont;
                settings.CustomCss = data["CustomCss"]?.GetValue<string>() ?? settings.CustomCss;
                break;
        }
    }

    // ---- Syncing ------------------------------------------------------------------------------

    /// <summary>When the last round finished, and what went wrong if it didn't.</summary>
    public DateTime? LastSynced { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// One round: takes newer sections from the file and writes this PC's changes to it.
    /// Returns the sections that changed here (so the app can refresh them), or null if the file
    /// couldn't be used this time (it's tried again next round).
    /// </summary>
    public IReadOnlyList<string>? RunOnce()
    {
        if (!settings.SyncEnabled || settings.SyncFolder.Length == 0) return [];
        try
        {
            Directory.CreateDirectory(settings.SyncFolder);
            var remote = ReadFile();
            var local = Collect();
            var plan = SyncEngine.Plan(local, settings.SyncHashes, settings.SyncStamps, remote);

            foreach (var section in plan.Apply)
            {
                var entry = remote!.Sections[section];
                if (entry.Data != null) Apply(section, entry.Data);
                settings.SyncStamps[section] = entry.Updated;
            }
            if (plan.Apply.Count > 0) local = Collect();
            foreach (var section in plan.Apply) settings.SyncHashes[section] = SyncEngine.Hash(local[section]);

            if (plan.Push.Count > 0)
            {
                var doc = remote ?? new SyncDocument();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var section in plan.Push)
                {
                    doc.Sections[section] = new SyncEntry { Updated = now, By = Environment.MachineName, Data = JsonNode.Parse(local[section]) };
                    settings.SyncStamps[section] = now;
                    settings.SyncHashes[section] = SyncEngine.Hash(local[section]);
                }
                WriteFile(doc);
            }
            if (plan.Apply.Count > 0 || plan.Push.Count > 0) _save();
            LastSynced = DateTime.Now;
            LastError = null;
            return plan.Apply;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // OneDrive can hold the file briefly while it uploads; the next round tries again.
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>Starting over: forget what was synced, so the next round takes the file's copy (or writes this PC's).</summary>
    public void Reset(bool preferFile)
    {
        settings.SyncStamps.Clear();
        settings.SyncHashes.Clear();
        if (preferFile)
        {
            // Everything here counts as "already synced", so the file's sections win.
            foreach (var (section, json) in Collect()) settings.SyncHashes[section] = SyncEngine.Hash(json);
        }
        _save();
    }

    SyncDocument? ReadFile()
    {
        if (!File.Exists(FilePath)) return null;
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<SyncDocument>(stream);
    }

    void WriteFile(SyncDocument doc)
    {
        // Write beside it, then swap, so OneDrive never uploads half a file.
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(doc, Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}

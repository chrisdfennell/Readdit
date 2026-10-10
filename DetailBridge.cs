using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Readdit;

/// <summary>
/// Drives the post/comments view, an HTML page (wwwroot/detail.html) rendered in a WebView2.
/// The page renders Reddit's JSON itself; it asks this class to perform anything that needs
/// the user's session (votes, replies, loading more comments) through web messages.
/// </summary>
public sealed class DetailBridge(WebView2 view, Services services)
{
    const string AppHost = "readdit.app";

    static readonly TimeSpan PrefetchLifetime = TimeSpan.FromMinutes(3);
    const int PrefetchCapacity = 8;

    readonly List<string> _queue = [];
    readonly Dictionary<string, (string Raw, DateTime At)> _prefetched = [];
    readonly HashSet<string> _prefetching = [];
    bool _ready;
    int _version;
    string? _postId;
    string? _focusId;
    string _commentSort = "confidence";

    public event Action<string>? OpenUrlRequested;      // route in-app when it's a Reddit link
    public event Action<string>? ExternalUrlRequested;  // always the default browser
    public event Action<string, bool, bool>? KeyPressed;          // key, ctrl, shift
    public event Action<string, bool?, bool?>? PostStateChanged;  // fullname, likes (if voted), saved (if saved)
    public event Action<string>? StatusMessage;

    CoreWebView2 Core => view.CoreWebView2;

    /// <param name="options">The active account's profile, so embeds and media use that account.</param>
    public async Task InitializeAsync(CoreWebView2Environment environment, CoreWebView2ControllerOptions options)
    {
        await view.EnsureCoreWebView2Async(environment, options);
        Core.SetVirtualHostNameToFolderMapping(AppHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            CoreWebView2HostResourceAccessKind.DenyCors);
        Core.Settings.AreDevToolsEnabled = true;
        Core.Settings.IsStatusBarEnabled = false;
        // F5 / Ctrl+R would reload our page; the page forwards those keys to the app instead.
        Core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        Core.WebMessageReceived += OnWebMessage;

        // The page only ever shows our own document; any other navigation becomes an "open".
        Core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Host == AppHost) return;
            e.Cancel = true;
            OpenUrlRequested?.Invoke(e.Uri);
        };
        Core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenUrlRequested?.Invoke(e.Uri);
        };

        // Offline library media is served from disk under its own host.
        Directory.CreateDirectory(services.Offline.MediaRoot);
        Core.SetVirtualHostNameToFolderMapping(OfflineHost, services.Offline.MediaRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        Core.Navigate($"https://{AppHost}/detail.html");
    }

    const string OfflineHost = "offline.readdit";

    string LocalMediaJson(string postId) =>
        JsonSerializer.Serialize(services.Offline.LocalMedia(postId, OfflineHost));

    // ---- Host → page ----------------------------------------------------------------------

    void Send(string json)
    {
        if (_ready) Core.PostWebMessageAsJson(json);
        else _queue.Add(json);
    }

    string SettingsJson => JsonSerializer.Serialize(new
    {
        blurNsfw = services.Settings.BlurNsfw,
        me = services.Api.UserName,
        collapseBelow = services.Settings.CollapseBelowScore,
        muted = services.Settings.MutedUsers,
        highlighted = services.Settings.HighlightedUsers,
        modOf = services.Moderated,
        // Lower-cased names → your tag and note on that user.
        notes = services.UserNotes.All.ToDictionary(n => n.Key.ToLowerInvariant(), n => new { tag = n.Value.Tag, note = n.Value.Note }),
    });

    /// <summary>Your note on a user changed from the post view (list rows update their tags).</summary>
    public event Action<string>? UserNoteChanged;

    /// <summary>Sends fresh settings (user notes, mutes…) to the page without re-rendering.</summary>
    public void RefreshSettings() => Send($$"""{"type":"settings","settings":{{SettingsJson}}}""");

    /// <summary>A moderator action succeeded (fullname, action), so lists can update.</summary>
    public event Action<string, string>? ModActionDone;

    /// <summary>Raised when a thread is opened, so lists can clear their "+N new" badge.</summary>
    public event Action<string>? ThreadVisited;

    // The visit before this one, kept while the same thread is re-rendered (e.g. re-sorted).
    string? _visitPostId;
    long? _previousVisitAt;

    /// <param name="record">
    /// False for partial or stale views (a single focused comment thread, an offline copy):
    /// they show the badges but mustn't count as having seen the whole thread.
    /// </param>
    long? TakeVisit(string postId, string raw, bool record)
    {
        if (!record) return services.Visits.Get(postId)?.At;
        if (_visitPostId == postId) return _previousVisitAt;
        _visitPostId = postId;
        _previousVisitAt = services.Visits.Get(postId)?.At;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var post = doc.RootElement[0].GetProperty("data").GetProperty("children")[0].GetProperty("data");
            services.Visits.Record(postId, post.Long("num_comments"));
            ThreadVisited?.Invoke(postId);
        }
        catch
        {
            // Visit tracking is best-effort.
        }
        return _previousVisitAt;
    }

    public void Clear()
    {
        _version++;
        _postId = null;
        Send("""{"type":"clear"}""");
    }

    public async Task ShowAsync(Post? item)
    {
        if (item == null)
        {
            Clear();
            return;
        }

        if (item.Kind == ItemKind.Message || item.Id.Length == 0)
        {
            _version++;
            _postId = null;
            Send($$"""{"type":"message","settings":{{SettingsJson}},"data":{{item.RawJson}}}""");
            return;
        }

        // Show the post right away from the listing; comments follow when they arrive.
        if (item.Kind == ItemKind.Link)
            Send($$"""{"type":"post","settings":{{SettingsJson}},"localMedia":null,"data":{{item.RawJson}}}""");
        else
            Send("""{"type":"loading"}""");

        await ShowThreadAsync(item.Id, item.CommentId);
    }

    /// <summary>
    /// Opens a thread from a link: clears the current post first, so a failure isn't shown
    /// under the previous post's header.
    /// </summary>
    public Task OpenThreadAsync(string postId, string? focusCommentId)
    {
        Send("""{"type":"loading"}""");
        return ShowThreadAsync(postId, focusCommentId);
    }

    public async Task ShowThreadAsync(string postId, string? focusCommentId)
    {
        var version = ++_version;
        _postId = postId;
        _focusId = focusCommentId;

        string raw;
        var offline = false;
        try
        {
            raw = TakePrefetched(postId, focusCommentId)
                  ?? await services.Api.GetThreadRawAsync(postId, _commentSort, focusCommentId, default);
        }
        catch (Exception ex)
        {
            if (services.Offline.Load(postId) is { } cached && focusCommentId == null)
            {
                raw = cached;
                offline = true;
            }
            else
            {
                if (version == _version)
                    Send($$"""{"type":"error","message":{{JsonSerializer.Serialize(ex.Message)}}}""");
                return;
            }
        }

        if (version != _version) return;
        var lastVisit = TakeVisit(postId, raw, record: !offline && focusCommentId == null);
        // Offline copies use their saved media. Online, always use Reddit's: the saved video has
        // no sound track, so using it would silence a video that plays fine online.
        var localMedia = offline ? LocalMediaJson(postId) : "null";
        Send($$"""
            {"type":"thread","sort":{{JsonSerializer.Serialize(_commentSort)}},
             "focus":{{JsonSerializer.Serialize(focusCommentId)}},"offline":{{(offline ? "true" : "false")}},
             "lastVisit":{{(lastVisit?.ToString() ?? "null")}},"localMedia":{{localMedia}},
             "settings":{{SettingsJson}},"data":{{raw}}}
            """);
    }

    /// <summary>Theme (System/Light/Dark/Black) and accent colour for the post view.</summary>
    public void SetTheme(string theme, string accent)
    {
        Appearance.ApplyToWebView(view.CoreWebView2, theme);
        Send(JsonSerializer.Serialize(new { type = "theme", theme = theme.ToLowerInvariant(), accent }));
    }

    /// <summary>Opens the find-in-thread bar.</summary>
    public void OpenFind() => Send("""{"type":"find"}""");

    /// <summary>Jumps to the next comment posted since the last visit.</summary>
    public void NextNewComment() => Send("""{"type":"nextNew"}""");

    /// <summary>Jumps to the next comment by the original poster.</summary>
    public void NextOpComment() => Send("""{"type":"nextOp"}""");

    /// <summary>Opens the picture viewer on the current post (or the next one with pictures).</summary>
    public void OpenViewer() => Send("""{"type":"viewer"}""");

    /// <summary>
    /// The viewer asks for the next (1) or previous (-1) post with pictures; the handler selects
    /// it and says whether there was one.
    /// </summary>
    public Func<int, Task<bool>>? MediaStepHandler { get; set; }

    /// <summary>The viewer asks for full screen on (true), off (false) or toggled (null).</summary>
    public event Action<bool?>? FullscreenRequested;

    /// <summary>Tells the page whether the window is full screen (for the viewer's ⛶ button).</summary>
    public void SetFullscreenState(bool on) => Send($$"""{"type":"fullscreenState","on":{{(on ? "true" : "false")}}}""");

    /// <summary>Asks the page to download the media of the post it's showing.</summary>
    public void RequestDownload() => Send("""{"type":"download"}""");

    // ---- Prefetch: load the next post's comments in the background so j feels instant -----------

    string PrefetchKey(string postId) => postId + "|" + _commentSort;

    string? TakePrefetched(string postId, string? focusCommentId)
    {
        if (focusCommentId != null) return null;
        var key = PrefetchKey(postId);
        if (!_prefetched.Remove(key, out var entry)) return null;
        return DateTime.UtcNow - entry.At < PrefetchLifetime ? entry.Raw : null;
    }

    /// <summary>Drops a prefetched thread after its vote/save state changed from the list.</summary>
    public void InvalidatePrefetch(string postId)
    {
        foreach (var key in _prefetched.Keys.Where(k => k.StartsWith(postId + "|")).ToList()) _prefetched.Remove(key);
    }

    public async Task PrefetchAsync(Post? post)
    {
        if (post == null || post.Kind != ItemKind.Link || post.Id.Length == 0) return;
        var key = PrefetchKey(post.Id);
        if (_prefetched.ContainsKey(key) || !_prefetching.Add(key)) return;
        try
        {
            var raw = await services.Api.GetThreadRawAsync(post.Id, _commentSort, null, default);
            if (_prefetched.Count >= PrefetchCapacity)
                _prefetched.Remove(_prefetched.MinBy(p => p.Value.At).Key);
            _prefetched[key] = (raw, DateTime.UtcNow);
        }
        catch
        {
            // Prefetch is opportunistic; the real load will report errors.
        }
        finally
        {
            _prefetching.Remove(key);
        }
    }

    // ---- Display -------------------------------------------------------------------------------

    /// <summary>Scroll the page by a screen (positive) or back (negative).</summary>
    public void Scroll(int direction) => Send($$"""{"type":"scroll","direction":{{direction}}}""");

    /// <summary>Jump to the next (1) or previous (-1) top-level comment.</summary>
    public void NavigateComments(int direction) => Send($$"""{"type":"navComments","direction":{{direction}}}""");

    public void SetDisplay(int fontSize, bool readingMode) =>
        Send($$"""{"type":"display","fontSize":{{fontSize}},"reading":{{(readingMode ? "true" : "false")}}}""");

    /// <summary>Keeps the page's vote/save buttons in step with changes made from the post list.</summary>
    public void SyncPostState(Post post) =>
        Send(JsonSerializer.Serialize(new
        {
            type = "postState",
            id = post.FullName,
            likes = post.Likes,
            score = post.Score,
            saved = post.Saved,
        }));

    // ---- Page → host ----------------------------------------------------------------------

    async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(e.WebMessageAsJson);
        }
        catch
        {
            return;
        }

        using (doc)
        {
            var m = doc.RootElement;
            var reqId = m.Long("reqId");
            try
            {
                var data = await HandleAsync(m.Str("type"), m);
                if (reqId > 0) Send($$"""{"type":"reply","reqId":{{reqId}},"ok":true,"data":{{data ?? "null"}}}""");
            }
            catch (Exception ex)
            {
                if (reqId > 0)
                    Send($$"""{"type":"reply","reqId":{{reqId}},"ok":false,"error":{{JsonSerializer.Serialize(ex.Message)}}}""");
                else
                    StatusMessage?.Invoke(ex.Message);
            }
        }
    }

    /// <summary>Handles one request from the page; returns raw JSON to hand back, if any.</summary>
    async Task<string?> HandleAsync(string type, JsonElement m)
    {
        switch (type)
        {
            case "ready":
                _ready = true;
                foreach (var json in _queue) Core.PostWebMessageAsJson(json);
                _queue.Clear();
                return null;

            case "open":
                OpenUrlRequested?.Invoke(m.Str("url"));
                return null;

            case "external":
                ExternalUrlRequested?.Invoke(m.Str("url"));
                return null;

            case "key":
                KeyPressed?.Invoke(m.Str("key"), m.Bool("ctrl"), m.Bool("shift"));
                return null;

            case "vote":
                {
                    var id = m.Str("id");
                    var dir = (int)m.Long("dir");
                    await services.Api.VoteAsync(id, dir);
                    PostStateChanged?.Invoke(id, dir switch { 1 => true, -1 => false, _ => null }, null);
                    return null;
                }

            case "save":
                {
                    var id = m.Str("id");
                    var save = m.Bool("saved");
                    await services.Api.SaveAsync(id, save);
                    if (id.StartsWith("t3_")) await UpdateOfflineCopyAsync(id[3..], save);
                    PostStateChanged?.Invoke(id, null, save);
                    return null;
                }

            case "comment":
                return await services.Api.CommentAsync(m.Str("parent"), m.Str("text"));

            case "edit":
                return await services.Api.EditAsync(m.Str("id"), m.Str("text"));

            case "delete":
                await services.Api.DeleteAsync(m.Str("id"));
                return null;

            case "mod":
                {
                    var action = m.Str("action");
                    var id = m.Str("id");
                    // Only in communities you moderate, and only the actions RedditApi allows.
                    if (!services.IsModeratorOf(m.Str("subreddit")))
                        throw new InvalidOperationException("You don't moderate that community.");
                    await services.Api.ModerateAsync(action, id);
                    ModActionDone?.Invoke(id, action);
                    return null;
                }

            case "ban":
                {
                    var subreddit = m.Str("subreddit");
                    if (!services.IsModeratorOf(subreddit)) throw new InvalidOperationException("You don't moderate that community.");
                    await services.Api.BanAsync(subreddit, m.Str("user"), (int)m.Long("days"), m.Str("reason"), m.Str("message"), m.Str("note"));
                    return null;
                }

            case "download":
                {
                    var folder = services.Settings.DownloadFolder;
                    var result = await MediaDownloader.DownloadPostAsync(m.GetProperty("post"), folder);
                    if (result.Files.Count > 0) StatusMessage?.Invoke($"Saved {result.Files.Count} file(s) to {folder}");
                    return JsonSerializer.Serialize(new { files = result.Files, note = result.Note });
                }

            case "reveal":
                {
                    // Only reveal files inside the download folder.
                    var path = Path.GetFullPath(m.Str("path"));
                    // Trailing separator so "…\Readdit-other\x" doesn't pass as inside "…\Readdit".
                    var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(services.Settings.DownloadFolder)) +
                                 Path.DirectorySeparatorChar;
                    if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                    return null;
                }

            case "more":
                {
                    var children = m.GetProperty("children").EnumerateArray().Select(c => c.GetString() ?? "");
                    return await services.Api.GetMoreChildrenRawAsync(m.Str("linkId"), children, _commentSort);
                }

            case "sort":
                _commentSort = m.Str("sort");
                if (_postId != null) await ShowThreadAsync(_postId, _focusId);
                return null;

            case "fullThread":
                if (_postId != null) await ShowThreadAsync(_postId, null);
                return null;

            case "userPref":
                {
                    // Mute / highlight a user from a comment's ⋯ menu.
                    var name = m.Str("name");
                    var list = m.Str("list") == "muted" ? services.Settings.MutedUsers : services.Settings.HighlightedUsers;
                    var existing = list.FirstOrDefault(u => u.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (m.Bool("on") && existing == null && name.Length > 0) list.Add(name);
                    else if (!m.Bool("on") && existing != null) list.Remove(existing);
                    services.Settings.Save();
                    return SettingsJson;
                }

            case "mediaStep":
                {
                    var found = MediaStepHandler != null && await MediaStepHandler(Math.Sign((int)m.Long("direction")));
                    return found ? "true" : "false";
                }

            case "fullscreen":
                FullscreenRequested?.Invoke(m.NullableBool("on"));
                return null;

            case "userNote":
                {
                    var name = m.Str("name");
                    if (name.Length == 0 || name.Length > 40) throw new InvalidOperationException("That isn't a username.");
                    services.UserNotes.Set(name, m.Str("tag"), m.Str("note"));
                    UserNoteChanged?.Invoke(name);
                    return SettingsJson;
                }

            case "userInfo":
                {
                    var user = await services.Api.GetUserAboutAsync(m.Str("name"), default);
                    return user == null
                        ? null
                        : JsonSerializer.Serialize(new
                        {
                            name = user.Name,
                            karma = user.Karma,
                            linkKarma = user.LinkKarma,
                            commentKarma = user.CommentKarma,
                            created = user.Created.ToUnixTimeSeconds(),
                            icon = user.Icon,
                            suspended = user.Suspended,
                        });
                }

            default:
                return null;
        }
    }

    public async Task UpdateOfflineCopyAsync(string postId, bool keep)
    {
        if (!keep)
        {
            // Reddit already unsaved it; a locked offline file mustn't make that look like a failure.
            try
            {
                services.Offline.Remove(postId);
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke($"Unsaved, but couldn't remove the offline copy yet: {ex.Message}");
            }
            return;
        }
        if (!services.Settings.SaveOffline) return;
        try
        {
            var raw = await services.Api.GetThreadRawAsync(postId, "confidence", null, default);
            services.Offline.Save(postId, raw);
            // Media can be large; fetch it in the background.
            services.Offline.StartMediaDownload(postId, raw);
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke($"Saved, but couldn't keep an offline copy: {ex.Message}");
        }
    }
}

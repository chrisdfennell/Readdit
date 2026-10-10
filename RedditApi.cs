using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Skein;

public sealed class RedditAuthException(string message) : Exception(message);

public sealed record SubredditInfo(string Name, string Title, long Subscribers, string Description, bool IsSubscribed);

/// <summary>A post flair a community offers. Editable ones let you change the text.</summary>
public sealed record PostFlair(string Id, string Text, bool Editable)
{
    public override string ToString() => Text.Length > 0 ? Text : "(flair)";
}

public sealed record CommunityRule(string Name, string Description);

/// <summary>What a community asks of new posts.</summary>
public sealed record PostingInfo(IReadOnlyList<PostFlair> Flairs, bool FlairRequired, IReadOnlyList<CommunityRule> Rules);

/// <summary>The flair chosen for a new post (none: both empty).</summary>
public sealed record FlairChoice(string Id, string Text)
{
    public static readonly FlairChoice None = new("", "");

    public void AddTo(Dictionary<string, string> form)
    {
        if (Id.Length == 0) return;
        form["flair_id"] = Id;
        if (Text.Length > 0) form["flair_text"] = Text;
    }
}

public sealed record UserInfo(
    string Name, long Karma, long LinkKarma, long CommentKarma, DateTimeOffset Created, string? Icon, bool Suspended = false);

/// <summary>Reddit's JSON API, called through the user's browser session.</summary>
public sealed partial class RedditApi(RedditSession session)
{
    const string Base = "https://www.reddit.com";

    static readonly HttpClient Http = CreateHttp();

    string? _modhash;

    public string? UserName { get; private set; }

    /// <summary>Your total karma, as of the last identity check.</summary>
    public long? Karma { get; private set; }

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "windows:Skein:0.2.0 (desktop Reddit client)");
        return http;
    }

    // ---- Reading -------------------------------------------------------------------------

    /// <summary>Returns the signed-in user and their unread inbox count, refreshing the modhash.</summary>
    public async Task<(string? Name, int InboxCount)> RefreshIdentityAsync()
    {
        using var doc = await GetJsonAsync($"{Base}/api/me.json", default);
        var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;
        UserName = data.Str("name") is { Length: > 0 } n ? n : null;
        Karma = UserName == null ? null
            : data.Long("total_karma") is > 0 and var total ? total
            : data.Long("link_karma") + data.Long("comment_karma");
        var hadModhash = _modhash != null;
        _modhash = data.Str("modhash") is { Length: > 0 } m ? m : null;
        if (hadModhash != (_modhash != null) || !hadModhash)
            ApiLog.Write($"identity: user={UserName ?? "(none)"}, modhash {(_modhash != null ? "present" : "missing")}");
        return (UserName, (int)data.Long("inbox_count"));
    }

    public async Task<(List<Post> Items, string? After)> GetListingAsync(string path, string? after, CancellationToken ct)
    {
        var url = Base + path + (path.Contains('?') ? "&" : "?") + "limit=50&raw_json=1";
        if (after != null) url += $"&after={Uri.EscapeDataString(after)}";

        using var doc = await GetJsonAsync(url, ct);
        var data = doc.RootElement.GetProperty("data");
        var items = data.GetProperty("children").EnumerateArray()
            .Select(Post.FromThing)
            .OfType<Post>()
            .ToList();
        var next = data.Str("after");
        return (items, string.IsNullOrEmpty(next) ? null : next);
    }

    /// <summary>Raw [post, comments] JSON for a thread, optionally focused on one comment.</summary>
    public async Task<string> GetThreadRawAsync(string postId, string sort, string? focusCommentId, CancellationToken ct)
    {
        var path = focusCommentId == null
            ? $"/comments/{postId}.json?"
            : $"/comments/{postId}/_/{focusCommentId}.json?context=3&";
        var body = await GetBodyAsync($"{Base}{path}limit=300&depth=10&sort={Uri.EscapeDataString(sort)}&raw_json=1", ct);
        return body;
    }

    public Task<string> GetMoreChildrenRawAsync(string linkFullName, IEnumerable<string> children, string sort) =>
        GetBodyAsync($"{Base}/api/morechildren.json?api_type=json&raw_json=1&limit_children=false" +
                     $"&link_id={Uri.EscapeDataString(linkFullName)}&sort={Uri.EscapeDataString(sort)}" +
                     $"&children={string.Join(",", children.Select(Uri.EscapeDataString))}", default);

    public async Task<List<SubredditRef>> GetSubscriptionsAsync()
    {
        var subs = new List<SubredditRef>();
        string? after = null;
        // Reddit allows a few thousand subscriptions; 60 pages of 100 covers them all.
        for (var page = 0; page < 60; page++)
        {
            var url = $"{Base}/subreddits/mine/subscriber.json?limit=100&raw_json=1" +
                      (after == null ? "" : $"&after={after}");
            using var doc = await GetJsonAsync(url, default);
            var data = doc.RootElement.GetProperty("data");
            subs.AddRange(data.GetProperty("children").EnumerateArray()
                .Select(c => ToSubredditRef(c.GetProperty("data")))
                .Where(s => s.Name.Length > 0));
            after = data.Str("after");
            if (string.IsNullOrEmpty(after)) break;
        }
        // Paging can hand back the same community twice when the list shifts between pages.
        return subs.DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static SubredditRef ToSubredditRef(JsonElement d)
    {
        var icon = d.Str("community_icon") is { Length: > 0 } c ? c : d.Str("icon_img");
        return new SubredditRef(d.Str("display_name"), icon.StartsWith("http") ? icon : null, d.Long("subscribers"),
            d.Bool("user_has_favorited"), d.Str("subreddit_type") is { Length: > 0 } t ? t : "public", d.Bool("quarantine"));
    }

    /// <summary>
    /// Current details for many communities at once (100 per request). Banned and deleted
    /// communities are simply missing from the result.
    /// </summary>
    public async Task<List<SubredditRef>> GetSubredditsInfoAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        var found = new List<SubredditRef>();
        foreach (var chunk in names.Chunk(100))
        {
            using var doc = await GetJsonAsync(
                $"{Base}/api/info.json?raw_json=1&sr_name={string.Join(",", chunk.Select(Uri.EscapeDataString))}", ct);
            found.AddRange(doc.RootElement.GetProperty("data").GetProperty("children").EnumerateArray()
                .Where(c => c.Str("kind") == "t5")
                .Select(c => ToSubredditRef(c.GetProperty("data"))));
        }
        return found;
    }

    /// <summary>
    /// When a community last had a post: the HTTP status Reddit answered with, and the newest
    /// post's time (null if there are none or it couldn't be read).
    /// </summary>
    public async Task<(int Status, DateTimeOffset? Newest)> GetNewestPostAsync(string subreddit, CancellationToken ct)
    {
        var (status, body) = await session.GetAsync($"{Base}/r/{Uri.EscapeDataString(subreddit)}/new.json?limit=1&raw_json=1", ct);
        if (status is < 200 or >= 300 || !LooksLikeJson(body)) return (status, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var children = doc.RootElement.GetProperty("data").GetProperty("children");
            if (children.GetArrayLength() == 0) return (status, null);
            return (status, DateTimeOffset.FromUnixTimeSeconds((long)children[0].GetProperty("data").Double("created_utc")));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (status, null);
        }
    }

    /// <summary>Community suggestions for a partial name, as Reddit's search box offers them.</summary>
    public async Task<List<SubredditRef>> AutocompleteAsync(string query, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"{Base}/api/subreddit_autocomplete_v2.json?include_over_18=true&include_profiles=false&limit=8&raw_json=1" +
            $"&query={Uri.EscapeDataString(query)}", ct);
        return doc.RootElement.GetProperty("data").GetProperty("children").EnumerateArray()
            .Where(c => c.Str("kind") == "t5")
            .Select(c => ToSubredditRef(c.GetProperty("data")))
            .Where(s => s.Name.Length > 0)
            .ToList();
    }

    public async Task<SubredditInfo?> GetSubredditAboutAsync(string name, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Base}/r/{name}/about.json?raw_json=1", ct);
        var d = doc.RootElement.GetProperty("data");
        return new SubredditInfo(d.Str("display_name"), d.Str("title"), d.Long("subscribers"),
            d.Str("public_description"), d.Bool("user_is_subscriber"));
    }

    /// <summary>
    /// Everything for a community's info page, as one JSON object: about (Reddit's about data),
    /// rules, and moderators (left empty where a community hides them).
    /// </summary>
    public async Task<string> GetCommunityInfoRawAsync(string name, CancellationToken ct)
    {
        if (!NameChars().IsMatch(name)) throw new InvalidOperationException("That isn't a community name.");
        async Task<string> Optional(string url)
        {
            try
            {
                return await GetBodyAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not RedditAuthException)
            {
                return "null";
            }
        }
        var about = GetBodyAsync($"{Base}/r/{name}/about.json?raw_json=1", ct);
        var rules = Optional($"{Base}/r/{name}/about/rules.json?raw_json=1");
        var mods = Optional($"{Base}/r/{name}/about/moderators.json?raw_json=1");
        await Task.WhenAll(about, rules, mods);
        return $$"""{"about":{{about.Result}},"rules":{{rules.Result}},"mods":{{mods.Result}}}""";
    }

    /// <summary>What a community asks of new posts: its flairs, whether one is required, and its rules.</summary>
    public async Task<PostingInfo> GetPostingInfoAsync(string name, CancellationToken ct)
    {
        if (!NameChars().IsMatch(name)) throw new InvalidOperationException("That isn't a community name.");
        async Task<JsonDocument?> Optional(string url)
        {
            try
            {
                return JsonDocument.Parse(await GetBodyAsync(url, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not RedditAuthException)
            {
                return null; // no flairs, no requirements published, or no rules
            }
        }
        var flairsTask = Optional($"{Base}/r/{name}/api/link_flair_v2.json?raw_json=1");
        var requirementsTask = Optional($"{Base}/api/v1/{name}/post_requirements.json?raw_json=1");
        var rulesTask = Optional($"{Base}/r/{name}/about/rules.json?raw_json=1");
        await Task.WhenAll(flairsTask, requirementsTask, rulesTask);
        using var flairs = flairsTask.Result;
        using var requirements = requirementsTask.Result;
        using var rules = rulesTask.Result;

        var flairList = flairs?.RootElement.ValueKind == JsonValueKind.Array
            ? flairs.RootElement.EnumerateArray()
                .Where(f => !f.Bool("mod_only"))
                .Select(f => new PostFlair(f.Str("id"), f.Str("text"), f.Bool("text_editable")))
                .Where(f => f.Id.Length > 0)
                .ToList()
            : [];
        var ruleList = rules?.RootElement.TryGetProperty("rules", out var r) == true && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => new CommunityRule(x.Str("short_name"), x.Str("description"))).ToList()
            : [];
        return new PostingInfo(flairList, requirements?.RootElement.Bool("is_flair_required") == true, ruleList);
    }

    /// <summary>Current details (comment counts…) for posts by id, up to 100 per request.</summary>
    public async Task<List<Post>> GetPostsInfoAsync(IReadOnlyList<string> postIds, CancellationToken ct)
    {
        var posts = new List<Post>();
        foreach (var chunk in postIds.Where(id => System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9]+$")).Chunk(100))
        {
            var (items, _) = await GetListingAsync($"/api/info.json?id={string.Join(",", chunk.Select(id => "t3_" + id))}", null, ct);
            posts.AddRange(items.Where(p => p.Kind == ItemKind.Link));
        }
        return posts;
    }

    /// <summary>A user's newest posts.</summary>
    public async Task<List<Post>> GetUserPostsAsync(string user, CancellationToken ct)
    {
        if (!NameChars().IsMatch(user)) throw new InvalidOperationException("That isn't a username.");
        var (items, _) = await GetListingAsync($"/user/{user}/submitted.json?sort=new", null, ct);
        return items.Where(p => p.Kind == ItemKind.Link).ToList();
    }

    /// <summary>A community wiki page (Reddit's wikipage JSON).</summary>
    public Task<string> GetWikiPageRawAsync(string name, string page, CancellationToken ct) =>
        NameChars().IsMatch(name) && System.Text.RegularExpressions.Regex.IsMatch(page, "^[A-Za-z0-9_/-]{1,100}$")
            ? GetBodyAsync($"{Base}/r/{name}/wiki/{page}.json?raw_json=1", ct)
            : throw new InvalidOperationException("That isn't a wiki page.");

    public async Task<UserInfo?> GetUserAboutAsync(string name, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Base}/user/{name}/about.json?raw_json=1", ct);
        var d = doc.RootElement.GetProperty("data");
        var icon = d.Str("snoovatar_img") is { Length: > 0 } s ? s : d.Str("icon_img");
        // Suspended accounts come back with just a name: no karma or creation date.
        return new UserInfo(d.Str("name"), d.Long("total_karma"), d.Long("link_karma"), d.Long("comment_karma"),
            DateTimeOffset.FromUnixTimeSeconds((long)d.Double("created_utc")), icon.StartsWith("http") ? icon : null,
            d.Bool("is_suspended") || d.Double("created_utc") == 0);
    }

    public async Task<List<string>> SearchSubredditsAsync(string query, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"{Base}/subreddits/search.json?limit=8&raw_json=1&q={Uri.EscapeDataString(query)}", ct);
        return doc.RootElement.GetProperty("data").GetProperty("children").EnumerateArray()
            .Select(c => c.GetProperty("data").Str("display_name"))
            .Where(n => n.Length > 0)
            .ToList();
    }

    // ---- Acting as the user ---------------------------------------------------------------

    /// <summary>
    /// Votes, then asks Reddit what it recorded. Reddit can answer 200 to a vote it silently
    /// ignores, so if the check disagrees, retry once via the other route before reporting failure.
    /// </summary>
    public async Task VoteAsync(string fullName, int direction)
    {
        bool? expected = direction switch { 1 => true, -1 => false, _ => null };
        foreach (var oauthFirst in new[] { false, true })
        {
            using (await PostAsync("/api/vote", new() { ["id"] = fullName, ["dir"] = direction.ToString() }, oauthFirst))
            {
            }
            var recorded = await GetLikesAsync(fullName);
            ApiLog.Write($"vote {fullName} dir={direction} (oauthFirst={oauthFirst}): Reddit now reports likes={Show(recorded)}");
            if (recorded == expected) return;
        }
        throw new InvalidOperationException("Reddit accepted the request but didn't record the vote.");

        static string Show(bool? b) => b?.ToString() ?? "none";
    }

    async Task<bool?> GetLikesAsync(string fullName)
    {
        // Cache-buster: we need Reddit's current view, not a cached listing.
        using var doc = await GetJsonAsync(
            $"{Base}/api/info.json?id={fullName}&raw_json=1&_={DateTime.UtcNow.Ticks}", default);
        var children = doc.RootElement.GetProperty("data").GetProperty("children");
        return children.GetArrayLength() > 0 ? children[0].GetProperty("data").NullableBool("likes") : null;
    }

    public async Task SaveAsync(string fullName, bool save)
    {
        using var _ = await PostAsync(save ? "/api/save" : "/api/unsave", new() { ["id"] = fullName });
    }

    public async Task HideAsync(string fullName, bool hide)
    {
        using var _ = await PostAsync(hide ? "/api/hide" : "/api/unhide", new() { ["id"] = fullName });
    }

    // ---- Moderation ---------------------------------------------------------------------------

    /// <summary>Communities you moderate.</summary>
    public async Task<List<SubredditRef>> GetModeratedAsync()
    {
        var subs = new List<SubredditRef>();
        string? after = null;
        for (var page = 0; page < 10; page++)
        {
            var url = $"{Base}/subreddits/mine/moderator.json?limit=100&raw_json=1" + (after == null ? "" : $"&after={after}");
            using var doc = await GetJsonAsync(url, default);
            var data = doc.RootElement.GetProperty("data");
            subs.AddRange(data.GetProperty("children").EnumerateArray()
                .Select(c => ToSubredditRef(c.GetProperty("data")))
                .Where(s => s.Name.Length > 0));
            after = data.Str("after");
            if (string.IsNullOrEmpty(after)) break;
        }
        return subs.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The moderator actions Skein offers, mapped to Reddit's endpoints. Anything else is
    /// refused, so the page can't call arbitrary endpoints.
    /// </summary>
    static readonly Dictionary<string, (string Path, Func<string, Dictionary<string, string>> Form)> ModActions = new()
    {
        ["approve"] = ("/api/approve", id => new() { ["id"] = id }),
        ["remove"] = ("/api/remove", id => new() { ["id"] = id, ["spam"] = "false" }),
        ["spam"] = ("/api/remove", id => new() { ["id"] = id, ["spam"] = "true" }),
        ["lock"] = ("/api/lock", id => new() { ["id"] = id }),
        ["unlock"] = ("/api/unlock", id => new() { ["id"] = id }),
        ["sticky"] = ("/api/set_subreddit_sticky", id => new() { ["id"] = id, ["state"] = "true" }),
        ["unsticky"] = ("/api/set_subreddit_sticky", id => new() { ["id"] = id, ["state"] = "false" }),
        ["sticky_comment"] = ("/api/distinguish", id => new() { ["id"] = id, ["how"] = "yes", ["sticky"] = "true" }),
        ["distinguish"] = ("/api/distinguish", id => new() { ["id"] = id, ["how"] = "yes" }),
        ["undistinguish"] = ("/api/distinguish", id => new() { ["id"] = id, ["how"] = "no" }),
        ["nsfw"] = ("/api/marknsfw", id => new() { ["id"] = id }),
        ["unnsfw"] = ("/api/unmarknsfw", id => new() { ["id"] = id }),
        ["spoiler"] = ("/api/spoiler", id => new() { ["id"] = id }),
        ["unspoiler"] = ("/api/unspoiler", id => new() { ["id"] = id }),
        ["ignore_reports"] = ("/api/ignore_reports", id => new() { ["id"] = id }),
        ["unignore_reports"] = ("/api/unignore_reports", id => new() { ["id"] = id }),
    };

    public static bool IsModAction(string action) => ModActions.ContainsKey(action);

    /// <summary>Runs a moderator action on a post (t3_) or comment (t1_).</summary>
    public async Task ModerateAsync(string action, string fullName)
    {
        if (!ModActions.TryGetValue(action, out var spec)) throw new InvalidOperationException($"Unknown mod action '{action}'.");
        if (!ThingId().IsMatch(fullName)) throw new InvalidOperationException("That isn't a post or comment.");
        using var _ = await PostAsync(spec.Path, spec.Form(fullName));
    }

    /// <summary>Bans a user from a community. Days 0 = permanent.</summary>
    public async Task BanAsync(string subreddit, string user, int days, string reason, string message, string note)
    {
        if (!NameChars().IsMatch(subreddit) || !NameChars().IsMatch(user)) throw new InvalidOperationException("Invalid name.");
        var form = new Dictionary<string, string>
        {
            ["name"] = user,
            ["type"] = "banned",
            ["ban_reason"] = reason,
            ["ban_message"] = message,
            ["note"] = note,
        };
        if (days > 0) form["duration"] = Math.Min(days, 999).ToString();
        using var _ = await PostAsync($"/r/{subreddit}/api/friend", form);
    }

    /// <summary>A community's removal reasons, as Reddit's JSON ({"data": {id: {title, message}}, "order": [...]}).</summary>
    public Task<string> GetRemovalReasonsRawAsync(string subreddit, CancellationToken ct) =>
        NameChars().IsMatch(subreddit)
            ? GetModApiAsync($"/api/v1/{subreddit}/removal_reasons", ct)
            : throw new InvalidOperationException("That isn't a community name.");

    /// <summary>
    /// Removes a post or comment, records the removal reason (if one is picked) and tells the
    /// author: "public" (a reply from you), "public_as_subreddit" (a reply from the community),
    /// "private" (a message), or "" (don't tell them).
    /// </summary>
    public async Task RemoveWithReasonAsync(string fullName, string reasonId, string modNote, string messageType, string title, string message)
    {
        if (!ThingId().IsMatch(fullName)) throw new InvalidOperationException("That isn't a post or comment.");
        await ModerateAsync("remove", fullName);
        if (reasonId.Length > 0 || modNote.Length > 0)
        {
            using var _ = await PostJsonAsync("/api/v1/modactions/removal_reasons",
                JsonSerializer.Serialize(new { item_ids = new[] { fullName }, mod_note = modNote, reason_id = reasonId.Length > 0 ? reasonId : null }));
        }
        if (messageType is "public" or "public_as_subreddit" or "private" or "private_exposed" && message.Trim().Length > 0)
        {
            var path = fullName.StartsWith("t3_") ? "/api/v1/modactions/removal_link_message" : "/api/v1/modactions/removal_comment_message";
            using var _ = await PostJsonAsync(path,
                JsonSerializer.Serialize(new { item_id = new[] { fullName }, message, title = title.Length > 0 ? title : "Your submission was removed", type = messageType }));
        }
    }

    // ---- Modmail -----------------------------------------------------------------------------------

    static readonly HashSet<string> ModmailStates = ["all", "new", "inprogress", "mod", "archived", "highlighted", "appeals", "notifications", "join_requests"];

    /// <summary>Modmail conversations in a state ("all", "new", "inprogress", "mod", "archived"…), as Reddit's JSON.</summary>
    public Task<string> GetModmailRawAsync(string state, CancellationToken ct) =>
        ModmailStates.Contains(state)
            ? GetModApiAsync($"/api/mod/conversations?limit=50&sort=recent&state={state}", ct)
            : throw new InvalidOperationException("Unknown modmail folder.");

    /// <summary>One modmail conversation with its messages (marked read).</summary>
    public Task<string> GetModmailConversationRawAsync(string id, CancellationToken ct) =>
        ModmailId().IsMatch(id)
            ? GetModApiAsync($"/api/mod/conversations/{id}?markRead=true", ct)
            : throw new InvalidOperationException("That isn't a modmail conversation.");

    /// <summary>Replies to a modmail conversation: as yourself, as the community (hidden), or as a private mod note.</summary>
    public async Task<string> ReplyModmailAsync(string id, string body, bool hideAuthor, bool isInternal)
    {
        if (!ModmailId().IsMatch(id)) throw new InvalidOperationException("That isn't a modmail conversation.");
        using var doc = await PostAsync($"/api/mod/conversations/{id}", new()
        {
            ["body"] = body,
            ["isAuthorHidden"] = hideAuthor ? "true" : "false",
            ["isInternal"] = isInternal ? "true" : "false",
        });
        return doc.RootElement.GetRawText();
    }

    public async Task ArchiveModmailAsync(string id, bool archive)
    {
        if (!ModmailId().IsMatch(id)) throw new InvalidOperationException("That isn't a modmail conversation.");
        using var _ = await PostAsync($"/api/mod/conversations/{id}/{(archive ? "archive" : "unarchive")}", []);
    }

    /// <summary>Unread modmail across your communities (new + in progress + mod discussions…).</summary>
    public async Task<int> GetModmailUnreadAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetModApiAsync("/api/mod/conversations/unread/count", ct));
        return doc.RootElement.ValueKind == JsonValueKind.Object
            ? (int)doc.RootElement.EnumerateObject()
                .Where(p => p.Name is "new" or "inprogress" or "mod" or "highlighted" or "appeals" or "join_requests")
                .Sum(p => p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt64() : 0)
            : 0;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9]{1,16}$")]
    private static partial System.Text.RegularExpressions.Regex ModmailId();

    // ---- User history (for moderators) and your activity ----------------------------------------------

    /// <summary>
    /// What a moderator wants to know about someone in their community: who they are, their recent
    /// posts and comments there (removed ones included, as mods see them), whether they're banned,
    /// and the community's mod notes on them. As one JSON object.
    /// </summary>
    public async Task<string> GetUserHistoryRawAsync(string user, string subreddit, CancellationToken ct)
    {
        if (!NameChars().IsMatch(user) || !NameChars().IsMatch(subreddit)) throw new InvalidOperationException("Invalid name.");
        async Task<string> Optional(Func<Task<string>> load)
        {
            try
            {
                return await load();
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not RedditAuthException)
            {
                return "null";
            }
        }
        var about = Optional(() => GetBodyAsync($"{Base}/user/{user}/about.json?raw_json=1", ct));
        var banned = Optional(() => GetBodyAsync($"{Base}/r/{subreddit}/about/banned.json?raw_json=1&user={user}", ct));
        var notes = Optional(() => GetModApiAsync($"/api/mod/notes?subreddit={subreddit}&user={user}&limit=20", ct));
        var items = new List<string>();
        string? after = null;
        for (var page = 0; page < 2; page++)
        {
            var url = $"{Base}/user/{user}/overview.json?limit=100&sort=new&raw_json=1" + (after == null ? "" : $"&after={after}");
            string body;
            try
            {
                body = await GetBodyAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not RedditAuthException)
            {
                break; // suspended or deleted: no history
            }
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            foreach (var child in data.GetProperty("children").EnumerateArray())
                if (child.GetProperty("data").Str("subreddit").Equals(subreddit, StringComparison.OrdinalIgnoreCase))
                    items.Add(child.GetRawText());
            after = data.Str("after");
            if (string.IsNullOrEmpty(after)) break;
        }
        await Task.WhenAll(about, banned, notes);
        return $$"""{"about":{{about.Result}},"banned":{{banned.Result}},"notes":{{notes.Result}},"items":[{{string.Join(",", items)}}]}""";
    }

    /// <summary>
    /// For your activity page: your karma per community and your recent posts and comments
    /// (up to <paramref name="pages"/> × 100), as one JSON object.
    /// </summary>
    public async Task<string> GetActivityRawAsync(string me, int pages, CancellationToken ct)
    {
        if (!NameChars().IsMatch(me)) throw new InvalidOperationException("That isn't a username.");
        string karma;
        try
        {
            karma = await GetModApiAsync("/api/v1/me/karma", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RedditAuthException)
        {
            karma = "null";
        }
        var about = await GetBodyAsync($"{Base}/user/{me}/about.json?raw_json=1", ct);
        var items = new List<string>();
        string? after = null;
        for (var page = 0; page < pages; page++)
        {
            var url = $"{Base}/user/{me}/overview.json?limit=100&sort=new&raw_json=1" + (after == null ? "" : $"&after={after}");
            using var doc = JsonDocument.Parse(await GetBodyAsync(url, ct));
            var data = doc.RootElement.GetProperty("data");
            foreach (var child in data.GetProperty("children").EnumerateArray())
            {
                // Just what the page uses, to keep the message small.
                var d = child.GetProperty("data");
                items.Add(JsonSerializer.Serialize(new
                {
                    kind = child.Str("kind"),
                    sub = d.Str("subreddit"),
                    score = d.Long("score"),
                    created = (long)d.Double("created_utc"),
                    title = d.Str("title"),
                    body = d.Str("body") is { Length: > 200 } b ? b[..200] + "…" : d.Str("body"),
                    link = d.Str("link_title"),
                    permalink = d.Str("permalink"),
                    comments = d.Long("num_comments"),
                }));
            }
            after = data.Str("after");
            if (string.IsNullOrEmpty(after)) break;
        }
        return $$"""{"about":{{about}},"karma":{{karma}},"items":[{{string.Join(",", items)}}]}""";
    }

    [System.Text.RegularExpressions.GeneratedRegex("^t[13]_[a-z0-9]+$")]
    private static partial System.Text.RegularExpressions.Regex ThingId();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial System.Text.RegularExpressions.Regex NameChars();

    /// <summary>Stars or unstars a community (Reddit's "favorites" in its sidebar).</summary>
    public async Task FavoriteAsync(string subreddit, bool favorite)
    {
        using var _ = await PostAsync("/api/favorite",
            new() { ["make_favorite"] = favorite ? "true" : "false", ["sr_name"] = subreddit });
    }

    public async Task SubscribeAsync(string subreddit, bool subscribe)
    {
        using var _ = await PostAsync("/api/subscribe",
            new() { ["action"] = subscribe ? "sub" : "unsub", ["sr_name"] = subreddit });
    }

    /// <summary>
    /// Leaves many communities, a batch per request. One bad name (a banned community, say) can
    /// fail a whole batch, so a failed batch is retried one by one. Returns the ones that still failed.
    /// </summary>
    public async Task<List<string>> UnsubscribeManyAsync(IReadOnlyList<string> subreddits)
    {
        var failed = new List<string>();
        foreach (var chunk in subreddits.Chunk(50))
        {
            try
            {
                using var _ = await PostAsync("/api/subscribe",
                    new() { ["action"] = "unsub", ["sr_name"] = string.Join(",", chunk) });
            }
            catch
            {
                foreach (var name in chunk)
                {
                    try
                    {
                        await SubscribeAsync(name, subscribe: false);
                    }
                    catch
                    {
                        failed.Add(name);
                    }
                }
            }
        }
        return failed;
    }

    /// <summary>Unread inbox items, newest first.</summary>
    public async Task<List<Post>> GetUnreadAsync()
    {
        var (items, _) = await GetListingAsync("/message/unread.json", null, default);
        return items;
    }

    /// <summary>Marks inbox items (messages or comment replies) as read.</summary>
    public async Task MarkReadAsync(string fullName)
    {
        using var _ = await PostAsync("/api/read_message", new() { ["id"] = fullName });
    }

    public async Task MarkAllReadAsync()
    {
        using var _ = await PostAsync("/api/read_all_messages", []);
    }

    /// <summary>Sends a new private message to a user (u/ prefix optional).</summary>
    public async Task ComposeAsync(string to, string subject, string text)
    {
        to = to.Trim().TrimStart('/');
        if (to.StartsWith("u/", StringComparison.OrdinalIgnoreCase)) to = to[2..];
        if (!NameChars().IsMatch(to)) throw new InvalidOperationException("That isn't a valid username.");
        using var _ = await PostAsync("/api/compose", new() { ["to"] = to, ["subject"] = subject, ["text"] = text });
    }

    /// <summary>A private message conversation (the first message, with every reply under it), as raw JSON.</summary>
    public Task<string> GetConversationRawAsync(string messageId, CancellationToken ct) =>
        System.Text.RegularExpressions.Regex.IsMatch(messageId, "^[a-z0-9]+$")
            ? GetBodyAsync($"{Base}/message/messages/{messageId}.json?raw_json=1", ct)
            : throw new InvalidOperationException("That isn't a message.");

    /// <summary>Posts a comment or message reply; returns the new comment's raw JSON data object.</summary>
    public async Task<string> CommentAsync(string parentFullName, string text)
    {
        using var doc = await PostAsync("/api/comment", new() { ["thing_id"] = parentFullName, ["text"] = text });
        var things = doc.RootElement.GetProperty("json").GetProperty("data").GetProperty("things");
        return things[0].GetProperty("data").GetRawText();
    }

    /// <summary>Edits your own comment or self-post; returns the updated thing's raw JSON data.</summary>
    public async Task<string> EditAsync(string fullName, string text)
    {
        using var doc = await PostAsync("/api/editusertext", new() { ["thing_id"] = fullName, ["text"] = text });
        var things = doc.RootElement.GetProperty("json").GetProperty("data").GetProperty("things");
        return things[0].GetProperty("data").GetRawText();
    }

    /// <summary>Deletes your own comment or post.</summary>
    public async Task DeleteAsync(string fullName)
    {
        using var _ = await PostAsync("/api/del", new() { ["id"] = fullName });
    }

    /// <summary>Submits a post; returns its URL.</summary>
    public async Task<string> SubmitAsync(string subreddit, string title, bool isLink, string content, bool nsfw, bool spoiler,
        FlairChoice? flair = null)
    {
        var form = new Dictionary<string, string>
        {
            ["sr"] = subreddit,
            ["title"] = title,
            ["kind"] = isLink ? "link" : "self",
            [isLink ? "url" : "text"] = content,
            ["nsfw"] = nsfw ? "true" : "false",
            ["spoiler"] = spoiler ? "true" : "false",
            ["resubmit"] = "true",
            ["sendreplies"] = "true",
        };
        (flair ?? FlairChoice.None).AddTo(form);
        using var doc = await PostAsync("/api/submit", form);
        return doc.RootElement.GetProperty("json").GetProperty("data").Str("url");
    }

    // ---- Plumbing ---------------------------------------------------------------------------

    async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct) =>
        JsonDocument.Parse(await GetBodyAsync(url, ct));

    async Task<string> GetBodyAsync(string url, CancellationToken ct)
    {
        var (status, body) = await session.GetAsync(url, ct);

        switch (status)
        {
            case 401:
                throw new RedditAuthException("Your Reddit session has expired. Please sign in again.");
            case 403:
                throw new InvalidOperationException(
                    "Forbidden. The subreddit may be private, quarantined or banned, or you may need to sign in again.");
            case 404:
                throw new InvalidOperationException("Not found. Check the name and try again.");
            case 429:
                throw new InvalidOperationException("Rate limited by Reddit. Wait a minute and try again.");
            case < 200 or >= 300:
                throw new InvalidOperationException($"Reddit returned HTTP {status}.");
        }

        // A signed-out session gets redirected to an HTML login page rather than an error code.
        if (!LooksLikeJson(body))
            throw new RedditAuthException("Reddit sent a web page instead of data. You may need to sign in again.");
        return body;
    }

    /// <summary>
    /// Sends a form POST. Tries the classic cookie + modhash route first, then falls back to
    /// oauth.reddit.com with the session's bearer cookie, which is what reddit.com itself uses.
    /// </summary>
    async Task<JsonDocument> PostAsync(string path, Dictionary<string, string> form, bool oauthFirst = false)
    {
        form["api_type"] = "json";
        var failures = new List<string>();

        async Task<string?> ViaModhash()
        {
            if (_modhash == null) await RefreshIdentityAsync();
            if (_modhash == null)
            {
                failures.Add("cookie route: no modhash from /api/me.json");
                ApiLog.Write($"POST {path} cookie route skipped: no modhash");
                return null;
            }
            var withModhash = new Dictionary<string, string>(form) { ["uh"] = _modhash };
            var (status, body) = await session.SendAsync("POST", Base + path + RawJson, withModhash, _modhash, default);
            ApiLog.Write($"POST {path} cookie route -> HTTP {status}: {ApiLog.Clip(body)}");
            if (status is >= 200 and < 300 && LooksLikeJson(body)) return body;
            failures.Add($"cookie route: HTTP {status}");
            return null;
        }

        async Task<string?> ViaOAuth()
        {
            var token = await session.GetCookieAsync("token_v2");
            if (token == null)
            {
                failures.Add("oauth route: no token_v2 cookie");
                ApiLog.Write($"POST {path} oauth route skipped: no token_v2 cookie");
                return null;
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://oauth.reddit.com" + path + RawJson)
            {
                Content = new FormUrlEncodedContent(form),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            string body;
            HttpResponseMessage res;
            try
            {
                res = await Http.SendAsync(req);
                body = await res.Content.ReadAsStringAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // A network failure here mustn't stop the other route from being tried.
                failures.Add($"oauth route: {ex.Message}");
                ApiLog.Write($"POST {path} oauth route failed: {ex.Message}");
                return null;
            }
            using var _ = res;
            ApiLog.Write($"POST {path} oauth route -> HTTP {(int)res.StatusCode}: {ApiLog.Clip(body)}");
            if (res.IsSuccessStatusCode && LooksLikeJson(body)) return body;
            failures.Add($"oauth route: HTTP {(int)res.StatusCode}");
            return null;
        }

        var result = oauthFirst
            ? await ViaOAuth() ?? await ViaModhash()
            : await ViaModhash() ?? await ViaOAuth();
        if (result != null) return Checked(result);

        throw new InvalidOperationException($"Reddit refused the request ({string.Join("; ", failures)}).");
    }

    /// <summary>
    /// Reddit's newer APIs (modmail, removal reasons, mod notes, karma) live on oauth.reddit.com:
    /// ask there with the session's token, then fall back to www.reddit.com with cookies.
    /// </summary>
    async Task<string> GetModApiAsync(string pathAndQuery, CancellationToken ct)
    {
        var withRaw = pathAndQuery + (pathAndQuery.Contains('?') ? "&" : "?") + "raw_json=1";
        if (await session.GetCookieAsync("token_v2") is { } token)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://oauth.reddit.com" + withRaw);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                using var res = await Http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode && LooksLikeJson(body)) return body;
                ApiLog.Write($"GET {pathAndQuery} oauth route -> HTTP {(int)res.StatusCode}: {ApiLog.Clip(body)}");
            }
            catch (HttpRequestException ex)
            {
                ApiLog.Write($"GET {pathAndQuery} oauth route failed: {ex.Message}");
            }
        }
        return await GetBodyAsync(Base + withRaw, ct);
    }

    /// <summary>
    /// Without raw_json=1 Reddit entity-escapes body_html in responses, so new replies and edits
    /// would render as literal markup.
    /// </summary>
    const string RawJson = "?raw_json=1";

    /// <summary>Parses an api_type=json response, surfacing Reddit's own error messages.</summary>
    static JsonDocument Checked(string body)
    {
        var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("json", out var json) &&
            json.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var first = errors[0];
            var message = first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 1
                ? first[1].GetString()
                : first.ToString();
            doc.Dispose();
            throw new InvalidOperationException(message);
        }
        return doc;
    }

    static bool LooksLikeJson(string body)
    {
        var start = body.AsSpan().TrimStart();
        return !start.IsEmpty && (start[0] == '{' || start[0] == '[');
    }
}

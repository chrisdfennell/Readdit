using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Skein;

public sealed class RedditAuthException(string message) : Exception(message);

public sealed record SubredditInfo(string Name, string Title, long Subscribers, string Description, bool IsSubscribed);

public sealed record UserInfo(
    string Name, long Karma, long LinkKarma, long CommentKarma, DateTimeOffset Created, string? Icon, bool Suspended = false);

/// <summary>Reddit's JSON API, called through the user's browser session.</summary>
public sealed partial class RedditApi(RedditSession session)
{
    const string Base = "https://www.reddit.com";

    static readonly HttpClient Http = CreateHttp();

    string? _modhash;

    public string? UserName { get; private set; }

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
    public async Task<string> SubmitAsync(string subreddit, string title, bool isLink, string content, bool nsfw, bool spoiler)
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

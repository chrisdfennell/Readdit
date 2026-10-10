using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Readdit;

public sealed class RedditAuthException(string message) : Exception(message);

public sealed record SubredditInfo(string Name, string Title, long Subscribers, string Description, bool IsSubscribed);

public sealed record UserInfo(
    string Name, long Karma, long LinkKarma, long CommentKarma, DateTimeOffset Created, string? Icon, bool Suspended = false);

/// <summary>Reddit's JSON API, called through the user's browser session.</summary>
public sealed class RedditApi(RedditSession session)
{
    const string Base = "https://www.reddit.com";

    static readonly HttpClient Http = CreateHttp();

    string? _modhash;

    public string? UserName { get; private set; }

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "windows:Readdit:0.2.0 (desktop Reddit client)");
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
        for (var page = 0; page < 20; page++)
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
        return subs.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static SubredditRef ToSubredditRef(JsonElement d)
    {
        var icon = d.Str("community_icon") is { Length: > 0 } c ? c : d.Str("icon_img");
        return new SubredditRef(d.Str("display_name"), icon.StartsWith("http") ? icon : null, d.Long("subscribers"),
            d.Bool("user_has_favorited"));
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

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Skein;

/// <summary>Uploading pictures and videos, and posting them (single, video, or a gallery).</summary>
public sealed partial class RedditApi
{
    /// <summary>A file on Reddit's media storage: its URL, and the asset id galleries refer to.</summary>
    public sealed record UploadedMedia(string Url, string AssetId);

    /// <summary>An item in a gallery post.</summary>
    public sealed record GalleryItem(string AssetId, string Caption, string OutboundUrl);

    // Uploads can be big and slow; the API client's 30-second timeout is for small requests.
    static readonly HttpClient UploadHttp = new() { Timeout = TimeSpan.FromMinutes(15) };

    static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
    };

    /// <summary>The upload type for a file, or null if Reddit doesn't take it.</summary>
    public static string? MediaTypeFor(string path) => MediaTypes.GetValueOrDefault(Path.GetExtension(path));

    public static bool IsVideoFile(string path) => MediaTypeFor(path)?.StartsWith("video/") == true;

    /// <summary>
    /// Uploads a file: Reddit hands out an upload slot on its media storage, and the file goes
    /// straight there (no Reddit cookies involved).
    /// </summary>
    public async Task<UploadedMedia> UploadMediaAsync(string path, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        var type = MediaTypeFor(path) ??
                   throw new InvalidOperationException($"{name}: Reddit takes JPEG, PNG, GIF and WebP pictures, and MP4 or MOV videos.");

        using var lease = await PostAsync("/api/media/asset.json", new() { ["filepath"] = name, ["mimetype"] = type });
        var args = lease.RootElement.GetProperty("args");
        var action = args.Str("action");
        var uploadUrl = action.StartsWith("//") ? "https:" + action : action;
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var target) || target.Scheme != "https")
            throw new InvalidOperationException("Reddit didn't give a usable upload address.");
        var fields = args.GetProperty("fields").EnumerateArray().Select(f => (Name: f.Str("name"), Value: f.Str("value"))).ToList();
        var key = fields.FirstOrDefault(f => f.Name == "key").Value ?? throw new InvalidOperationException("Reddit's upload slot had no key.");
        var assetId = lease.RootElement.GetProperty("asset").Str("asset_id");

        using var form = new MultipartFormDataContent();
        foreach (var (fieldName, value) in fields)
        {
            var part = new StringContent(value);
            part.Headers.ContentType = null; // storage expects bare form fields
            form.Add(part, fieldName);
        }
        await using var stream = File.OpenRead(path);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name); // the file must come after the fields

        using var response = await UploadHttp.PostAsync(target, form, ct);
        ApiLog.Write($"upload {name} ({type}) -> HTTP {(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Uploading {name} failed (HTTP {(int)response.StatusCode}).");
        return new UploadedMedia($"{uploadUrl}/{key}", assetId);
    }

    /// <summary>
    /// Posts a single uploaded picture or video. Reddit finishes media posts in the background and
    /// reports the new post's address over a websocket; returns it (or null if that didn't come).
    /// </summary>
    public async Task<string?> SubmitMediaAsync(string subreddit, string title, UploadedMedia media, bool isVideo,
        UploadedMedia? poster, bool nsfw, bool spoiler, CancellationToken ct, FlairChoice? flair = null)
    {
        var form = new Dictionary<string, string>
        {
            ["sr"] = subreddit,
            ["title"] = title,
            ["kind"] = isVideo ? "video" : "image",
            ["url"] = media.Url,
            ["nsfw"] = nsfw ? "true" : "false",
            ["spoiler"] = spoiler ? "true" : "false",
            ["resubmit"] = "true",
            ["sendreplies"] = "true",
        };
        if (isVideo && poster != null) form["video_poster_url"] = poster.Url;
        (flair ?? FlairChoice.None).AddTo(form);
        using var doc = await PostAsync("/api/submit", form);
        var data = doc.RootElement.GetProperty("json").GetProperty("data");
        if (data.Str("url") is { Length: > 0 } direct) return direct;
        var socket = data.Str("websocket_url");
        var url = socket.Length > 0 ? await WaitForPostAsync(socket, TimeSpan.FromSeconds(isVideo ? 180 : 45), ct) : null;
        return url ?? await FindMyNewestPostAsync(title, ct);
    }

    /// <summary>Posts several uploaded pictures as one gallery; returns its address.</summary>
    public async Task<string?> SubmitGalleryAsync(string subreddit, string title, IReadOnlyList<GalleryItem> items,
        bool nsfw, bool spoiler, CancellationToken ct, FlairChoice? flair = null)
    {
        flair ??= FlairChoice.None;
        var body = JsonSerializer.Serialize(new
        {
            sr = subreddit,
            title,
            items = items.Select(i => new { media_id = i.AssetId, caption = i.Caption, outbound_url = i.OutboundUrl }),
            nsfw,
            spoiler,
            api_type = "json",
            show_error_list = true,
            sendreplies = true,
            validate_on_submit = true,
            flair_id = flair.Id.Length > 0 ? flair.Id : null,
            flair_text = flair.Text.Length > 0 ? flair.Text : null,
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        using var doc = await PostJsonAsync("/api/submit_gallery_post.json", body);
        var data = doc.RootElement.GetProperty("json").GetProperty("data");
        return data.Str("url") is { Length: > 0 } url ? url : await FindMyNewestPostAsync(title, ct);
    }

    /// <summary>Waits on Reddit's media websocket for the finished post's address.</summary>
    static async Task<string?> WaitForPostAsync(string socketUrl, TimeSpan timeout, CancellationToken ct)
    {
        if (!Uri.TryCreate(socketUrl, UriKind.Absolute, out var uri) || uri.Scheme != "wss") return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, cts.Token);
            var buffer = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open)
            {
                var message = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cts.Token);
                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close) return null;

                using var doc = JsonDocument.Parse(message.ToString());
                switch (doc.RootElement.Str("type"))
                {
                    case "success":
                        return doc.RootElement.Child("payload")?.Str("redirect") is { Length: > 0 } redirect ? redirect : null;
                    case "failed":
                        throw new InvalidOperationException("Reddit couldn't process the upload. Try a different file.");
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException && !ct.IsCancellationRequested)
        {
            // Fall back to looking the post up.
        }
        return null;
    }

    /// <summary>Your newest post with this title (when Reddit didn't say where it went).</summary>
    async Task<string?> FindMyNewestPostAsync(string title, CancellationToken ct)
    {
        if (UserName == null) return null;
        try
        {
            var (items, _) = await GetListingAsync($"/user/{UserName}/submitted.json?sort=new", null, ct);
            return items.FirstOrDefault(p => p.Kind == ItemKind.Link && p.Title == title)?.PermalinkUrl;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A POST with a JSON body (gallery posts), by the same two routes as form posts.</summary>
    async Task<JsonDocument> PostJsonAsync(string path, string json)
    {
        var failures = new List<string>();
        if (_modhash == null) await RefreshIdentityAsync();
        if (_modhash != null)
        {
            var (status, body) = await session.SendAsync("POST", Base + path + RawJson, null, _modhash, default, json: json);
            ApiLog.Write($"POST {path} (json) cookie route -> HTTP {status}: {ApiLog.Clip(body)}");
            if (status is >= 200 and < 300 && LooksLikeJson(body)) return Checked(body);
            failures.Add($"cookie route: HTTP {status}");
        }

        if (await session.GetCookieAsync("token_v2") is { } token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.reddit.com" + path + RawJson)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            ApiLog.Write($"POST {path} (json) oauth route -> HTTP {(int)response.StatusCode}: {ApiLog.Clip(body)}");
            if (response.IsSuccessStatusCode && LooksLikeJson(body)) return Checked(body);
            failures.Add($"oauth route: HTTP {(int)response.StatusCode}");
        }
        throw new InvalidOperationException($"Reddit refused the request ({string.Join("; ", failures)}).");
    }
}

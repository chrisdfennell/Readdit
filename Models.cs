using System.Text.Json;
using System.Text.RegularExpressions;

namespace Readdit;

public enum ItemKind { Link, Comment, Message }

/// <summary>
/// One row in a feed: usually a post, but user profiles, saved items and the inbox
/// also contain comments and private messages.
/// </summary>
public sealed partial class Post : ObservableObject
{
    public ItemKind Kind { get; init; }

    /// <summary>Reddit "fullname" (t3_/t1_/t4_…) that votes and saves act on.</summary>
    public required string FullName { get; init; }

    /// <summary>The thread (link) id to open; empty for private messages.</summary>
    public string Id { get; init; } = "";

    /// <summary>Comment to focus when opening the thread (comment and reply items).</summary>
    public string? CommentId { get; init; }

    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Subreddit { get; init; } = "";
    public string Url { get; init; } = "";
    public string PermalinkUrl { get; init; } = "";
    public string Domain { get; init; } = "";
    public string SelfText { get; init; } = "";
    public string Flair { get; init; } = "";
    public string Subject { get; init; } = "";
    public long NumComments { get; init; }
    public DateTimeOffset Created { get; init; }
    public bool IsSelf { get; init; }
    public bool IsVideo { get; init; }
    public bool IsGallery { get; init; }
    public bool Over18 { get; init; }
    public bool Spoiler { get; init; }
    public bool Stickied { get; init; }
    public bool IsUnread { get; init; }
    public string? Thumbnail { get; init; }

    /// <summary>Card/grid-sized image (≤640px wide) for image-led layouts and hover previews.</summary>
    public string? PreviewImage { get; init; }

    /// <summary>The item's raw JSON "data" object, handed to the detail view as-is.</summary>
    public string RawJson { get; init; } = "{}";

    public bool BlurThumbnail { get; set; }

    long _score;
    public long Score
    {
        get => _score;
        set
        {
            if (Set(ref _score, value)) Raise(nameof(ScoreText));
        }
    }

    bool? _likes;
    public bool? Likes { get => _likes; set => Set(ref _likes, value); }

    bool _saved;
    public bool Saved { get => _saved; set => Set(ref _saved, value); }

    bool _isRead;
    public bool IsRead { get => _isRead; set => Set(ref _isRead, value); }

    long _newComments;
    /// <summary>Comments added since the thread was last opened.</summary>
    public long NewComments
    {
        get => _newComments;
        set
        {
            if (!Set(ref _newComments, value)) return;
            Raise(nameof(NewCommentsText));
            Raise(nameof(HasNewComments));
        }
    }

    public bool HasNewComments => NewComments > 0;
    public string NewCommentsText => $"+{Format.Count(NewComments)} new";

    /// <summary>Posted by someone on the highlight list.</summary>
    public bool IsHighlightedAuthor { get; set; }

    /// <summary>Offline library tags, comma separated.</summary>
    public string LibraryTags { get; set; } = "";

    public bool IsLink => Kind == ItemKind.Link;
    public bool HasPreview => PreviewImage != null;
    public string CommentsText => $"💬 {Format.Count(NumComments)}";

    /// <summary>Text excerpt for cards without an image.</summary>
    public string CardText
    {
        get
        {
            if (PreviewImage != null) return "";
            var flat = Whitespace().Replace(SelfText, " ").Trim();
            return flat.Length > 400 ? flat[..400] + "…" : flat;
        }
    }

    /// <summary>"r/sub · u/author · 3h" for cards and grid tiles.</summary>
    public string ShortMeta => $"r/{Subreddit} · u/{Author} · {Format.Age(Created)}";
    public bool HasLink => Kind == ItemKind.Link && !IsSelf;
    public bool CanVote => Kind != ItemKind.Message;
    public string ScoreText => Format.Count(Score);

    /// <summary>Applies a vote locally, adjusting the score the way Reddit will.</summary>
    public void ApplyVote(bool? likes)
    {
        Score += Weight(likes) - Weight(Likes);
        Likes = likes;
        static int Weight(bool? l) => l switch { true => 1, false => -1, _ => 0 };
    }

    public string MetaLine => Kind switch
    {
        ItemKind.Comment =>
            $"💬 u/{Author} in r/{Subreddit} · {Format.Age(Created)} · {Format.Count(Score)} points",
        ItemKind.Message => $"✉ from u/{Author} · {Format.Age(Created)}",
        _ => $"r/{Subreddit} · u/{Author} · {Format.Age(Created)} · {Format.Count(NumComments)} comments" +
             (IsSelf ? "" : $" · {Domain}"),
    };

    /// <summary>Body preview for comment and message rows.</summary>
    public string Snippet
    {
        get
        {
            if (Kind == ItemKind.Link) return "";
            var flat = Whitespace().Replace(SelfText, " ").Trim();
            return flat.Length > 280 ? flat[..280] + "…" : flat;
        }
    }

    public string Tags => string.Join("  ", new[]
    {
        IsHighlightedAuthor ? $"⭐ u/{Author}" : null,
        LibraryTags.Length > 0 ? "🏷 " + LibraryTags : null,
        IsUnread ? "● NEW" : null,
        Kind != ItemKind.Link && Subject.Length > 0 ? Subject.ToUpperInvariant() : null,
        Stickied ? "📌 PINNED" : null,
        Over18 ? "NSFW" : null,
        Spoiler ? "SPOILER" : null,
        IsVideo ? "▶ VIDEO" : null,
        IsGallery ? "🖼 GALLERY" : null,
        string.IsNullOrWhiteSpace(Flair) ? null : Flair,
    }.Where(t => t != null));

    public static Post? FromThing(JsonElement thing) => thing.Str("kind") switch
    {
        "t3" => FromLink(thing.GetProperty("data")),
        "t1" => FromComment(thing.GetProperty("data")),
        "t4" => FromMessage(thing.GetProperty("data")),
        _ => null,
    };

    public static Post FromLink(JsonElement d)
    {
        var url = d.Str("url");
        var permalink = "https://www.reddit.com" + d.Str("permalink");
        var thumb = d.Str("thumbnail");

        return new Post
        {
            Kind = ItemKind.Link,
            FullName = d.Str("name"),
            Id = d.Str("id"),
            Title = d.Str("title"),
            Author = d.Str("author"),
            Subreddit = d.Str("subreddit"),
            Url = url.StartsWith("http") ? url : permalink,
            PermalinkUrl = permalink,
            Domain = d.Str("domain"),
            SelfText = d.Str("selftext"),
            Flair = d.Str("link_flair_text"),
            Score = d.Long("score"),
            Likes = d.NullableBool("likes"),
            Saved = d.Bool("saved"),
            NumComments = d.Long("num_comments"),
            Created = DateTimeOffset.FromUnixTimeSeconds((long)d.Double("created_utc")),
            IsSelf = d.Bool("is_self"),
            IsVideo = d.Bool("is_video"),
            IsGallery = d.Bool("is_gallery"),
            Over18 = d.Bool("over_18"),
            Spoiler = d.Bool("spoiler"),
            Stickied = d.Bool("stickied"),
            // "self", "default", "nsfw", "spoiler" etc. are placeholders, not URLs.
            Thumbnail = thumb.StartsWith("http") ? thumb : null,
            PreviewImage = FindPreview(d, url, 640),
            RawJson = d.GetRawText(),
        };
    }

    /// <summary>Best still image for a post, no wider than <paramref name="maxWidth"/> where possible.</summary>
    static string? FindPreview(JsonElement d, string url, int maxWidth)
    {
        if (d.TryGetProperty("preview", out var preview) &&
            preview.TryGetProperty("images", out var images) &&
            images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
        {
            var image = images[0];
            // Resolutions are ascending; take the largest that fits.
            string? best = null;
            if (image.TryGetProperty("resolutions", out var resolutions))
                foreach (var r in resolutions.EnumerateArray())
                    if (r.Long("width") <= maxWidth) best = r.Str("url");
            if (best == null && image.TryGetProperty("source", out var source)) best = source.Str("url");
            if (!string.IsNullOrEmpty(best)) return best;
        }

        // Galleries carry their images in media_metadata keyed by gallery_data order.
        if (d.TryGetProperty("gallery_data", out var gallery) &&
            gallery.TryGetProperty("items", out var items) && items.GetArrayLength() > 0 &&
            d.TryGetProperty("media_metadata", out var meta) &&
            meta.TryGetProperty(items[0].Str("media_id"), out var media))
        {
            string? best = null;
            if (media.TryGetProperty("p", out var previews))
                foreach (var p in previews.EnumerateArray())
                    if (p.Long("x") <= maxWidth) best = p.Str("u");
            if (best == null && media.TryGetProperty("s", out var s)) best = s.Str("u");
            if (!string.IsNullOrEmpty(best)) return best;
        }

        var path = url.Split('?')[0].ToLowerInvariant();
        return path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png") ||
               path.EndsWith(".gif") || path.EndsWith(".webp")
            ? url
            : null;
    }

    static Post FromComment(JsonElement d)
    {
        // Profile/saved comments carry link_id; inbox replies only carry a context URL.
        var linkId = d.Str("link_id");
        var context = d.Str("context");
        var threadId = linkId.StartsWith("t3_") ? linkId[3..] : ThreadIdFromUrl(context) ?? "";
        var permalink = d.Str("permalink");

        return new Post
        {
            Kind = ItemKind.Comment,
            FullName = d.Str("name"),
            Id = threadId,
            CommentId = d.Str("id"),
            Title = d.Str("link_title"),
            Author = d.Str("author"),
            Subreddit = d.Str("subreddit"),
            Subject = d.Str("subject"),
            PermalinkUrl = "https://www.reddit.com" + (permalink.Length > 0 ? permalink : context),
            SelfText = d.Str("body"),
            Score = d.Long("score"),
            Likes = d.NullableBool("likes"),
            Saved = d.Bool("saved"),
            Created = DateTimeOffset.FromUnixTimeSeconds((long)d.Double("created_utc")),
            Over18 = d.Bool("over_18"),
            IsUnread = d.Bool("new"),
            RawJson = d.GetRawText(),
        };
    }

    static Post FromMessage(JsonElement d) => new()
    {
        Kind = ItemKind.Message,
        FullName = d.Str("name"),
        Title = d.Str("subject"),
        Author = d.Str("author") is { Length: > 0 } a ? a : d.Str("subreddit_name_prefixed"),
        PermalinkUrl = "https://www.reddit.com/message/messages/" + d.Str("id"),
        SelfText = d.Str("body"),
        Created = DateTimeOffset.FromUnixTimeSeconds((long)d.Double("created_utc")),
        IsUnread = d.Bool("new"),
        RawJson = d.GetRawText(),
    };

    public static string? ThreadIdFromUrl(string url)
    {
        var m = ThreadInUrl().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"/comments/([a-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ThreadInUrl();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>An entry in the sidebar: a label, the feed spec it opens, and an optional icon.</summary>
public sealed record SidebarItem(string Label, string Spec, string? Icon = null)
{
    public bool HasIcon => Icon != null;

    /// <summary>Letter badge shown when a community has no icon.</summary>
    public string Initial
    {
        get
        {
            var name = Spec.StartsWith("r/") || Spec.StartsWith("u/") ? Spec[2..] : Label;
            return name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
        }
    }

    public bool IsCommunity => Spec.StartsWith("r/");
}

/// <summary>A subreddit name and its icon URL, if it has one.</summary>
public sealed record SubredditRef(string Name, string? Icon, long Subscribers = 0);

static class Format
{
    public static string Count(long n) => Math.Abs(n) switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}m",
        >= 10_000 => $"{n / 1_000.0:0}k",
        >= 1_000 => $"{n / 1_000.0:0.#}k",
        _ => n.ToString(),
    };

    public static string Age(DateTimeOffset created)
    {
        var span = DateTimeOffset.UtcNow - created;
        return span.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)span.TotalMinutes}m",
            < 60 * 24 => $"{(int)span.TotalHours}h",
            < 60 * 24 * 30 => $"{(int)span.TotalDays}d",
            < 60 * 24 * 365 => $"{(int)(span.TotalDays / 30)}mo",
            _ => $"{(int)(span.TotalDays / 365)}y",
        };
    }
}

static class JsonExtensions
{
    public static string Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    public static long Long(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (long)v.GetDouble()
            : 0;

    public static double Double(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : 0;

    public static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static bool? NullableBool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;
}

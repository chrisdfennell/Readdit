using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Readdit;

/// <summary>A downloadable piece of a post: an image, or a video plus its separate audio track.</summary>
public sealed record MediaItem(string Url, bool IsVideo, IReadOnlyList<string> AudioCandidates);

/// <summary>Finds the media in a post's JSON, mirroring how the post view chooses what to show.</summary>
public static partial class MediaExtractor
{
    public static List<MediaItem> Extract(JsonElement post)
    {
        // Crossposts carry their media on the original post.
        var s = CrosspostSource(post);
        var url = s.Str("url_overridden_by_dest") is { Length: > 0 } o ? o : s.Str("url");
        var items = new List<MediaItem>();

        // Removed galleries can send null for these, so check kinds before indexing.
        if (s.Bool("is_gallery") &&
            s.Child("gallery_data") is { } gallery &&
            gallery.TryGetProperty("items", out var galleryItems) && galleryItems.ValueKind == JsonValueKind.Array &&
            s.Child("media_metadata") is { } meta)
        {
            foreach (var item in galleryItems.EnumerateArray())
            {
                if (meta.Child(item.Str("media_id")) is not { } m || m.Str("status") != "valid" ||
                    m.Child("s") is not { } source)
                    continue;
                if (m.Str("e") == "AnimatedImage" && source.Str("mp4") is { Length: > 0 } mp4)
                    items.Add(new MediaItem(mp4, true, []));
                else if ((source.Str("u") is { Length: > 0 } u ? u : source.Str("gif")) is { Length: > 0 } still)
                    items.Add(new MediaItem(still, false, []));
            }
            if (items.Count > 0) return items;
        }

        if (RedditVideo(s) is { } video)
        {
            var fallback = video.Str("fallback_url");
            if (fallback.Length > 0)
            {
                items.Add(new MediaItem(fallback, true, video.Bool("is_gif") ? [] : AudioCandidatesFor(fallback)));
                return items;
            }
        }

        var path = url.Split('?')[0].ToLowerInvariant();
        if (path.EndsWith(".gifv")) items.Add(new MediaItem(Regex.Replace(url, @"\.gifv", ".mp4", RegexOptions.IgnoreCase), true, []));
        else if (path.EndsWith(".mp4") || path.EndsWith(".webm")) items.Add(new MediaItem(url, true, []));
        else if (s.Str("post_hint") == "image" || ImagePath().IsMatch(path)) items.Add(new MediaItem(url, false, []));
        return items;
    }

    /// <summary>Crossposts carry their media on the original post.</summary>
    static JsonElement CrosspostSource(JsonElement post) =>
        post.TryGetProperty("crosspost_parent_list", out var parents) &&
        parents.ValueKind == JsonValueKind.Array && parents.GetArrayLength() > 0 &&
        parents[0].ValueKind == JsonValueKind.Object
            ? parents[0]
            : post;

    static JsonElement? RedditVideo(JsonElement s)
    {
        foreach (var container in new[] { "secure_media", "media" })
            if (s.Child(container)?.Child("reddit_video") is { } video)
                return video;
        return s.Child("preview")?.Child("reddit_video_preview");
    }

    /// <summary>
    /// Reddit serves video and audio as separate DASH files next to each other; the audio file's
    /// name has changed over the years, so try the known variants.
    /// </summary>
    static List<string> AudioCandidatesFor(string videoUrl)
    {
        var baseUrl = videoUrl.Split('?')[0];
        baseUrl = baseUrl[..baseUrl.LastIndexOf('/')];
        return
        [
            $"{baseUrl}/CMAF_AUDIO_128.mp4", $"{baseUrl}/CMAF_AUDIO_64.mp4",
            $"{baseUrl}/DASH_AUDIO_128.mp4", $"{baseUrl}/DASH_AUDIO_64.mp4", $"{baseUrl}/DASH_audio.mp4", $"{baseUrl}/audio",
        ];
    }

    /// <summary>The still images the post view uses as posters and previews (≤1080 wide, else the source).</summary>
    public static List<string> Stills(JsonElement post)
    {
        var stills = new List<string>();
        var s = CrosspostSource(post);
        if (s.Child("preview") is { } preview && preview.TryGetProperty("images", out var images) &&
            images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0 &&
            images[0].ValueKind == JsonValueKind.Object)
        {
            string? best = null;
            if (images[0].TryGetProperty("resolutions", out var resolutions) && resolutions.ValueKind == JsonValueKind.Array)
                foreach (var r in resolutions.EnumerateArray())
                    if (r.Long("width") <= 1080) best = r.Str("url");
            if (best == null && images[0].TryGetProperty("source", out var source)) best = source.Str("url");
            if (!string.IsNullOrEmpty(best)) stills.Add(best);
        }
        return stills;
    }

    [GeneratedRegex(@"\.(jpe?g|png|gif|webp)$")]
    private static partial Regex ImagePath();
}

public sealed record DownloadResult(List<string> Files, string? Note);

/// <summary>Downloads post media to disk, merging Reddit's split video/audio when ffmpeg is available.</summary>
public static partial class MediaDownloader
{
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "windows:Readdit:0.3.0 (desktop Reddit client)");
        return http;
    }

    /// <summary>Downloads every media item of a post into <paramref name="folder"/>.</summary>
    public static async Task<DownloadResult> DownloadPostAsync(JsonElement post, string folder, CancellationToken ct = default)
    {
        var items = MediaExtractor.Extract(post);
        if (items.Count == 0) return new DownloadResult([], "This post has no images or video to download.");

        Directory.CreateDirectory(folder);
        var stem = Safe(post.Str("subreddit")) + "_" + Safe(post.Str("id"));
        var files = new List<string>();
        string? note = null;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var name = items.Count > 1 ? $"{stem}_{i + 1}" : stem;
            var videoFile = await DownloadToAsync(item.Url, Path.Combine(folder, name), ct);
            if (videoFile == null) continue;

            if (item.IsVideo && item.AudioCandidates.Count > 0)
            {
                var (merged, audioNote) = await AddAudioAsync(videoFile, item.AudioCandidates, ct);
                files.Add(merged);
                note ??= audioNote;
            }
            else
            {
                files.Add(videoFile);
            }
        }

        return new DownloadResult(files, files.Count == 0 ? "Reddit didn't serve any of the media files." : note);
    }

    /// <summary>Downloads a URL, picking the file extension from the URL or content type. Returns null on failure.</summary>
    public static async Task<string?> DownloadToAsync(string url, string pathWithoutExtension, CancellationToken ct)
    {
        string? path = null;
        try
        {
            using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return null;
            var extension = ExtensionFor(url, res.Content.Headers.ContentType?.MediaType);
            path = UniquePath(pathWithoutExtension + extension);
            await using (var file = File.Create(path))
                await res.Content.CopyToAsync(file, ct);
            return path;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Don't leave a truncated file behind.
            try
            {
                if (path != null) File.Delete(path);
            }
            catch
            {
                // Best-effort.
            }
            return null;
        }
    }

    static async Task<(string File, string? Note)> AddAudioAsync(string videoFile, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        string? audioFile = null;
        foreach (var candidate in candidates)
        {
            var stem = Path.Combine(Path.GetDirectoryName(videoFile)!, Path.GetFileNameWithoutExtension(videoFile) + "_audio");
            audioFile = await DownloadToAsync(candidate, stem, ct);
            if (audioFile != null) break;
        }
        if (audioFile == null) return (videoFile, null); // genuinely silent video

        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
            return (videoFile, "Reddit stores video and sound separately; both were saved. " +
                               "Install ffmpeg (winget install ffmpeg) and Readdit will merge them automatically.");

        var merged = UniquePath(Path.Combine(Path.GetDirectoryName(videoFile)!,
            Path.GetFileNameWithoutExtension(videoFile) + "_merged.mp4"));
        var start = new ProcessStartInfo(ffmpeg)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-y", "-loglevel", "error", "-i", videoFile, "-i", audioFile, "-c", "copy", merged })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        await using var kill = ct.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // Already exited.
            }
        });
        await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 || !File.Exists(merged))
            return (videoFile, "Couldn't merge video and sound with ffmpeg; both files were kept.");

        File.Delete(videoFile);
        File.Delete(audioFile);
        var final = UniquePath(videoFile);
        File.Move(merged, final);
        return (final, null);
    }

    static string? FindFfmpeg()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }
        return null;
    }

    static string ExtensionFor(string url, string? contentType)
    {
        var ext = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".mp4" or ".webm") return ext;
        return contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "video/webm" => ".webm",
            _ => ".mp4",
        };
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    static string Safe(string text) => SafeChars().Replace(text, "");

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex SafeChars();
}

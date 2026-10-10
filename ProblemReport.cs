using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Skein;

/// <summary>
/// Builds a problem report: a zip with what went wrong, version details and cleaned-up logs, plus
/// the text for a GitHub issue. Logs are stripped of the username, the Windows user folder and
/// everything Reddit sent back (only request paths and status codes are kept).
/// </summary>
public static partial class ProblemReport
{
    const int MaxLogBytes = 200 * 1024;

    /// <summary>Removes personal details from a log or report text.</summary>
    public static string Sanitize(string text, string? userName, string userProfile)
    {
        if (userProfile.Length > 3) text = text.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(userName) && userName.Length >= 3)
            text = Regex.Replace(text, $@"(?<![A-Za-z0-9_-]){Regex.Escape(userName)}(?![A-Za-z0-9_-])", "<you>", RegexOptions.IgnoreCase);
        return Secrets().Replace(text, m => m.Groups[1].Value + "<removed>");
    }

    /// <summary>
    /// API log lines keep only what was asked and how Reddit answered ("POST /api/vote … -> HTTP 200");
    /// the clipped response bodies can hold messages, names and the like.
    /// </summary>
    public static string SanitizeApiLog(string log, string? userName, string userProfile)
    {
        var lines = log.Split('\n').Select(line =>
        {
            var answer = line.IndexOf("-> HTTP ", StringComparison.Ordinal);
            if (answer < 0) return line;
            var end = line.IndexOf(':', answer);
            return end < 0 ? line : line[..end];
        });
        return Sanitize(string.Join('\n', lines), userName, userProfile);
    }

    /// <summary>The part of a log worth sending: its last <see cref="MaxLogBytes"/>.</summary>
    public static string Tail(string text) =>
        text.Length <= MaxLogBytes ? text : "…(earlier lines left out)\n" + text[^MaxLogBytes..];

    public static string Summary(string description, string version, string kind, string webView, string os) =>
        new StringBuilder()
            .AppendLine("What went wrong:")
            .AppendLine(description.Trim().Length > 0 ? description.Trim() : "(not described)")
            .AppendLine()
            .AppendLine($"Skein {version} ({kind})")
            .AppendLine($"Windows: {os}")
            .AppendLine($".NET: {Environment.Version}")
            .AppendLine($"WebView2: {webView}")
            .ToString();

    /// <summary>Writes the zip and returns its path.</summary>
    public static string CreateZip(string folder, string summary, bool includeLogs, string? userName)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(folder, $"Skein problem report {DateTime.Now:yyyy-MM-dd HHmm}.zip");
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "report.txt", Sanitize(summary, userName, profile));
        if (includeLogs)
        {
            if (ReadLog("crash.log") is { } crash) Add(zip, "crash.log", Sanitize(Tail(crash), userName, profile));
            if (ReadLog("api.log") is { } api) Add(zip, "api.log", SanitizeApiLog(Tail(api), userName, profile));
        }
        return path;

        static void Add(ZipArchive zip, string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
    }

    static string? ReadLog(string name)
    {
        try
        {
            var file = Path.Combine(Paths.Local, name);
            if (!File.Exists(file)) return null;
            // Shared read: Skein may be writing to it.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new StreamReader(stream).ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The address of a new GitHub issue with a title and body filled in.</summary>
    public static string? IssueUrl(string repositoryUrl, string description, string summary)
    {
        if (!Uri.TryCreate(repositoryUrl.TrimEnd('/'), UriKind.Absolute, out var repo) || repo.Host != "github.com") return null;
        var title = description.Trim().ReplaceLineEndings(" ");
        if (title.Length > 80) title = title[..80] + "…";
        var body = summary + "\n(Attach the problem report zip from your desktop here, if you made one.)";
        // Browsers and GitHub cope with a few thousand characters in the address.
        if (body.Length > 4000) body = body[..4000] + "…";
        return $"{repo.AbsoluteUri.TrimEnd('/')}/issues/new?title={Uri.EscapeDataString(title.Length > 0 ? title : "Problem report")}" +
               $"&body={Uri.EscapeDataString(body)}";
    }

    // modhash / token / bearer values, in JSON ("modhash": "abc") or text (Bearer abc) form.
    [GeneratedRegex(@"((?:modhash|token_v2|access_token|session_tracker|reddit_session)""?\s*[:=]\s*""?|Bearer\s+)[^""\s,;}]+", RegexOptions.IgnoreCase)]
    private static partial Regex Secrets();
}

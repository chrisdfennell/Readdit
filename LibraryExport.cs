using System.Net;
using System.Text;
using System.Text.Json;

namespace Readdit;

/// <summary>Writes the offline library (or the part of it on screen) out as Markdown, HTML, CSV or JSON.</summary>
public static class LibraryExport
{
    /// <summary>Picks the format from the file extension (.md, .html, .csv, otherwise JSON).</summary>
    public static string Render(IReadOnlyList<Post> posts, Func<Post, IReadOnlyList<string>> tagsFor, string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".md" or ".markdown" => ToMarkdown(posts, tagsFor),
            ".html" or ".htm" => ToHtml(posts, tagsFor),
            ".csv" => ToCsv(posts, tagsFor),
            _ => ToJson(posts, tagsFor),
        };

    public static string ToMarkdown(IReadOnlyList<Post> posts, Func<Post, IReadOnlyList<string>> tagsFor)
    {
        var sb = new StringBuilder($"# Saved posts ({posts.Count})\n\n");
        foreach (var p in posts)
        {
            var tags = tagsFor(p);
            sb.Append("- [").Append(EscapeMarkdown(p.Title)).Append("](").Append(p.PermalinkUrl).Append(')')
              .Append(" · r/").Append(p.Subreddit).Append(" · u/").Append(p.Author)
              .Append(" · ").Append(p.Created.ToString("yyyy-MM-dd"));
            if (p.HasLink) sb.Append(" · [link](").Append(p.Url).Append(')');
            if (tags.Count > 0) sb.Append(" · ").Append(string.Join(" ", tags.Select(t => "#" + t.Replace(' ', '-'))));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static string ToHtml(IReadOnlyList<Post> posts, Func<Post, IReadOnlyList<string>> tagsFor)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><title>Saved posts</title>\n")
          .Append("<style>body{font:15px/1.5 system-ui,sans-serif;max-width:860px;margin:2em auto;padding:0 1em}")
          .Append("li{margin:.6em 0}.meta{color:#777;font-size:13px}.tag{background:#ff450022;color:#c33a00;")
          .Append("border-radius:4px;padding:0 6px;margin-left:4px;font-size:12px}</style></head><body>\n")
          .Append("<h1>Saved posts (").Append(posts.Count).Append(")</h1>\n<ul>\n");
        foreach (var p in posts)
        {
            sb.Append("<li><a href=\"").Append(E(p.PermalinkUrl)).Append("\">").Append(E(p.Title)).Append("</a>")
              .Append("<div class=\"meta\">r/").Append(E(p.Subreddit)).Append(" · u/").Append(E(p.Author))
              .Append(" · ").Append(p.Created.ToString("yyyy-MM-dd"));
            if (p.HasLink) sb.Append(" · <a href=\"").Append(E(p.Url)).Append("\">").Append(E(p.Domain)).Append("</a>");
            foreach (var tag in tagsFor(p)) sb.Append("<span class=\"tag\">").Append(E(tag)).Append("</span>");
            sb.Append("</div></li>\n");
        }
        return sb.Append("</ul>\n</body></html>\n").ToString();
    }

    public static string ToCsv(IReadOnlyList<Post> posts, Func<Post, IReadOnlyList<string>> tagsFor)
    {
        // Excel starts a cell with = + - @ as a formula; a leading quote keeps titles as text.
        static string Cell(string s)
        {
            if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        var sb = new StringBuilder("title,subreddit,author,created,url,permalink,tags\r\n");
        foreach (var p in posts)
        {
            sb.AppendJoin(',', Cell(p.Title), Cell(p.Subreddit), Cell(p.Author), Cell(p.Created.ToString("yyyy-MM-dd HH:mm")),
                Cell(p.HasLink ? p.Url : ""), Cell(p.PermalinkUrl), Cell(string.Join("; ", tagsFor(p)))).Append("\r\n");
        }
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<Post> posts, Func<Post, IReadOnlyList<string>> tagsFor) =>
        JsonSerializer.Serialize(posts.Select(p => new
        {
            id = p.Id,
            title = p.Title,
            subreddit = p.Subreddit,
            author = p.Author,
            created = p.Created,
            url = p.HasLink ? p.Url : null,
            permalink = p.PermalinkUrl,
            tags = tagsFor(p),
            text = p.SelfText.Length > 0 ? p.SelfText : null,
        }), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    static string EscapeMarkdown(string s) => s.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");
}

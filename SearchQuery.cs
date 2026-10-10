using System.Text.RegularExpressions;

namespace Skein;

/// <summary>
/// A Reddit search split into its words and the refinements Skein offers, which are written as
/// Reddit's own search operators (author:, flair:, site:, self:, title:).
/// </summary>
public sealed partial record SearchQuery(string Text, string Author = "", string Flair = "", string Site = "",
    string Type = "any", bool TitleOnly = false)
{
    /// <summary>"any", "text" (self posts) or "links".</summary>
    public static IReadOnlyList<string> Types { get; } = ["any", "text", "links"];

    public static SearchQuery Parse(string query)
    {
        string Take(Regex pattern)
        {
            var m = pattern.Match(query);
            if (!m.Success) return "";
            query = query.Remove(m.Index, m.Length);
            return m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
        }
        var author = Take(AuthorOperator());
        var flair = Take(FlairOperator());
        var site = Take(SiteOperator());
        var self = Take(SelfOperator()).ToLowerInvariant() switch { "yes" or "true" => "yes", "no" or "false" => "no", _ => "" };
        var title = Take(TitleOperator());
        var text = Spaces().Replace(query, " ").Trim();
        var titleOnly = title.Length > 0 && text.Length == 0;
        return new SearchQuery(titleOnly ? title : text, author, flair, site,
            self == "yes" ? "text" : self == "no" ? "links" : "any", titleOnly);
    }

    /// <summary>The query Reddit gets (and the tab's identity).</summary>
    public string Build()
    {
        var parts = new List<string>();
        var text = Text.Trim();
        if (text.Length > 0) parts.Add(TitleOnly ? $"title:\"{text.Replace("\"", "")}\"" : text);
        if (Author.Trim() is { Length: > 0 } author) parts.Add("author:" + Clean(author.TrimStart('/').Replace("u/", "")));
        if (Flair.Trim() is { Length: > 0 } flair) parts.Add(flair.Contains(' ') ? $"flair:\"{flair.Replace("\"", "")}\"" : "flair:" + flair);
        if (Site.Trim() is { Length: > 0 } site) parts.Add("site:" + Clean(site));
        if (Type == "text") parts.Add("self:yes");
        else if (Type == "links") parts.Add("self:no");
        return string.Join(' ', parts);
    }

    public bool IsRefined => Author.Length > 0 || Flair.Length > 0 || Site.Length > 0 || Type != "any" || TitleOnly;

    static string Clean(string s) => Spaces().Replace(s, "");

    [GeneratedRegex(@"(?:^|\s)author:(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorOperator();

    [GeneratedRegex(@"(?:^|\s)flair:(?:""([^""]*)""|(\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex FlairOperator();

    [GeneratedRegex(@"(?:^|\s)site:(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SiteOperator();

    [GeneratedRegex(@"(?:^|\s)self:(yes|no|true|false)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SelfOperator();

    [GeneratedRegex(@"(?:^|\s)title:(?:""([^""]*)""|(\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex TitleOperator();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>A search kept in the sidebar.</summary>
public sealed class SavedSearch
{
    public string Name { get; set; } = "";

    /// <summary>The feed spec it opens ("search:scope:query").</summary>
    public string Spec { get; set; } = "";
}

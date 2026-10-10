namespace Readdit;

/// <summary>One thing the command palette can do: open a community, switch tab, run an action…</summary>
/// <param name="Glyph">A small symbol shown before the label.</param>
/// <param name="Detail">Greyed text after the label (what kind of thing it is).</param>
public sealed record PaletteItem(string Glyph, string Label, string Detail, Action Run);

/// <summary>Scores palette entries against what's been typed.</summary>
public static class PaletteMatcher
{
    /// <summary>
    /// Higher is better; -1 means no match. Prefix matches beat word starts, which beat matches
    /// mid-word, which beat letters in order ("rpi" finds "r/raspberry_pi").
    /// </summary>
    public static int Score(string query, string text)
    {
        query = query.Trim();
        if (query.Length == 0) return 0;
        var q = query.ToLowerInvariant();
        var t = text.ToLowerInvariant();

        // "r/" prefixes are noise when comparing ("pics" should find "r/pics" first).
        var bare = t.StartsWith("r/") || t.StartsWith("u/") ? t[2..] : t;
        if (bare == q || t == q) return 2000;
        if (bare.StartsWith(q) || t.StartsWith(q)) return 1500 - Math.Min(t.Length, 400);

        var at = t.IndexOf(q, StringComparison.Ordinal);
        if (at >= 0)
        {
            var wordStart = at == 0 || !char.IsLetterOrDigit(t[at - 1]);
            return (wordStart ? 1000 : 700) - Math.Min(at, 300);
        }

        // Letters in order, with a penalty for the gaps between them.
        var gaps = 0;
        var ti = 0;
        foreach (var c in q)
        {
            if (c == ' ') continue;
            var found = t.IndexOf(c, ti);
            if (found < 0) return -1;
            gaps += found - ti;
            ti = found + 1;
        }
        return Math.Max(1, 400 - gaps * 10);
    }

    /// <summary>The best matches, best first; with nothing typed, the items as given.</summary>
    public static List<PaletteItem> Filter(IEnumerable<PaletteItem> items, string query, int max)
    {
        if (query.Trim().Length == 0) return items.Take(max).ToList();
        return items.Select((item, order) => (item, order, score: Score(query, item.Label)))
            .Where(x => x.score >= 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.order)
            .Take(max)
            .Select(x => x.item)
            .ToList();
    }
}

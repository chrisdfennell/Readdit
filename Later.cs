namespace Skein;

/// <summary>"Remind me about this post later."</summary>
public sealed class Reminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PostId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subreddit { get; set; } = "";
    public string Permalink { get; set; } = "";

    /// <summary>When to remind (unix seconds).</summary>
    public long Due { get; set; }
}

/// <summary>A text or link post written now and posted later (while Skein is running).</summary>
public sealed class ScheduledPost
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The account (browser profile) it's posted from.</summary>
    public string Profile { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Subreddit { get; set; } = "";
    public string Title { get; set; } = "";
    public bool IsLink { get; set; }
    public string Body { get; set; } = "";
    public bool Nsfw { get; set; }
    public bool Spoiler { get; set; }
    public string FlairId { get; set; } = "";
    public string FlairText { get; set; } = "";

    /// <summary>When to post (unix seconds).</summary>
    public long Due { get; set; }

    /// <summary>Why the last try failed (it waits for you to try again or cancel).</summary>
    public string? Error { get; set; }
}

/// <summary>A snippet you keep for replies and posts.</summary>
public sealed class SavedReply
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
}

public static class SavedReplyBook
{
    /// <summary>Adds a snippet, replacing one with the same name. Kept in name order.</summary>
    public static void Add(List<SavedReply> list, string name, string text)
    {
        name = name.Trim();
        if (name.Length == 0 || text.Trim().Length == 0) throw new InvalidOperationException("A saved reply needs a name and some text.");
        if (name.Length > 80) name = name[..80];
        list.RemoveAll(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        list.Add(new SavedReply { Name = name, Text = text });
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Remove(List<SavedReply> list, string name) =>
        list.RemoveAll(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>A "savedReply" request from a page: add or remove. Saves the settings.</summary>
    public static void Handle(AppSettings settings, string action, string name, string text)
    {
        if (action == "add") Add(settings.SavedReplies, name, text);
        else if (action == "remove") Remove(settings.SavedReplies, name);
        else throw new InvalidOperationException("Unknown saved reply action.");
        settings.Save();
    }
}

public static class KarmaHistory
{
    /// <summary>Records today's karma for an account (one point a day, the latest kept; at most two years).</summary>
    public static bool Record(Dictionary<string, List<KarmaPoint>> history, string account, DateTime today, long karma)
    {
        if (account.Length == 0) return false;
        if (!history.TryGetValue(account, out var points)) history[account] = points = [];
        var day = today.ToString("yyyy-MM-dd");
        if (points.Count > 0 && points[^1].Day == day)
        {
            if (points[^1].Karma == karma) return false;
            points[^1] = new KarmaPoint(day, karma);
            return true;
        }
        points.Add(new KarmaPoint(day, karma));
        if (points.Count > 730) points.RemoveRange(0, points.Count - 730);
        return true;
    }
}

/// <summary>Your total karma on a day.</summary>
public sealed record KarmaPoint(string Day, long Karma);

/// <summary>The quick choices offered for "later".</summary>
public static class LaterTimes
{
    public static readonly (string Key, string Label)[] ReminderChoices =
    [
        ("1h", "In an hour"), ("3h", "In 3 hours"), ("evening", "This evening"),
        ("tomorrow", "Tomorrow morning"), ("weekend", "This weekend"), ("week", "Next week"),
    ];

    public static readonly (string Key, string Label)[] ScheduleChoices =
    [
        ("1h", "In an hour"), ("3h", "In 3 hours"), ("evening", "This evening"), ("tomorrow", "Tomorrow morning"),
    ];

    /// <summary>
    /// The time a choice means, from <paramref name="now"/>: mornings are 9:00, evenings 19:00,
    /// a weekend is Saturday morning (or Sunday's, on a Saturday), next week is 7 days on at 9:00.
    /// </summary>
    public static DateTime For(string key, DateTime now) => key switch
    {
        "1h" => now.AddHours(1),
        "3h" => now.AddHours(3),
        "evening" => now.Hour < 18 ? now.Date.AddHours(19) : now.Date.AddDays(1).AddHours(19),
        "tomorrow" => now.Date.AddDays(1).AddHours(9),
        "weekend" => now.DayOfWeek switch
        {
            DayOfWeek.Saturday => now.Date.AddDays(1).AddHours(9),
            DayOfWeek.Sunday => now.Date.AddDays(6).AddHours(9),
            _ => now.Date.AddDays(DayOfWeek.Saturday - now.DayOfWeek).AddHours(9),
        },
        "week" => now.Date.AddDays(7).AddHours(9),
        _ => throw new ArgumentException($"Unknown choice '{key}'.", nameof(key)),
    };

    /// <summary>"Today 19:00", "Tomorrow 09:00", "Sat 09:00", "12 Oct 09:00".</summary>
    public static string Describe(DateTime at, DateTime now)
    {
        var day = at.Date == now.Date ? "Today"
            : at.Date == now.Date.AddDays(1) ? "Tomorrow"
            : at.Date < now.Date.AddDays(7) ? at.ToString("ddd")
            : at.ToString("d MMM");
        return $"{day} {at:t}";
    }

    /// <summary>"9:30", "21:15", "9pm", "9:30 pm" → time of day; null if it isn't one.</summary>
    public static TimeSpan? ParseTime(string text)
    {
        text = text.Trim().ToLowerInvariant().Replace(".", ":");
        var pm = text.EndsWith("pm");
        var am = text.EndsWith("am");
        if (pm || am) text = text[..^2].Trim();
        var parts = text.Split(':');
        if (parts.Length > 2 || !int.TryParse(parts[0], out var hour)) return null;
        var minute = 0;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out minute) || minute is < 0 or > 59)) return null;
        if (pm || am)
        {
            if (hour is < 1 or > 12) return null;
            hour = hour % 12 + (pm ? 12 : 0);
        }
        return hour is >= 0 and <= 23 ? new TimeSpan(hour, minute, 0) : null;
    }
}

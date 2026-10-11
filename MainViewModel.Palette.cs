using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Skein;

public sealed partial class MainViewModel
{
    // ---- Command palette (Ctrl+K) -------------------------------------------------------------------

    /// <summary>Everything the palette offers, in the order shown before anything is typed.</summary>
    public IReadOnlyList<PaletteItem> BuildPaletteItems()
    {
        var items = new List<PaletteItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Community(string glyph, string name, string detail)
        {
            if (seen.Add(name)) items.Add(new PaletteItem(glyph, "r/" + name, detail, () => OpenFeed("r/" + name, newTab: false)));
        }

        foreach (var tab in Tabs.Where(t => t != SelectedTab))
            items.Add(new PaletteItem("🗂", tab.Title, "Open tab", () => SelectedTab = tab));
        foreach (var feed in Feeds)
            items.Add(new PaletteItem("", feed.Label, "Feed", () => OpenFeed(feed.Spec, newTab: false)));
        foreach (var name in Favorites) Community("★", name, "Favorite");
        if (SelectedTab is { HasCommunityInfo: true } current)
            items.Insert(0, new PaletteItem("ℹ", $"About r/{current.Target}: rules, wiki, moderators", "Community", () => ShowCommunity(current.Target)));
        foreach (var search in Settings.SavedSearches)
            items.Add(new PaletteItem("🔍", search.Name, "Saved search", () => OpenFeed(search.Spec, newTab: false)));
        foreach (var folder in Folders)
            items.Add(new PaletteItem("📁", folder.Name, $"Folder · {folder.Subreddits.Count}", () => OpenFeed("folder:" + folder.Name, newTab: false)));

        items.AddRange(
        [
            new PaletteItem("⟳", "Refresh", "R", () => RefreshCommand.Execute(null)),
            new PaletteItem("✏", "New post", "N", () => ShowSubmit()),
            new PaletteItem("✉", "New message", "Inbox", () => ShowCompose("")),
            new PaletteItem("🔔", "Manage alerts", "Alerts", ShowAlerts),
            new PaletteItem("📁", "New folder", "Folders", () => EditFolder(null)),
            new PaletteItem("🧹", "Clean up subscriptions", "Subscriptions", ShowCleanup),
            new PaletteItem("⇅", Settings.SubscriptionSort == "Visits" ? "Sort subscriptions A–Z" : "Sort subscriptions by most visited",
                "Subscriptions", ToggleSubscriptionSort),
            new PaletteItem("⤢", IsReadingMode ? "Leave reading mode" : "Reading mode", "F", () => IsReadingMode = !IsReadingMode),
            new PaletteItem("▣", "Mini mode", "Ctrl+M", () => MiniModeRequested?.Invoke(true)),
            new PaletteItem("🔊", "Read aloud (or stop)", "Shift+R", () => _detail.ReadAloud()),
            new PaletteItem("⏰", "Remind me about this post", "Reminders", () => RemindCommand.Execute(null)),
            new PaletteItem("⧉", "Open this post side by side", "Window", () => PopOutCommand.Execute(null)),
            new PaletteItem("▤", "Layout: Compact", "1", () => LayoutMode = "Compact"),
            new PaletteItem("▦", "Layout: Cards", "2", () => LayoutMode = "Cards"),
            new PaletteItem("▩", "Layout: Grid", "3", () => LayoutMode = "Grid"),
            new PaletteItem("⚙", "Preferences", "Settings", ShowPreferences),
            new PaletteItem("⌨", "Keyboard shortcuts", "?", () => ShortcutsCommand.Execute(null)),
            new PaletteItem("🐞", "Report a problem", "Help", ShowReportProblem),
        ]);
        foreach (var account in Accounts.Where(a => a.Profile != ActiveProfile && a.UserName.Length > 0))
            items.Add(new PaletteItem("👤", $"Switch to u/{account.UserName}", "Account", () => SwitchAccount(account.Profile)));

        if (HasModeration) items.Add(new PaletteItem("✉", "Modmail", "Moderation", () => OpenFeed("modmail", newTab: false)));
        if (HasModeration)
            foreach (var sub in _moderated)
                items.Add(new PaletteItem("🛡", $"Mod queue: r/{sub.Name}", "Moderation", () => OpenFeed("mod:" + sub.Name, newTab: false)));
        foreach (var sub in _subscriptions) Community("", sub.Name, "Subscribed");
        return items;
    }

    /// <summary>Palette entries made from what was typed: go to it, or search for it.</summary>
    public IEnumerable<PaletteItem> PaletteExtras(string query)
    {
        if (query.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            yield return new PaletteItem("🔗", "Open " + query, "Link", () => OpenUrl(query));
            yield break;
        }
        if (RedditLinks.ParseFeedInput(query) is { } spec)
            yield return new PaletteItem("→", "Go to " + spec, "Open", () => OpenFeed(spec, newTab: false));
        else if (!query.Contains(' ') && query.Length <= 21)
            yield return new PaletteItem("→", $"Go to r/{query}", "Community", () => OpenFeed("r/" + query, newTab: false));
        yield return new PaletteItem("🔍", $"Search Reddit for “{query}”", "Search", () => OpenFeed($"search::{query}", newTab: true));
    }
}

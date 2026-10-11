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
    // ---- Moderation -------------------------------------------------------------------------

    List<SubredditRef> _moderated = [];
    int _modmailUnread;

    /// <summary>Unread modmail, shown on the sidebar's Modmail entry (checked with the inbox).</summary>
    async Task RefreshModmailCountAsync()
    {
        if (!HasModeration) return;
        try
        {
            var unread = await _services.Api.GetModmailUnreadAsync(default);
            if (unread == _modmailUnread) return;
            _modmailUnread = unread;
            RefreshSidebar();
        }
        catch
        {
            // A count is a nicety.
        }
    }

    public bool HasModeration => _moderated.Count > 0;

    /// <summary>The combined queue first, then a queue per community you moderate.</summary>
    public IReadOnlyList<SidebarItem> ModerationItems =>
        _moderated.Count == 0
            ? []
            : [new SidebarItem("🛡 Mod queue (all)", "mod:mod"),
               new SidebarItem(_modmailUnread > 0 ? $"✉ Modmail ({_modmailUnread})" : "✉ Modmail", "modmail", IsFeed: true),
               .. _moderated.Where(s => MatchesFilter(s.Name)).Select(s => new SidebarItem("r/" + s.Name, "mod:" + s.Name, s.Icon))];

    async Task LoadModeratedAsync()
    {
        if (DemoMode) return;
        try
        {
            _moderated = await _services.Api.GetModeratedAsync();
            _services.Moderated.Clear();
            foreach (var sub in _moderated) _services.Moderated.Add(sub.Name);
            RefreshSidebar();
        }
        catch
        {
            // Not being a moderator anywhere is the common case; nothing to report.
        }
    }

    async Task ModerateAsync(Post? post, string action)
    {
        if (post is not { CanModerate: true }) return;
        try
        {
            await _services.Api.ModerateAsync(action, post.FullName);
            OnModActionDone(post.FullName, action);
            Status = action switch
            {
                "approve" => "Approved.",
                "spam" => "Removed as spam.",
                _ => "Removed.",
            };
        }
        catch (Exception ex)
        {
            Status = $"Couldn't {action}: {ex.Message}";
        }
    }

    /// <summary>Reflects a mod action on every loaded copy, and takes handled items out of queues.</summary>
    void OnModActionDone(string fullName, string action)
    {
        foreach (var post in Tabs.SelectMany(t => t.Posts).Where(p => p.FullName == fullName).ToList())
        {
            if (action == "approve") { post.IsApproved = true; post.IsRemoved = false; }
            else if (action is "remove" or "spam") { post.IsRemoved = true; post.IsApproved = false; }
        }
        if (action is "approve" or "remove" or "spam" or "ignore_reports")
            foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.ModQueue)) tab.DropFromQueue(fullName);
    }
}

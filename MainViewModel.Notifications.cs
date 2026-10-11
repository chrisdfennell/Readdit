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
    // ---- Inbox ------------------------------------------------------------------------------

    int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        private set
        {
            if (Set(ref _unreadCount, value)) Raise(nameof(InboxLabel));
        }
    }

    public string InboxLabel => UnreadCount > 0 ? $"✉ {UnreadCount}" : "✉";

    async Task RefreshInboxAsync()
    {
        try
        {
            var (name, count) = await _services.Api.RefreshIdentityAsync();
            if (name != null) UserName = name;
            // A point a day for the activity page's karma chart.
            if (name != null && _services.Api.Karma is { } karma &&
                KarmaHistory.Record(Settings.KarmaHistory, name, DateTime.Now, karma))
                Settings.Save();
            _ = RefreshModmailCountAsync();
            if (!_inboxPrimed)
            {
                // First check after launch: remember what's already unread rather than announcing it.
                // Only count as primed once that list is in, so a failure retries next poll.
                if (count > 0)
                    foreach (var item in await _services.Api.GetUnreadAsync()) _notified.Add(item.FullName);
                UnreadCount = count;
                _inboxPrimed = true;
                return;
            }
            UnreadCount = count;
            // Compare actual unread items, not the count: reading one message while another
            // arrives leaves the count unchanged but is still news.
            if (count > 0) await NotifyNewMessagesAsync(count);
        }
        catch
        {
            // Polling is best-effort.
        }
    }

    async Task NotifyNewMessagesAsync(int count)
    {
        try
        {
            // Always record what's been seen, so turning notifications back on doesn't announce old items.
            var fresh = (await _services.Api.GetUnreadAsync()).Where(m => _notified.Add(m.FullName)).ToList();
            if (fresh.Count == 0 || !Settings.InboxNotifications) return;
            Status = count == 1 ? "You have a new message." : $"You have {count} unread messages.";
            var first = fresh[0];
            // Modmail and subreddit messages have no author; Author is then "r/name".
            var from = first.Author.StartsWith("r/") ? first.Author : "u/" + first.Author;
            var title = fresh.Count > 1
                ? $"{fresh.Count} new messages on Reddit"
                : first.Kind == ItemKind.Comment
                    ? $"{from} replied" + (first.Title.Length > 0 ? $" in “{first.Title}”" : "")
                    : $"Message from {from}: {first.Title}";
            var body = first.Snippet.Length > 0 ? first.Snippet : first.SelfText;
            InboxNotification?.Invoke(title, body.Length > 200 ? body[..200] + "…" : body, fresh.Count == 1 ? first : null);
        }
        catch
        {
            InboxNotification?.Invoke("New message on Reddit", Status, null);
        }
    }

    // ---- Watching threads and following people ----------------------------------------------------

    public bool IsWatching(string postId) => Settings.WatchedThreads.Any(w => w.PostId == postId);

    public bool IsFollowing(string user) => Settings.FollowedUsers.Any(f => f.Name.Equals(user, StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts or stops watching a thread for new comments (counting from what's there now).</summary>
    public void SetWatching(string postId, string title, string subreddit, long comments, bool on)
    {
        Settings.WatchedThreads.RemoveAll(w => w.PostId == postId);
        if (on) Settings.WatchedThreads.Add(new WatchedThread { PostId = postId, Title = title, Subreddit = subreddit, SeenComments = comments });
        Settings.Save();
        Status = on ? $"Watching “{title}”. You'll be told about new comments." : $"Stopped watching “{title}”.";
        _detail.RefreshSettings();
    }

    /// <summary>Starts or stops following someone's new posts.</summary>
    public void SetFollowing(string user, bool on)
    {
        if (user.Length == 0 || user == "[deleted]") return;
        Settings.FollowedUsers.RemoveAll(f => f.Name.Equals(user, StringComparison.OrdinalIgnoreCase));
        if (on) Settings.FollowedUsers.Add(new FollowedUser { Name = user });
        Settings.Save();
        Status = on ? $"Following u/{user}. Their new posts show up in Alerts." : $"Stopped following u/{user}.";
        _detail.RefreshSettings();
        foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.User && t.Target.Equals(user, StringComparison.OrdinalIgnoreCase)))
            tab.RefreshFollowLabel();
        // Start counting from their newest post now.
        if (on) _ = CheckAlertsNowAsync();
    }

    void OnThreadsUpdated(List<(WatchedThread Thread, Post Post, long NewComments)> updates)
    {
        var (thread, post, count) = updates[0];
        var title = updates.Count == 1
            ? $"💬 {count} new {(count == 1 ? "comment" : "comments")} in a thread you watch"
            : $"💬 New comments in {updates.Count} threads you watch";
        var text = updates.Count == 1 ? thread.Title : string.Join("\n", updates.Take(3).Select(u => $"• {u.NewComments} in {u.Thread.Title}"));
        Status = updates.Count == 1 ? $"{count} new comments in “{thread.Title}”." : $"New comments in {updates.Count} watched threads.";
        foreach (var copy in Tabs.SelectMany(t => t.Posts).Where(p => p.Id == thread.PostId)) copy.NewComments += count;
        AlertNotification?.Invoke(title, text, post);
    }

    // ---- Reminders and scheduled posts ----------------------------------------------------------

    readonly DispatcherTimer _laterTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    bool _postingScheduled;

    Window ActiveWindow => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    /// <summary>Asks when, then reminds you about a post at that time (even after a restart).</summary>
    public void Remind(string postId, string title, string subreddit, string permalink)
    {
        if (postId.Length == 0) return;
        var existing = Settings.Reminders.FirstOrDefault(r => r.PostId == postId);
        var prompt = existing == null
            ? "Remind me about this post:"
            : $"You'll be reminded {LaterTimes.Describe(DateTimeOffset.FromUnixTimeSeconds(existing.Due).LocalDateTime, DateTime.Now)}. Change it to:";
        var window = new WhenWindow("Remind me", prompt, title, LaterTimes.ReminderChoices) { Owner = ActiveWindow };
        if (window.ShowDialog() != true) return;
        Settings.Reminders.RemoveAll(r => r.PostId == postId);
        Settings.Reminders.Add(new Reminder
        {
            PostId = postId,
            Title = title,
            Subreddit = subreddit,
            Permalink = permalink.StartsWith('/') ? "https://www.reddit.com" + permalink : permalink,
            Due = new DateTimeOffset(window.When).ToUnixTimeSeconds(),
        });
        Settings.Save();
        Status = $"Reminder set for {LaterTimes.Describe(window.When, DateTime.Now)}. See them all under ⏰ Reminders.";
        _detail.RefreshSettings();
        _detail.RefreshLater();
    }

    /// <summary>Every half minute: due reminders pop up, due scheduled posts go out.</summary>
    async Task CheckLaterAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var due = Settings.Reminders.Where(r => r.Due <= now).OrderBy(r => r.Due).ToList();
        if (due.Count > 0)
        {
            Settings.Reminders.RemoveAll(r => r.Due <= now);
            Settings.Save();
            foreach (var reminder in due.Take(3))
                ReminderNotification?.Invoke("⏰ Reminder" + (reminder.Subreddit.Length > 0 ? $" · r/{reminder.Subreddit}" : ""),
                    reminder.Title, reminder.Permalink, true);
            Status = due.Count == 1 ? $"⏰ Reminder: {due[0].Title}" : $"⏰ {due.Count} reminders are due.";
            _detail.RefreshSettings();
            _detail.RefreshLater();
        }
        if (!_isReady) return;
        foreach (var post in Settings.ScheduledPosts.Where(p => p.Due <= now && p.Error == null && p.Profile == Settings.ActiveProfile).ToList())
            await PostScheduledAsync(post.Id, now: false);
    }

    /// <summary>Posts a scheduled post (when due, or straight away from the Reminders page).</summary>
    async Task PostScheduledAsync(string id, bool now)
    {
        var post = Settings.ScheduledPosts.FirstOrDefault(p => p.Id == id);
        if (post == null || _postingScheduled) return;
        if (post.Profile != Settings.ActiveProfile)
        {
            if (now) Status = $"That post is for u/{post.UserName}. Switch to that account and it'll go out.";
            return;
        }
        if (!_isReady) return;
        _postingScheduled = true;
        try
        {
            Status = $"Posting your scheduled post “{post.Title}”…";
            var url = await _services.Api.SubmitAsync(post.Subreddit, post.Title, post.IsLink, post.Body, post.Nsfw, post.Spoiler,
                new FlairChoice(post.FlairId, post.FlairText));
            Settings.ScheduledPosts.Remove(post);
            Status = $"Posted “{post.Title}” to r/{post.Subreddit}.";
            ReminderNotification?.Invoke("✅ Your scheduled post is up", $"r/{post.Subreddit}: {post.Title}",
                url.Length > 0 ? url : $"https://www.reddit.com/user/{post.UserName}/submitted/", false);
        }
        catch (Exception ex)
        {
            post.Error = ex.Message;
            Status = $"Couldn't post “{post.Title}”: {ex.Message}";
            ReminderNotification?.Invoke("⚠ Your scheduled post didn't go out", $"{post.Title}: {ex.Message}", "later", false);
        }
        finally
        {
            _postingScheduled = false;
            Settings.Save();
            _detail.RefreshLater();
        }
    }

    /// <summary>"Snooze" on a reminder's notification: remind again in an hour.</summary>
    public void SnoozeReminder(string title, string permalink)
    {
        var postId = RedditLinks.Classify(permalink) is ThreadLink thread ? thread.PostId : permalink;
        Settings.Reminders.RemoveAll(r => r.PostId == postId);
        Settings.Reminders.Add(new Reminder
        {
            PostId = postId,
            Title = title,
            Permalink = permalink,
            Due = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        });
        Settings.Save();
        _detail.RefreshSettings();
        _detail.RefreshLater();
    }

    /// <summary>A notification was clicked: a link, or "later" for the Reminders page.</summary>
    public void OpenNotificationTarget(string target)
    {
        if (target is "later" or "alerts" or "inbox") OpenFeed(target, newTab: false);
        else OpenUrl(target);
    }

    // ---- Alerts --------------------------------------------------------------------------------

    void ShowAlerts()
    {
        var window = new AlertsWindow(Settings.AlertRules, Settings.AlertIntervalMinutes, Settings.WatchedThreads, Settings.FollowedUsers)
        {
            Owner = Application.Current.MainWindow,
        };
        if (window.ShowDialog() != true) return;
        Settings.AlertRules = window.Result;
        Settings.AlertIntervalMinutes = window.IntervalMinutes;
        Settings.WatchedThreads = window.WatchedResult;
        Settings.FollowedUsers = window.FollowedResult;
        _detail.RefreshSettings();
        Settings.Save();
        _alertTimer.Stop();
        _alertTimer.Interval = AlertInterval;
        if (_isReady) _alertTimer.Start();
        var on = Settings.AlertRules.Count(r => r.Enabled);
        Status = on == 0 ? "No alerts are on." : $"Watching {on} {(on == 1 ? "alert" : "alerts")}. Checking now…";
        _ = CheckAlertsNowAsync();
    }

    async Task CheckAlertsNowAsync()
    {
        if (!_isReady) return;
        await _alertMonitor.CheckAsync();
        RefreshAlertTabs();
        if (Status.EndsWith("Checking now…")) Status = $"Alerts checked. {_services.Alerts.Count} matching posts so far.";
    }

    void RefreshAlertTabs()
    {
        foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.Alerts))
        {
            if (tab == SelectedTab) _ = tab.ReloadAsync();
            else tab.MarkStale();
        }
    }

    void OnAlertsMatched(List<(string Rule, Post Post)> found)
    {
        RefreshAlertTabs();
        var (rule, post) = found[0];
        var title = found.Count == 1 ? $"🔔 {rule} · r/{post.Subreddit}" : $"🔔 {found.Count} new posts match your alerts";
        var text = found.Count == 1 ? post.Title : string.Join("\n", found.Take(3).Select(f => "• " + f.Post.Title));
        Status = found.Count == 1 ? $"Alert “{rule}”: {post.Title}" : $"{found.Count} new posts match your alerts.";
        AlertNotification?.Invoke(title, text, found.Count == 1 ? post : null);
    }

    // ---- Quick reply ----------------------------------------------------------------------------

    /// <summary>An inbox item was marked read outside the inbox (from Quick reply).</summary>
    public void OnItemMarkedRead(Post item)
    {
        UnreadCount = Math.Max(0, UnreadCount - 1);
        foreach (var copy in Tabs.SelectMany(t => t.Posts).Where(p => p.FullName == item.FullName)) copy.IsUnread = false;
    }

    public RedditApi Api => _services.Api;
}

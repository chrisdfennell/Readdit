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
    // ---- Search box ---------------------------------------------------------------------------

    string _searchInput = "";
    public string SearchInput
    {
        get => _searchInput;
        set
        {
            if (!Set(ref _searchInput, value)) return;
            _suggestTimer.Stop();
            if (SuggestionQuery.Length >= 2) _suggestTimer.Start();
            else ShowSuggestions = false;
        }
    }

    string SuggestionQuery
    {
        get
        {
            var q = SearchInput.Trim();
            if (q.StartsWith("/")) q = q[1..];
            if (q.StartsWith("r/", StringComparison.OrdinalIgnoreCase)) q = q[2..];
            return q.StartsWith("http", StringComparison.OrdinalIgnoreCase) || q.StartsWith("u/") ? "" : q;
        }
    }

    IReadOnlyList<SidebarItem> _suggestions = [];
    public IReadOnlyList<SidebarItem> Suggestions { get => _suggestions; private set => Set(ref _suggestions, value); }

    bool _showSuggestions;
    public bool ShowSuggestions { get => _showSuggestions; set => Set(ref _showSuggestions, value); }

    async Task LoadSuggestionsAsync()
    {
        var query = SuggestionQuery;
        if (query.Length < 2 || !_isReady) return;
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        try
        {
            var subs = await _services.Api.AutocompleteAsync(query, cts.Token);
            if (cts.IsCancellationRequested || query != SuggestionQuery) return;
            foreach (var sub in subs.Where(s => s.Icon != null)) _icons.TryAdd(sub.Name, sub.Icon!);
            Suggestions = subs
                .Select(s => new SidebarItem($"r/{s.Name}  ·  {Format.Count(s.Subscribers)} members", "r/" + s.Name, s.Icon))
                .ToList();
            ShowSuggestions = Suggestions.Count > 0;
        }
        catch
        {
            // Suggestions are a nicety.
        }
    }

    bool _searchInSubreddit;
    public bool SearchInSubreddit { get => _searchInSubreddit; set => Set(ref _searchInSubreddit, value); }

    public bool CanSearchInSubreddit => SelectedTab?.IsSubreddit == true;
    public string SearchScopeLabel => CanSearchInSubreddit ? $"in r/{SelectedTab!.Target}" : "";

    /// <summary>"r/name", "u/name" or a Reddit URL navigates; anything else searches.</summary>
    void Go()
    {
        var input = SearchInput.Trim();
        ShowSuggestions = false;
        _suggestTimer.Stop();
        _suggestCts?.Cancel(); // an in-flight lookup would otherwise reopen the popup over the result
        if (input.Length == 0) return;

        if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            OpenUrl(input);
            return;
        }

        if (RedditLinks.ParseFeedInput(input) is { } feed)
        {
            OpenFeed(feed, newTab: false);
            return;
        }

        var scope = SearchInSubreddit && CanSearchInSubreddit ? SelectedTab!.Target : "";
        OpenFeed($"search:{scope}:{input}", newTab: true);
    }

    // ---- Votes and saves from the post list ---------------------------------------------------

    async Task VoteAsync(Post? post, bool up)
    {
        if (post == null || !post.CanVote) return;
        var previous = post.Likes;
        bool? target = up ? (previous == true ? null : true) : (previous == false ? null : false);
        foreach (var copy in CopiesOf(post)) copy.ApplyVote(target);
        _detail.InvalidatePrefetch(post.Id);
        if (SelectedPost == post) _detail.SyncPostState(post);
        try
        {
            await _services.Api.VoteAsync(post.FullName, target switch { true => 1, false => -1, _ => 0 });
        }
        catch (Exception ex)
        {
            foreach (var copy in CopiesOf(post)) copy.ApplyVote(previous);
            if (SelectedPost == post) _detail.SyncPostState(post);
            Status = $"Vote failed: {ex.Message}";
        }
    }

    /// <summary>The same post can be loaded in several tabs (Home and its subreddit, say).</summary>
    IEnumerable<Post> CopiesOf(Post post) =>
        Tabs.SelectMany(t => t.Posts).Where(p => p.FullName == post.FullName).Append(post).Distinct().ToList();

    async Task ToggleSaveAsync(Post? post)
    {
        if (post == null || post.Kind == ItemKind.Message) return;
        var save = !post.Saved;
        foreach (var copy in CopiesOf(post)) copy.Saved = save;
        _detail.InvalidatePrefetch(post.Id);
        if (SelectedPost == post) _detail.SyncPostState(post);
        try
        {
            await _services.Api.SaveAsync(post.FullName, save);
        }
        catch (Exception ex)
        {
            foreach (var copy in CopiesOf(post)) copy.Saved = !save;
            if (SelectedPost == post) _detail.SyncPostState(post);
            Status = $"Save failed: {ex.Message}";
            return;
        }
        Status = save ? "Saved." : "Removed from saved.";
        // Reddit has the change; the offline copy is separate and reports its own problems.
        if (post.Kind == ItemKind.Link) await _detail.UpdateOfflineCopyAsync(post.Id, save);
    }

    async Task HideAsync(Post? post)
    {
        var tab = SelectedTab;
        if (post == null || tab == null || post.Kind != ItemKind.Link) return;
        var index = tab.Posts.IndexOf(post);
        tab.Posts.Remove(post);
        if (tab.SelectedPost == post || tab.SelectedPost == null)
            tab.SelectedPost = tab.Posts.Count == 0 ? null : tab.Posts[Math.Clamp(index, 0, tab.Posts.Count - 1)];
        try
        {
            await _services.Api.HideAsync(post.FullName, hide: true);
            Status = "Post hidden. It won't show up in your feeds again.";
        }
        catch (Exception ex)
        {
            tab.Posts.Insert(Math.Clamp(index, 0, tab.Posts.Count), post);
            Status = $"Couldn't hide that post: {ex.Message}";
        }
    }

    void OnDetailPostStateChanged(string fullName, bool? likes, bool? saved)
    {
        // The page already updated itself; mirror the change onto list rows.
        foreach (var post in Tabs.SelectMany(t => t.Posts).Where(p => p.FullName == fullName))
        {
            if (saved.HasValue) post.Saved = saved.Value;
            else post.ApplyVote(likes);
        }
    }
}

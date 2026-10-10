using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Readdit;

public enum FeedKind { Home, Subreddit, Search, User, Saved, Inbox, Offline, ModQueue, Folder, Alerts }

/// <summary>
/// A feed's identity as text, used to save open tabs and in sidebar links: "home", "r/name",
/// "u/name", "search:scope:query" (empty scope = all of Reddit), "saved", "inbox", "offline",
/// "mod:name" ("mod:mod" = every community you moderate), "folder:name" (one of your folders), "alerts".
/// </summary>
public readonly record struct FeedSpec(FeedKind Kind, string Target = "", string? Scope = null)
{
    public static FeedSpec? Parse(string spec)
    {
        spec = spec.Trim();
        if (spec.StartsWith("search:"))
        {
            var rest = spec["search:".Length..];
            var colon = rest.IndexOf(':');
            if (colon < 0 || colon == rest.Length - 1) return null;
            var scope = rest[..colon];
            return new FeedSpec(FeedKind.Search, rest[(colon + 1)..], scope.Length > 0 ? scope : null);
        }
        if (spec.StartsWith("mod:", StringComparison.OrdinalIgnoreCase))
            return spec.Length > 4 ? new FeedSpec(FeedKind.ModQueue, spec[4..]) : null;
        if (spec.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            return spec.Length > 7 ? new FeedSpec(FeedKind.Folder, spec[7..]) : null;
        if (spec.StartsWith("r/", StringComparison.OrdinalIgnoreCase))
            return spec.Length > 2 ? new FeedSpec(FeedKind.Subreddit, spec[2..]) : null;
        if (spec.StartsWith("u/", StringComparison.OrdinalIgnoreCase))
            return spec.Length > 2 ? new FeedSpec(FeedKind.User, spec[2..]) : null;
        return spec switch
        {
            "home" => new FeedSpec(FeedKind.Home),
            "saved" => new FeedSpec(FeedKind.Saved),
            "inbox" => new FeedSpec(FeedKind.Inbox),
            "offline" => new FeedSpec(FeedKind.Offline),
            "alerts" => new FeedSpec(FeedKind.Alerts),
            _ => null,
        };
    }

    public override string ToString() => Kind switch
    {
        FeedKind.Home => "home",
        FeedKind.Subreddit => "r/" + Target,
        FeedKind.Search => $"search:{Scope}:{Target}",
        FeedKind.User => "u/" + Target,
        FeedKind.Saved => "saved",
        FeedKind.Inbox => "inbox",
        FeedKind.ModQueue => "mod:" + Target,
        FeedKind.Folder => "folder:" + Target,
        FeedKind.Alerts => "alerts",
        _ => "offline",
    };
}

/// <summary>One tab: a listing (subreddit, search, profile, inbox…) and its loaded posts.</summary>
public sealed class FeedViewModel : ObservableObject
{
    const int PageAttemptsWhenFiltered = 4;

    readonly Services _services;
    readonly HashSet<string> _seen = [];
    CancellationTokenSource? _cts;
    string? _after;

    public FeedViewModel(Services services, FeedKind kind, string target = "", string? scope = null)
    {
        _services = services;
        Kind = kind;
        Target = target;
        Scope = scope;
        _sort = Sorts.Count > 0 ? Sorts[0] : "";
        LoadMoreCommand = new RelayCommand(_ => _ = LoadPageAsync(_cts?.Token ?? default), _ => HasMore && !IsLoading);
        ShowNewCommand = new RelayCommand(_ => _ = ReloadAsync());
        HeaderActionCommand = new RelayCommand(_ => _ = RunHeaderActionAsync(), _ => HeaderActionLabel.Length > 0 && !_headerBusy);
    }

    public event Action<FeedViewModel, string>? StatusChanged;
    public event Action<RedditAuthException>? AuthFailed;
    public event Action? SubscriptionsChanged;
    public event Action? InboxRead;
    public event Action? ManageAlertsRequested;

    public FeedKind Kind { get; }
    public string Target { get; }
    public string? Scope { get; }

    public ObservableCollection<Post> Posts { get; } = [];
    public ICommand LoadMoreCommand { get; }
    public ICommand ShowNewCommand { get; }
    public ICommand HeaderActionCommand { get; }

    /// <summary>Serialized form used to restore tabs and route sidebar clicks.</summary>
    public string Spec => new FeedSpec(Kind, Target, Scope).ToString();

    public static FeedViewModel? FromSpec(Services services, string spec) =>
        FeedSpec.Parse(spec) is { } parsed ? new FeedViewModel(services, parsed.Kind, parsed.Target, parsed.Scope) : null;

    public string Title => Kind switch
    {
        FeedKind.Home => "🏠 Home",
        FeedKind.Subreddit => "r/" + Target,
        FeedKind.Search => "🔍 " + Target,
        FeedKind.User => "u/" + Target,
        FeedKind.Saved => "★ Saved",
        FeedKind.Inbox => "✉ Inbox",
        FeedKind.ModQueue => IsAllModQueue ? "🛡 Mod queue" : $"🛡 r/{Target}",
        FeedKind.Folder => "📁 " + Target,
        FeedKind.Alerts => "🔔 Alerts",
        _ => "📥 Offline",
    };

    /// <summary>The communities in this folder (Folder tabs only).</summary>
    List<string> FolderSubreddits =>
        _services.Settings.SubscriptionFolders
            .FirstOrDefault(f => f.Name.Equals(Target, StringComparison.OrdinalIgnoreCase))?.Subreddits ?? [];

    /// <summary>"r/mod" is Reddit's combined view of every community you moderate.</summary>
    bool IsAllModQueue => Target.Equals("mod", StringComparison.OrdinalIgnoreCase);

    /// <summary>r/all, r/popular, multireddits, home and search mix many communities.</summary>
    bool IsAggregate =>
        Kind is FeedKind.Home or FeedKind.Search or FeedKind.Folder ||
        (Kind == FeedKind.Subreddit && IsAggregateName(Target));

    /// <summary>r/all, r/popular (any case), r/all-excluded or a multireddit like a+b.</summary>
    public static bool IsAggregateName(string name) =>
        name.Equals("all", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("popular", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("all-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("popular-", StringComparison.OrdinalIgnoreCase) ||
        name.Contains('+');

    public bool IsSubreddit => Kind == FeedKind.Subreddit && !Target.Contains('+');

    public IReadOnlyList<string> Sorts => Kind switch
    {
        FeedKind.Home => ["best", "hot", "new", "top", "rising", "controversial"],
        FeedKind.Subreddit or FeedKind.Folder => ["hot", "new", "top", "rising", "controversial"],
        FeedKind.Search => ["relevance", "hot", "top", "new", "comments"],
        FeedKind.User => ["new", "hot", "top", "controversial"],
        // For queues the "sort" picks which queue to show.
        FeedKind.ModQueue => ["modqueue", "reports", "unmoderated", "spam", "edited"],
        _ => [],
    };

    public bool HasSorts => Sorts.Count > 0;

    public IReadOnlyList<string> TimeFilters { get; } = ["hour", "day", "week", "month", "year", "all"];

    string _sort;
    public string Sort
    {
        get => _sort;
        set
        {
            if (value == null || !Set(ref _sort, value)) return;
            Raise(nameof(UsesTimeFilter));
            _ = ReloadAsync();
        }
    }

    public bool UsesTimeFilter =>
        Sort is "top" or "controversial" || (Kind == FeedKind.Search && Sort is "relevance" or "comments");

    string _timeFilter = "day";
    public string TimeFilter
    {
        get => _timeFilter;
        set
        {
            if (value != null && Set(ref _timeFilter, value) && UsesTimeFilter) _ = ReloadAsync();
        }
    }

    Post? _selectedPost;
    bool _holdSelection;

    public Post? SelectedPost
    {
        get => _selectedPost;
        set
        {
            // The shared post list writes null while swapping to another tab's items; keep ours.
            if (value == null && _holdSelection)
            {
                Raise(nameof(SelectedPost));
                return;
            }
            Set(ref _selectedPost, value);
        }
    }

    /// <summary>Ignore null selections until the tab switch has settled.</summary>
    public void HoldSelection()
    {
        _holdSelection = true;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            _holdSelection = false;
            Raise(nameof(SelectedPost));
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    // ---- "N new posts" -----------------------------------------------------------------------

    int _newAvailable;
    /// <summary>Items at the top of the feed that aren't loaded yet (from the last background check).</summary>
    public int NewAvailable
    {
        get => _newAvailable;
        private set
        {
            if (!Set(ref _newAvailable, value)) return;
            Raise(nameof(HasNewPosts));
            Raise(nameof(NewPostsLabel));
        }
    }

    public bool HasNewPosts => NewAvailable > 0;

    public string NewPostsLabel => Kind == FeedKind.Inbox
        ? $"↑ {NewAvailable} new {(NewAvailable == 1 ? "message" : "messages")}"
        : $"↑ {NewAvailable}{(NewAvailable >= 50 ? "+" : "")} new {(NewAvailable == 1 ? "post" : "posts")}";

    bool _checkingForNew;

    /// <summary>
    /// Looks at the feed's first page and counts items that aren't loaded yet, without touching
    /// the list, so reading isn't interrupted. Clicking the "N new posts" pill reloads.
    /// </summary>
    public async Task CheckForNewAsync()
    {
        if (!_loaded || IsLoading || _checkingForNew || Kind is FeedKind.Offline or FeedKind.Saved or FeedKind.Alerts) return;
        _checkingForNew = true;
        try
        {
            var (items, _) = await _services.Api.GetListingAsync(BuildPath(), null, default);
            // Only genuinely new items: ranked feeds (best, hot) reshuffle all the time, so
            // "not loaded yet" alone would count old posts that just moved up.
            var newest = Posts.Count > 0 ? Posts.Max(p => p.Created) : DateTimeOffset.MinValue;
            NewAvailable = items.Count(i => !_seen.Contains(i.FullName) && i.Created > newest && !IsFiltered(i));
        }
        catch
        {
            // A background check; the next one or a manual refresh will report real problems.
        }
        finally
        {
            _checkingForNew = false;
        }
    }

    /// <summary>Takes a handled item out of a mod queue.</summary>
    public void DropFromQueue(string fullName)
    {
        if (Kind != FeedKind.ModQueue) return;
        var item = Posts.FirstOrDefault(p => p.FullName == fullName);
        if (item == null) return;
        var index = Posts.IndexOf(item);
        var wasSelected = SelectedPost == item;
        Posts.Remove(item);
        if (wasSelected) SelectedPost = Posts.Count == 0 ? null : Posts[Math.Min(index, Posts.Count - 1)];
    }

    /// <summary>Stop loading; the tab was closed or replaced.</summary>
    public void Close() => _cts?.Cancel();

    /// <summary>Reload next time the tab is shown (e.g. after filters change).</summary>
    public void MarkStale() => _loaded = false;

    bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    bool _hasMore;
    public bool HasMore { get => _hasMore; private set => Set(ref _hasMore, value); }

    bool _loaded;
    public bool IsLoaded => _loaded;

    string _status = "";
    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value)) StatusChanged?.Invoke(this, value);
        }
    }

    // ---- Header banner (subreddit info, profile, search suggestions…) -------------------------

    string _headerTitle = "";
    public string HeaderTitle
    {
        get => _headerTitle;
        private set
        {
            if (Set(ref _headerTitle, value)) Raise(nameof(HasHeader));
        }
    }

    public bool HasHeader => HeaderTitle.Length > 0;

    string _headerText = "";
    public string HeaderText { get => _headerText; private set => Set(ref _headerText, value); }

    string _headerActionLabel = "";
    public string HeaderActionLabel { get => _headerActionLabel; private set => Set(ref _headerActionLabel, value); }

    IReadOnlyList<SidebarItem> _headerChips = [];
    public IReadOnlyList<SidebarItem> HeaderChips { get => _headerChips; private set => Set(ref _headerChips, value); }

    bool _isSubscribed;

    // ---- Offline library search and tags ----------------------------------------------------------

    public const string AllTags = "All tags";

    public bool IsOffline => Kind == FeedKind.Offline;

    string _libraryQuery = "";
    public string LibraryQuery
    {
        get => _libraryQuery;
        set
        {
            if (value != null && Set(ref _libraryQuery, value)) _ = ReloadAsync();
        }
    }

    string _libraryTag = AllTags;
    public string LibraryTag
    {
        get => _libraryTag;
        set
        {
            if (value != null && Set(ref _libraryTag, value)) _ = ReloadAsync();
        }
    }

    public IReadOnlyList<string> LibraryTagOptions => [AllTags, .. _services.Offline.AllTags()];

    /// <summary>Reloads after tags change elsewhere.</summary>
    public Task RefreshLibraryAsync()
    {
        Raise(nameof(LibraryTagOptions));
        return ReloadAsync();
    }

    // ---- Loading ------------------------------------------------------------------------------

    public async Task ReloadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loaded = true;
        NewAvailable = 0;
        Posts.Clear();
        _seen.Clear();
        _after = null;
        HasMore = false;
        _selectedPost = null; // deliberate, so bypass the tab-switch hold
        Raise(nameof(SelectedPost));
        _ = LoadHeaderAsync(ct);
        await LoadPageAsync(ct);
    }

    async Task LoadPageAsync(CancellationToken ct)
    {
        IsLoading = true;
        Status = $"Loading {Title}…";
        var hidden = 0;
        try
        {
            if (Kind == FeedKind.Offline)
            {
                var tag = LibraryTag == AllTags ? null : LibraryTag;
                foreach (var post in _services.Offline.List(LibraryQuery, tag)) AddPost(post);
                var filtered = LibraryQuery.Length > 0 || tag != null;
                Status = Posts.Count == 0
                    ? filtered ? "No saved posts match." : "Nothing saved offline yet. Saving a post keeps a copy here."
                    : $"{Posts.Count} posts in your offline library";
                return;
            }

            if (Kind == FeedKind.Alerts)
            {
                foreach (var post in _services.Alerts.List())
                {
                    post.IsRead = _services.Read.Contains(post.FullName);
                    AddPost(post);
                }
                Status = Posts.Count == 0
                    ? _services.Settings.AlertRules.Count == 0
                        ? "No alerts yet. Click “Manage alerts…” to watch for keywords."
                        : "Nothing has matched your alerts yet."
                    : $"{Posts.Count} posts matched your alerts";
                return;
            }

            // Filters can empty a whole page, so keep paging a little before giving up.
            var added = 0;
            for (var attempt = 0; attempt < PageAttemptsWhenFiltered && added == 0; attempt++)
            {
                var (items, after) = await _services.Api.GetListingAsync(BuildPath(), _after, ct);
                ct.ThrowIfCancellationRequested();
                foreach (var item in items)
                {
                    if (!_seen.Add(item.FullName)) continue;
                    item.IsRead = _services.Read.Contains(item.FullName);
                    if (IsFiltered(item))
                    {
                        hidden++;
                        continue;
                    }
                    AddPost(item);
                    added++;
                }
                _after = after;
                HasMore = after != null;
                if (!HasMore) break;
            }

            Status = (Posts.Count == 0 ? $"Nothing to show in {Title}." : $"{Title} · {Posts.Count} items") +
                     (hidden > 0 ? $" · {hidden} hidden by filters" : "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (RedditAuthException ex)
        {
            Status = ex.Message;
            AuthFailed?.Invoke(ex);
        }
        catch (Exception ex)
        {
            Status = $"Couldn't load {Title}: {ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    void AddPost(Post post)
    {
        post.BlurThumbnail = _services.Settings.BlurNsfw && (post.Over18 || post.Spoiler);
        post.IsHighlightedAuthor = _services.IsHighlighted(post.Author);
        post.AuthorTag = _services.UserNotes.Get(post.Author)?.Tag ?? "";
        post.CanModerate = post.Kind != ItemKind.Message && _services.IsModeratorOf(post.Subreddit);
        if (post.Kind == ItemKind.Link && _services.Visits.Get(post.Id) is { } visit)
            post.NewComments = Math.Max(0, post.NumComments - visit.CommentCount);
        Posts.Add(post);
    }

    string BuildPath()
    {
        var time = UsesTimeFilter ? $"t={TimeFilter}" : "";
        return Kind switch
        {
            FeedKind.Home => $"/{Sort}.json?{time}",
            FeedKind.Subreddit => $"/r/{Target}/{Sort}.json?{time}",
            FeedKind.Search => (Scope == null ? "/search.json?" : $"/r/{Scope}/search.json?restrict_sr=on&") +
                               $"q={Uri.EscapeDataString(Target)}&sort={Sort}&{time}",
            FeedKind.User => $"/user/{Target}/overview.json?sort={Sort}&{time}",
            FeedKind.Saved => _services.Api.UserName is { } me
                ? $"/user/{me}/saved.json"
                : throw new InvalidOperationException("Readdit doesn't know your username yet. Refresh in a moment."),
            FeedKind.Inbox => "/message/inbox.json",
            FeedKind.ModQueue => $"/r/{Target}/about/{Sort}.json?",
            FeedKind.Folder => FolderSubreddits.Count > 0
                ? $"/r/{string.Join("+", FolderSubreddits.Take(MaxFolderSize))}/{Sort}.json?{time}"
                : throw new InvalidOperationException(
                    "This folder is empty. Right-click communities in the sidebar and choose \"Add to folder…\"."),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>Reddit combines up to about this many communities in one listing.</summary>
    public const int MaxFolderSize = 100;

    bool IsFiltered(Post p)
    {
        var s = _services.Settings;
        // Queues must show everything waiting for review, whatever your personal filters say.
        if (Kind is FeedKind.Saved or FeedKind.Inbox or FeedKind.ModQueue or FeedKind.Alerts) return false;
        if (Kind != FeedKind.User && _services.IsMuted(p.Author)) return true;
        if (s.HideNsfw && p.Over18 && (IsAggregate || Kind == FeedKind.User)) return true;
        if (s.HideRead && p.IsRead && Kind is FeedKind.Home or FeedKind.Subreddit or FeedKind.Search) return true;
        if (!IsAggregate) return false;

        // Fresh posts haven't had time to collect votes, so the score floor skips new/rising.
        if (s.MinScore is { } min && p.Score < min && Sort is not ("new" or "rising")) return true;

        if (s.KeywordFilters.Any(k => k.Length > 0 && p.Title.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (s.DomainFilters.Any(d => d.Length > 0 &&
                (p.Domain.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                 p.Domain.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))))
            return true;
        return s.SubredditFilters.Any(r => r.Length > 0 &&
            p.Subreddit.Equals(r.StartsWith("r/") ? r[2..] : r, StringComparison.OrdinalIgnoreCase));
    }

    async Task LoadHeaderAsync(CancellationToken ct)
    {
        HeaderTitle = HeaderText = HeaderActionLabel = "";
        HeaderChips = [];
        try
        {
            switch (Kind)
            {
                case FeedKind.Subreddit when IsSubreddit && !IsAggregate:
                    var sub = await _services.Api.GetSubredditAboutAsync(Target, ct);
                    if (sub == null || ct.IsCancellationRequested) return;
                    HeaderTitle = sub.Title.Length > 0 ? sub.Title : "r/" + sub.Name;
                    HeaderText = $"r/{sub.Name} · {Format.Count(sub.Subscribers)} members" +
                                 (sub.Description.Length > 0 ? "\n" + sub.Description : "");
                    _isSubscribed = sub.IsSubscribed;
                    HeaderActionLabel = _isSubscribed ? "✓ Joined" : "+ Join";
                    break;

                case FeedKind.User:
                    var user = await _services.Api.GetUserAboutAsync(Target, ct);
                    if (user == null || ct.IsCancellationRequested) return;
                    HeaderTitle = "u/" + user.Name;
                    HeaderText = user.Suspended
                        ? "This account has been suspended."
                        : $"{Format.Count(user.Karma)} karma · Redditor for {Format.Age(user.Created)}";
                    break;

                case FeedKind.Search:
                    HeaderTitle = $"Results for “{Target}”" + (Scope == null ? "" : $" in r/{Scope}");
                    if (Scope == null)
                    {
                        var subs = await _services.Api.SearchSubredditsAsync(Target, ct);
                        if (ct.IsCancellationRequested) return;
                        HeaderChips = subs.Select(s => new SidebarItem("r/" + s, "r/" + s)).ToList();
                        HeaderText = subs.Count > 0 ? "Matching communities:" : "";
                    }
                    break;

                case FeedKind.ModQueue:
                    HeaderTitle = IsAllModQueue ? "Mod queue: all your communities" : $"Mod queue: r/{Target}";
                    HeaderText = Sort switch
                    {
                        "reports" => "Reported posts and comments.",
                        "unmoderated" => "Posts no moderator has reviewed yet.",
                        "spam" => "Removed as spam, by moderators or Reddit's filters.",
                        "edited" => "Recently edited posts and comments.",
                        _ => "Reported and filtered items waiting for a decision. Right-click to approve or remove.",
                    };
                    break;

                case FeedKind.Folder:
                    var members = FolderSubreddits;
                    HeaderTitle = "📁 " + Target;
                    HeaderText = members.Count == 0
                        ? "No communities in this folder yet."
                        : $"{members.Count} {(members.Count == 1 ? "community" : "communities")}" +
                          (members.Count > MaxFolderSize ? $" (showing posts from the first {MaxFolderSize})" : "");
                    HeaderChips = members.Take(40).Select(s => new SidebarItem("r/" + s, "r/" + s)).ToList();
                    break;

                case FeedKind.Alerts:
                    var rules = _services.Settings.AlertRules;
                    HeaderTitle = "🔔 Alerts";
                    HeaderText = rules.Count == 0
                        ? "Get notified when a post matching your keywords appears, in any community or the ones you pick."
                        : $"Watching {rules.Count(r => r.Enabled)} of {rules.Count} alerts, every " +
                          $"{_services.Settings.AlertIntervalMinutes} minutes while Readdit runs (in the tray too).";
                    HeaderActionLabel = "Manage alerts…";
                    break;

                case FeedKind.Inbox:
                    HeaderTitle = "Inbox";
                    HeaderText = "Replies, mentions and private messages.";
                    HeaderActionLabel = "Mark all as read";
                    break;

                case FeedKind.Saved:
                    HeaderTitle = "Saved";
                    HeaderText = "Posts and comments you've saved on Reddit.";
                    break;

                case FeedKind.Offline:
                    HeaderTitle = "Offline library";
                    HeaderText = "Saved posts kept on this PC with their comments, images and videos. " +
                                 "Search covers titles, text and every saved comment.";
                    break;
            }
        }
        catch
        {
            // The banner is decoration; the listing reports real errors.
        }
        finally
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    bool _headerBusy;

    async Task RunHeaderActionAsync()
    {
        // A second click while Join/Leave is in flight would flip the state twice.
        if (_headerBusy) return;
        _headerBusy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (Kind == FeedKind.Alerts)
            {
                ManageAlertsRequested?.Invoke();
            }
            else if (Kind == FeedKind.Inbox)
            {
                await _services.Api.MarkAllReadAsync();
                Status = "Inbox marked as read.";
                InboxRead?.Invoke();
            }
            else if (Kind == FeedKind.Subreddit)
            {
                await _services.Api.SubscribeAsync(Target, !_isSubscribed);
                _isSubscribed = !_isSubscribed;
                HeaderActionLabel = _isSubscribed ? "✓ Joined" : "+ Join";
                Status = _isSubscribed ? $"Joined r/{Target}." : $"Left r/{Target}.";
                SubscriptionsChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            _headerBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

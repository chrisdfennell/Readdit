using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Readdit;

public sealed partial class MainViewModel : ObservableObject
{
    readonly Services _services;
    readonly DetailBridge _detail;
    readonly DispatcherTimer _inboxTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    readonly DispatcherTimer _suggestTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    CancellationTokenSource? _suggestCts;
    List<SubredditRef> _subscriptions = [];
    readonly Dictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);
    bool _isReady;          // signed in and the hidden browser is parked on reddit.com
    bool _awaitingLogin;    // watch navigations for a completed sign-in
    bool _checkingLogin;

    readonly InAppBrowser _browser;
    readonly UpdateService _updates;

    public MainViewModel(Services services, DetailBridge detail, InAppBrowser browser)
    {
        _services = services;
        _detail = detail;
        _browser = browser;
        _updates = new UpdateService(services.Settings);
        _ = _updates.WarmUpAsync();
        InstallUpdateCommand = new RelayCommand(_ => InstallUpdate(), _ => _updates.IsUpdateReady);
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesQuietlyAsync();
        // Only the tab you're looking at, and only while Readdit is on screen.
        _newPostsTimer.Tick += async (_, _) =>
        {
            if (Settings.CheckForNewPosts && Application.Current.MainWindow is { IsVisible: true } && SelectedTab is { } tab)
                await tab.CheckForNewAsync();
        };
        _browser.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(InAppBrowser.Url): Raise(nameof(BrowserUrl)); break;
                case nameof(InAppBrowser.Title): Raise(nameof(BrowserTitle)); break;
                case nameof(InAppBrowser.IsLoading): Raise(nameof(BrowserLoading)); break;
            }
            CommandManager.InvalidateRequerySuggested();
        };
        _browser.EscapePressed += () => ShowBrowser = false;
        BrowserBackCommand = new RelayCommand(_ => _browser.GoBack(), _ => _browser.CanGoBack);
        BrowserForwardCommand = new RelayCommand(_ => _browser.GoForward(), _ => _browser.CanGoForward);
        BrowserReloadCommand = new RelayCommand(_ => _browser.Reload());
        BrowserOpenExternalCommand = new RelayCommand(_ => OpenExternal(BrowserUrl));
        CloseBrowserCommand = new RelayCommand(_ => ShowBrowser = false);
        OpenLinkCommand = new RelayCommand(p => { if (p is string url) OpenLink(url); });
        Favorites = new ObservableCollection<string>(Settings.Favorites);

        _detail.OpenUrlRequested += url => OpenUrl(url);
        _detail.ExternalUrlRequested += url => OpenLink(url);
        _detail.KeyPressed += (key, ctrl, shift) => HandleKey(key, ctrl, shift);
        _detail.StatusMessage += message => Status = message;
        _detail.PostStateChanged += OnDetailPostStateChanged;
        _detail.ModActionDone += OnModActionDone;
        _detail.ThreadVisited += postId =>
        {
            foreach (var post in Tabs.SelectMany(t => t.Posts).Where(p => p.Id == postId)) post.NewComments = 0;
        };
        _inboxTimer.Tick += async (_, _) => await RefreshInboxAsync();
        _suggestTimer.Tick += async (_, _) =>
        {
            _suggestTimer.Stop();
            await LoadSuggestionsAsync();
        };

        GoCommand = new RelayCommand(_ => Go());
        RefreshCommand = new RelayCommand(_ => _ = SelectedTab?.ReloadAsync(), _ => _isReady);
        OpenFeedCommand = new RelayCommand(p => { if (p is string s) OpenFeed(s, newTab: false); });
        OpenInNewTabCommand = new RelayCommand(p => { if (p is string s) OpenFeed(s, newTab: true); });
        NewTabCommand = new RelayCommand(_ => OpenFeed("home", newTab: true));
        CloseTabCommand = new RelayCommand(p => CloseTab(p as FeedViewModel ?? SelectedTab));
        ToggleFavoriteCommand = new RelayCommand(_ => ToggleFavorite(), _ => SelectedTab?.IsSubreddit == true);
        AddFavoriteCommand = new RelayCommand(p => { if (p is string s) AddFavorite(s); });
        RemoveFavoriteCommand = new RelayCommand(p => { if (p is string s) RemoveFavorite(s); });
        VoteCommand = new RelayCommand(p => _ = VoteAsync(p as Post ?? SelectedPost, up: true));
        DownvoteCommand = new RelayCommand(p => _ = VoteAsync(p as Post ?? SelectedPost, up: false));
        ToggleSaveCommand = new RelayCommand(p => _ = ToggleSaveAsync(p as Post ?? SelectedPost));
        OpenInBrowserCommand = new RelayCommand(p => OpenExternal(p as string));
        CopyLinkCommand = new RelayCommand(p =>
        {
            if (p is not string s) return;
            Clipboard.SetText(s);
            Status = "Link copied to clipboard.";
        });
        AccountCommand = new RelayCommand(_ => _ = ShowAccountAsync(), _ => !ShowLogin);
        ContinueCommand = new RelayCommand(_ => _ = ContinueAsync(), _ => !_checkingLogin);
        ContinueAnywayCommand = new RelayCommand(_ => _ = TryCompleteLoginAsync(force: true), _ => !_checkingLogin);
        PreferencesCommand = new RelayCommand(_ => ShowPreferences());
        SubmitCommand = new RelayCommand(_ => ShowSubmit(), _ => _isReady);
        ShortcutsCommand = new RelayCommand(_ => new ShortcutsWindow { Owner = Application.Current.MainWindow }.ShowDialog());
        RefreshSubscriptionsCommand = new RelayCommand(_ => _ = LoadSubscriptionsAsync(), _ => _isReady);
        ToggleReadingModeCommand = new RelayCommand(_ => IsReadingMode = !IsReadingMode);
        HideCommand = new RelayCommand(p => _ = HideAsync(p as Post ?? SelectedPost));
        ApproveCommand = new RelayCommand(p => _ = ModerateAsync(p as Post ?? SelectedPost, "approve"));
        RemoveCommand = new RelayCommand(p => _ = ModerateAsync(p as Post ?? SelectedPost, "remove"));
        SpamCommand = new RelayCommand(p => _ = ModerateAsync(p as Post ?? SelectedPost, "spam"));
        OpenAuthorCommand = new RelayCommand(p =>
        {
            if (p is Post { Author.Length: > 0 } post && post.Author != "[deleted]") OpenFeed("u/" + post.Author, newTab: true);
        });
        OpenPostSubredditCommand = new RelayCommand(p =>
        {
            if (p is Post { Subreddit.Length: > 0 } post) OpenFeed("r/" + post.Subreddit, newTab: true);
        });
        PickSuggestionCommand = new RelayCommand(p =>
        {
            if (p is not string spec) return;
            ShowSuggestions = false;
            _searchInput = "";
            Raise(nameof(SearchInput));
            OpenFeed(spec, newTab: false);
        });
        EditTagsCommand = new RelayCommand(p => EditTags(p as Post ?? SelectedPost));
        BackToAccountCommand = new RelayCommand(_ => { if (PreviousAccount is { } a) SwitchAccount(a.Profile); });
        DownloadCommand = new RelayCommand(p =>
        {
            // The page downloads whatever post it shows, so show the right-clicked one first.
            if (p is Post post && SelectedTab != null && SelectedPost != post) SelectedTab.SelectedPost = post;
            _detail.RequestDownload();
        });
        BiggerTextCommand = new RelayCommand(_ => ReadingFontSize++);
        SmallerTextCommand = new RelayCommand(_ => ReadingFontSize--);
    }

    AppSettings Settings => _services.Settings;

    /// <summary>Raised to ask the window to focus the search box.</summary>
    public event Action? FocusSearchRequested;

    /// <summary>Raised when new inbox items arrive: notification title and text.</summary>
    public event Action<string, string>? InboxNotification;

    readonly HashSet<string> _notified = [];
    bool _inboxPrimed;

    public bool CloseToTray => Settings.CloseToTray;

    /// <summary>True the first time the window hides to the tray, so the user learns where it went.</summary>
    public bool TakeTrayHint()
    {
        if (Settings.TrayHintShown) return false;
        Settings.TrayHintShown = true;
        Settings.Save();
        return true;
    }

    public ObservableCollection<FeedViewModel> Tabs { get; } = [];
    public ObservableCollection<string> Favorites { get; }

    public IReadOnlyList<SidebarItem> Feeds { get; } =
    [
        new("🏠 Home", "home"),
        new("🔥 Popular", "r/popular", IsFeed: true),
        new("🌐 All", "r/all", IsFeed: true),
        new("★ Saved", "saved"),
        new("✉ Inbox", "inbox"),
        new("📥 Offline library", "offline"),
        new("👤 My profile", "u/me"),
    ];

    public ICommand GoCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenFeedCommand { get; }
    public ICommand OpenInNewTabCommand { get; }
    public ICommand NewTabCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand AddFavoriteCommand { get; }
    public ICommand RemoveFavoriteCommand { get; }
    public ICommand VoteCommand { get; }
    public ICommand DownvoteCommand { get; }
    public ICommand ToggleSaveCommand { get; }
    public ICommand OpenInBrowserCommand { get; }
    public ICommand CopyLinkCommand { get; }
    public ICommand AccountCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand ContinueAnywayCommand { get; }
    public ICommand PreferencesCommand { get; }
    public ICommand SubmitCommand { get; }
    public ICommand ShortcutsCommand { get; }
    public ICommand RefreshSubscriptionsCommand { get; }
    public ICommand BrowserBackCommand { get; }
    public ICommand BrowserForwardCommand { get; }
    public ICommand BrowserReloadCommand { get; }
    public ICommand BrowserOpenExternalCommand { get; }
    public ICommand CloseBrowserCommand { get; }
    public ICommand OpenLinkCommand { get; }

    // ---- In-app browser -------------------------------------------------------------------------

    bool _showBrowser;
    public bool ShowBrowser
    {
        get => _showBrowser;
        set
        {
            if (!Set(ref _showBrowser, value)) return;
            if (!value) _browser.Unload();
        }
    }

    public string BrowserUrl => _browser.Url;
    public string BrowserTitle => _browser.Title;
    public bool BrowserLoading => _browser.IsLoading;

    /// <summary>Opens a web link in the in-app browser pane (or the default browser, if preferred).</summary>
    void OpenLink(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return;
        if (!Settings.OpenLinksInApp)
        {
            OpenExternal(url);
            return;
        }
        _browser.Navigate(uri.AbsoluteUri);
        ShowBrowser = true;
    }

    public ICommand ToggleReadingModeCommand { get; }
    public ICommand HideCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand SpamCommand { get; }

    // ---- Moderation -------------------------------------------------------------------------

    List<SubredditRef> _moderated = [];

    public bool HasModeration => _moderated.Count > 0;

    /// <summary>The combined queue first, then a queue per community you moderate.</summary>
    public IReadOnlyList<SidebarItem> ModerationItems =>
        _moderated.Count == 0
            ? []
            : [new SidebarItem("🛡 Mod queue (all)", "mod:mod"),
               .. _moderated.Where(s => MatchesFilter(s.Name)).Select(s => new SidebarItem("r/" + s.Name, "mod:" + s.Name, s.Icon))];

    async Task LoadModeratedAsync()
    {
        try
        {
            _moderated = await _services.Api.GetModeratedAsync();
            _services.Moderated.Clear();
            foreach (var sub in _moderated) _services.Moderated.Add(sub.Name);
            Raise(nameof(HasModeration));
            Raise(nameof(ModerationItems));
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
    public ICommand OpenAuthorCommand { get; }
    public ICommand OpenPostSubredditCommand { get; }
    public ICommand PickSuggestionCommand { get; }
    public ICommand InstallUpdateCommand { get; }

    bool _updateReady;
    public bool UpdateReady { get => _updateReady; private set => Set(ref _updateReady, value); }

    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    readonly DispatcherTimer _newPostsTimer = new() { Interval = TimeSpan.FromSeconds(90) };

    public string UpdateButtonLabel => $"⬆ Restart to update to {_updates.ReadyVersion}";

    async Task CheckForUpdatesQuietlyAsync()
    {
        if (!Settings.AutoUpdate || string.IsNullOrWhiteSpace(Settings.UpdateUrl)) return;
        var wasReady = UpdateReady;
        var message = await _updates.CheckAsync();
        UpdateReady = _updates.IsUpdateReady;
        Raise(nameof(UpdateButtonLabel));
        if (UpdateReady && !wasReady) Status = message;
    }

    /// <summary>Raised just before Velopack exits the process to install an update.</summary>
    public event Action? ExitingForUpdate;

    void InstallUpdate()
    {
        try
        {
            if (_updates.IsInstalledCopy) ExitingForUpdate?.Invoke();
            // Portable: files are swapped, so restart into the new exe. Installed: Velopack restarts itself.
            if (_updates.Apply()) RestartRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Status = $"Couldn't install the update: {ex.Message}";
        }
    }

    /// <summary>Called as Readdit exits: installs a downloaded update quietly if auto-update is on.</summary>
    public void OnExit()
    {
        if (Settings.AutoUpdate && _updates.IsUpdateReady) _updates.ApplyOnExit();
    }

    public ICommand EditTagsCommand { get; }
    public ICommand BackToAccountCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand BiggerTextCommand { get; }
    public ICommand SmallerTextCommand { get; }

    // ---- Layout and reading ---------------------------------------------------------------------

    public IReadOnlyList<string> LayoutModes { get; } = ["Compact", "Cards", "Grid"];

    public string LayoutMode
    {
        get => Settings.LayoutMode;
        set
        {
            if (value == null || value == Settings.LayoutMode) return;
            Settings.LayoutMode = value;
            Settings.Save();
            Raise(nameof(LayoutMode));
        }
    }

    bool _isReadingMode;
    /// <summary>Post view fills the window; sidebar and list are hidden.</summary>
    public bool IsReadingMode
    {
        get => _isReadingMode;
        set
        {
            if (!Set(ref _isReadingMode, value)) return;
            _detail.SetDisplay(ReadingFontSize, value);
            Raise(nameof(ReadingModeLabel));
        }
    }

    public string ReadingModeLabel => IsReadingMode ? "⤡ Exit reading" : "⤢ Read";

    public int ReadingFontSize
    {
        get => Settings.ReadingFontSize;
        set
        {
            value = Math.Clamp(value, 12, 24);
            if (value == Settings.ReadingFontSize) return;
            Settings.ReadingFontSize = value;
            Settings.Save();
            Raise(nameof(ReadingFontSize));
            _detail.SetDisplay(value, IsReadingMode);
        }
    }

    // ---- Tabs -------------------------------------------------------------------------------

    FeedViewModel? _selectedTab;
    public FeedViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab == value) return;
            if (_selectedTab != null) _selectedTab.PropertyChanged -= OnTabPropertyChanged;
            // All tabs share one ListBox; while it swaps lists it pushes a null selection back.
            _selectedTab?.HoldSelection();
            value?.HoldSelection();
            Set(ref _selectedTab, value);
            if (value != null)
            {
                value.PropertyChanged += OnTabPropertyChanged;
                if (!value.IsLoaded && _isReady) _ = value.ReloadAsync();
                Status = value.Status;
                Settings.SelectedTab = Tabs.IndexOf(value);
                Settings.Save();
            }
            Raise(nameof(WindowTitle));
            Raise(nameof(FavoriteLabel));
            Raise(nameof(SearchScopeLabel));
            Raise(nameof(CanSearchInSubreddit));
            ShowSelectedPost();
        }
    }

    Post? SelectedPost => SelectedTab?.SelectedPost;

    public string WindowTitle => SelectedTab == null ? "Readdit" : $"{SelectedTab.Title} · Readdit";

    void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FeedViewModel.SelectedPost)) ShowSelectedPost();
    }

    void ShowSelectedPost()
    {
        var post = SelectedPost;
        ShowBrowser = false;
        if (post != null && post.Kind == ItemKind.Link)
        {
            post.IsRead = true;
            _services.Read.Mark(post.FullName);
        }
        if (post is { IsUnread: true }) _ = MarkInboxItemReadAsync(post);
        _ = ShowAndPrefetchAsync(post);
    }

    /// <summary>Opening an unread inbox item marks it read on Reddit, like reddit.com does.</summary>
    async Task MarkInboxItemReadAsync(Post item)
    {
        item.IsUnread = false;
        UnreadCount = Math.Max(0, UnreadCount - 1);
        try
        {
            await _services.Api.MarkReadAsync(item.FullName);
        }
        catch
        {
            // Harmless: it stays unread on Reddit and the next inbox poll corrects the count.
        }
    }

    async Task ShowAndPrefetchAsync(Post? post)
    {
        await _detail.ShowAsync(post);

        // Warm up the next post's comments so moving down the list is instant.
        var tab = SelectedTab;
        if (post == null || tab == null) return;
        var index = tab.Posts.IndexOf(post);
        if (index >= 0 && index + 1 < tab.Posts.Count) _ = _detail.PrefetchAsync(tab.Posts[index + 1]);
    }

    FeedViewModel? AddTab(string spec, bool select = true)
    {
        if (spec == "u/me") spec = "u/" + (_services.Api.UserName ?? "me");
        var feed = FeedViewModel.FromSpec(_services, spec);
        if (feed == null) return null;

        feed.StatusChanged += (f, status) => { if (f == SelectedTab) Status = status; };
        feed.AuthFailed += ex => { if (Tabs.Contains(feed)) _ = HandleAuthFailureAsync(ex); };
        feed.SubscriptionsChanged += () => _ = LoadSubscriptionsAsync();
        feed.InboxRead += () =>
        {
            UnreadCount = 0;
            foreach (var item in feed.Posts) item.IsUnread = false;
        };
        Tabs.Add(feed);
        if (select) SelectedTab = feed;
        SaveTabs();
        return feed;
    }

    void OpenFeed(string spec, bool newTab)
    {
        if (spec is "u/me" or "saved" && _services.Api.UserName == null)
        {
            // "u/me" would otherwise open the profile of a real user called "me".
            Status = "Readdit doesn't know your username yet. Try again in a moment.";
            _ = RefreshInboxAsync(); // also refreshes the identity
            return;
        }
        if (spec == "u/me") spec = "u/" + _services.Api.UserName;

        // Reuse an existing tab for the same feed rather than piling up duplicates.
        var existing = Tabs.FirstOrDefault(t => t.Spec.Equals(spec, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectedTab = existing;
            return;
        }

        if (newTab || SelectedTab == null)
        {
            AddTab(spec);
            return;
        }

        // Replace the current tab in place.
        var index = Tabs.IndexOf(SelectedTab);
        var feed = AddTab(spec, select: false);
        if (feed == null) return;
        Tabs.Move(Tabs.Count - 1, index + 1);
        var old = SelectedTab;
        SelectedTab = feed;
        Tabs.Remove(old);
        old.Close();
        SaveTabs();
    }

    void CloseTab(FeedViewModel? tab)
    {
        if (tab == null) return;
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        tab.Close();
        if (Tabs.Count == 0) AddTab("home");
        else if (SelectedTab == tab || SelectedTab == null) SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        SaveTabs();
    }

    void SaveTabs()
    {
        Settings.OpenTabs = Tabs.Select(t => t.Spec).ToList();
        Settings.SelectedTab = SelectedTab == null ? 0 : Math.Max(0, Tabs.IndexOf(SelectedTab));
        Settings.Save();
    }

    void CycleTab(int delta)
    {
        if (Tabs.Count == 0 || SelectedTab == null) return;
        var index = (Tabs.IndexOf(SelectedTab) + delta + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[index];
    }

    // ---- Sidebar ------------------------------------------------------------------------------

    string _sidebarFilter = "";
    public string SidebarFilter
    {
        get => _sidebarFilter;
        set
        {
            if (!Set(ref _sidebarFilter, value)) return;
            Raise(nameof(FavoriteItems));
            Raise(nameof(SubscriptionItems));
            Raise(nameof(ModerationItems));
            Raise(nameof(ShowModeration));
            Raise(nameof(ShowFavorites));
            Raise(nameof(ShowSubscriptions));
        }
    }

    bool MatchesFilter(string name) =>
        SidebarFilter.Length == 0 || name.Contains(SidebarFilter.Trim(), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SidebarItem> FavoriteItems =>
        Favorites.Where(MatchesFilter).Select(f => new SidebarItem("r/" + f, "r/" + f, _icons.GetValueOrDefault(f))).ToList();

    public IReadOnlyList<SidebarItem> SubscriptionItems =>
        _subscriptions.Where(s => MatchesFilter(s.Name))
            .Select(s => new SidebarItem("r/" + s.Name, "r/" + s.Name, s.Icon))
            .ToList();

    // ---- Collapsible sidebar sections (remembered in settings) ---------------------------------

    bool IsExpanded(string section) =>
        !Settings.CollapsedSidebarSections.Contains(section, StringComparer.OrdinalIgnoreCase);

    void SetExpanded(string section, bool expanded, params string[] changed)
    {
        if (expanded == IsExpanded(section)) return;
        if (expanded) Settings.CollapsedSidebarSections.RemoveAll(s => s.Equals(section, StringComparison.OrdinalIgnoreCase));
        else Settings.CollapsedSidebarSections.Add(section);
        Settings.Save();
        foreach (var name in changed) Raise(name);
    }

    bool IsFiltering => SidebarFilter.Trim().Length > 0;

    public bool FeedsExpanded
    {
        get => IsExpanded("feeds");
        set => SetExpanded("feeds", value, nameof(FeedsExpanded));
    }

    public bool ModerationExpanded
    {
        get => IsExpanded("moderation");
        set => SetExpanded("moderation", value, nameof(ModerationExpanded), nameof(ShowModeration));
    }

    public bool FavoritesExpanded
    {
        get => IsExpanded("favorites");
        set => SetExpanded("favorites", value, nameof(FavoritesExpanded), nameof(ShowFavorites));
    }

    public bool SubscriptionsExpanded
    {
        get => IsExpanded("subscriptions");
        set => SetExpanded("subscriptions", value, nameof(SubscriptionsExpanded), nameof(ShowSubscriptions));
    }

    // A collapsed section still shows its matches while you filter.
    public bool ShowModeration => ModerationExpanded || IsFiltering;
    public bool ShowFavorites => FavoritesExpanded || IsFiltering;
    public bool ShowSubscriptions => SubscriptionsExpanded || IsFiltering;

    public string FavoritesHeader => Favorites.Count == 0 ? "FAVORITES" : $"FAVORITES ({Favorites.Count})";

    public string SubscriptionsHeader =>
        _subscriptions.Count == 0 ? "SUBSCRIPTIONS" : $"SUBSCRIPTIONS ({_subscriptions.Count})";

    async Task LoadSubscriptionsAsync()
    {
        try
        {
            _subscriptions = await _services.Api.GetSubscriptionsAsync();
            foreach (var sub in _subscriptions.Where(s => s.Icon != null)) _icons[sub.Name] = sub.Icon!;

            // Favorites come from Reddit (the communities you've starred); the saved list is just
            // a cache so the sidebar isn't empty before this loads.
            Favorites.Clear();
            foreach (var sub in _subscriptions.Where(s => s.IsFavorite)) Favorites.Add(sub.Name);
            SaveFavorites();

            Raise(nameof(SubscriptionItems));
            Raise(nameof(SubscriptionsHeader));
        }
        catch (Exception ex)
        {
            Status = $"Couldn't load your subscriptions: {ex.Message}";
        }
    }

    public string FavoriteLabel =>
        SelectedTab?.IsSubreddit == true &&
        Favorites.Contains(SelectedTab.Target, StringComparer.OrdinalIgnoreCase)
            ? "★ Favorited"
            : "☆ Favorite";

    void ToggleFavorite()
    {
        if (SelectedTab?.IsSubreddit != true) return;
        var name = SelectedTab.Target;
        _ = SetFavoriteAsync(name, !Favorites.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    void AddFavorite(string spec)
    {
        if (!spec.StartsWith("r/")) return; // only communities can be favorites
        _ = SetFavoriteAsync(spec[2..], true);
    }

    void RemoveFavorite(string spec) =>
        _ = SetFavoriteAsync(spec.StartsWith("r/") ? spec[2..] : spec, false);

    /// <summary>Stars or unstars a community on Reddit, updating the sidebar straight away.</summary>
    async Task SetFavoriteAsync(string name, bool favorite)
    {
        string? Find() => Favorites.FirstOrDefault(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
        var existing = Find();
        if (favorite == (existing != null)) return;

        // Reddit's favorites are a flag on communities you've joined, so a favorite of an unjoined
        // community wouldn't survive the next sync.
        if (favorite && _subscriptions.Count > 0 &&
            !_subscriptions.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = $"Join r/{name} first. Reddit only keeps favorites for communities you've joined.";
            return;
        }

        if (favorite) Favorites.Add(name);
        else Favorites.Remove(existing!);
        SaveFavorites();
        try
        {
            await _services.Api.FavoriteAsync(name, favorite);
            Status = favorite ? $"Added r/{name} to your Reddit favorites." : $"Removed r/{name} from your Reddit favorites.";
        }
        catch (Exception ex)
        {
            // Put it back the way Reddit has it (a subscriptions reload may already have).
            if (favorite && Find() is { } added) Favorites.Remove(added);
            else if (!favorite && Find() == null) Favorites.Add(existing!);
            SaveFavorites();
            Status = $"Couldn't update favorites on Reddit: {ex.Message}";
        }
    }

    void SaveFavorites()
    {
        Settings.Favorites = [.. Favorites];
        Settings.Save();
        Raise(nameof(FavoriteLabel));
        Raise(nameof(FavoriteItems));
        Raise(nameof(HasNoFavorites));
        Raise(nameof(FavoritesHeader));
    }

    public bool HasNoFavorites => Favorites.Count == 0;

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

        var target = SubredditOrUser().Match(input);
        if (target.Success)
        {
            var prefix = target.Groups[1].Value.ToLowerInvariant() == "r" ? "r/" : "u/";
            OpenFeed(prefix + target.Groups[2].Value, newTab: false);
            return;
        }

        var scope = SearchInSubreddit && CanSearchInSubreddit ? SelectedTab!.Target : "";
        OpenFeed($"search:{scope}:{input}", newTab: true);
    }

    [GeneratedRegex(@"^/?(r|u|user)/([A-Za-z0-9_+-]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex SubredditOrUser();

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

    // ---- Links ------------------------------------------------------------------------------

    /// <summary>Opens Reddit links inside Readdit and everything else in the default browser.</summary>
    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        var host = uri.Host.ToLowerInvariant();

        if (host == "redd.it" && uri.AbsolutePath.Trim('/') is { Length: > 0 } shortId)
        {
            ShowBrowser = false;
            _ = _detail.OpenThreadAsync(shortId, null);
            return;
        }

        if (host == "reddit.com" || host.EndsWith(".reddit.com"))
        {
            var path = uri.AbsolutePath;
            var thread = RedditThread().Match(path);
            if (thread.Success)
            {
                ShowBrowser = false;
                var comment = thread.Groups[2].Success ? thread.Groups[2].Value : null;
                _ = _detail.OpenThreadAsync(thread.Groups[1].Value, comment);
                return;
            }
            var feed = RedditFeedPath().Match(path);
            if (feed.Success)
            {
                var prefix = feed.Groups[1].Value.ToLowerInvariant() == "r" ? "r/" : "u/";
                OpenFeed(prefix + feed.Groups[2].Value, newTab: true);
                return;
            }
        }

        OpenLink(url);
    }

    [GeneratedRegex(@"/comments/([a-z0-9]+)(?:/[^/]*/([a-z0-9]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex RedditThread();

    /// <summary>Subreddit or profile pages, including sorted/sub-pages like /r/x/top or /user/x/submitted.</summary>
    [GeneratedRegex(@"^/(r|u|user)/([A-Za-z0-9_+-]+)(?:/(?:hot|new|top|rising|controversial|best|submitted|comments|overview|about)/?)?/?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RedditFeedPath();

    void OpenExternal(string? url)
    {
        // Only hand web links to the shell; never arbitrary schemes from page content.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"Couldn't open browser: {ex.Message}";
        }
    }

    // ---- Keyboard ---------------------------------------------------------------------------

    /// <summary>Global shortcuts; key names follow the web's KeyboardEvent.key values.</summary>
    public bool HandleKey(string key, bool ctrl, bool shift)
    {
        if (!_isReady) return false;

        if (ctrl)
        {
            switch (key.ToLowerInvariant())
            {
                case "t": NewTabCommand.Execute(null); return true;
                case "w": CloseTab(SelectedTab); return true;
                case "tab": CycleTab(shift ? -1 : 1); return true;
                case "l": FocusSearchRequested?.Invoke(); return true;
                case "f": _detail.OpenFind(); return true;
                case "r": RefreshCommand.Execute(null); return true;
                default: return false;
            }
        }

        switch (key)
        {
            case "Escape" when ShowBrowser: ShowBrowser = false; return true;
            case "Escape" when IsReadingMode: IsReadingMode = false; return true;
            case "f": IsReadingMode = !IsReadingMode; return true;
            case "x": _ = HideAsync(SelectedPost); return true;
            case "N": _detail.NextNewComment(); return true;
            case "d": _detail.RequestDownload(); return true;
            case "J": _detail.NavigateComments(1); return true;
            case "K": _detail.NavigateComments(-1); return true;
            case "+" or "=": ReadingFontSize++; return true;
            case "-": ReadingFontSize--; return true;
            case "1": LayoutMode = "Compact"; return true;
            case "2": LayoutMode = "Cards"; return true;
            case "3": LayoutMode = "Grid"; return true;
            case "j": MoveSelection(1); return true;
            case "k": MoveSelection(-1); return true;
            case "a": _ = VoteAsync(SelectedPost, up: true); return true;
            case "z": _ = VoteAsync(SelectedPost, up: false); return true;
            case "s": _ = ToggleSaveAsync(SelectedPost); return true;
            case "o":
                if (SelectedPost != null) OpenLink(SelectedPost.HasLink ? SelectedPost.Url : SelectedPost.PermalinkUrl);
                return true;
            case "O": if (SelectedPost != null) OpenExternal(SelectedPost.HasLink ? SelectedPost.Url : SelectedPost.PermalinkUrl); return true;
            case "r": case "F5": RefreshCommand.Execute(null); return true;
            case "m":
                if (SelectedTab?.LoadMoreCommand.CanExecute(null) == true) SelectedTab.LoadMoreCommand.Execute(null);
                return true;
            case "/": FocusSearchRequested?.Invoke(); return true;
            case "n": SubmitCommand.Execute(null); return true;
            case "i": OpenFeed("inbox", newTab: false); return true;
            case "h": OpenFeed("home", newTab: false); return true;
            case " ": _detail.Scroll(shift ? -1 : 1); return true;
            case "?": ShortcutsCommand.Execute(null); return true;
            default: return false;
        }
    }

    void MoveSelection(int delta)
    {
        var tab = SelectedTab;
        if (tab == null || tab.Posts.Count == 0) return;
        var index = tab.SelectedPost == null ? (delta > 0 ? 0 : tab.Posts.Count - 1) : tab.Posts.IndexOf(tab.SelectedPost) + delta;
        if (index >= tab.Posts.Count && tab.LoadMoreCommand.CanExecute(null)) tab.LoadMoreCommand.Execute(null);
        tab.SelectedPost = tab.Posts[Math.Clamp(index, 0, tab.Posts.Count - 1)];
    }

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
            InboxNotification?.Invoke(title, body.Length > 200 ? body[..200] + "…" : body);
        }
        catch
        {
            InboxNotification?.Invoke("New message on Reddit", Status);
        }
    }

    // ---- Dialogs ----------------------------------------------------------------------------

    void ShowPreferences()
    {
        var window = new PreferencesWindow(Settings, _updates) { Owner = Application.Current.MainWindow };
        var saved = window.ShowDialog() == true;
        UpdateReady = _updates.IsUpdateReady;
        Raise(nameof(UpdateButtonLabel));
        if (!saved) return;
        Settings.Save();
        ApplyAppearance();
        _detail.SetDisplay(ReadingFontSize, IsReadingMode);
        Raise(nameof(ReadingFontSize));
        // Filters changed: reload this tab now, and the others when they're next opened.
        foreach (var tab in Tabs.Where(t => t != SelectedTab)) tab.MarkStale();
        _ = SelectedTab?.ReloadAsync();
    }

    /// <summary>Raised after theme/scale changes so the window can restyle itself.</summary>
    public event Action? AppearanceChanged;

    public double UiScale => Math.Clamp(Settings.UiScale, 0.8, 1.5);
    public bool IsBlackTheme => Settings.Theme == "Black";

    void ApplyAppearance()
    {
        Appearance.ApplyToApp(Settings);
        _services.Session.ApplyTheme(Settings.Theme);
        _browser.ApplyTheme(Settings.Theme);
        _detail.SetTheme(Settings.Theme, Settings.Accent);
        AppearanceChanged?.Invoke();
    }

    void ShowSubmit()
    {
        var defaultSub = SelectedTab?.IsSubreddit == true && !FeedViewModel.IsAggregateName(SelectedTab.Target)
            ? SelectedTab.Target
            : "";
        var window = new SubmitWindow(_services.Api, defaultSub) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() == true && window.PostUrl is { Length: > 0 } url)
        {
            Status = "Posted!";
            OpenUrl(url);
        }
    }

    // ---- Status, account and sign-in ------------------------------------------------------------

    string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    bool _showLogin = true;
    public bool ShowLogin { get => _showLogin; private set => Set(ref _showLogin, value); }

    string _loginMessage = "Connecting to Reddit…";
    public string LoginMessage { get => _loginMessage; private set => Set(ref _loginMessage, value); }

    bool _showContinueAnyway;
    public bool ShowContinueAnyway { get => _showContinueAnyway; private set => Set(ref _showContinueAnyway, value); }

    string _userName = "";
    public string UserName
    {
        get => _userName;
        private set
        {
            if (Set(ref _userName, value)) Raise(nameof(AccountLabel));
        }
    }

    public string AccountLabel => UserName.Length > 0 ? $"👤 u/{UserName}" : "👤 Account";

    public async Task InitializeAsync()
    {
        try
        {
            await _services.Session.InitializeAsync();
            await _detail.InitializeAsync(_services.Session.Environment, _services.Session.CreateControllerOptions());
            await _browser.InitializeAsync(_services.Session.Environment, _services.Session.CreateControllerOptions());
            _detail.SetDisplay(ReadingFontSize, IsReadingMode);
            ApplyAppearance();
        }
        catch (Exception ex)
        {
            LoginMessage = $"Couldn't start the embedded browser (Microsoft Edge WebView2): {ex.Message}";
            return;
        }

        _services.Session.NavigationCompleted += async (_, _) =>
        {
            if (_awaitingLogin) await TryCompleteLoginAsync(force: false);
        };

        await _services.Session.ParkAsync();
        if (!await TryCompleteLoginAsync(force: false)) PromptLogin();
    }

    void PromptLogin()
    {
        _awaitingLogin = true;
        _isReady = false;
        _inboxTimer.Stop();
        ShowLogin = true;
        LoginMessage = "Sign in to Reddit below. Readdit reads Reddit through your own session, " +
                       "so your password only goes to Reddit. You'll stay signed in next time.";
        _ = _services.Session.NavigateAsync(RedditSession.LoginPage);
    }

    /// <summary>If the browser session is signed in, hide the browser and start loading.</summary>
    async Task<bool> TryCompleteLoginAsync(bool force)
    {
        if (_checkingLogin)
        {
            // A navigation finished mid-check (e.g. the last sign-in redirect); look again after.
            _recheckLogin = true;
            return false;
        }
        _checkingLogin = true;
        try
        {
            var name = await _services.Session.GetUserNameAsync();
            if (name == null && !force) return false;

            _awaitingLogin = false;
            ShowContinueAnyway = false;
            if (name != null) RememberAccount(name);
            await _services.Session.ParkAsync();
            ShowLogin = false;
            _isReady = true;
            await OnSignedInAsync();
            return true;
        }
        finally
        {
            _checkingLogin = false;
            CommandManager.InvalidateRequerySuggested();
            if (_recheckLogin)
            {
                _recheckLogin = false;
                if (_awaitingLogin) _ = TryCompleteLoginAsync(force: false);
            }
        }
    }

    bool _recheckLogin;

    /// <summary>
    /// A feed got a login page instead of data. Only show sign-in if the session really is signed
    /// out; otherwise it was a one-off (rate limit page, interstitial), and prompting would loop.
    /// </summary>
    async Task HandleAuthFailureAsync(RedditAuthException ex)
    {
        if (ShowLogin) return;
        await _services.Session.ParkAsync();
        if (await _services.Session.GetUserNameAsync() != null)
        {
            Status = "Reddit sent an unexpected page instead of data. Wait a moment and refresh (F5).";
            return;
        }
        PromptLogin();
        LoginMessage = ex.Message;
    }

    async Task OnSignedInAsync()
    {
        await RefreshInboxAsync();
        _inboxTimer.Start();
        // Before the first feed loads, so its rows know which ones you can moderate.
        await LoadModeratedAsync();
        _ = LoadSubscriptionsAsync();
        _ = CheckForUpdatesQuietlyAsync();
        _updateTimer.Start();
        _newPostsTimer.Start();

        if (Tabs.Count == 0)
        {
            // Read it before AddTab, which saves the tab list (and would reset the selection to 0).
            var selected = Settings.SelectedTab;
            foreach (var spec in Settings.OpenTabs.ToList()) AddTab(spec, select: false);
            if (Tabs.Count == 0) AddTab("home", select: false);
            SelectedTab = Tabs[Math.Clamp(selected, 0, Tabs.Count - 1)];
        }
        else
        {
            _ = SelectedTab?.ReloadAsync();
        }
    }

    // ---- Multiple accounts --------------------------------------------------------------------
    //
    // Each account lives in its own WebView2 profile. A WebView can't change profile once
    // created, so switching saves the choice and restarts Readdit (takes a second or two).

    /// <summary>Raised when Readdit must restart to switch account profiles.</summary>
    public event Action? RestartRequested;

    public IReadOnlyList<AccountInfo> Accounts => Settings.Accounts;
    public string ActiveProfile => Settings.ActiveProfile;

    void RememberAccount(string userName)
    {
        var entry = Settings.Accounts.FirstOrDefault(a => a.Profile == Settings.ActiveProfile);
        if (entry == null) Settings.Accounts.Add(entry = new AccountInfo { Profile = Settings.ActiveProfile });
        if (entry.UserName == userName) return;
        entry.UserName = userName;
        Settings.Save();
    }

    public void SwitchAccount(string profile)
    {
        if (profile == Settings.ActiveProfile) return;
        // Remember where we came from (for "Back to …" on the sign-in screen), and drop accounts
        // that were added but never signed in to.
        Settings.PreviousProfile = Settings.ActiveProfile;
        Settings.Accounts.RemoveAll(a => a.UserName.Length == 0 && a.Profile != profile && a.Profile != Settings.ActiveProfile);
        Settings.ActiveProfile = profile;
        Settings.OpenTabs = ["home"];
        Settings.SelectedTab = 0;
        Settings.Save();
        RestartRequested?.Invoke();
    }

    /// <summary>The signed-in account we switched from, offered on the sign-in screen.</summary>
    AccountInfo? PreviousAccount =>
        Settings.Accounts.FirstOrDefault(a => a.Profile == Settings.PreviousProfile && a.Profile != Settings.ActiveProfile &&
                                              a.UserName.Length > 0);

    public bool CanGoBackToAccount => PreviousAccount != null;
    public string BackToAccountLabel => PreviousAccount is { } a ? $"← Back to u/{a.UserName}" : "";

    public void AddAccount()
    {
        var profile = "acct-" + Guid.NewGuid().ToString("N")[..8];
        Settings.Accounts.Add(new AccountInfo { Profile = profile });
        SwitchAccount(profile);
    }

    public void RemoveAccount(string profile)
    {
        var entry = Settings.Accounts.FirstOrDefault(a => a.Profile == profile);
        if (entry == null) return;
        Settings.Accounts.Remove(entry);
        Settings.Save();
        if (profile == Settings.ActiveProfile)
            SwitchAccount(Settings.Accounts.FirstOrDefault()?.Profile ?? "");
    }

    // ---- Offline library tags -------------------------------------------------------------------

    void EditTags(Post? post)
    {
        if (post == null || post.Kind != ItemKind.Link) return;
        if (!_services.Offline.Has(post.Id))
        {
            Status = "Save the post first (s) to add it to your offline library, then tag it.";
            return;
        }
        var current = string.Join(", ", _services.Offline.TagsFor(post.Id));
        var dialog = new TextInputWindow("Library tags", "Tags for this saved post, separated by commas:", current)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true) return;
        _services.Offline.SetTags(post.Id, dialog.Value.Split(','));
        Status = "Tags updated.";
        foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.Offline)) _ = tab.RefreshLibraryAsync();
    }

    async Task ShowAccountAsync()
    {
        _awaitingLogin = false;
        ShowLogin = true;
        LoginMessage = "This is your Reddit session. Sign out or switch accounts here, then click Continue.";
        await _services.Session.NavigateAsync(RedditSession.Home);
    }

    async Task ContinueAsync()
    {
        // Stop the navigation watcher from racing this explicit check.
        _awaitingLogin = false;
        await _services.Session.ParkAsync();
        if (await TryCompleteLoginAsync(force: false)) return;

        PromptLogin();
        LoginMessage = "Reddit says this session isn't signed in yet. Sign in below. " +
                       "If you're sure you already are, use \"Continue anyway\".";
        ShowContinueAnyway = true;
    }
}

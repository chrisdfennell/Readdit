using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Skein;

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
        // Only the tab you're looking at, and only while Skein is on screen.
        _newPostsTimer.Tick += async (_, _) =>
        {
            if (Settings.CheckForNewPosts && (IsMiniMode || Application.Current.MainWindow is { IsVisible: true }) && SelectedTab is { } tab)
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
        // Last session's list, so the sidebar is full straight away; Reddit's copy replaces it after sign-in.
        _subscriptions = _services.SubscriptionCache.Load(Settings.ActiveProfile);
        foreach (var sub in _subscriptions.Where(s => s.Icon != null)) _icons[sub.Name] = sub.Icon!;
        if (DemoMode)
        {
            _subscriptions = DemoCommunities.Select(n => new SubredditRef(n, _icons.GetValueOrDefault(n))).ToList();
            Favorites = new ObservableCollection<string>(DemoFavorites);
        }

        _detail.OpenUrlRequested += url => OpenUrl(url);
        _detail.ExternalUrlRequested += url => OpenLink(url);
        _detail.KeyPressed += (key, ctrl, shift) => HandleKey(key, ctrl, shift);
        _detail.StatusMessage += message => Status = message;
        _detail.PostStateChanged += OnDetailPostStateChanged;
        _detail.ModActionDone += OnModActionDone;
        _detail.UserNoteChanged += OnUserNoteChanged;
        _detail.ComposeRequested += to => ShowCompose(to);
        _detail.WatchRequested += SetWatching;
        _detail.FollowRequested += SetFollowing;
        _detail.RemindRequested += (id, title, sub, permalink) => Remind(id, title, sub, permalink);
        _detail.PopOutRequested += id => PopOutRequested?.Invoke(id);
        _detail.LaterChanged += () => Status = "Cancelled.";
        _detail.PostNowRequested += id => _ = PostScheduledAsync(id, now: true);
        _laterTimer.Tick += async (_, _) => await CheckLaterAsync();
        _laterTimer.Start();
        _detail.MediaStepHandler = StepToPicturesAsync;
        _detail.FullscreenRequested += on => FullscreenRequested?.Invoke(on);
        _detail.ThreadVisited += postId =>
        {
            foreach (var post in Tabs.SelectMany(t => t.Posts).Where(p => p.Id == postId)) post.NewComments = 0;
        };
        _inboxTimer.Tick += async (_, _) => await RefreshInboxAsync();
        _sync = new SettingsSync(services.Settings, services.UserNotes);
        _syncTimer.Tick += (_, _) => RunSync();
        _sleepTimer.Tick += (_, _) => SleepIdleTabs();
        _sleepTimer.Start();
        _alertMonitor = new AlertMonitor(services);
        _alertMonitor.Matched += OnAlertsMatched;
        _alertMonitor.ThreadsUpdated += OnThreadsUpdated;
        _alertTimer.Tick += async (_, _) =>
        {
            _alertTimer.Interval = AlertInterval; // the first tick comes early; then the chosen rate
            await _alertMonitor.CheckAsync();
        };
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
        ToggleFavoriteSpecCommand = new RelayCommand(p =>
        {
            if (p is string spec && spec.StartsWith("r/"))
                _ = SetFavoriteAsync(spec[2..], !Favorites.Contains(spec[2..], StringComparer.OrdinalIgnoreCase));
        });
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
        ToggleSubscriptionSortCommand = new RelayCommand(_ => ToggleSubscriptionSort());
        CleanupSubscriptionsCommand = new RelayCommand(_ => ShowCleanup(), _ => _isReady);
        NewFolderCommand = new RelayCommand(_ => EditFolder(null));
        EditFolderCommand = new RelayCommand(p => EditFolder(p as string));
        OpenFolderCommand = new RelayCommand(p => { if (p is string name) OpenFeed("folder:" + name, newTab: false); });
        AddToFolderCommand = new RelayCommand(p => { if (p is string spec) AddToFolder(spec); });
        RemoveFromFolderCommand = new RelayCommand(p => { if (p is SidebarItem item) RemoveFromFolder(item); });
        ManageAlertsCommand = new RelayCommand(_ => ShowAlerts());
        ReportProblemCommand = new RelayCommand(_ => ShowReportProblem());
        RemoveSavedSearchCommand = new RelayCommand(p => { if (p is SidebarItem item) RemoveSavedSearch(item.Spec); });
        WatchPostCommand = new RelayCommand(p =>
        {
            if (p is Post { Kind: ItemKind.Link } post) SetWatching(post.Id, post.Title, post.Subreddit, post.NumComments, !IsWatching(post.Id));
        });
        FollowAuthorCommand = new RelayCommand(p =>
        {
            if (p is Post post) SetFollowing(post.Author, !IsFollowing(post.Author));
        });
        PopOutCommand = new RelayCommand(p =>
        {
            if ((p as Post ?? SelectedPost) is { Kind: ItemKind.Link or ItemKind.Comment, Id.Length: > 0 } post) PopOutRequested?.Invoke(post.Id);
        });
        RemindCommand = new RelayCommand(p =>
        {
            if ((p as Post ?? SelectedPost) is { Kind: ItemKind.Link or ItemKind.Comment, Id.Length: > 0 } post)
                Remind(post.Id, post.Title, post.Subreddit, post.PermalinkUrl);
        });
        ShowCommunityCommand = new RelayCommand(p => ShowCommunity((p as FeedViewModel ?? SelectedTab)?.Target), _ => _isReady);
        NewMessageCommand = new RelayCommand(p => ShowCompose(p switch
        {
            Post { Author.Length: > 0 } post when post.Author != "[deleted]" => post.Author,
            string to => to,
            _ => "",
        }), _ => _isReady);
        CommandPaletteCommand = new RelayCommand(_ => PaletteRequested?.Invoke(), _ => _isReady);
        EditUserNoteCommand = new RelayCommand(p =>
        {
            if (p is Post { Author.Length: > 0 } post && post.Author != "[deleted]") EditUserNote(post.Author);
        });
        OpenCopiedLinkCommand = new RelayCommand(_ =>
        {
            if (CopiedLink is { } link) OpenUrl(link);
            CopiedLink = null;
        });
        DismissCopiedLinkCommand = new RelayCommand(_ => CopiedLink = null);
        SyncLibraryCommand = new RelayCommand(_ => _ = SyncSavedToLibraryAsync(), _ => _isReady && !_syncingLibrary);
        ExportLibraryCommand = new RelayCommand(p => { if (p is FeedViewModel tab) ExportLibrary(tab); });
        TagShownCommand = new RelayCommand(p => { if (p is FeedViewModel tab) TagShown(tab); });
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

    // ---- Screenshot mode -----------------------------------------------------------------------
    //
    // With SKEIN_DEMO=1 set, the sidebar shows a stand-in set of popular communities (and a
    // sample folder) and the account button hides your username, so pictures of Skein don't
    // show whose it is. Nothing shown in this mode is saved.

    static readonly bool DemoMode = Environment.GetEnvironmentVariable("SKEIN_DEMO") == "1";

    static readonly string[] DemoCommunities =
    [
        "AskReddit", "aww", "Cooking", "dataisbeautiful", "DIY", "dotnet", "csharp", "explainlikeimfive", "Fitness",
        "food", "gadgets", "gaming", "gardening", "history", "homelab", "InternetIsBeautiful", "interestingasfuck",
        "Jokes", "LifeProTips", "linux", "MadeMeSmile", "Minecraft", "movies", "Music", "nature", "NoStupidQuestions",
        "oddlysatisfying", "PCgaming", "photography", "pics", "programming", "science", "selfhosted", "space",
        "sports", "technology", "television", "todayilearned", "travel", "UpliftingNews", "videos", "wholesomememes",
        "woodworking", "worldnews", "YouShouldKnow",
    ];

    static readonly string[] DemoFavorites = ["aww", "explainlikeimfive", "space", "programming"];

    static readonly List<SubscriptionFolder> DemoFolders =
        [new SubscriptionFolder { Name = "Tech", Subreddits = ["dotnet", "csharp", "linux", "programming", "selfhosted"] }];

    /// <summary>Your folders (or, in screenshot mode, the sample one).</summary>
    List<SubscriptionFolder> Folders => DemoMode ? DemoFolders : Settings.SubscriptionFolders;

    /// <summary>Raised to ask the window to focus the search box.</summary>
    public event Action? FocusSearchRequested;

    /// <summary>
    /// Raised when new inbox items arrive: notification title and text, and the item itself when
    /// there's just one (so it can be answered from the notification).
    /// </summary>
    public event Action<string, string, Post?>? InboxNotification;

    /// <summary>Raised when alerts match new posts: title, text, and the post when there's just one.</summary>
    public event Action<string, string, Post?>? AlertNotification;

    /// <summary>Raised to switch to (true) or from (false) mini mode.</summary>
    public event Action<bool>? MiniModeRequested;

    /// <summary>Mini mode is showing (set by the window).</summary>
    public bool IsMiniMode { get; set; }

    public ICommand MiniModeCommand => _miniModeCommand ??= new RelayCommand(_ => MiniModeRequested?.Invoke(true));
    ICommand? _miniModeCommand;

    /// <summary>Settings, for windows the main window opens itself (mini mode).</summary>
    internal AppSettings CurrentSettings => Settings;

    /// <summary>Raised to open the command palette (Ctrl+K).</summary>
    public event Action? PaletteRequested;

    /// <summary>Raised to turn full screen on (true), off (false) or toggle it (null).</summary>
    public event Action<bool?>? FullscreenRequested;

    /// <summary>
    /// For the picture viewer: selects the next (1) or previous (-1) post in the list that has
    /// pictures, loading more of the feed if needed. False when there isn't one.
    /// </summary>
    async Task<bool> StepToPicturesAsync(int direction)
    {
        var tab = SelectedTab;
        if (tab == null || direction == 0) return false;
        var from = tab.SelectedPost == null ? (direction > 0 ? -1 : tab.Posts.Count) : tab.Posts.IndexOf(tab.SelectedPost);
        // A few pages at most, so a feed with no pictures doesn't page on forever.
        for (var pages = 0; pages < 4; pages++)
        {
            for (var i = from + direction; i >= 0 && i < tab.Posts.Count; i += direction)
            {
                if (!tab.Posts[i].HasPictures) continue;
                tab.SelectedPost = tab.Posts[i];
                return true;
            }
            if (direction < 0 || !tab.HasMore || tab != SelectedTab) return false;
            from = tab.Posts.Count - 1;
            await tab.LoadMoreAsync();
        }
        return false;
    }

    readonly AlertMonitor _alertMonitor;
    readonly DispatcherTimer _alertTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    TimeSpan AlertInterval => TimeSpan.FromMinutes(Math.Clamp(Settings.AlertIntervalMinutes, 1, 240));

    readonly HashSet<string> _notified = [];
    bool _inboxPrimed;

    /// <summary>Ask, Tray or Exit. See <see cref="AppSettings.CloseAction"/>.</summary>
    public string CloseAction
    {
        get => Settings.CloseAction is "Tray" or "Exit" ? Settings.CloseAction : "Ask";
        set
        {
            Settings.CloseAction = value;
            Settings.Save();
        }
    }

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
        new("🔔 Alerts", "alerts"),
        new("⏰ Reminders", "later"),
        new("👤 My profile", "u/me"),
        new("📊 My activity", "activity"),
    ];

    /// <summary>Skein's own pages, shown in the post view rather than as a tab.</summary>
    bool ShowPage(string spec)
    {
        if (spec is not ("later" or "activity" or "modmail")) return false;
        ShowBrowser = false;
        if (SelectedTab != null) SelectedTab.SelectedPost = null; // the post view shows the page now
        _detail.ShowPage(spec);
        if (spec == "modmail" && _modmailUnread > 0)
        {
            _modmailUnread = 0; // you're looking at it
            RefreshSidebar();
        }
        return true;
    }

    public ICommand GoCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenFeedCommand { get; }
    public ICommand OpenInNewTabCommand { get; }
    public ICommand NewTabCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand AddFavoriteCommand { get; }
    public ICommand RemoveFavoriteCommand { get; }
    public ICommand ToggleFavoriteSpecCommand { get; }
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
    public ICommand SyncLibraryCommand { get; }
    public ICommand ExportLibraryCommand { get; }
    public ICommand TagShownCommand { get; }
    public ICommand ManageAlertsCommand { get; }
    public ICommand ReportProblemCommand { get; }
    public ICommand RemoveSavedSearchCommand { get; }
    public ICommand ShowCommunityCommand { get; }
    public ICommand WatchPostCommand { get; }
    public ICommand FollowAuthorCommand { get; }
    public ICommand PopOutCommand { get; }
    public ICommand RemindCommand { get; }

    /// <summary>Raised to open a post (by id) in a window of its own, side by side with this one.</summary>
    public event Action<string>? PopOutRequested;

    public void PopOut(string postId) => PopOutRequested?.Invoke(postId);

    /// <summary>Raised when a reminder is due: title, text, and the post's link.</summary>
    public event Action<string, string, string, bool>? ReminderNotification;

    /// <summary>Shows a community's rules, sidebar, moderators and wiki in the post view.</summary>
    void ShowCommunity(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        ShowBrowser = false;
        if (SelectedTab != null) SelectedTab.SelectedPost = null; // the post view shows the community now
        _ = _detail.ShowCommunityAsync(name);
    }
    public ICommand NewMessageCommand { get; }
    public ICommand CommandPaletteCommand { get; }
    public ICommand EditUserNoteCommand { get; }
    public ICommand OpenCopiedLinkCommand { get; }
    public ICommand DismissCopiedLinkCommand { get; }
    public ICommand ToggleSubscriptionSortCommand { get; }
    public ICommand CleanupSubscriptionsCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand EditFolderCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand AddToFolderCommand { get; }
    public ICommand RemoveFromFolderCommand { get; }
    public ICommand BrowserBackCommand { get; }
    public ICommand BrowserForwardCommand { get; }
    public ICommand BrowserReloadCommand { get; }
    public ICommand BrowserOpenExternalCommand { get; }
    public ICommand CloseBrowserCommand { get; }
    public ICommand OpenLinkCommand { get; }

    // ---- Updates ------------------------------------------------------------------------------

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

    /// <summary>Called as Skein exits: installs a downloaded update quietly if auto-update is on.</summary>
    public void OnExit()
    {
        if (Settings.SyncEnabled) _sync.RunOnce(); // last changes out before closing
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

    bool _isFullscreen;
    /// <summary>Full screen (set by the window, which does the work).</summary>
    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            if (Set(ref _isFullscreen, value)) _detail.SetFullscreenState(value);
        }
    }

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
            if (_selectedTab != null)
            {
                _selectedTab.PropertyChanged -= OnTabPropertyChanged;
                _selectedTab.LastShown = DateTime.Now; // asleep after a while in the background
            }
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

    public string WindowTitle => SelectedTab == null ? "Skein" : $"{SelectedTab.Title} · Skein";

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
        feed.ManageAlertsRequested += ShowAlerts;
        feed.SearchRefined += (tab, spec) => ReplaceTab(tab, spec);
        feed.SaveSearchRequested += SaveSearch;
        feed.FollowToggleRequested += user => SetFollowing(user, !IsFollowing(user));
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
        if (ShowPage(spec)) return;
        if (spec is "u/me" or "saved" && _services.Api.UserName == null)
        {
            // "u/me" would otherwise open the profile of a real user called "me".
            Status = "Skein doesn't know your username yet. Try again in a moment.";
            _ = RefreshInboxAsync(); // also refreshes the identity
            return;
        }
        if (spec == "u/me") spec = "u/" + _services.Api.UserName;

        // Counted for the "most visited" order of the Subscriptions list.
        if (FeedSpec.Parse(spec) is { Kind: FeedKind.Subreddit } community && !FeedViewModel.IsAggregateName(community.Target))
            _services.CommunityVisits.Record(community.Target);

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
                case "k": PaletteRequested?.Invoke(); return true;
                case "m": MiniModeRequested?.Invoke(!IsMiniMode); return true;
                default: return false;
            }
        }

        switch (key)
        {
            case "Escape" when IsFullscreen: FullscreenRequested?.Invoke(false); return true;
            case "Escape" when ShowBrowser: ShowBrowser = false; return true;
            case "Escape" when IsReadingMode: IsReadingMode = false; return true;
            case "f": IsReadingMode = !IsReadingMode; return true;
            case "x": _ = HideAsync(SelectedPost); return true;
            case "N": _detail.NextNewComment(); return true;
            case "P": _detail.NextOpComment(); return true;
            case "F11": FullscreenRequested?.Invoke(null); return true;
            case "d": _detail.RequestDownload(); return true;
            case "v": _detail.OpenViewer(); return true;
            case "R": _detail.ReadAloud(); return true;
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

    public string AccountLabel => UserName.Length > 0 && !DemoMode ? $"👤 u/{UserName}" : "👤 Account";

    public async Task InitializeAsync()
    {
        try
        {
            await _services.Session.InitializeAsync();
            await _detail.InitializeAsync(_services.Session.Environment, _services.Session.CreateControllerOptions());
            await _browser.InitializeAsync(_services.Session.Environment, _services.Session.CreateControllerOptions());
            _detail.SetDisplay(ReadingFontSize, IsReadingMode);
            ApplyAppearance();
            // Once the views exist, since synced appearance restyles them.
            StartSync();
            LogStartup("web views ready");
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
        LoginMessage = "Sign in to Reddit below. Skein reads Reddit through your own session, " +
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
        _alertTimer.Start();

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

        LogStartup("signed in, tabs open");

        // A link another app handed over before Skein was ready.
        if (_pendingUrl is { } pending)
        {
            _pendingUrl = null;
            OpenUrl(pending);
        }
    }

    string? _pendingUrl;

    /// <summary>Notes in api.log how long start-up took to reach a step (to spot slow starts).</summary>
    static void LogStartup(string step)
    {
        try
        {
            var since = DateTime.Now - Process.GetCurrentProcess().StartTime;
            ApiLog.Write($"startup: {step} after {since.TotalMilliseconds:0} ms, {GC.GetTotalMemory(false) / 1048576.0:0.0} MB managed");
        }
        catch
        {
            // Timing is a nicety.
        }
    }

    /// <summary>Opens a link from another app now, or once signed in.</summary>
    public void OpenWhenReady(string url)
    {
        if (_isReady) OpenUrl(url);
        else _pendingUrl = url;
    }

}

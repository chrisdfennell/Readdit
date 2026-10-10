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

    // ---- Sidebar ------------------------------------------------------------------------------

    string _sidebarFilter = "";
    public string SidebarFilter
    {
        get => _sidebarFilter;
        set
        {
            if (Set(ref _sidebarFilter, value)) RefreshSidebar();
        }
    }

    // The whole sidebar is one flat, virtualized list of headers, items and hints, so only the
    // rows on screen are built. (Separate lists in a StackPanel built every row up front, which
    // froze the window for people subscribed to thousands of communities.)
    IReadOnlyList<object>? _sidebarRows;
    public IReadOnlyList<object> SidebarRows => _sidebarRows ??= BuildSidebarRows();

    void RefreshSidebar()
    {
        _sidebarRows = null;
        Raise(nameof(SidebarRows));
        // Only swap the jump bar's list when the letters change, so its buttons aren't rebuilt
        // on every sidebar refresh.
        var letters = ComputeJumpLetters();
        if (!letters.SequenceEqual(JumpLetters))
        {
            _jumpLetters = letters;
            Raise(nameof(JumpLetters));
            Raise(nameof(ShowJumpBar));
        }
    }

    /// <summary>Where the Subscriptions list starts in <see cref="SidebarRows"/> (-1 if hidden).</summary>
    int _subscriptionsStart = -1;

    List<object> BuildSidebarRows()
    {
        var rows = new List<object>();
        void Section(string key, string title, bool show, string toolTip, IEnumerable<object> items,
            IReadOnlyList<SidebarAction>? actions = null, bool nested = false)
        {
            rows.Add(new SidebarHeader(key, title, IsExpanded(key), toolTip, actions ?? [], SetSectionExpanded, nested));
            if (show) rows.AddRange(items);
        }

        Section("feeds", "FEEDS", FeedsExpanded, "Show or hide", Feeds);
        if (HasModeration)
            Section("moderation", "MODERATION", ShowModeration, "Mod queues for the communities you moderate", ModerationItems);
        Section("favorites", FavoritesHeader, ShowFavorites,
            "Your favorite communities on Reddit. Starring one here stars it on Reddit too.",
            HasNoFavorites
                ? [new SidebarHint("Star a community with ☆ Favorite (or right-click it) and it shows up here and on Reddit.")]
                : FavoriteItems);

        // Folders: your own groups. Each folder is its own collapsible heading inside the section.
        var folders = Folders;
        Section("folders", folders.Count == 0 ? "FOLDERS" : $"FOLDERS ({folders.Count})", false,
            "Your own groups of communities. Open one to see all of their posts together.", [],
            [new SidebarAction("+", "New folder", NewFolderCommand)]);
        if (FoldersExpanded || IsFiltering)
        {
            if (folders.Count == 0 && !IsFiltering)
                rows.Add(new SidebarHint("Right-click a community and choose “Add to folder…”, or click + to make a folder."));
            foreach (var folder in folders)
            {
                var members = folder.Subreddits.Where(MatchesFilter).ToList();
                if (IsFiltering && members.Count == 0 && !MatchesFilter(folder.Name)) continue;
                var key = "folder:" + folder.Name;
                Section(key, $"📁 {folder.Name} ({folder.Subreddits.Count})", IsExpanded(key) || IsFiltering,
                    "Show or hide this folder's communities",
                    members.Select(s => new SidebarItem("r/" + s, "r/" + s, _icons.GetValueOrDefault(s), Folder: folder.Name,
                        IsFavorite: Favorites.Contains(s, StringComparer.OrdinalIgnoreCase))),
                    [
                        new SidebarAction("▶", "Open all of this folder's posts together", OpenFolderCommand, folder.Name),
                        new SidebarAction("✎", "Rename, edit or delete this folder", EditFolderCommand, folder.Name),
                    ],
                    nested: true);
            }
        }

        if (Settings.SavedSearches.Count > 0)
        {
            var searches = Settings.SavedSearches.Where(s => MatchesFilter(s.Name)).ToList();
            Section("searches", $"SAVED SEARCHES ({Settings.SavedSearches.Count})", (IsExpanded("searches") || IsFiltering) && searches.Count > 0,
                "Searches you've saved. Right-click one to remove it.",
                searches.Select(s => new SidebarItem("🔍 " + s.Name, s.Spec, IsFeed: true, IsSavedSearch: true)));
        }

        var byVisits = Settings.SubscriptionSort == "Visits";
        Section("subscriptions", SubscriptionsHeader, false, "Show or hide", [],
            [
                new SidebarAction(byVisits ? "★" : "AZ",
                    byVisits ? "Sorted by most visited. Click to sort A–Z." : "Sorted A–Z. Click to sort by most visited.",
                    ToggleSubscriptionSortCommand),
                new SidebarAction("🧹", "Clean up: find banned, private and quiet communities", CleanupSubscriptionsCommand),
                new SidebarAction("⟳", "Reload subscriptions from Reddit", RefreshSubscriptionsCommand),
            ]);
        _subscriptionsStart = ShowSubscriptions ? rows.Count : -1;
        if (ShowSubscriptions) rows.AddRange(SubscriptionItems);
        return rows;
    }

    void SetSectionExpanded(string key, bool expanded)
    {
        // Rebuild after the toggle has finished updating, not from inside its binding.
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            switch (key)
            {
                case "feeds": FeedsExpanded = expanded; break;
                case "moderation": ModerationExpanded = expanded; break;
                case "favorites": FavoritesExpanded = expanded; break;
                case "subscriptions": SubscriptionsExpanded = expanded; break;
                default: SetExpanded(key, expanded); break; // folders, and each folder
            }
        });
    }

    bool FoldersExpanded => IsExpanded("folders");

    // ---- A–Z jump bar ---------------------------------------------------------------------------

    IReadOnlyList<string>? _jumpLetters;

    /// <summary>Letters the Subscriptions list can jump to (only while it's shown A–Z and unfiltered).</summary>
    public IReadOnlyList<string> JumpLetters => _jumpLetters ??= ComputeJumpLetters();

    List<string> ComputeJumpLetters() =>
        Settings.SubscriptionSort == "Visits" || IsFiltering || !SubscriptionsExpanded || _subscriptions.Count < 40
            ? []
            : _subscriptions.Select(s => JumpLetterFor(s.Name)).Distinct().Order(StringComparer.Ordinal).ToList();

    public bool ShowJumpBar => JumpLetters.Count > 0;

    static string JumpLetterFor(string name) =>
        name.Length > 0 && char.IsAsciiLetter(name[0]) ? char.ToUpperInvariant(name[0]).ToString() : "#";

    /// <summary>Row in <see cref="SidebarRows"/> of the first subscription under <paramref name="letter"/>.</summary>
    public int IndexOfJumpLetter(string letter)
    {
        if (_subscriptionsStart < 0) return -1;
        var rows = SidebarRows;
        for (var i = _subscriptionsStart; i < rows.Count; i++)
            if (rows[i] is SidebarItem item && JumpLetterFor(item.Spec[2..]) == letter)
                return i;
        return -1;
    }

    // ---- Subscription order and cleanup --------------------------------------------------------

    void ToggleSubscriptionSort()
    {
        Settings.SubscriptionSort = Settings.SubscriptionSort == "Visits" ? "Name" : "Visits";
        Settings.Save();
        Status = Settings.SubscriptionSort == "Visits"
            ? "Subscriptions sorted by how often you open them."
            : "Subscriptions sorted A–Z.";
        RefreshSidebar();
    }

    void ShowCleanup()
    {
        if (_subscriptions.Count == 0)
        {
            Status = "Your subscriptions haven't loaded yet.";
            return;
        }
        var window = new CleanupWindow(_services.Api, _subscriptions) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        if (window.Unsubscribed.Count == 0) return;
        var gone = window.Unsubscribed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _subscriptions = _subscriptions.Where(s => !gone.Contains(s.Name)).ToList();
        _services.SubscriptionCache.Save(Settings.ActiveProfile, _subscriptions);
        Status = $"Unsubscribed from {gone.Count} {(gone.Count == 1 ? "community" : "communities")}.";
        Raise(nameof(SubscriptionsHeader));
        RefreshSidebar();
    }

    // ---- Saved searches ---------------------------------------------------------------------------

    void SaveSearch(string spec, string suggestedName)
    {
        var dialog = new TextInputWindow("Save search", "Name for this search in the sidebar:", suggestedName)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true || dialog.Value.Trim().Length == 0) return;
        var name = dialog.Value.Trim();
        Settings.SavedSearches.RemoveAll(s => s.Spec.Equals(spec, StringComparison.OrdinalIgnoreCase));
        Settings.SavedSearches.Add(new SavedSearch { Name = name, Spec = spec });
        Settings.SavedSearches.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        Status = $"Saved the search “{name}” in the sidebar.";
        RefreshSidebar();
    }

    void RemoveSavedSearch(string spec)
    {
        if (Settings.SavedSearches.RemoveAll(s => s.Spec.Equals(spec, StringComparison.OrdinalIgnoreCase)) == 0) return;
        Settings.Save();
        Status = "Removed the saved search.";
        RefreshSidebar();
    }

    // ---- Folders --------------------------------------------------------------------------------

    SubscriptionFolder? FindFolder(string? name) =>
        Settings.SubscriptionFolders.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    void EditFolder(string? name)
    {
        var folder = FindFolder(name);
        var others = Settings.SubscriptionFolders.Where(f => f != folder).Select(f => f.Name).ToList();
        var window = new FolderWindow(folder, others) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;

        var oldSpec = folder == null ? null : "folder:" + folder.Name;
        if (window.Deleted && folder != null)
        {
            Settings.SubscriptionFolders.Remove(folder);
            Status = $"Deleted the folder “{folder.Name}”.";
        }
        else
        {
            folder ??= AddFolder(window.FolderNameValue);
            RenameFolder(folder, window.FolderNameValue);
            folder.Subreddits = window.MemberNames;
        }
        Settings.Save();
        RefreshSidebar();
        // An open tab for the folder shows its new contents (or closes, if the folder is gone).
        foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.Folder && t.Spec.Equals(oldSpec, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (window.Deleted) CloseTab(tab);
            else if (!tab.Target.Equals(folder!.Name, StringComparison.Ordinal)) ReplaceTab(tab, "folder:" + folder.Name);
            else _ = tab.ReloadAsync();
        }
    }

    SubscriptionFolder AddFolder(string name)
    {
        var folder = new SubscriptionFolder { Name = name };
        Settings.SubscriptionFolders.Add(folder);
        Settings.SubscriptionFolders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return folder;
    }

    void RenameFolder(SubscriptionFolder folder, string name)
    {
        if (folder.Name == name) return;
        // Keep its collapsed state under the new name.
        var collapsed = Settings.CollapsedSidebarSections.RemoveAll(s => s.Equals("folder:" + folder.Name, StringComparison.OrdinalIgnoreCase)) > 0;
        folder.Name = name;
        if (collapsed) Settings.CollapsedSidebarSections.Add("folder:" + name);
        Settings.SubscriptionFolders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    void AddToFolder(string spec)
    {
        if (!spec.StartsWith("r/")) return;
        var subreddit = spec[2..];
        var picker = new FolderPickerWindow(subreddit, Settings.SubscriptionFolders.Select(f => f.Name).ToList())
        {
            Owner = Application.Current.MainWindow,
        };
        if (picker.ShowDialog() != true) return;
        var folder = FindFolder(picker.FolderName) ?? AddFolder(picker.FolderName);
        if (folder.Subreddits.Contains(subreddit, StringComparer.OrdinalIgnoreCase))
        {
            Status = $"r/{subreddit} is already in “{folder.Name}”.";
            return;
        }
        folder.Subreddits.Add(subreddit);
        folder.Subreddits.Sort(StringComparer.OrdinalIgnoreCase);
        Settings.Save();
        Status = $"Added r/{subreddit} to “{folder.Name}”.";
        RefreshSidebar();
        RefreshFolderTabs(folder.Name);
    }

    /// <summary>A folder's contents changed: its open tab reloads now if showing, or when next shown.</summary>
    void RefreshFolderTabs(string name)
    {
        foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.Folder && t.Target.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            if (tab == SelectedTab) _ = tab.ReloadAsync();
            else tab.MarkStale();
        }
    }

    void RemoveFromFolder(SidebarItem item)
    {
        if (FindFolder(item.Folder) is not { } folder || !item.Spec.StartsWith("r/")) return;
        folder.Subreddits.RemoveAll(s => s.Equals(item.Spec[2..], StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        Status = $"Removed {item.Spec} from “{folder.Name}”.";
        RefreshSidebar();
        RefreshFolderTabs(folder.Name);
    }

    /// <summary>Swaps a tab for another feed in the same place.</summary>
    void ReplaceTab(FeedViewModel tab, string spec)
    {
        var index = Tabs.IndexOf(tab);
        var wasSelected = SelectedTab == tab;
        var feed = AddTab(spec, select: false);
        if (feed == null) return;
        Tabs.Move(Tabs.Count - 1, index + 1);
        if (wasSelected) SelectedTab = feed;
        Tabs.Remove(tab);
        tab.Close();
        SaveTabs();
    }

    bool MatchesFilter(string name) =>
        SidebarFilter.Length == 0 || name.Contains(SidebarFilter.Trim(), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SidebarItem> FavoriteItems =>
        Favorites.Where(MatchesFilter).Select(f => new SidebarItem("r/" + f, "r/" + f, _icons.GetValueOrDefault(f), IsFavorite: true)).ToList();

    public IReadOnlyList<SidebarItem> SubscriptionItems
    {
        get
        {
            var matching = _subscriptions.Where(s => MatchesFilter(s.Name));
            if (Settings.SubscriptionSort == "Visits")
                matching = matching.OrderByDescending(s => _services.CommunityVisits.CountFor(s.Name))
                    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
            var favorites = Favorites.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return matching.Select(s => new SidebarItem("r/" + s.Name, "r/" + s.Name, s.Icon, IsFavorite: favorites.Contains(s.Name))).ToList();
        }
    }

    // ---- Collapsible sidebar sections (remembered in settings) ---------------------------------

    bool IsExpanded(string section) =>
        DemoMode || !Settings.CollapsedSidebarSections.Contains(section, StringComparer.OrdinalIgnoreCase);

    void SetExpanded(string section, bool expanded, params string[] changed)
    {
        if (expanded == IsExpanded(section)) return;
        if (expanded) Settings.CollapsedSidebarSections.RemoveAll(s => s.Equals(section, StringComparison.OrdinalIgnoreCase));
        else Settings.CollapsedSidebarSections.Add(section);
        Settings.Save();
        foreach (var name in changed) Raise(name);
        RefreshSidebar();
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
        if (DemoMode) return; // keep the stand-in list
        try
        {
            _subscriptions = await _services.Api.GetSubscriptionsAsync();
            _services.SubscriptionCache.Save(Settings.ActiveProfile, _subscriptions);
            foreach (var sub in _subscriptions.Where(s => s.Icon != null)) _icons[sub.Name] = sub.Icon!;

            // Favorites come from Reddit (the communities you've starred); the saved list is just
            // a cache so the sidebar isn't empty before this loads.
            Favorites.Clear();
            foreach (var sub in _subscriptions.Where(s => s.IsFavorite)) Favorites.Add(sub.Name);
            SaveFavorites();

            Raise(nameof(SubscriptionsHeader));
            RefreshSidebar();
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
        Raise(nameof(FavoritesHeader));
        RefreshSidebar();
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

    // ---- Links ------------------------------------------------------------------------------

    /// <summary>Opens Reddit links inside Skein and everything else in the default browser.</summary>
    public void OpenUrl(string url)
    {
        switch (RedditLinks.Classify(url))
        {
            case ThreadLink thread:
                ShowBrowser = false;
                _ = _detail.OpenThreadAsync(thread.PostId, thread.CommentId);
                break;
            case FeedLink feed:
                OpenFeed(feed.Spec, newTab: true);
                break;
            case WebLink web:
                OpenLink(web.Url);
                break;
        }
    }

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
            InboxNotification?.Invoke(title, body.Length > 200 ? body[..200] + "…" : body, fresh.Count == 1 ? first : null);
        }
        catch
        {
            InboxNotification?.Invoke("New message on Reddit", Status, null);
        }
    }

    // ---- Sleeping tabs ------------------------------------------------------------------------------

    readonly DispatcherTimer _sleepTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    /// <summary>Background tabs unused for a while let go of their posts (they reload when opened).</summary>
    void SleepIdleTabs()
    {
        var after = Settings.SleepTabsAfterMinutes;
        if (after <= 0) return;
        var cutoff = DateTime.Now.AddMinutes(-after);
        foreach (var tab in Tabs.Where(t => t != SelectedTab && t.LastShown < cutoff)) tab.Sleep();
    }

    // ---- Sync between PCs -----------------------------------------------------------------------

    readonly SettingsSync _sync;
    readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    System.IO.FileSystemWatcher? _syncWatcher;
    DispatcherTimer? _syncSoon;

    /// <summary>For Preferences: whether sync is working.</summary>
    public string SyncStatus =>
        !Settings.SyncEnabled ? "Off."
        : _sync.LastError != null ? $"Couldn't use the sync folder just now ({_sync.LastError}). Trying again shortly."
        : _sync.LastSynced is { } at ? $"✓ In sync. Last checked at {at:t}."
        : "Waiting to sync…";

    /// <summary>Starts (or restarts) syncing: a round now, then every so often and whenever the file changes.</summary>
    void StartSync()
    {
        _syncTimer.Stop();
        _syncWatcher?.Dispose();
        _syncWatcher = null;
        if (!Settings.SyncEnabled || Settings.SyncFolder.Length == 0) return;
        RunSync();
        _syncTimer.Start();
        try
        {
            System.IO.Directory.CreateDirectory(Settings.SyncFolder);
            _syncWatcher = new System.IO.FileSystemWatcher(Settings.SyncFolder, SettingsSync.FileName)
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            // Another PC's changes arrive as the file changes; wait a moment for OneDrive to finish writing it.
            void Soon(object? _, System.IO.FileSystemEventArgs __) => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _syncSoon ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _syncSoon.Tick -= OnSyncSoon;
                _syncSoon.Tick += OnSyncSoon;
                _syncSoon.Stop();
                _syncSoon.Start();
            });
            _syncWatcher.Changed += Soon;
            _syncWatcher.Created += Soon;
            _syncWatcher.Renamed += (s, e) => Soon(s, e);
        }
        catch
        {
            // The timer still syncs.
        }
    }

    void OnSyncSoon(object? sender, EventArgs e)
    {
        _syncSoon?.Stop();
        RunSync();
    }

    void RunSync()
    {
        var applied = _sync.RunOnce();
        if (applied is { Count: > 0 }) OnSyncApplied(applied);
    }

    /// <summary>Settings arrived from another PC: show them.</summary>
    void OnSyncApplied(IReadOnlyList<string> sections)
    {
        if (sections.Contains("folders") || sections.Contains("searches"))
        {
            RefreshSidebar();
            foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.Folder)) tab.MarkStale();
        }
        if (sections.Contains("alerts"))
        {
            _alertTimer.Interval = AlertInterval;
            RefreshAlertTabs();
        }
        if (sections.Contains("notes"))
            foreach (var post in Tabs.SelectMany(t => t.Posts))
                post.AuthorTag = _services.UserNotes.Get(post.Author)?.Tag ?? "";
        if (sections.Contains("filters"))
            foreach (var tab in Tabs.Where(t => t != SelectedTab)) tab.MarkStale();
        if (sections.Contains("notes") || sections.Contains("filters")) _detail.RefreshSettings();
        if (sections.Contains("appearance"))
        {
            ApplyAppearance();
            Raise(nameof(LayoutMode));
            Raise(nameof(ReadingFontSize));
            _detail.SetDisplay(ReadingFontSize, IsReadingMode);
        }
        Status = $"Synced {string.Join(", ", sections)} from your other PC.";
    }

    /// <summary>After Preferences: sync was turned on or pointed somewhere else.</summary>
    void OnSyncSettingsChanged(bool wasEnabled, string oldFolder)
    {
        if (!Settings.SyncEnabled)
        {
            StartSync(); // stops it
            return;
        }
        if (wasEnabled && oldFolder.Equals(Settings.SyncFolder, StringComparison.OrdinalIgnoreCase)) return;
        // Starting fresh with this folder. If another PC already syncs there, ask which copy to keep.
        var preferFile = _sync.FileExists && MessageBox.Show(Application.Current.MainWindow,
            "Another PC already syncs Skein's settings in this folder.\n\n" +
            "Yes: use those (folders, alerts, notes, filters and appearance on this PC are replaced).\n" +
            "No: use this PC's settings everywhere instead.",
            "Sync between PCs", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        _sync.Reset(preferFile);
        StartSync();
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
            new PaletteItem("▤", "Layout: Compact", "1", () => LayoutMode = "Compact"),
            new PaletteItem("▦", "Layout: Cards", "2", () => LayoutMode = "Cards"),
            new PaletteItem("▩", "Layout: Grid", "3", () => LayoutMode = "Grid"),
            new PaletteItem("⚙", "Preferences", "Settings", ShowPreferences),
            new PaletteItem("⌨", "Keyboard shortcuts", "?", () => ShortcutsCommand.Execute(null)),
            new PaletteItem("🐞", "Report a problem", "Help", ShowReportProblem),
        ]);
        foreach (var account in Accounts.Where(a => a.Profile != ActiveProfile && a.UserName.Length > 0))
            items.Add(new PaletteItem("👤", $"Switch to u/{account.UserName}", "Account", () => SwitchAccount(account.Profile)));

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

    // ---- User notes and tags ----------------------------------------------------------------------

    void EditUserNote(string user)
    {
        var window = new UserNoteWindow(user, _services.UserNotes.Get(user)) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        _services.UserNotes.Set(user, window.TagValue, window.NoteValue);
        OnUserNoteChanged(user);
        _detail.RefreshSettings();
        Status = window.TagValue.Length + window.NoteValue.Length == 0 ? $"Cleared your note on u/{user}." : $"Saved your note on u/{user}.";
    }

    /// <summary>Shows a user's new tag on every loaded post of theirs.</summary>
    void OnUserNoteChanged(string user)
    {
        var tag = _services.UserNotes.Get(user)?.Tag ?? "";
        foreach (var post in Tabs.SelectMany(t => t.Posts).Where(p => p.Author.Equals(user, StringComparison.OrdinalIgnoreCase)))
            post.AuthorTag = tag;
    }

    // ---- Copied reddit links ------------------------------------------------------------------------

    string? _copiedLink;
    string? _lastClipboard;

    /// <summary>A reddit link on the clipboard that Skein offers to open.</summary>
    public string? CopiedLink
    {
        get => _copiedLink;
        private set
        {
            if (!Set(ref _copiedLink, value)) return;
            Raise(nameof(HasCopiedLink));
            Raise(nameof(CopiedLinkLabel));
        }
    }

    public bool HasCopiedLink => CopiedLink != null;

    public string CopiedLinkLabel =>
        CopiedLink == null ? "" : "📋 Open the reddit link you copied? " + (CopiedLink.Length > 70 ? CopiedLink[..70] + "…" : CopiedLink);

    /// <summary>Called when Skein comes to the front: offer a newly copied reddit link.</summary>
    public void CheckClipboard(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text == _lastClipboard) return;
        _lastClipboard = text;
        if (!_isReady || text.Length > 2000 || text.Contains(' ') || text.Contains('\n')) return;
        if (!LinkHandler.IsRedditUrl(text) || RedditLinks.Classify(text) is WebLink) return;
        // Not the post that's already open (you probably copied it from here).
        if (SelectedPost != null && text.Contains(SelectedPost.PermalinkUrl.Replace("https://www.reddit.com", ""), StringComparison.OrdinalIgnoreCase))
            return;
        CopiedLink = text;
    }

    // ---- Quick reply ----------------------------------------------------------------------------

    /// <summary>An inbox item was marked read outside the inbox (from Quick reply).</summary>
    public void OnItemMarkedRead(Post item)
    {
        UnreadCount = Math.Max(0, UnreadCount - 1);
        foreach (var copy in Tabs.SelectMany(t => t.Posts).Where(p => p.FullName == item.FullName)) copy.IsUnread = false;
    }

    public RedditApi Api => _services.Api;

    // ---- Dialogs ----------------------------------------------------------------------------

    void ShowCompose(string to)
    {
        var window = new ComposeMessageWindow(_services.Api, to)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow,
        };
        if (window.ShowDialog() != true) return;
        Status = $"Message sent to u/{window.Recipient}.";
        // It shows up under Inbox → sent.
        foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.Inbox && t.Sort == "sent")) tab.MarkStale();
    }

    void ShowReportProblem() =>
        new ReportProblemWindow(_updates.CurrentVersion, _updates.Kind, Settings.UpdateUrl, _services.Api.UserName)
        {
            // Over Preferences when opened from there.
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow,
        }.ShowDialog();

    void ShowPreferences()
    {
        var (syncWas, syncFolderWas) = (Settings.SyncEnabled, Settings.SyncFolder);
        var window = new PreferencesWindow(Settings, _updates, SyncStatus) { Owner = Application.Current.MainWindow };
        window.ReportProblemRequested += () => ShowReportProblem();
        var saved = window.ShowDialog() == true;
        if (window.RestartToRestore)
        {
            RestartRequested?.Invoke(); // the backup is put in place as Skein starts again
            return;
        }
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
        if (Settings.SyncEnabled != syncWas || Settings.SyncFolder != syncFolderWas) OnSyncSettingsChanged(syncWas, syncFolderWas);
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
        var window = new SubmitWindow(_services.Api, defaultSub, _services) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        if (window.PostUrl is { Length: > 0 } url)
        {
            Status = "Posted!";
            OpenUrl(url);
        }
        else
        {
            // Media posts finish in the background on Reddit's side.
            Status = "Posted! Reddit is still processing your upload; it'll appear on your profile in a moment.";
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

        // A link another app handed over before Skein was ready.
        if (_pendingUrl is { } pending)
        {
            _pendingUrl = null;
            OpenUrl(pending);
        }
    }

    string? _pendingUrl;

    /// <summary>Opens a link from another app now, or once signed in.</summary>
    public void OpenWhenReady(string url)
    {
        if (_isReady) OpenUrl(url);
        else _pendingUrl = url;
    }

    // ---- Multiple accounts --------------------------------------------------------------------
    //
    // Each account lives in its own WebView2 profile. A WebView can't change profile once
    // created, so switching saves the choice and restarts Skein (takes a second or two).

    /// <summary>Raised when Skein must restart to switch account profiles.</summary>
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

    // ---- Organizing the offline library ---------------------------------------------------------

    bool _syncingLibrary;

    /// <summary>
    /// Copies every post you've saved on Reddit into the offline library (the ones not there
    /// yet), so all of them can be tagged, searched and exported.
    /// </summary>
    async Task SyncSavedToLibraryAsync()
    {
        if (_syncingLibrary) return;
        if (_services.Api.UserName is not { } me)
        {
            Status = "Skein doesn't know your username yet. Try again in a moment.";
            return;
        }
        _syncingLibrary = true;
        CommandManager.InvalidateRequerySuggested();
        var added = 0;
        var failed = 0;
        try
        {
            string? after = null;
            // Reddit keeps your newest 1,000 saved items.
            for (var page = 0; page < 20; page++)
            {
                var (items, next) = await _services.Api.GetListingAsync($"/user/{me}/saved.json", after, default);
                foreach (var post in items.Where(p => p.Kind == ItemKind.Link && !_services.Offline.Has(p.Id)))
                {
                    Status = $"Adding saved posts to your offline library… {added} so far";
                    try
                    {
                        var raw = await _services.Api.GetThreadRawAsync(post.Id, "confidence", null, default);
                        _services.Offline.Save(post.Id, raw);
                        _services.Offline.StartMediaDownload(post.Id, raw);
                        added++;
                    }
                    catch
                    {
                        failed++; // deleted, or a private community: skip it
                    }
                }
                after = next;
                if (after == null) break;
            }
            Status = added == 0
                ? "Your offline library already has every post you've saved."
                : $"Added {added} saved {(added == 1 ? "post" : "posts")} to your offline library" +
                  (failed > 0 ? $" ({failed} couldn't be fetched)." : ".") + " Pictures and videos download in the background.";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't finish syncing (added {added}): {ex.Message}";
        }
        finally
        {
            _syncingLibrary = false;
            CommandManager.InvalidateRequerySuggested();
            foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.Offline)) _ = tab.RefreshLibraryAsync();
        }
    }

    void ExportLibrary(FeedViewModel tab)
    {
        if (tab.Posts.Count == 0)
        {
            Status = "Nothing to export: no posts are showing.";
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export saved posts",
            FileName = $"Skein saved posts {DateTime.Now:yyyy-MM-dd}",
            Filter = "Markdown (*.md)|*.md|Web page (*.html)|*.html|Spreadsheet (*.csv)|*.csv|JSON (*.json)|*.json",
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        try
        {
            var text = LibraryExport.Render([.. tab.Posts], p => _services.Offline.TagsFor(p.Id),
                System.IO.Path.GetExtension(dialog.FileName));
            System.IO.File.WriteAllText(dialog.FileName, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Status = $"Exported {tab.Posts.Count} posts to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't export: {ex.Message}";
        }
    }

    /// <summary>Adds tags to every post the library is showing (after a search or tag filter).</summary>
    void TagShown(FeedViewModel tab)
    {
        var posts = tab.Posts.Where(p => _services.Offline.Has(p.Id)).ToList();
        if (posts.Count == 0) return;
        var dialog = new TextInputWindow("Tag posts", $"Add tags to the {posts.Count} posts shown, separated by commas:", "")
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true) return;
        var add = dialog.Value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (add.Count == 0) return;
        foreach (var post in posts) _services.Offline.SetTags(post.Id, _services.Offline.TagsFor(post.Id).Concat(add));
        Status = $"Tagged {posts.Count} posts with {string.Join(", ", add)}.";
        _ = tab.RefreshLibraryAsync();
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

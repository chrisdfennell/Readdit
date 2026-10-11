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
}

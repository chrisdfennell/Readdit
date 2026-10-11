using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Skein;

public partial class PreferencesWindow : Window
{
    readonly AppSettings _settings;
    readonly UpdateService _updates;
    readonly string _originalUpdateUrl;

    public PreferencesWindow(AppSettings settings, UpdateService updates, string syncStatus = "")
    {
        InitializeComponent();
        _settings = settings;
        _updates = updates;
        _originalUpdateUrl = settings.UpdateUrl;
        // "Check now" tries the typed URL straight away; Cancel puts the old one back.
        Closing += (_, _) =>
        {
            if (DialogResult == true || _settings.UpdateUrl == _originalUpdateUrl) return;
            _settings.UpdateUrl = _originalUpdateUrl;
            _updates.Reset();
        };

        Theme.ItemsSource = Appearance.Themes;
        Theme.SelectedItem = Appearance.Themes.Contains(settings.Theme) ? settings.Theme : "System";
        foreach (var (name, hex) in Appearance.Accents)
        {
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Appearance.ParseColor(hex)),
            };
            var item = new ComboBoxItem
            {
                Tag = hex,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = name } } },
            };
            Accent.Items.Add(item);
            if (hex.Equals(settings.Accent, StringComparison.OrdinalIgnoreCase)) Accent.SelectedItem = item;
        }
        if (Accent.SelectedIndex < 0) Accent.SelectedIndex = 0;
        ReadingSize.Value = settings.ReadingFontSize;
        UiScale.Value = Math.Round(settings.UiScale * 100);
        DownloadFolder.Text = settings.DownloadFolder;
        UpdateUrl.Text = settings.UpdateUrl;
        VersionText.Text = $"Skein version {updates.CurrentVersion} ({updates.Kind})";
        AutoUpdate.IsChecked = settings.AutoUpdate;
        BlurNsfw.IsChecked = settings.BlurNsfw;
        HideNsfw.IsChecked = settings.HideNsfw;
        HideRead.IsChecked = settings.HideRead;
        OpenLinksInApp.IsChecked = settings.OpenLinksInApp;
        HideDuplicates.IsChecked = settings.HideDuplicates;
        SleepTabs.SelectedItem = SleepTabs.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.SleepTabsAfterMinutes.ToString())
                                 ?? SleepTabs.Items[1];
        CheckForNewPosts.IsChecked = settings.CheckForNewPosts;
        SaveOffline.IsChecked = settings.SaveOffline;
        InboxNotifications.IsChecked = settings.InboxNotifications;
        CloseAction.SelectedItem = CloseAction.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == settings.CloseAction) ?? CloseAction.Items[0];
        MinScore.Text = settings.MinScore?.ToString() ?? "";
        CollapseBelow.Text = settings.CollapseBelowScore?.ToString() ?? "";
        MutedUsers.Text = string.Join(Environment.NewLine, settings.MutedUsers);
        HighlightedUsers.Text = string.Join(Environment.NewLine, settings.HighlightedUsers);
        Keywords.Text = string.Join(Environment.NewLine, settings.KeywordFilters);
        Domains.Text = string.Join(Environment.NewLine, settings.DomainFilters);
        Subreddits.Text = string.Join(Environment.NewLine, settings.SubredditFilters);
        ShowLinkHandlerStatus();
        ShowLibrarySize();
        SyncEnabled.IsChecked = settings.SyncEnabled;
        SyncFolder.Text = settings.SyncFolder;
        SyncStatusText.Text = SettingsSync.DefaultFolder() == null && settings.SyncFolder.Length == 0
            ? "OneDrive isn't set up on this PC; pick any folder your PCs share."
            : syncStatus;
        // Coming back from Windows' settings: show whether Skein was picked.
        Activated += (_, _) => ShowLinkHandlerStatus();

        TranslateEnabled.IsChecked = settings.TranslateEnabled;
        var windows = System.Globalization.CultureInfo.CurrentUICulture;
        TranslateTo.Items.Add(new ComboBoxItem { Content = $"Windows' language ({windows.Parent.EnglishName})", Tag = "" });
        foreach (var (code, name) in Languages)
            TranslateTo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        TranslateTo.SelectedItem = TranslateTo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.TranslateTo)
                                   ?? TranslateTo.Items[0];
        TranslateServer.Text = settings.TranslateServer;
        TranslateKey.Text = settings.TranslateKey;
        SendTo.IsChecked = TaskbarExtras.IsInSendTo;

        foreach (var (id, label) in ThreadSummarizer.Models) SummaryModel.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        SummaryModel.SelectedItem = SummaryModel.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.SummaryModel)
                                    ?? SummaryModel.Items[0];
        ShowSummaryKeyState();

        ReadingFont.Items.Add(new ComboBoxItem { Content = "Segoe UI (standard)", Tag = "" });
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.OrdinalIgnoreCase))
            ReadingFont.Items.Add(new ComboBoxItem { Content = family, Tag = family, FontFamily = new System.Windows.Media.FontFamily(family) });
        ReadingFont.SelectedItem = ReadingFont.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.ReadingFont)
                                   ?? ReadingFont.Items[0];
        CustomCss.Text = settings.CustomCss;
        SetUpPages();
    }

    void ShowSummaryKeyState()
    {
        var saved = KeyStore.Unprotect(_settings.SummaryKey) != null;
        var fromEnvironment = !saved && Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 };
        SummaryStatus.Text = saved ? "✓ A key is saved. Type a new one to replace it."
            : fromEnvironment ? "✓ Using the key in the ANTHROPIC_API_KEY environment variable."
            : "No key yet: ✨ Summarize stays hidden until you add one (from console.anthropic.com).";
        ForgetKey.IsEnabled = saved;
    }

    void OnForgetKey(object sender, RoutedEventArgs e)
    {
        _settings.SummaryKey = "";
        SummaryKey.Password = "";
        ShowSummaryKeyState();
    }

    static readonly (string Code, string Name)[] Languages =
    [
        ("ar", "Arabic"), ("zh", "Chinese"), ("cs", "Czech"), ("da", "Danish"), ("nl", "Dutch"), ("en", "English"),
        ("fi", "Finnish"), ("fr", "French"), ("de", "German"), ("el", "Greek"), ("he", "Hebrew"), ("hi", "Hindi"),
        ("hu", "Hungarian"), ("id", "Indonesian"), ("it", "Italian"), ("ja", "Japanese"), ("ko", "Korean"),
        ("no", "Norwegian"), ("pl", "Polish"), ("pt", "Portuguese"), ("ro", "Romanian"), ("ru", "Russian"),
        ("es", "Spanish"), ("sv", "Swedish"), ("th", "Thai"), ("tr", "Turkish"), ("uk", "Ukrainian"), ("vi", "Vietnamese"),
    ];

    void OnCopyBookmarklet(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AppCommands.Bookmarklet);
            ShareStatus.Text = "✓ Copied. In your browser, add a bookmark and paste this as its address (URL). " +
                               "The first time you click it, the browser asks whether to open Skein.";
        }
        catch (Exception ex)
        {
            ShareStatus.Text = $"Couldn't copy it: {ex.Message}";
        }
    }

    // ---- Pages and search -----------------------------------------------------------------------

    /// <summary>The page shown last time, so Preferences opens where you left it.</summary>
    static string _lastPage = "appearance";

    sealed record NavPage(string Key, string Icon, string Name, ScrollViewer Page)
    {
        /// <summary>Every word on the page (titles, descriptions, options), for search.</summary>
        public string Text { get; set; } = "";
    }

    List<NavPage> _pages = [];

    void SetUpPages()
    {
        _pages =
        [
            new("appearance", "🎨", "Appearance", PageAppearance),
            new("feeds", "📰", "Feeds", PageFeeds),
            new("filters", "🚫", "Filters", PageFilters),
            new("people", "👥", "People", PagePeople),
            new("window", "🔔", "Notifications and window", PageWindow),
            new("links", "🔗", "Links and sharing", PageLinks),
            new("language", "🌐", "Translation and summaries", PageLanguage),
            new("library", "📥", "Library and downloads", PageLibrary),
            new("sync", "🔄", "Sync and backup", PageSync),
            new("updates", "⬆", "Updates", PageUpdates),
            new("help", "❓", "Help", PageHelp),
        ];
        foreach (var page in _pages) page.Text = string.Join(" ", Words(page.Page)).ToLowerInvariant();
        ShowNav(_pages);
        Nav.SelectedItem = Nav.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((NavPage)i.Tag).Key == _lastPage) ?? Nav.Items[0];
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Search.Focus();
                Search.SelectAll();
                e.Handled = true;
            }
        };
    }

    void ShowNav(IEnumerable<NavPage> pages)
    {
        Nav.Items.Clear();
        foreach (var page in pages)
        {
            Nav.Items.Add(new ListBoxItem
            {
                Tag = page,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = page.Icon, Width = 28, FontSize = 15 },
                        new TextBlock { Text = page.Name, FontSize = 14 },
                    },
                },
            });
            System.Windows.Automation.AutomationProperties.SetName((ListBoxItem)Nav.Items[^1], page.Name);
        }
    }

    /// <summary>All the text on a page: headings, descriptions, checkbox labels, list choices.</summary>
    static IEnumerable<string> Words(DependencyObject node)
    {
        switch (node)
        {
            case TextBlock { Text.Length: > 0 } text:
                yield return text.Text;
                break;
            case ContentControl { Content: string label }:
                yield return label;
                break;
        }
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var word in Words(child))
                yield return word;
    }

    void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: NavPage selected }) return;
        foreach (var page in _pages) page.Page.Visibility = page == selected ? Visibility.Visible : Visibility.Collapsed;
        selected.Page.ScrollToTop();
        _lastPage = selected.Key;
    }

    void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint == null) return; // during InitializeComponent
        var query = Search.Text.Trim().ToLowerInvariant();
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var current = (Nav.SelectedItem as ListBoxItem)?.Tag as NavPage;
        // Every word typed must appear on the page (or in its name).
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = _pages.Where(p => terms.All(t => p.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || p.Text.Contains(t))).ToList();
        ShowNav(matches);
        NoMatches.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Nav.SelectedItem = Nav.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag is NavPage p && p == current) ?? (Nav.Items.Count > 0 ? Nav.Items[0] : null);
    }

    void OnBrowseDownloadFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Save downloads to",
            InitialDirectory = System.IO.Directory.Exists(DownloadFolder.Text) ? DownloadFolder.Text : "",
        };
        if (dialog.ShowDialog(this) == true) DownloadFolder.Text = dialog.FolderName;
    }

    void OnOpenWebsite(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://chrisdfennell.github.io/Skein/") { UseShellExecute = true });
        }
        catch
        {
            // No browser registered; nothing sensible to do.
        }
    }

    void OnBrowseSyncFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder your PCs share",
            InitialDirectory = SyncFolder.Text.Length > 0 && System.IO.Directory.Exists(SyncFolder.Text) ? SyncFolder.Text : SettingsSync.DefaultFolder() ?? "",
        };
        if (dialog.ShowDialog(this) == true) SyncFolder.Text = dialog.FolderName;
    }

    // ---- Backup -------------------------------------------------------------------------------

    /// <summary>A backup was chosen to restore: Skein restarts to put it in place.</summary>
    public bool RestartToRestore { get; private set; }

    void ShowLibrarySize()
    {
        var bytes = Backup.LibraryBytes(Paths.Local);
        BackupLibrary.Content = bytes == 0
            ? "Include the offline library (it's empty)"
            : $"Include the offline library ({bytes / 1024.0 / 1024.0:0.#} MB of saved posts, pictures and videos)";
        BackupLibrary.IsEnabled = bytes > 0;
    }

    void OnBackUp(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Back up Skein",
            FileName = $"Skein backup {DateTime.Now:yyyy-MM-dd}",
            Filter = "Skein backup (*.zip)|*.zip",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            Backup.Create(dialog.FileName, Paths.Roaming, Paths.Local, BackupLibrary.IsChecked == true, _updates.CurrentVersion);
            BackupStatus.Text = $"✓ Backed up to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            BackupStatus.Text = $"Couldn't back up: {ex.Message}";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    void OnRestore(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Restore a Skein backup", Filter = "Skein backup (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        if (Backup.ReadManifest(dialog.FileName) is not { } manifest)
        {
            BackupStatus.Text = "That file isn't a Skein backup.";
            return;
        }
        var library = manifest.IncludesLibrary ? " and your offline library" : "";
        if (MessageBox.Show(this,
                $"Restore the backup from {manifest.Created.LocalDateTime:f}?\n\n" +
                $"Your settings, folders, alerts, notes and reading history{library} are replaced by the backup's. " +
                "Skein restarts to do it.",
                "Restore a backup", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try
        {
            Backup.Stage(dialog.FileName, Paths.Local);
            RestartToRestore = true;
            DialogResult = false; // nothing here should be saved over the backup
        }
        catch (Exception ex)
        {
            BackupStatus.Text = $"Couldn't restore: {ex.Message}";
        }
    }

    /// <summary>"Report a problem…" was clicked (the main window shows the report dialog).</summary>
    public event Action? ReportProblemRequested;

    void OnReportProblem(object sender, RoutedEventArgs e) => ReportProblemRequested?.Invoke();

    // ---- Links from other apps ------------------------------------------------------------------

    void ShowLinkHandlerStatus()
    {
        var isDefault = LinkHandler.IsDefaultForWebLinks();
        var browser = _settings.FallbackBrowserName.Length > 0 ? _settings.FallbackBrowserName : "your browser";
        LinkHandlerStatus.Text = isDefault
            ? $"✓ Skein opens reddit links. Other links go to {browser}."
            : _settings.LinkHandlerEnabled
                ? "Skein is set up, but Windows still sends web links elsewhere. Click Set up… and choose Skein for HTTP and HTTPS."
                : "Off: links from other apps open in your browser as usual.";
        LinkHandlerUndo.IsEnabled = _settings.LinkHandlerEnabled || isDefault;
    }

    void OnSetUpLinkHandler(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LinkHandler.RegisterAsBrowser() is { } previous)
            {
                // Remember the browser that had web links, to pass non-reddit links on to.
                _settings.FallbackBrowserCommand = previous.Command;
                _settings.FallbackBrowserName = previous.Name;
            }
            _settings.LinkHandlerEnabled = true;
            _settings.Save();
        }
        catch (Exception ex)
        {
            LinkHandlerStatus.Text = $"Couldn't set it up: {ex.Message}";
            return;
        }
        MessageBox.Show(this,
            "Windows will now show Skein's default apps page.\n\nSet Skein as the default for HTTP and for HTTPS. " +
            $"Skein will open reddit links and pass every other link to {(_settings.FallbackBrowserName.Length > 0 ? _settings.FallbackBrowserName : "your browser")}.",
            "Open reddit links in Skein", MessageBoxButton.OK, MessageBoxImage.Information);
        LinkHandler.OpenDefaultAppsSettings();
    }

    void OnUndoLinkHandler(object sender, RoutedEventArgs e)
    {
        var wasDefault = LinkHandler.IsDefaultForWebLinks();
        LinkHandler.UnregisterAsBrowser();
        _settings.LinkHandlerEnabled = false;
        _settings.Save();
        ShowLinkHandlerStatus();
        if (!wasDefault) return;
        MessageBox.Show(this,
            "Windows will now show its default apps page. Pick your browser for web links again " +
            $"({(_settings.FallbackBrowserName.Length > 0 ? _settings.FallbackBrowserName : "for example Edge or Chrome")}).",
            "Stop opening links in Skein", MessageBoxButton.OK, MessageBoxImage.Information);
        LinkHandler.OpenDefaultAppsSettings(forSkein: false);
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.BlurNsfw = BlurNsfw.IsChecked == true;
        _settings.HideNsfw = HideNsfw.IsChecked == true;
        _settings.HideRead = HideRead.IsChecked == true;
        _settings.OpenLinksInApp = OpenLinksInApp.IsChecked == true;
        _settings.HideDuplicates = HideDuplicates.IsChecked == true;
        _settings.SleepTabsAfterMinutes = int.Parse((string)((ComboBoxItem)SleepTabs.SelectedItem).Tag);
        _settings.CheckForNewPosts = CheckForNewPosts.IsChecked == true;
        _settings.SaveOffline = SaveOffline.IsChecked == true;
        _settings.InboxNotifications = InboxNotifications.IsChecked == true;
        _settings.CloseAction = (CloseAction.SelectedItem as ComboBoxItem)?.Tag as string ?? "Ask";
        _settings.SyncFolder = SyncFolder.Text.Trim();
        _settings.SyncEnabled = SyncEnabled.IsChecked == true && _settings.SyncFolder.Length > 0;
        _settings.MinScore = int.TryParse(MinScore.Text.Trim(), out var min) ? min : null;
        _settings.CollapseBelowScore = int.TryParse(CollapseBelow.Text.Trim(), out var collapse) ? collapse : null;
        _settings.MutedUsers = Lines(MutedUsers.Text).Select(StripUserPrefix).ToList();
        _settings.HighlightedUsers = Lines(HighlightedUsers.Text).Select(StripUserPrefix).ToList();
        _settings.KeywordFilters = Lines(Keywords.Text);
        _settings.DomainFilters = Lines(Domains.Text);
        _settings.SubredditFilters = Lines(Subreddits.Text);
        if (SummaryKey.Password.Trim().Length > 0) _settings.SummaryKey = KeyStore.Protect(SummaryKey.Password.Trim());
        _settings.SummaryModel = (SummaryModel.SelectedItem as ComboBoxItem)?.Tag as string ?? ThreadSummarizer.Models[0].Id;
        _settings.ReadingFont = (ReadingFont.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _settings.CustomCss = CustomCss.Text.Length > 20000 ? CustomCss.Text[..20000] : CustomCss.Text;
        _settings.TranslateEnabled = TranslateEnabled.IsChecked == true;
        _settings.TranslateTo = (TranslateTo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _settings.TranslateServer = TranslateServer.Text.Trim();
        _settings.TranslateKey = TranslateKey.Text.Trim();
        if (SendTo.IsChecked != TaskbarExtras.IsInSendTo)
        {
            try
            {
                TaskbarExtras.SetSendTo(SendTo.IsChecked == true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't change the Send to menu: {ex.Message}", "Share to Skein", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        ApplyAppearanceAndUpdateFields();
        DialogResult = true;
    }

    void ApplyAppearanceAndUpdateFields()
    {
        _settings.Theme = Theme.SelectedItem as string ?? "System";
        _settings.Accent = (Accent.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FF4500";
        _settings.ReadingFontSize = (int)ReadingSize.Value;
        _settings.UiScale = UiScale.Value / 100;
        if (DownloadFolder.Text.Trim().Length > 0) _settings.DownloadFolder = DownloadFolder.Text.Trim();
        _settings.AutoUpdate = AutoUpdate.IsChecked == true;
        var url = UpdateUrl.Text.Trim();
        if (url != _settings.UpdateUrl)
        {
            _settings.UpdateUrl = url;
            _updates.Reset();
        }
    }

    async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        var url = UpdateUrl.Text.Trim();
        if (url != _settings.UpdateUrl)
        {
            _settings.UpdateUrl = url; // saved only if the dialog is saved
            _updates.Reset();
        }
        CheckButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        UpdateStatus.Text = await _updates.CheckAsync();
        CheckButton.IsEnabled = true;
    }

    static string StripUserPrefix(string name) =>
        name.StartsWith("/u/") ? name[3..] : name.StartsWith("u/") ? name[2..] : name;

    static List<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
}

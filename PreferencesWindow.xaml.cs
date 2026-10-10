using System.Windows;
using System.Windows.Controls;
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
        SyncEnabled.IsChecked = settings.SyncEnabled;
        SyncFolder.Text = settings.SyncFolder;
        SyncStatusText.Text = SettingsSync.DefaultFolder() == null && settings.SyncFolder.Length == 0
            ? "OneDrive isn't set up on this PC; pick any folder your PCs share."
            : syncStatus;
        // Coming back from Windows' settings: show whether Skein was picked.
        Activated += (_, _) => ShowLinkHandlerStatus();
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

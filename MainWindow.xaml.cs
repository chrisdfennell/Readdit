using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Skein;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;
    readonly TrayIcon _tray = new();
    bool _exiting;

    // Column sizes to restore when leaving reading mode.
    GridLength _sidebarWidth, _listWidth;
    double _sidebarMin, _listMin;

    public MainWindow()
    {
        InitializeComponent();
        var services = new Services(LoginWebView);
        _vm = new MainViewModel(services, new DetailBridge(DetailWebView, services), new InAppBrowser(BrowserWebView));
        DataContext = _vm;
        _vm.FocusSearchRequested += () =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        };
        _vm.InboxNotification += OnInboxNotification;
        _vm.AlertNotification += OnAlertNotification;
        _vm.PaletteRequested += ShowPalette;
        _vm.FullscreenRequested += SetFullscreen;
        Loaded += (_, _) => { if (Program.StartupUrl is { } url) _vm.OpenWhenReady(url); };
        // Offer a reddit link you copied elsewhere when you come back to Skein.
        Activated += (_, _) =>
        {
            try
            {
                if (Clipboard.ContainsText()) _vm.CheckClipboard(Clipboard.GetText());
            }
            catch
            {
                // Another app can hold the clipboard open; just skip this time.
            }
        };
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.AppearanceChanged += ApplyAppearance;
        Loaded += async (_, _) => await _vm.InitializeAsync();

        // Middle-click auto-scroll on the post list.
        _autoScroll = new AutoScroller(this);
        PreviewMouseDown += OnWindowPreviewMouseDown;
        PreviewMouseUp += (_, e) => { if (_autoScroll.OnMouseUp(e)) e.Handled = true; };
        PreviewMouseWheel += (_, _) => { if (_autoScroll.IsActive) _autoScroll.Stop(); };
        Deactivated += (_, _) => { if (_autoScroll.IsActive) _autoScroll.Stop(); };

        _tray.OpenRequested += BringToFront;
        _tray.InboxRequested += OpenInbox;
        _tray.ExitRequested += () =>
        {
            _exiting = true;
            Close();
        };
        Closing += (_, e) =>
        {
            // Tray "Exit", Windows shutting down and restarts skip the question.
            if (_exiting) return;

            var action = _vm.CloseAction;
            if (action == "Ask")
            {
                var dialog = new CloseChoiceWindow { Owner = this };
                if (dialog.ShowDialog() != true || dialog.Choice == null)
                {
                    e.Cancel = true; // Cancel: stay open
                    return;
                }
                action = dialog.Choice;
                if (dialog.RememberChoice) _vm.CloseAction = action;
            }

            if (action == "Exit")
            {
                _exiting = true;
                return;
            }

            e.Cancel = true;
            Hide();
            if (_vm.TakeTrayHint())
                _tray.Notify("Skein is still running",
                    "It'll let you know about new messages. Right-click the tray icon to exit, or change this in Preferences.",
                    BringToFront);
        };
        Closed += (_, _) =>
        {
            _tray.Dispose();
            // A restart is already starting the next copy; installing an update now would swap
            // files under it.
            if (!_restarting) _vm.OnExit();
        };
        _vm.RestartRequested += () =>
        {
            _restarting = true;
            _exiting = true;
            // The new copy waits for this one to exit (single-instance mutex) before starting.
            System.Diagnostics.Process.Start(Environment.ProcessPath!, Program.RestartArgument);
            Application.Current.Shutdown();
        };
        // Velopack exits the process itself to install an update; don't leave a ghost tray icon.
        _vm.ExitingForUpdate += () => _tray.Dispose();
        Application.Current.SessionEnding += (_, _) => _exiting = true;
    }

    bool _restarting;
    readonly AutoScroller _autoScroll;

    void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only on the post list; middle-click keeps its meaning elsewhere (open sidebar entries
        // in a new tab, close tabs).
        ScrollViewer? viewer = null;
        if (e.ChangedButton == MouseButton.Middle && !_autoScroll.IsActive)
        {
            var list = FindAncestor<ListBox>(e.OriginalSource as DependencyObject);
            if (list?.DataContext is not FeedViewModel) return;
            viewer = AutoScroller.FindScrollViewer(list);
        }
        if (_autoScroll.OnMouseDown(e, viewer)) e.Handled = true;
    }

    static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void OpenInbox()
    {
        BringToFront();
        _vm.OpenFeedCommand.Execute("inbox");
    }

    void OnInboxNotification(string title, string text, Post? item)
    {
        // A single new message or reply can be answered right from the notification.
        if (!IsVisible || !IsActive) _tray.Notify(title, text, item != null ? () => ShowQuickReply(item) : OpenInbox);
        FlashTaskbar();
    }

    void OnAlertNotification(string title, string text, Post? post)
    {
        if (IsVisible && IsActive) return; // the status bar says it already
        _tray.Notify(title, text, () =>
        {
            BringToFront();
            if (post != null) _vm.OpenUrl(post.PermalinkUrl);
            else _vm.OpenFeedCommand.Execute("alerts");
        });
        FlashTaskbar();
    }

    QuickReplyWindow? _quickReply;

    void ShowQuickReply(Post item)
    {
        _quickReply?.Close();
        _quickReply = new QuickReplyWindow(item, _vm.Api);
        _quickReply.MarkedRead += _vm.OnItemMarkedRead;
        _quickReply.OpenRequested += opened =>
        {
            BringToFront();
            _vm.OpenFeedCommand.Execute("inbox");
            if (opened.Kind == ItemKind.Comment) _vm.OpenUrl(opened.PermalinkUrl);
        };
        _quickReply.Closed += (_, _) => _quickReply = null;
        _quickReply.Show();
        _quickReply.Activate();
    }

    /// <summary>A link handed over by another app (Skein as link handler, or skein://).</summary>
    public void OpenIncomingLink(string url)
    {
        BringToFront();
        _vm.OpenWhenReady(url);
    }

    void ShowPalette()
    {
        if (!IsVisible) BringToFront();
        var palette = new CommandPaletteWindow(_vm.BuildPaletteItems(), _vm.PaletteExtras) { Owner = this };
        palette.Show();
        palette.Activate();
    }

    /// <summary>Interface scale and the true-black background for the Black theme.</summary>
    void ApplyAppearance()
    {
        var scale = _vm.UiScale;
        RootGrid.LayoutTransform = Math.Abs(scale - 1) < 0.01 ? Transform.Identity : new ScaleTransform(scale, scale);
        if (_vm.IsBlackTheme) Background = Brushes.Black;
        else ClearValue(BackgroundProperty);
    }

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsReadingMode)) ApplyReadingMode(_vm.IsReadingMode);
    }

    /// <summary>Reading mode collapses the sidebar and post list so the post fills the window.</summary>
    void ApplyReadingMode(bool reading)
    {
        if (reading)
        {
            _sidebarWidth = SidebarColumn.Width;
            _listWidth = ListColumn.Width;
            _sidebarMin = SidebarColumn.MinWidth;
            _listMin = ListColumn.MinWidth;
            SidebarColumn.MinWidth = ListColumn.MinWidth = 0;
            SidebarColumn.Width = ListColumn.Width = new GridLength(0);
            SidebarSplitter.Visibility = ListSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            SidebarColumn.Width = _sidebarWidth;
            ListColumn.Width = _listWidth;
            SidebarColumn.MinWidth = _sidebarMin;
            ListColumn.MinWidth = _listMin;
            SidebarSplitter.Visibility = ListSplitter.Visibility = Visibility.Visible;
        }
    }

    // ---- Full screen (F11, or the picture viewer's ⛶) -------------------------------------------

    WindowState _stateBeforeFullscreen;
    WindowStyle _styleBeforeFullscreen;
    ResizeMode _resizeBeforeFullscreen;
    bool _readingBeforeFullscreen;

    /// <summary>On, off, or (null) toggle. Full screen is reading mode without the window frame and bars.</summary>
    void SetFullscreen(bool? on)
    {
        var target = on ?? !_vm.IsFullscreen;
        if (target == _vm.IsFullscreen) return;
        _vm.IsFullscreen = target;
        if (target)
        {
            _stateBeforeFullscreen = WindowState;
            _styleBeforeFullscreen = WindowStyle;
            _resizeBeforeFullscreen = ResizeMode;
            _readingBeforeFullscreen = _vm.IsReadingMode;
            _vm.IsReadingMode = true;
            // Collapse the toolbar and status bar rows (their visibility is bound to sign-in state).
            RootGrid.RowDefinitions[0].Height = RootGrid.RowDefinitions[2].Height = new GridLength(0);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Via Normal, so an already maximized window re-maximizes over the taskbar.
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _styleBeforeFullscreen;
            ResizeMode = _resizeBeforeFullscreen;
            WindowState = _stateBeforeFullscreen;
            RootGrid.RowDefinitions[0].Height = RootGrid.RowDefinitions[2].Height = GridLength.Auto;
            if (!_readingBeforeFullscreen) _vm.IsReadingMode = false;
        }
    }

    // ---- Accounts -----------------------------------------------------------------------------

    void OnAccountButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };

        foreach (var account in _vm.Accounts)
        {
            var label = account.UserName.Length > 0 ? "u/" + account.UserName : "(not signed in yet)";
            var item = new MenuItem { Header = label, IsChecked = account.Profile == _vm.ActiveProfile };
            var profile = account.Profile;
            item.Click += (_, _) => _vm.SwitchAccount(profile);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var add = new MenuItem { Header = "Add another account…" };
        add.Click += (_, _) => _vm.AddAccount();
        menu.Items.Add(add);

        var manage = new MenuItem { Header = "Sign out or manage on reddit.com…" };
        manage.Click += (_, _) => _vm.AccountCommand.Execute(null);
        menu.Items.Add(manage);

        if (_vm.Accounts.Count > 1)
        {
            var active = _vm.Accounts.FirstOrDefault(a => a.Profile == _vm.ActiveProfile);
            var remove = new MenuItem { Header = $"Remove {(active?.UserName is { Length: > 0 } n ? "u/" + n : "this account")} from Skein" };
            remove.Click += (_, _) => _vm.RemoveAccount(_vm.ActiveProfile);
            menu.Items.Add(remove);
        }

        menu.IsOpen = true;
    }

    // ---- Sidebar A–Z jump bar ----------------------------------------------------------------

    void OnJumpLetter(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string letter }) return;
        var index = _vm.IndexOfJumpLetter(letter);
        if (index < 0 || FindDescendant<VirtualizingStackPanel>(SidebarList) is not { } panel ||
            AutoScroller.FindScrollViewer(SidebarList) is not { } viewer)
            return;

        // The list is virtualized, so the row may not exist yet: bring it in, then put it at the top.
        panel.BringIndexIntoViewPublic(index);
        SidebarList.UpdateLayout();
        if (SidebarList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row)
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + row.TransformToAncestor(viewer).Transform(new Point()).Y);
    }

    static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // ---- Post list ----------------------------------------------------------------------------

    void OnPostSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
    }

    /// <summary>Infinite scroll: fetch the next page as the list nears its end.</summary>
    void OnPostListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ListBox { DataContext: FeedViewModel feed }) return;
        if (e.ExtentHeight <= 0 || e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 900) return;
        if (feed.HasMore && !feed.IsLoading && feed.LoadMoreCommand.CanExecute(null)) feed.LoadMoreCommand.Execute(null);
    }

    // ---- Search suggestions -------------------------------------------------------------------

    void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _vm.ShowSuggestions && SuggestionList.Items.Count > 0)
        {
            SuggestionList.SelectedIndex = 0;
            (SuggestionList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.ShowSuggestions = false;
        }
    }

    void OnSuggestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && SuggestionList.SelectedItem is SidebarItem item)
        {
            _vm.PickSuggestionCommand.Execute(item.Spec);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.ShowSuggestions = false;
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    void OnSuggestionClicked(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionList.SelectedItem is SidebarItem item) _vm.PickSuggestionCommand.Execute(item.Spec);
    }

    // ---- Keyboard shortcuts -------------------------------------------------------------------

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_autoScroll.IsActive)
        {
            _autoScroll.Stop(); // any key, Esc especially, ends auto-scroll
            if (key == Key.Escape)
            {
                e.Handled = true;
                return;
            }
        }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;

        // WebView2 hands Ctrl combinations to WPF before the page sees them, so Ctrl+W while
        // typing a reply would close the tab. Web panes handle their own keys: the post view
        // forwards shortcuts itself when you're not typing, and the browser pane reports Esc.
        if (DetailWebView.IsKeyboardFocusWithin || BrowserWebView.IsKeyboardFocusWithin || LoginWebView.IsKeyboardFocusWithin)
            return;

        // Leave typing alone (Esc still backs out of reading mode / the browser pane).
        var typing = Keyboard.FocusedElement is TextBoxBase or PasswordBox ||
                     Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true } ||
                     SuggestionList.IsKeyboardFocusWithin;
        if (typing && key != Key.Escape) return;
        // Esc in the search box closes its suggestions first (its own handler does that).
        if (key == Key.Escape && (SearchBox.IsKeyboardFocusWithin || SuggestionList.IsKeyboardFocusWithin) && _vm.ShowSuggestions)
            return;

        var name = KeyName(key, shift);
        if (name != null && _vm.HandleKey(name, ctrl, shift)) e.Handled = true;
    }

    /// <summary>Maps WPF keys to KeyboardEvent.key-style names, matching what the web view forwards.</summary>
    static string? KeyName(Key key, bool shift) => key switch
    {
        >= Key.A and <= Key.Z => shift ? key.ToString() : key.ToString().ToLowerInvariant(),
        >= Key.D0 and <= Key.D9 when !shift => ((int)(key - Key.D0)).ToString(),
        Key.Space => " ",
        Key.Tab => "Tab",
        Key.F5 => "F5",
        Key.F11 => "F11",
        Key.Escape => "Escape",
        Key.OemPlus => shift ? "+" : "=",
        Key.Add => "+",
        Key.OemMinus or Key.Subtract => "-",
        Key.OemQuestion => shift ? "?" : "/",
        Key.Divide => "/",
        _ => null,
    };

    // ---- Taskbar flash for new inbox items -----------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FlashInfo info);

    void FlashTaskbar()
    {
        if (IsActive || !IsVisible) return;
        const uint FlashTray = 2, FlashUntilForeground = 12;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Hwnd = new WindowInteropHelper(this).Handle,
            Flags = FlashTray | FlashUntilForeground,
        };
        FlashWindowEx(ref info);
    }
}

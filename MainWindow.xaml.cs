using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Readdit;

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
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.AppearanceChanged += ApplyAppearance;
        Loaded += async (_, _) => await _vm.InitializeAsync();

        _tray.OpenRequested += BringToFront;
        _tray.InboxRequested += OpenInbox;
        _tray.NotificationClicked += OpenInbox;
        _tray.ExitRequested += () =>
        {
            _exiting = true;
            Close();
        };
        Closing += (_, e) =>
        {
            if (_exiting || !_vm.CloseToTray) return;
            e.Cancel = true;
            Hide();
            if (_vm.TakeTrayHint())
                _tray.Notify("Readdit is still running",
                    "It'll let you know about new messages. Right-click the tray icon to exit, or turn this off in Preferences.");
        };
        Closed += (_, _) =>
        {
            _tray.Dispose();
            _vm.OnExit();
        };
        _vm.RestartRequested += () =>
        {
            System.Diagnostics.Process.Start(Environment.ProcessPath!);
            _exiting = true;
            Application.Current.Shutdown();
        };
        Application.Current.SessionEnding += (_, _) => _exiting = true;
    }

    void BringToFront()
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

    void OnInboxNotification(string title, string text)
    {
        if (!IsVisible || !IsActive) _tray.Notify(title, text);
        FlashTaskbar();
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
            var remove = new MenuItem { Header = $"Remove {(active?.UserName is { Length: > 0 } n ? "u/" + n : "this account")} from Readdit" };
            remove.Click += (_, _) => _vm.RemoveAccount(_vm.ActiveProfile);
            menu.Items.Add(remove);
        }

        menu.IsOpen = true;
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

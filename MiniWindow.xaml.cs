using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Skein;

/// <summary>
/// Mini mode: a small window (on top of others, if you like) with one feed in a compact list.
/// Opening a post goes back to the full window with that post showing.
/// </summary>
public partial class MiniWindow : Window
{
    readonly MainViewModel _vm;
    readonly AppSettings _settings;

    public MiniWindow(MainViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        Topmost = settings.MiniTopmost;
        PinButton.IsChecked = settings.MiniTopmost;
        PlaceFromSettings();
        Closing += (_, _) => SaveBounds();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>The full window was asked for (⤢, Ctrl+M, opening a post, or closing this one).</summary>
    public event Action? FullWindowRequested;

    void PlaceFromSettings()
    {
        var area = SystemParameters.WorkArea;
        var saved = _settings.MiniBounds;
        if (saved is { Length: 4 } && saved[2] >= MinWidth && saved[3] >= MinHeight &&
            saved[0] >= SystemParameters.VirtualScreenLeft && saved[1] >= SystemParameters.VirtualScreenTop &&
            saved[0] + 40 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            saved[1] + 40 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            (Left, Top, Width, Height) = (saved[0], saved[1], saved[2], saved[3]);
            return;
        }
        // First time: the right-hand edge of the screen.
        Left = area.Right - Width - 16;
        Top = area.Top + 16;
        Height = Math.Min(Height, area.Height - 32);
    }

    void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        _settings.MiniBounds = [Left, Top, Width, Height];
        _settings.Save();
    }

    void OnPinClicked(object sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
        _settings.MiniTopmost = Topmost;
        _settings.Save();
    }

    void OnFullWindow(object sender, RoutedEventArgs e) => FullWindowRequested?.Invoke();

    void OnOpenPost(object sender, RoutedEventArgs e)
    {
        if (Posts.SelectedItem is Post) FullWindowRequested?.Invoke();
    }

    void OnOpenInBrowser(object sender, RoutedEventArgs e)
    {
        if (Posts.SelectedItem is Post post) _vm.OpenInBrowserCommand.Execute(post.HasLink ? post.Url : post.PermalinkUrl);
    }

    void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return) return;
        OnOpenPost(sender, e);
        e.Handled = true;
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FullWindowRequested?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _vm.SelectedTab?.ShowNewCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Loads the next page as the list nears its end.</summary>
    void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0 || e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 400) return;
        if (_vm.SelectedTab is { HasMore: true, IsLoading: false } tab && tab.LoadMoreCommand.CanExecute(null))
            tab.LoadMoreCommand.Execute(null);
    }
}

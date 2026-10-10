using System.Windows;
using System.Windows.Input;

namespace Skein;

/// <summary>Ctrl+K: type to jump anywhere or run a command.</summary>
public partial class CommandPaletteWindow : Window
{
    const int MaxResults = 60;

    readonly IReadOnlyList<PaletteItem> _items;
    readonly Func<string, IEnumerable<PaletteItem>> _extra;
    bool _closing;

    /// <param name="items">Everything to choose from, in the order to show with nothing typed.</param>
    /// <param name="extra">Entries made from the typed text itself ("Search Reddit for …").</param>
    public CommandPaletteWindow(IReadOnlyList<PaletteItem> items, Func<string, IEnumerable<PaletteItem>> extra)
    {
        InitializeComponent();
        _items = items;
        _extra = extra;
        Refresh();
        Loaded += (_, _) =>
        {
            // Near the top of the main window, like a browser's address bar.
            if (Owner != null)
            {
                // A maximized window's Left/Top are its restored position, so use the screen then.
                var maximized = Owner.WindowState == WindowState.Maximized;
                var left = maximized ? SystemParameters.WorkArea.Left : Owner.Left;
                var top = maximized ? SystemParameters.WorkArea.Top : Owner.Top;
                Left = left + (Owner.ActualWidth - ActualWidth) / 2;
                Top = top + 90;
            }
            Query.Focus();
        };
        // Clicking back in Skein dismisses it. (Not any loss of focus: Windows can briefly hand
        // the foreground elsewhere while the palette opens, which would close it on sight.)
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (Owner?.IsActive == true) SafeClose();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    void Refresh()
    {
        var query = Query.Text;
        Placeholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var matches = PaletteMatcher.Filter(_items, query, MaxResults);
        if (query.Trim().Length > 0) matches.AddRange(_extra(query.Trim()));
        Results.ItemsSource = matches;
        if (matches.Count > 0) Results.SelectedIndex = 0;
    }

    void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                Move(10);
                e.Handled = true;
                break;
            case Key.PageUp:
                Move(-10);
                e.Handled = true;
                break;
            case Key.Return:
                RunSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                SafeClose();
                e.Handled = true;
                break;
        }
    }

    void Move(int delta)
    {
        if (Results.Items.Count == 0) return;
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + delta, 0, Results.Items.Count - 1);
        Results.ScrollIntoView(Results.SelectedItem);
    }

    void OnResultClicked(object sender, MouseButtonEventArgs e) => RunSelected();

    void RunSelected()
    {
        if (Results.SelectedItem is not PaletteItem item) return;
        SafeClose();
        // After closing, so dialogs the action opens get the main window as their owner.
        Dispatcher.BeginInvoke(item.Run);
    }
}

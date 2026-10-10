using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Skein;

/// <summary>
/// Browser-style middle-click auto-scroll for a WPF list. Press and hold the wheel button and
/// move the mouse to scroll (release to stop), or click it once to keep scrolling until the next
/// click, Esc or wheel turn. A small ⇕ marker shows where it started.
/// </summary>
public sealed class AutoScroller
{
    const double DeadZone = 10;      // px around the start point where nothing moves
    const double Speed = 0.12;       // scroll px per frame, per px of distance past the dead zone
    const double MaxStep = 120;

    readonly FrameworkElement _host;
    readonly Popup _marker;
    readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    ScrollViewer? _viewer;
    Point _origin;
    bool _moved;

    public AutoScroller(FrameworkElement host)
    {
        _host = host;
        _marker = new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.Relative,
            IsHitTestVisible = false,
            Child = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x30, 0x30, 0x30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1.5),
                Child = new TextBlock
                {
                    Text = "⇕",
                    Foreground = Brushes.White,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        _timer.Tick += (_, _) => Step();
    }

    public bool IsActive => _viewer != null;

    /// <summary>Call from PreviewMouseDown. Returns true if the event was used.</summary>
    public bool OnMouseDown(MouseButtonEventArgs e, ScrollViewer? viewer)
    {
        // Any click while scrolling (from a single middle click) ends it.
        if (IsActive)
        {
            Stop();
            return true;
        }
        if (e.ChangedButton != MouseButton.Middle || viewer == null || viewer.ScrollableHeight <= 0) return false;

        _viewer = viewer;
        _origin = e.GetPosition(_host);
        _moved = false;
        _marker.PlacementTarget = _host;
        _marker.HorizontalOffset = _origin.X - 15;
        _marker.VerticalOffset = _origin.Y - 15;
        _marker.IsOpen = true;
        Mouse.OverrideCursor = Cursors.ScrollNS;
        _host.CaptureMouse();
        _timer.Start();
        return true;
    }

    /// <summary>Call from PreviewMouseUp: releasing after dragging ends hold-to-scroll.</summary>
    public bool OnMouseUp(MouseButtonEventArgs e)
    {
        if (!IsActive || e.ChangedButton != MouseButton.Middle) return false;
        if (_moved) Stop();
        return true; // a click without movement keeps scrolling until the next click
    }

    public void Stop()
    {
        _timer.Stop();
        _viewer = null;
        _marker.IsOpen = false;
        Mouse.OverrideCursor = null;
        if (_host.IsMouseCaptured) _host.ReleaseMouseCapture();
    }

    void Step()
    {
        if (_viewer == null) return;
        if (Mouse.MiddleButton == MouseButtonState.Released && _moved && !_host.IsMouseCaptured)
        {
            Stop();
            return;
        }
        var dy = Mouse.GetPosition(_host).Y - _origin.Y;
        if (Math.Abs(dy) <= DeadZone) return;
        _moved = true;
        var step = Math.Min(MaxStep, (Math.Abs(dy) - DeadZone) * Speed) * Math.Sign(dy);
        _viewer.ScrollToVerticalOffset(Math.Clamp(_viewer.VerticalOffset + step, 0, _viewer.ScrollableHeight));
    }

    /// <summary>The ScrollViewer inside a control's template (e.g. a ListBox's).</summary>
    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }
}

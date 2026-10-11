using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Skein;

/// <summary>
/// Gives icon-only buttons (✕, ⟳, ⚙, ▲…) a proper name for screen readers: their tooltip, which
/// says what they do. Without it a screen reader announces the symbol itself ("multiplication x").
/// Applied to every button in every window as it appears, so new buttons are covered too.
/// </summary>
public static class Accessibility
{
    public static void Start() =>
        EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is ButtonBase button) NameFromToolTip(button);
        }));

    public static void NameFromToolTip(ButtonBase button)
    {
        if (button.ToolTip is not string tip || tip.Trim().Length == 0) return;
        if (!string.IsNullOrEmpty(AutomationProperties.GetName(button))) return;
        var label = button.Content switch
        {
            string text => text,
            TextBlock block => block.Text,
            _ => null,
        };
        // Only where the visible label isn't words already.
        if (label != null && !IsSymbol(label)) return;
        AutomationProperties.SetName(button, tip.Trim());
    }

    /// <summary>A label made of symbols rather than words ("✕", "⟳", "📋").</summary>
    public static bool IsSymbol(string label) =>
        label.Trim().Length is > 0 and <= 4 && !label.Any(char.IsLetterOrDigit);
}

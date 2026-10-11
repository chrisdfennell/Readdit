using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Skein;

/// <summary>Applies the theme and accent colour chosen in Preferences.</summary>
public static class Appearance
{
    public static readonly IReadOnlyList<string> Themes = ["System", "Light", "Dark", "Black", "High contrast"];

    public static readonly IReadOnlyList<(string Name, string Hex)> Accents =
    [
        ("Reddit orange", "#FF4500"),
        ("Blue", "#0A84FF"),
        ("Green", "#2EA043"),
        ("Purple", "#8957E5"),
        ("Pink", "#E0457B"),
        ("Teal", "#12A4A4"),
    ];

    public static void ApplyToApp(AppSettings settings)
    {
        var app = Application.Current;
        app.ThemeMode = settings.Theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" or "Black" or "High contrast" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        // High contrast uses a bright yellow accent whatever colour was picked.
        app.Resources["Accent"] = new SolidColorBrush(settings.Theme == "High contrast" ? Color.FromRgb(0xFF, 0xD4, 0x00) : ParseColor(settings.Accent));
    }

    /// <summary>Makes web pages' prefers-color-scheme follow the app theme.</summary>
    public static void ApplyToWebView(CoreWebView2? core, string theme)
    {
        if (core == null) return;
        core.Profile.PreferredColorScheme = theme switch
        {
            "Light" => CoreWebView2PreferredColorScheme.Light,
            "Dark" or "Black" or "High contrast" => CoreWebView2PreferredColorScheme.Dark,
            _ => CoreWebView2PreferredColorScheme.Auto,
        };
    }

    public static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Color.FromRgb(0xFF, 0x45, 0x00);
        }
    }
}

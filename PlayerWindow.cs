using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Skein;

/// <summary>
/// A small always-on-top window that keeps a video playing while you read, for when the post
/// view can't use the browser's own picture in picture.
/// </summary>
public sealed class PlayerWindow : Window
{
    readonly WebView2 _view = new();

    /// <param name="area">The screen to sit in the corner of (Skein's own, not necessarily the main one).</param>
    public PlayerWindow(Services services, string kind, string url, string poster, double time, string title, Rect area)
    {
        Title = title.Length > 0 ? title : "Skein player";
        Width = 560;
        Height = 340;
        MinWidth = 240;
        MinHeight = 160;
        Topmost = true;
        WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = true;
        Background = System.Windows.Media.Brushes.Black;
        Content = _view;
        // Bottom right of the screen, out of the way.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Right - Width - 24;
        Top = area.Bottom - Height - 24;

        var source = JsonSerializer.Serialize(new { kind, url, poster, time });
        Loaded += async (_, _) =>
        {
            try
            {
                await _view.EnsureCoreWebView2Async(services.Session.Environment, services.Session.CreateControllerOptions());
                var core = _view.CoreWebView2;
                core.SetVirtualHostNameToFolderMapping("skein.app", System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                    CoreWebView2HostResourceAccessKind.DenyCors);
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                // Only the player page; nothing in it should navigate anywhere.
                core.NavigationStarting += (_, e) =>
                {
                    if (!e.Uri.StartsWith("https://skein.app/player.html", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
                };
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.Navigate("https://skein.app/player.html#" + Uri.EscapeDataString(source));
            }
            catch
            {
                Close();
            }
        };
        Closed += (_, _) => _view.Dispose();
    }
}

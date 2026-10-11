using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Skein;

/// <summary>
/// Native Windows notifications with buttons: reply to a message right there, mark it read,
/// snooze a reminder. Clicks come back through <see cref="Activated"/>; if notifications can't
/// be shown, callers fall back to the tray's simpler ones.
/// </summary>
public static class Toasts
{
    /// <summary>A notification (or one of its buttons) was clicked: its arguments, and anything typed in it.</summary>
    public static event Action<ToastArguments, IReadOnlyDictionary<string, object>>? Activated;

    static bool _listening;

    public static void Start()
    {
        if (_listening) return;
        _listening = true;
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                var input = e.UserInput.ToDictionary(p => p.Key, p => p.Value);
                // Arrives on a background thread.
                Application.Current?.Dispatcher.BeginInvoke(() => Activated?.Invoke(args, input));
            };
        }
        catch
        {
            // Older Windows: the tray's notifications are used instead.
        }
    }

    /// <summary>Shows a notification; false if Windows wouldn't (the caller falls back).</summary>
    public static bool Show(Action<ToastContentBuilder> build)
    {
        try
        {
            var builder = new ToastContentBuilder();
            build(builder);
            builder.Show();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes Skein's notification registration (on uninstall).</summary>
    public static void Uninstall()
    {
        try
        {
            ToastNotificationManagerCompat.Uninstall();
        }
        catch
        {
            // Nothing registered.
        }
    }
}

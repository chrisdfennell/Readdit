using System.Windows;
using Forms = System.Windows.Forms;

namespace Readdit;

/// <summary>System tray icon with a small menu and Windows notifications.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;

    public event Action? OpenRequested;
    public event Action? InboxRequested;
    public event Action? ExitRequested;
    public event Action? NotificationClicked;

    public TrayIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/readdit.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "Readdit",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Items.Add("Open Readdit", null, (_, _) => OpenRequested?.Invoke());
        _icon.ContextMenuStrip.Items.Add("Inbox", null, (_, _) => InboxRequested?.Invoke());
        _icon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) => NotificationClicked?.Invoke();
    }

    /// <summary>Shows a Windows notification (rendered as a toast on Windows 10/11).</summary>
    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(6000, title, text.Length > 0 ? text : " ", Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

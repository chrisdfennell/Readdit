using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Skein;

/// <summary>
/// Skein on the Windows desktop: the unread badge and jump list on its taskbar button, the
/// Send to menu entry, and putting two windows side by side.
/// </summary>
static class TaskbarExtras
{
    static string ExePath => Environment.ProcessPath ?? "Skein.exe";

    // ---- Unread badge ---------------------------------------------------------------------------

    /// <summary>A small red badge with the count (9+ beyond nine); null for none.</summary>
    public static ImageSource? Badge(int count)
    {
        if (count <= 0) return null;
        var text = count > 9 ? "9+" : count.ToString();
        var glyph = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            text.Length > 1 ? 9 : 11, Brushes.White, 1.0);
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), new Pen(Brushes.White, 1), new Point(8, 8), 7.5, 7.5);
            dc.DrawText(glyph, new Point(8 - glyph.Width / 2, 8 - glyph.Height / 2));
        }
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    // ---- Jump list --------------------------------------------------------------------------------

    /// <summary>Right-click Skein's taskbar button: straight to Inbox, Home, a new post, reminders.</summary>
    public static void SetJumpList()
    {
        try
        {
            JumpTask Item(string title, string command, string description) => new()
            {
                Title = title,
                Description = description,
                ApplicationPath = ExePath,
                Arguments = AppCommands.Link(command),
                IconResourcePath = ExePath,
            };
            var list = new JumpList([
                Item("New post", "submit", "Write a new post"),
                Item("Inbox", "inbox", "Your messages and replies"),
                Item("Home", "home", "Your home feed"),
                Item("Reminders", "later", "Posts you asked to be reminded about, and scheduled posts"),
            ], showFrequent: false, showRecent: false);
            JumpList.SetJumpList(Application.Current, list);
            list.Apply();
        }
        catch
        {
            // A nicety; the taskbar button works without it.
        }
    }

    // ---- Send to ----------------------------------------------------------------------------------

    static string SendToShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Skein (new post).lnk");

    public static bool IsInSendTo => File.Exists(SendToShortcut);

    /// <summary>Adds (or removes) "Skein (new post)" in Explorer's Send to menu, for pictures and videos.</summary>
    public static void SetSendTo(bool on)
    {
        if (!on)
        {
            if (File.Exists(SendToShortcut)) File.Delete(SendToShortcut);
            return;
        }
        // The shell's own shortcut maker (WScript.Shell), late-bound so there's no COM reference to carry.
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available.");
        var shell = Activator.CreateInstance(type)!;
        try
        {
            var link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [SendToShortcut])!;
            var linkType = link.GetType();
            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, [ExePath]);
            linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, [ExePath + ",0"]);
            linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, ["Start a new Skein post with these pictures or video"]);
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Keeps the Send to entry pointing at this copy (updates can move the exe).</summary>
    public static void RefreshSendTo()
    {
        try
        {
            if (IsInSendTo) SetSendTo(true);
        }
        catch
        {
            // The old entry stays; it may still work.
        }
    }

    // ---- Side by side -------------------------------------------------------------------------------

    /// <summary>Puts <paramref name="left"/> on the left half of its screen and <paramref name="right"/> on the right half.</summary>
    public static void SideBySide(Window left, Window right)
    {
        if (WorkAreaOf(left) is not { } area) return;
        var half = area.Width / 2;
        left.WindowState = WindowState.Normal;
        left.Left = area.Left;
        left.Top = area.Top;
        left.Width = half;
        left.Height = area.Height;
        right.WindowState = WindowState.Normal;
        right.Left = area.Left + half;
        right.Top = area.Top;
        right.Width = half;
        right.Height = area.Height;
    }

    /// <summary>The usable part (without the taskbar) of the screen a window is on, in WPF units.</summary>
    public static System.Windows.Rect? WorkAreaOf(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return null;
        var toDips = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new System.Windows.Rect(toDips.Transform(new Point(info.Work.Left, info.Work.Top)),
            toDips.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

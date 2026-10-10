using System.IO;
using System.IO.Pipes;
using System.Windows;
using Velopack;

namespace Skein;

public static class Program
{
    /// <summary>Passed by Skein when it relaunches itself (account switch, portable update).</summary>
    public const string RestartArgument = "--restarted";

    static readonly string InstanceName = $"Skein-{Environment.UserName}";
    static string LinkPipeName => InstanceName + "-links";

    /// <summary>A link Skein was started with (from another app), to open once signed in.</summary>
    public static string? StartupUrl { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install/uninstall/update hooks from the Velopack installer.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => LinkHandler.UnregisterAll())
            .Run();

        // A link from another app (Skein as the web link handler, or a skein:// link), or
        // pictures sent from Explorer's Send to menu.
        var url = args.Select(LinkHandler.NormalizeIncoming).FirstOrDefault(u => u != null) ?? AppCommands.ForFiles(args);
        if (url != null && !url.StartsWith(AppCommands.FilesPrefix) && !LinkHandler.IsRedditUrl(url))
        {
            // Not Reddit: straight on to the real browser, without starting Skein at all.
            LinkHandler.OpenInFallbackBrowser(url, AppSettings.Load().FallbackBrowserCommand);
            return;
        }

        // One Skein per user. Launching it again (e.g. from Start while it sits in the tray)
        // brings the running one forward instead of starting a second copy with its own tray icon,
        // notifications and settings writes.
        using var instance = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-show");
        if (!isFirst)
        {
            if (!args.Contains(RestartArgument))
            {
                if (url == null || !SendLink(url)) showSignal.Set();
                return;
            }
            // Relaunched by the previous copy: wait for it to finish exiting (and release its
            // browser profile) before starting.
            try
            {
                if (!instance.WaitOne(TimeSpan.FromSeconds(20))) return;
            }
            catch (AbandonedMutexException)
            {
                // The previous copy ended without releasing it; we own it now.
            }
        }
        StartupUrl = url;

        // Before anything (WebView2, logs) opens the data folder.
        Paths.MigrateLocalData();
        // A backup chosen in Preferences is put in place now, before anything reads these files.
        Backup.ApplyPending(Paths.Roaming, Paths.Local);
        LinkHandler.RemoveLegacyRegistrations();
        LinkHandler.RegisterProtocol();
        // Keep the web link registration pointing at this copy (updates can move the exe).
        if (AppSettings.Load().LinkHandlerEnabled)
        {
            try
            {
                LinkHandler.RegisterAsBrowser();
            }
            catch
            {
                // Links from other apps keep using the previous registration.
            }
        }

        var app = new App();
        app.InitializeComponent();

        // Another launch asked us to show ourselves.
        var listener = new Thread(() =>
        {
            while (showSignal.WaitOne())
                app.Dispatcher.BeginInvoke(() => (app.MainWindow as MainWindow)?.BringToFront());
        })
        { IsBackground = true, Name = "Skein single-instance listener" };
        listener.Start();

        // …or handed us a link to open.
        var links = new Thread(() => ReceiveLinks(url => app.Dispatcher.BeginInvoke(() => (app.MainWindow as MainWindow)?.OpenIncomingLink(url))))
        { IsBackground = true, Name = "Skein link listener" };
        links.Start();

        app.Run();
        instance.ReleaseMutex();
    }

    /// <summary>Hands a link to the running copy. False if it isn't listening (an older version).</summary>
    static bool SendLink(string url)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", LinkPipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe);
            writer.Write(url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void ReceiveLinks(Action<string> open)
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(LinkPipeName, PipeDirection.In, 1);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe);
                var text = reader.ReadToEnd();
                // Only ever a web link or shared files; anything else is ignored.
                if (text.Length >= 8192) continue;
                if (text.StartsWith(AppCommands.FilesPrefix, StringComparison.Ordinal)) open(text);
                else if (LinkHandler.NormalizeIncoming(text) is { } link) open(link);
            }
            catch
            {
                Thread.Sleep(500); // e.g. a client that disconnected mid-way
            }
        }
    }
}

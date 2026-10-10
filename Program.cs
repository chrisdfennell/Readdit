using System.Windows;
using Velopack;

namespace Readdit;

public static class Program
{
    /// <summary>Passed by Readdit when it relaunches itself (account switch, portable update).</summary>
    public const string RestartArgument = "--restarted";

    static readonly string InstanceName = $"Readdit-{Environment.UserName}";

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install/uninstall/update hooks from the Velopack installer.
        VelopackApp.Build().Run();

        // One Readdit per user. Launching it again (e.g. from Start while it sits in the tray)
        // brings the running one forward instead of starting a second copy with its own tray icon,
        // notifications and settings writes.
        using var instance = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-show");
        if (!isFirst)
        {
            if (!args.Contains(RestartArgument))
            {
                showSignal.Set();
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

        // Before anything (WebView2, logs) opens the data folder.
        Paths.MigrateLocalData();

        var app = new App();
        app.InitializeComponent();

        // Another launch asked us to show ourselves.
        var listener = new Thread(() =>
        {
            while (showSignal.WaitOne())
                app.Dispatcher.BeginInvoke(() => (app.MainWindow as MainWindow)?.BringToFront());
        })
        { IsBackground = true, Name = "Readdit single-instance listener" };
        listener.Start();

        app.Run();
        instance.ReleaseMutex();
    }
}

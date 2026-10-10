using System.IO;
using System.Windows;

namespace Skein;

public partial class App : Application
{
    static readonly string CrashLog = Path.Combine(Paths.Local, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            MessageBox.Show($"Something went wrong:\n\n{args.Exception.Message}\n\nDetails were written to {CrashLog}.\n" +
                            "To send them in, use Preferences → Report a problem (or Ctrl+K → Report a problem).",
                "Skein", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };
        UpdateService.CleanUpAfterPortableUpdate();
        // Apply the saved theme before the window appears, to avoid a flash of the wrong one.
        Appearance.ApplyToApp(AppSettings.Load());
        base.OnStartup(e);
    }

    static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Paths.Local);
            File.AppendAllText(CrashLog, $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch
        {
            // Nothing more we can do.
        }
    }
}

using Velopack;

namespace Readdit;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install/uninstall/update hooks from the Velopack installer.
        VelopackApp.Build().Run();

        // Before anything (WebView2, logs) opens the data folder.
        Paths.MigrateLocalData();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

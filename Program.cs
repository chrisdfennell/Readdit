using Velopack;

namespace Readdit;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install/uninstall/update hooks from the Velopack installer.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

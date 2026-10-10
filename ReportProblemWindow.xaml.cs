using System.Diagnostics;
using System.Windows;

namespace Skein;

/// <summary>Collects a description and makes a problem report (see <see cref="ProblemReport"/>).</summary>
public partial class ReportProblemWindow : Window
{
    readonly string _summaryVersion;
    readonly string _kind;
    readonly string _repositoryUrl;
    readonly string? _userName;

    public ReportProblemWindow(string version, string kind, string repositoryUrl, string? userName)
    {
        InitializeComponent();
        _summaryVersion = version;
        _kind = kind;
        _repositoryUrl = repositoryUrl;
        _userName = userName;
        Loaded += (_, _) => Description.Focus();
    }

    string Summary()
    {
        string webView;
        try
        {
            webView = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            webView = "not found";
        }
        return ProblemReport.Summary(Description.Text, _summaryVersion, _kind, webView,
            Environment.OSVersion.VersionString);
    }

    void OnCreate(object sender, RoutedEventArgs e)
    {
        try
        {
            var summary = Summary();
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var zip = ProblemReport.CreateZip(desktop, summary, IncludeLogs.IsChecked == true, _userName);
            // Show the zip (so it's easy to check and attach), then the issue page.
            Process.Start("explorer.exe", $"/select,\"{zip}\"");
            OpenIssue(ProblemReport.Sanitize(summary, _userName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Status.Text = $"Couldn't make the report: {ex.Message}";
            Status.Visibility = Visibility.Visible;
        }
    }

    void OnIssueOnly(object sender, RoutedEventArgs e)
    {
        OpenIssue(ProblemReport.Sanitize(Summary(), _userName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        DialogResult = true;
    }

    void OpenIssue(string summary)
    {
        var url = ProblemReport.IssueUrl(_repositoryUrl, Description.Text, summary)
                  ?? ProblemReport.IssueUrl("https://github.com/chrisdfennell/Skein", Description.Text, summary);
        if (url != null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

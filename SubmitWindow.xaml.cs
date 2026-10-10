using System.Windows;

namespace Readdit;

public partial class SubmitWindow : Window
{
    readonly RedditApi _api;

    public SubmitWindow(RedditApi api, string subreddit)
    {
        InitializeComponent();
        _api = api;
        Subreddit.Text = subreddit;
        Loaded += (_, _) => (subreddit.Length > 0 ? PostTitle : Subreddit).Focus();
    }

    public string? PostUrl { get; private set; }

    void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (ContentLabel == null) return; // fires during InitializeComponent
        var isLink = LinkKind.IsChecked == true;
        ContentLabel.Text = isLink ? "URL" : "Text (Markdown supported)";
        Body.AcceptsReturn = !isLink;
    }

    async void OnPost(object sender, RoutedEventArgs e)
    {
        var sub = Subreddit.Text.Trim().TrimStart('/');
        if (sub.StartsWith("r/", StringComparison.OrdinalIgnoreCase)) sub = sub[2..];
        var title = PostTitle.Text.Trim();
        var isLink = LinkKind.IsChecked == true;
        var content = Body.Text.Trim();

        if (sub.Length == 0 || title.Length == 0 || (isLink && content.Length == 0))
        {
            ErrorText.Text = isLink ? "Community, title and URL are required." : "Community and title are required.";
            return;
        }

        PostButton.IsEnabled = false;
        ErrorText.Text = "";
        try
        {
            PostUrl = await _api.SubmitAsync(sub, title, isLink, content, Nsfw.IsChecked == true, Spoiler.IsChecked == true);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            PostButton.IsEnabled = true;
        }
    }
}

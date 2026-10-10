using System.Windows;

namespace Skein;

/// <summary>
/// A post in a window of its own, so two can be read side by side (or one kept open while you
/// browse). It's the same post view as the main window's, sharing its account and settings.
/// </summary>
public partial class PostWindow : Window
{
    readonly DetailBridge _detail;

    public PostWindow(Services services, MainViewModel main, string postId)
    {
        InitializeComponent();
        _detail = new DetailBridge(View, services);
        // Other threads open here too; anything else goes to the main window.
        _detail.OpenUrlRequested += url =>
        {
            if (RedditLinks.Classify(url) is ThreadLink thread) _ = _detail.OpenThreadAsync(thread.PostId, thread.CommentId);
            else main.OpenUrl(url);
        };
        _detail.ExternalUrlRequested += url => main.OpenLinkCommand.Execute(url);
        _detail.StatusMessage += message => StatusText.Text = message;
        _detail.WatchRequested += main.SetWatching;
        _detail.FollowRequested += main.SetFollowing;
        _detail.RemindRequested += main.Remind;
        _detail.ComposeRequested += to => main.NewMessageCommand.Execute(to);
        _detail.PopOutRequested += main.PopOut;
        _detail.KeyPressed += (key, ctrl, _) =>
        {
            if (key == "Escape" || (ctrl && key.Equals("w", StringComparison.OrdinalIgnoreCase))) Close();
            else if (key == "R") _detail.ReadAloud();
        };
        Loaded += async (_, _) =>
        {
            try
            {
                await _detail.InitializeAsync(services.Session.Environment, services.Session.CreateControllerOptions());
                _detail.SetTheme(services.Settings.Theme, services.Settings.Accent);
                _detail.SetDisplay(services.Settings.ReadingFontSize, readingMode: false);
                await _detail.ShowThreadAsync(postId, null);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Couldn't open the post: {ex.Message}";
            }
        };
        Closed += (_, _) => View.Dispose();
    }
}

using System.Windows;
using System.Windows.Input;

namespace Readdit;

/// <summary>
/// Opened from a new-message notification: read the message and answer it without bringing up
/// the main window.
/// </summary>
public partial class QuickReplyWindow : Window
{
    readonly Post _item;
    readonly RedditApi _api;
    bool _markedRead;

    public QuickReplyWindow(Post item, RedditApi api)
    {
        InitializeComponent();
        _item = item;
        _api = api;
        var from = item.Author.StartsWith("r/") ? item.Author : "u/" + item.Author;
        Heading.Text = item.Kind == ItemKind.Comment ? $"{from} replied to you" : $"Message from {from}";
        Context.Text = item.Kind == ItemKind.Comment
            ? (item.Title.Length > 0 ? $"in “{item.Title}”" : "") + (item.Subreddit.Length > 0 ? $" · r/{item.Subreddit}" : "")
            : item.Title;
        Body.Text = item.SelfText;
        Loaded += (_, _) =>
        {
            // Bottom-right, where Windows notifications appear.
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
            Reply.Focus();
        };
    }

    /// <summary>"Open in Readdit" was clicked: show the conversation in the main window.</summary>
    public event Action<Post>? OpenRequested;

    /// <summary>The item was marked read here (the main window updates its count).</summary>
    public event Action<Post>? MarkedRead;

    void OnReplyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            OnSend(sender, e);
        }
    }

    async void OnSend(object sender, RoutedEventArgs e)
    {
        var text = Reply.Text.Trim();
        if (text.Length == 0)
        {
            Reply.Focus();
            return;
        }
        SendButton.IsEnabled = Reply.IsEnabled = false;
        ShowStatus("Sending…");
        try
        {
            await _api.CommentAsync(_item.FullName, text);
            await MarkReadAsync();
            ShowStatus("Sent ✓");
            await Task.Delay(900);
            Close();
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't send: {ex.Message}");
            SendButton.IsEnabled = Reply.IsEnabled = true;
        }
    }

    async void OnMarkRead(object sender, RoutedEventArgs e)
    {
        await MarkReadAsync();
        Close();
    }

    async Task MarkReadAsync()
    {
        if (_markedRead) return;
        try
        {
            await _api.MarkReadAsync(_item.FullName);
            _markedRead = true;
            MarkedRead?.Invoke(_item);
        }
        catch
        {
            // It stays unread on Reddit; harmless.
        }
    }

    void OnOpen(object sender, RoutedEventArgs e)
    {
        OpenRequested?.Invoke(_item);
        Close();
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();

    void ShowStatus(string text)
    {
        Status.Text = text;
        Status.Visibility = Visibility.Visible;
    }
}

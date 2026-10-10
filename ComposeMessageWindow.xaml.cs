using System.Windows;
using System.Windows.Input;

namespace Skein;

/// <summary>Writes a new private message to a user.</summary>
public partial class ComposeMessageWindow : Window
{
    readonly RedditApi _api;
    bool _sending;

    public ComposeMessageWindow(RedditApi api, string to = "")
    {
        InitializeComponent();
        _api = api;
        To.Text = to.StartsWith("u/", StringComparison.OrdinalIgnoreCase) ? to[2..] : to;
        Loaded += (_, _) => (To.Text.Length > 0 ? Subject : To).Focus();
        Closing += (_, e) => e.Cancel = _sending;
    }

    /// <summary>The username typed, without any u/ prefix.</summary>
    public string Recipient
    {
        get
        {
            var to = To.Text.Trim().TrimStart('/');
            return to.StartsWith("u/", StringComparison.OrdinalIgnoreCase) ? to[2..] : to;
        }
    }

    void OnBodyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            OnSend(sender, e);
        }
    }

    async void OnSend(object sender, RoutedEventArgs e)
    {
        if (Recipient.Length == 0 || Subject.Text.Trim().Length == 0 || Body.Text.Trim().Length == 0)
        {
            Status.Text = "Fill in who it's to, a subject and a message.";
            return;
        }
        _sending = true;
        SendButton.IsEnabled = false;
        Status.Text = "Sending…";
        try
        {
            await _api.ComposeAsync(Recipient, Subject.Text.Trim(), Body.Text.Trim());
            _sending = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _sending = false;
            Status.Text = ex.Message;
            SendButton.IsEnabled = true;
        }
    }
}

using System.Windows;
using System.Windows.Controls;

namespace Skein;

/// <summary>Asks when: quick choices ("Tomorrow morning"), or a day and time.</summary>
public partial class WhenWindow : Window
{
    public WhenWindow(string title, string prompt, string subject, IEnumerable<(string Key, string Label)> choices)
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        Subject.Text = subject;
        Subject.Visibility = subject.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var now = DateTime.Now;
        Choices.ItemsSource = choices
            .Select(c => new { c.Key, c.Label, When = LaterTimes.Describe(LaterTimes.For(c.Key, now), now) })
            .ToList();
        var later = now.AddHours(1);
        DateBox.SelectedDate = later.Date;
        DateBox.DisplayDateStart = now.Date;
        TimeBox.Text = later.ToString("HH:00");
    }

    /// <summary>The chosen time.</summary>
    public DateTime When { get; private set; }

    void OnChoice(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        When = LaterTimes.For(key, DateTime.Now);
        DialogResult = true;
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        var time = LaterTimes.ParseTime(TimeBox.Text);
        if (DateBox.SelectedDate is not { } day || time == null)
        {
            Show("Pick a day, and a time like 9:30 or 7pm.");
            return;
        }
        var when = day.Date + time.Value;
        if (when <= DateTime.Now.AddMinutes(1))
        {
            Show("That time has already passed.");
            return;
        }
        When = when;
        DialogResult = true;
    }

    void Show(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}

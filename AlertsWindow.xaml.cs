using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace Readdit;

/// <summary>Adds, edits and removes keyword alerts.</summary>
public partial class AlertsWindow : Window
{
    readonly ObservableCollection<AlertRule> _rules;

    public AlertsWindow(IEnumerable<AlertRule> rules, int intervalMinutes)
    {
        InitializeComponent();
        // Edit copies, so Cancel leaves the real rules alone.
        _rules = new ObservableCollection<AlertRule>(rules.Select(r => r.Clone()));
        _watching = _rules.ToDictionary(r => r, WatchKey);
        Rules.ItemsSource = _rules;
        Interval.SelectedItem = Interval.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == intervalMinutes.ToString())
                                ?? Interval.Items[1];
        Loaded += (_, _) => { if (_rules.Count == 0) OnAdd(this, new RoutedEventArgs()); };
    }

    public List<AlertRule> Result => [.. _rules];

    public int IntervalMinutes => int.Parse((string)((ComboBoxItem)Interval.SelectedItem).Tag);

    void OnAdd(object sender, RoutedEventArgs e)
    {
        var rule = new AlertRule { Name = $"Alert {_rules.Count + 1}" };
        _rules.Add(rule);
        Rules.SelectedItem = rule;
        Rules.CurrentCell = new DataGridCellInfo(rule, Rules.Columns[2]);
        Rules.Focus();
        Rules.BeginEdit();
    }

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Rules.SelectedItem is AlertRule rule) _rules.Remove(rule);
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        Rules.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        var unusable = _rules.FirstOrDefault(r => !AlertMatcher.IsUsable(r));
        if (unusable != null)
        {
            Error.Text = $"“{unusable.Name}” needs some words, some communities, or both.";
            Error.Visibility = Visibility.Visible;
            return;
        }
        foreach (var rule in _rules.Where(r => r.Name.Trim().Length == 0)) rule.Name = rule.Keywords.Trim();
        // A rule that now watches for something else starts over: its matches so far are listed,
        // and only posts after that are announced.
        foreach (var rule in _rules.Where(r => !_watching.TryGetValue(r, out var key) || key != WatchKey(r)))
            rule.LastChecked = 0;
        DialogResult = true;
    }

    readonly Dictionary<AlertRule, string> _watching;

    static string WatchKey(AlertRule r) => $"{r.Keywords}|{r.Subreddits}|{r.SearchText}";
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Readdit;

/// <summary>A community the cleanup window suggests leaving, and why.</summary>
public sealed class CleanupCandidate(string name, long members, string reason) : ObservableObject
{
    public string Name => name;
    public string Label => "r/" + name;
    public string MembersText => members > 0 ? Format.Count(members) + " members" : "";
    public string Reason => reason;

    bool _isChecked = true;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

/// <summary>Finds dead or quiet subscriptions and unsubscribes from the ones you pick.</summary>
public partial class CleanupWindow : Window
{
    readonly RedditApi _api;
    readonly List<SubredditRef> _subscriptions;
    readonly ObservableCollection<CleanupCandidate> _candidates = [];
    readonly HashSet<string> _flagged = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? _scan;

    public CleanupWindow(RedditApi api, List<SubredditRef> subscriptions)
    {
        InitializeComponent();
        _api = api;
        // Profiles you follow (u_name) are listed as communities but aren't what this is for.
        _subscriptions = subscriptions.Where(s => s.Type != "user").ToList();
        List.ItemsSource = _candidates;
        _candidates.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (CleanupCandidate c in e.NewItems) c.PropertyChanged += OnCandidateChanged;
            UpdateUnsubscribeButton();
        };
        Loaded += async (_, _) => await QuickCheckAsync();
        Closing += (_, _) => _scan?.Cancel();
    }

    /// <summary>Communities you unsubscribed from here (the sidebar reloads if any).</summary>
    public List<string> Unsubscribed { get; } = [];

    void OnCandidateChanged(object? sender, PropertyChangedEventArgs e) => UpdateUnsubscribeButton();

    void UpdateUnsubscribeButton()
    {
        var count = _candidates.Count(c => c.IsChecked);
        UnsubscribeButton.IsEnabled = count > 0 && _scan == null;
        UnsubscribeButton.Content = count > 0 ? $"Unsubscribe from {count}" : "Unsubscribe";
    }

    void Flag(string name, long members, string reason)
    {
        if (_flagged.Add(name)) _candidates.Add(new CleanupCandidate(name, members, reason));
    }

    /// <summary>Banned, deleted, private and quarantined communities: a few requests in all.</summary>
    async Task QuickCheckAsync()
    {
        foreach (var sub in _subscriptions)
        {
            if (sub.Type == "private") Flag(sub.Name, sub.Subscribers, "Private: you can't see its posts");
            else if (sub.Quarantined) Flag(sub.Name, sub.Subscribers, "Quarantined");
            else if (sub.Type == "archived") Flag(sub.Name, sub.Subscribers, "Archived: no new posts allowed");
        }

        Progress.Text = $"Looking up {_subscriptions.Count:N0} communities…";
        try
        {
            var found = await _api.GetSubredditsInfoAsync(_subscriptions.Select(s => s.Name).ToList(), default);
            var alive = found.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in _subscriptions.Where(s => !alive.Contains(s.Name)))
                Flag(sub.Name, sub.Subscribers, "Banned or deleted");
            foreach (var sub in found)
            {
                if (sub.Type == "private") Flag(sub.Name, sub.Subscribers, "Private: you can't see its posts");
                else if (sub.Quarantined) Flag(sub.Name, sub.Subscribers, "Quarantined");
            }
            Progress.Text = _candidates.Count == 0
                ? "None of your communities are banned, deleted or private. Check activity to find quiet ones."
                : $"Found {_candidates.Count} so far. Check activity to also find communities that have gone quiet.";
        }
        catch (Exception ex)
        {
            Progress.Text = $"Couldn't look them up: {ex.Message}";
        }
    }

    async void OnCheckActivity(object sender, RoutedEventArgs e)
    {
        var months = int.Parse((string)((ComboBoxItem)QuietMonths.SelectedItem).Tag);
        var cutoff = DateTimeOffset.UtcNow.AddMonths(-months);
        // Smallest first: big communities are almost never dead, so the useful finds come early.
        var queue = new Queue<SubredditRef>(_subscriptions.Where(s => !_flagged.Contains(s.Name)).OrderBy(s => s.Subscribers));
        var total = queue.Count;
        var done = 0;

        _scan = new CancellationTokenSource();
        var ct = _scan.Token;
        CheckButton.IsEnabled = false;
        QuietMonths.IsEnabled = false;
        StopButton.Visibility = ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.Maximum = Math.Max(1, total);
        UpdateUnsubscribeButton();

        async Task Worker()
        {
            while (!ct.IsCancellationRequested && queue.TryDequeue(out var sub))
            {
                var (status, newest) = await CheckWithRetryAsync(sub.Name, ct);
                if (ct.IsCancellationRequested) return;
                switch (status)
                {
                    case 403: Flag(sub.Name, sub.Subscribers, "Private or banned"); break;
                    case 404: Flag(sub.Name, sub.Subscribers, "Banned or deleted"); break;
                    case >= 200 and < 300 when newest == null: Flag(sub.Name, sub.Subscribers, "No posts at all"); break;
                    case >= 200 and < 300 when newest < cutoff:
                        // Restricted communities often went quiet because only approved users can post
                        // (an old lookalike of a bigger community, say); saying so explains the result.
                        Flag(sub.Name, sub.Subscribers, $"Last post {Format.Age(newest.Value)} ago" +
                            (sub.Type == "restricted" ? " · restricted: only approved users can post" : ""));
                        break;
                }
                done++;
                ProgressBar.Value = done;
                Progress.Text = $"Checked {done:N0} of {total:N0} · {_candidates.Count} suggested";
            }
        }

        try
        {
            // A few at a time: quick, without tripping Reddit's rate limit.
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Worker()));
            Progress.Text = ct.IsCancellationRequested
                ? $"Stopped after {done:N0} of {total:N0} · {_candidates.Count} suggested"
                : $"Checked all {total:N0} · {_candidates.Count} suggested";
        }
        finally
        {
            _scan = null;
            CheckButton.IsEnabled = QuietMonths.IsEnabled = true;
            StopButton.Visibility = ProgressBar.Visibility = Visibility.Collapsed;
            UpdateUnsubscribeButton();
        }
    }

    /// <summary>One lookup, waiting out Reddit's rate limit (HTTP 429) a few times.</summary>
    async Task<(int Status, DateTimeOffset? Newest)> CheckWithRetryAsync(string name, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = await _api.GetNewestPostAsync(name, ct);
                if (result.Status != 429 || attempt >= 4) return result;
                Progress.Text = "Reddit asked Readdit to slow down; waiting a moment…";
                await Task.Delay(TimeSpan.FromSeconds(15 * (attempt + 1)), ct);
            }
            catch (OperationCanceledException)
            {
                return (0, null);
            }
            catch
            {
                return (0, null); // network hiccup: skip this one rather than guess
            }
        }
    }

    void OnStop(object sender, RoutedEventArgs e) => _scan?.Cancel();

    void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var c in _candidates) c.IsChecked = true;
    }

    void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var c in _candidates) c.IsChecked = false;
    }

    async void OnUnsubscribe(object sender, RoutedEventArgs e)
    {
        var chosen = _candidates.Where(c => c.IsChecked).ToList();
        if (chosen.Count == 0) return;
        if (MessageBox.Show(this, $"Unsubscribe from {chosen.Count} {(chosen.Count == 1 ? "community" : "communities")}?",
                "Clean up subscriptions", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        UnsubscribeButton.IsEnabled = CheckButton.IsEnabled = false;
        Progress.Text = "Unsubscribing…";
        try
        {
            var failed = (await _api.UnsubscribeManyAsync(chosen.Select(c => c.Name).ToList()))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var left = chosen.Where(c => !failed.Contains(c.Name)).ToList();
            foreach (var c in left) _candidates.Remove(c);
            Unsubscribed.AddRange(left.Select(c => c.Name));
            Progress.Text = failed.Count == 0
                ? $"Unsubscribed from {left.Count}."
                : $"Unsubscribed from {left.Count}. Reddit wouldn't let you leave {failed.Count} (they stay ticked).";
        }
        catch (Exception ex)
        {
            Progress.Text = $"Couldn't unsubscribe: {ex.Message}";
        }
        finally
        {
            CheckButton.IsEnabled = true;
            UpdateUnsubscribeButton();
        }
    }
}

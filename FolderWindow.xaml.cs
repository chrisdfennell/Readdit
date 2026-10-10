using System.Windows;

namespace Readdit;

/// <summary>Creates or edits a sidebar folder: its name and the communities in it.</summary>
public partial class FolderWindow : Window
{
    readonly IReadOnlyCollection<string> _otherNames;

    /// <param name="folder">The folder to edit, or null to make a new one.</param>
    /// <param name="otherNames">Names already taken by other folders.</param>
    public FolderWindow(SubscriptionFolder? folder, IReadOnlyCollection<string> otherNames)
    {
        InitializeComponent();
        _otherNames = otherNames;
        Title = folder == null ? "New folder" : "Edit folder";
        FolderName.Text = folder?.Name ?? "";
        Members.Text = folder == null ? "" : string.Join(Environment.NewLine, folder.Subreddits);
        DeleteButton.Visibility = folder == null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) =>
        {
            FolderName.Focus();
            FolderName.SelectAll();
        };
    }

    public string FolderNameValue => FolderName.Text.Trim();

    /// <summary>Community names, without r/ prefixes or duplicates.</summary>
    public List<string> MemberNames => ParseMembers(Members.Text);

    public bool Deleted { get; private set; }

    public static List<string> ParseMembers(string text) =>
        text.Split(['\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().TrimStart('/'))
            .Select(l => l.StartsWith("r/", StringComparison.OrdinalIgnoreCase) ? l[2..] : l)
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    void OnSave(object sender, RoutedEventArgs e)
    {
        var problem = FolderNameValue.Length == 0 ? "Give the folder a name."
            : _otherNames.Contains(FolderNameValue, StringComparer.OrdinalIgnoreCase) ? "There's already a folder with that name."
            : null;
        if (problem != null)
        {
            Error.Text = problem;
            Error.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    void OnDelete(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Delete the folder “{FolderNameValue}”? The communities stay subscribed.", "Delete folder",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        Deleted = true;
        DialogResult = true;
    }
}

using System.Windows;

namespace Readdit;

/// <summary>Picks an existing folder, or names a new one, to put a community in.</summary>
public partial class FolderPickerWindow : Window
{
    public FolderPickerWindow(string subreddit, IReadOnlyList<string> folders)
    {
        InitializeComponent();
        Prompt.Text = $"Add r/{subreddit} to:";
        Folder.ItemsSource = folders;
        if (folders.Count > 0) Folder.SelectedIndex = 0;
        Loaded += (_, _) => Folder.Focus();
    }

    public string FolderName => Folder.Text.Trim();

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (FolderName.Length > 0) DialogResult = true;
    }
}

using System.Windows;

namespace Readdit;

/// <summary>Your private tag and note on a Reddit user.</summary>
public partial class UserNoteWindow : Window
{
    public UserNoteWindow(string user, UserNotes.UserNote? current)
    {
        InitializeComponent();
        Title = "Note on u/" + user;
        Heading.Text = "u/" + user;
        TagBox.Text = current?.Tag ?? "";
        NoteBox.Text = current?.Note ?? "";
        Loaded += (_, _) => TagBox.Focus();
    }

    public string TagValue => TagBox.Text.Trim();
    public string NoteValue => NoteBox.Text.Trim();

    void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    void OnClear(object sender, RoutedEventArgs e)
    {
        TagBox.Text = NoteBox.Text = "";
        DialogResult = true;
    }
}

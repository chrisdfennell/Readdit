using System.Windows;

namespace Skein;

/// <summary>A one-line text prompt.</summary>
public partial class TextInputWindow : Window
{
    public TextInputWindow(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text;

    void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}

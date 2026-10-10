using System.Windows;

namespace Readdit;

/// <summary>Asked when the window is closed: keep running in the tray, or exit?</summary>
public partial class CloseChoiceWindow : Window
{
    public CloseChoiceWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => TrayButton.Focus();
    }

    /// <summary>"Tray" or "Exit"; null if cancelled.</summary>
    public string? Choice { get; private set; }

    public bool RememberChoice => Remember.IsChecked == true;

    void OnTray(object sender, RoutedEventArgs e)
    {
        Choice = "Tray";
        DialogResult = true;
    }

    void OnExit(object sender, RoutedEventArgs e)
    {
        Choice = "Exit";
        DialogResult = true;
    }
}

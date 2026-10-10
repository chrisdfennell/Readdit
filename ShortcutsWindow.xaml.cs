using System.Windows;

namespace Readdit;

public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        List.ItemsSource = new Dictionary<string, string>
        {
            ["Ctrl+K"] = "Go anywhere: communities, folders, tabs and commands",
            ["j / k"] = "Next / previous post",
            ["Shift+J / K"] = "Next / previous top-level comment",
            ["Shift+N"] = "Next comment that's new since your last visit",
            ["Shift+P"] = "Next comment by the original poster",
            ["v"] = "Picture viewer: ← → step through pictures and on to the next post, Space for a slideshow",
            ["F11"] = "Full screen (Esc to leave)",
            ["Ctrl+F"] = "Find in thread",
            ["d"] = "Download the post's images or video",
            ["Space"] = "Scroll the post (Shift+Space scrolls back)",
            ["a / z"] = "Upvote / downvote",
            ["s"] = "Save or unsave",
            ["x"] = "Hide the post",
            ["o"] = "Open the link inside Readdit",
            ["Shift+O"] = "Open the link in your default browser",
            ["f"] = "Reading mode (Esc to exit)",
            ["+ / -"] = "Bigger / smaller text in the post view",
            ["1 / 2 / 3"] = "Compact / Cards / Grid layout",
            ["Esc"] = "Close the in-app browser or reading mode",
            ["m"] = "Load more posts",
            ["r / F5"] = "Refresh",
            ["/ or Ctrl+L"] = "Search, or go to r/name or u/name",
            ["n"] = "New post",
            ["h"] = "Home",
            ["i"] = "Inbox",
            ["Ctrl+T"] = "New tab",
            ["Ctrl+W"] = "Close tab",
            ["Ctrl+Tab"] = "Next tab (Shift for previous)",
            ["Middle-click"] = "Open a sidebar entry in a new tab",
            ["Click a comment's line"] = "Collapse or expand the thread",
            ["?"] = "This list",
        };
    }
}

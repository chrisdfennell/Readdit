using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Skein;

public sealed partial class MainViewModel
{
    // ---- Offline library tags -------------------------------------------------------------------

    void EditTags(Post? post)
    {
        if (post == null || post.Kind != ItemKind.Link) return;
        if (!_services.Offline.Has(post.Id))
        {
            Status = "Save the post first (s) to add it to your offline library, then tag it.";
            return;
        }
        var current = string.Join(", ", _services.Offline.TagsFor(post.Id));
        var dialog = new TextInputWindow("Library tags", "Tags for this saved post, separated by commas:", current)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true) return;
        _services.Offline.SetTags(post.Id, dialog.Value.Split(','));
        Status = "Tags updated.";
        foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.Offline)) _ = tab.RefreshLibraryAsync();
    }

    // ---- Organizing the offline library ---------------------------------------------------------

    bool _syncingLibrary;

    /// <summary>
    /// Copies every post you've saved on Reddit into the offline library (the ones not there
    /// yet), so all of them can be tagged, searched and exported.
    /// </summary>
    async Task SyncSavedToLibraryAsync()
    {
        if (_syncingLibrary) return;
        if (_services.Api.UserName is not { } me)
        {
            Status = "Skein doesn't know your username yet. Try again in a moment.";
            return;
        }
        _syncingLibrary = true;
        CommandManager.InvalidateRequerySuggested();
        var added = 0;
        var failed = 0;
        try
        {
            string? after = null;
            // Reddit keeps your newest 1,000 saved items.
            for (var page = 0; page < 20; page++)
            {
                var (items, next) = await _services.Api.GetListingAsync($"/user/{me}/saved.json", after, default);
                foreach (var post in items.Where(p => p.Kind == ItemKind.Link && !_services.Offline.Has(p.Id)))
                {
                    Status = $"Adding saved posts to your offline library… {added} so far";
                    try
                    {
                        var raw = await _services.Api.GetThreadRawAsync(post.Id, "confidence", null, default);
                        _services.Offline.Save(post.Id, raw);
                        _services.Offline.StartMediaDownload(post.Id, raw);
                        added++;
                    }
                    catch
                    {
                        failed++; // deleted, or a private community: skip it
                    }
                }
                after = next;
                if (after == null) break;
            }
            Status = added == 0
                ? "Your offline library already has every post you've saved."
                : $"Added {added} saved {(added == 1 ? "post" : "posts")} to your offline library" +
                  (failed > 0 ? $" ({failed} couldn't be fetched)." : ".") + " Pictures and videos download in the background.";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't finish syncing (added {added}): {ex.Message}";
        }
        finally
        {
            _syncingLibrary = false;
            CommandManager.InvalidateRequerySuggested();
            foreach (var tab in Tabs.Where(t => t.Kind == FeedKind.Offline)) _ = tab.RefreshLibraryAsync();
        }
    }

    void ExportLibrary(FeedViewModel tab)
    {
        if (tab.Posts.Count == 0)
        {
            Status = "Nothing to export: no posts are showing.";
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export saved posts",
            FileName = $"Skein saved posts {DateTime.Now:yyyy-MM-dd}",
            Filter = "Markdown (*.md)|*.md|Web page (*.html)|*.html|Spreadsheet (*.csv)|*.csv|JSON (*.json)|*.json",
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        try
        {
            var text = LibraryExport.Render([.. tab.Posts], p => _services.Offline.TagsFor(p.Id),
                System.IO.Path.GetExtension(dialog.FileName));
            System.IO.File.WriteAllText(dialog.FileName, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Status = $"Exported {tab.Posts.Count} posts to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't export: {ex.Message}";
        }
    }

    /// <summary>Adds tags to every post the library is showing (after a search or tag filter).</summary>
    void TagShown(FeedViewModel tab)
    {
        var posts = tab.Posts.Where(p => _services.Offline.Has(p.Id)).ToList();
        if (posts.Count == 0) return;
        var dialog = new TextInputWindow("Tag posts", $"Add tags to the {posts.Count} posts shown, separated by commas:", "")
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true) return;
        var add = dialog.Value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (add.Count == 0) return;
        foreach (var post in posts) _services.Offline.SetTags(post.Id, _services.Offline.TagsFor(post.Id).Concat(add));
        Status = $"Tagged {posts.Count} posts with {string.Join(", ", add)}.";
        _ = tab.RefreshLibraryAsync();
    }
}

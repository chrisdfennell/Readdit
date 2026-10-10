using System.IO;

namespace Skein.Tests;

/// <summary>A throwaway folder per test, deleted afterwards.</summary>
public abstract class TempFolderTest : IDisposable
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "skein-tests-" + Guid.NewGuid().ToString("N"));

    protected TempFolderTest() => Directory.CreateDirectory(Dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch
        {
            // Leftovers in %TEMP% are harmless.
        }
        GC.SuppressFinalize(this);
    }
}

public class ReadTrackerTests : TempFolderTest
{
    [Fact]
    public void Remembers_across_sessions()
    {
        var file = Path.Combine(Dir, "read.txt");
        var first = new ReadTracker(file);
        first.Mark("t3_a");
        first.Mark("t3_b");
        first.Mark("t3_a"); // duplicates aren't written twice

        var second = new ReadTracker(file);
        Assert.True(second.Contains("t3_a"));
        Assert.True(second.Contains("t3_b"));
        Assert.False(second.Contains("t3_c"));
        Assert.Equal(2, File.ReadAllLines(file).Length);
    }
}

public class VisitStoreTests : TempFolderTest
{
    [Fact]
    public void Records_time_and_comment_count()
    {
        var file = Path.Combine(Dir, "visits.json");
        new VisitStore(file).Record("abc", 42);

        var visit = new VisitStore(file).Get("abc");
        Assert.NotNull(visit);
        Assert.Equal(42, visit.CommentCount);
        Assert.InRange(visit.At, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
        Assert.Null(new VisitStore(file).Get("other"));
    }

    [Fact]
    public void Corrupt_file_starts_fresh()
    {
        var file = Path.Combine(Dir, "visits.json");
        File.WriteAllText(file, "{ not json");
        Assert.Null(new VisitStore(file).Get("abc"));
    }
}

public class OfflineStoreTests : TempFolderTest
{
    static string Thread(string id, string title, string comment) => """
        [{"kind":"Listing","data":{"children":[{"kind":"t3","data":{"name":"t3_ID","id":"ID","title":"TITLE",
           "subreddit":"csharp","author":"alice","selftext":"post text","created_utc":1700000000}}]}},
         {"kind":"Listing","data":{"children":[{"kind":"t1","data":{"author":"bob","body":"COMMENT",
           "replies":{"kind":"Listing","data":{"children":[{"kind":"t1","data":{"author":"carol","body":"nested words"}}]}}}}]}}]
        """.Replace("ID", id).Replace("TITLE", title).Replace("COMMENT", comment);

    [Fact]
    public void Saves_lists_and_removes()
    {
        var store = new OfflineStore(Dir);
        store.Save("abc", Thread("abc", "First post", "hello"));
        store.Save("def", Thread("def", "Second post", "world"));

        Assert.True(store.Has("abc"));
        Assert.Equal(2, store.List().Count);
        Assert.NotNull(store.Load("abc"));

        store.Remove("abc");
        Assert.False(store.Has("abc"));
        Assert.Equal("Second post", Assert.Single(store.List()).Title);
    }

    [Fact]
    public void Search_covers_titles_text_and_nested_comments()
    {
        var store = new OfflineStore(Dir);
        store.Save("abc", Thread("abc", "Recipes", "pancakes are great"));
        store.Save("def", Thread("def", "Cars", "engines"));

        Assert.Equal("Recipes", Assert.Single(store.List("pancakes")).Title);
        Assert.Equal(2, store.List("nested").Count);          // a reply in both threads
        Assert.Equal("Cars", Assert.Single(store.List("CARS engines")).Title); // all words, any case
        Assert.Empty(store.List("nothing-matches"));
    }

    [Fact]
    public void Tags_filter_and_are_searchable()
    {
        var store = new OfflineStore(Dir);
        store.Save("abc", Thread("abc", "One", "x"));
        store.Save("def", Thread("def", "Two", "y"));
        store.SetTags("abc", ["cooking", " Cooking ", "later", ""]);

        Assert.Equal(["cooking", "later"], store.TagsFor("abc"));
        Assert.Equal(["cooking", "later"], store.AllTags());
        Assert.Equal("One", Assert.Single(store.List(tag: "COOKING")).Title);
        Assert.Equal("One", Assert.Single(store.List("later")).Title); // tags are part of search
        Assert.Contains("cooking", Assert.Single(store.List(tag: "cooking")).LibraryTags);

        // Tags survive a restart.
        Assert.Equal(["cooking", "later"], new OfflineStore(Dir).TagsFor("abc"));
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("../evil")]
    [InlineData("ABC")]
    [InlineData("a/b")]
    public void Ids_that_could_escape_the_folder_are_refused(string id)
    {
        var store = new OfflineStore(Dir);
        store.Save(id, "[]");
        Assert.False(store.Has(id));
        Assert.Null(store.Load(id));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Dir)!, "evil*"));
    }

    [Fact]
    public void Local_media_maps_to_the_offline_host()
    {
        var media = Path.Combine(Dir, "media", "abc");
        Directory.CreateDirectory(media);
        File.WriteAllText(Path.Combine(media, "map.json"), """{"https://i.redd.it/a.jpg":"0.jpg"}""");

        var map = new OfflineStore(Dir).LocalMedia("abc", "offline.skein");
        Assert.Equal("https://offline.skein/abc/0.jpg", map!["https://i.redd.it/a.jpg"]);
        Assert.Null(new OfflineStore(Dir).LocalMedia("none", "offline.skein"));
    }
}

public class MigrationTests : TempFolderTest
{
    [Fact]
    public void Moves_app_data_and_leaves_installer_files()
    {
        var oldDir = Path.Combine(Dir, "Skein");
        var newDir = Path.Combine(Dir, "SkeinData");
        Directory.CreateDirectory(Path.Combine(oldDir, "WebView2", "Default"));
        File.WriteAllText(Path.Combine(oldDir, "WebView2", "Default", "Cookies"), "c");
        Directory.CreateDirectory(Path.Combine(oldDir, "offline"));
        File.WriteAllText(Path.Combine(oldDir, "read.txt"), "t3_a\n");
        File.WriteAllText(Path.Combine(oldDir, "Update.exe"), "installer");        // Velopack's
        Directory.CreateDirectory(Path.Combine(oldDir, "current"));                 // Velopack's

        Paths.MigrateLocalData(oldDir, newDir, TimeSpan.Zero);

        Assert.True(File.Exists(Path.Combine(newDir, "WebView2", "Default", "Cookies")));
        Assert.True(Directory.Exists(Path.Combine(newDir, "offline")));
        Assert.True(File.Exists(Path.Combine(newDir, "read.txt")));
        Assert.False(Directory.Exists(Path.Combine(oldDir, "WebView2")));
        Assert.True(File.Exists(Path.Combine(oldDir, "Update.exe")));
        Assert.True(Directory.Exists(Path.Combine(oldDir, "current")));
    }

    [Fact]
    public void Moves_everything_from_the_old_named_folders_and_removes_them()
    {
        // The rename from Readdit: %LocalAppData%\ReadditData → SkeinData, keeping newer files.
        var oldDir = Path.Combine(Dir, "ReadditData");
        var newDir = Path.Combine(Dir, "SkeinData");
        Directory.CreateDirectory(Path.Combine(oldDir, "WebView2", "Default"));
        File.WriteAllText(Path.Combine(oldDir, "WebView2", "Default", "Cookies"), "c");
        File.WriteAllText(Path.Combine(oldDir, "subscriptions.json"), "[]");
        File.WriteAllText(Path.Combine(oldDir, "user-notes.json"), "{}");
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, "user-notes.json"), "newer");

        Paths.MigrateFolder(oldDir, newDir, null, TimeSpan.Zero);

        Assert.True(File.Exists(Path.Combine(newDir, "WebView2", "Default", "Cookies")));
        Assert.True(File.Exists(Path.Combine(newDir, "subscriptions.json")));
        Assert.Equal("newer", File.ReadAllText(Path.Combine(newDir, "user-notes.json")));
        // The one file that couldn't move keeps the old folder around (for a later launch).
        Assert.True(File.Exists(Path.Combine(oldDir, "user-notes.json")));

        File.Delete(Path.Combine(oldDir, "user-notes.json"));
        Paths.MigrateFolder(oldDir, newDir, null, TimeSpan.Zero);
        Assert.False(Directory.Exists(oldDir));
    }

    [Fact]
    public void Never_overwrites_data_already_in_the_new_place()
    {
        var oldDir = Path.Combine(Dir, "Skein");
        var newDir = Path.Combine(Dir, "SkeinData");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(oldDir, "read.txt"), "old");
        File.WriteAllText(Path.Combine(newDir, "read.txt"), "new");

        Paths.MigrateLocalData(oldDir, newDir, TimeSpan.Zero);

        Assert.Equal("new", File.ReadAllText(Path.Combine(newDir, "read.txt")));
    }

    [Fact]
    public void One_locked_item_does_not_stop_the_rest()
    {
        var oldDir = Path.Combine(Dir, "Skein");
        var newDir = Path.Combine(Dir, "SkeinData");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "read.txt"), "r");
        File.WriteAllText(Path.Combine(oldDir, "visits.json"), "{}");

        // Hold read.txt open without sharing, like a still-running process would.
        using (File.Open(Path.Combine(oldDir, "read.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            Paths.MigrateLocalData(oldDir, newDir, TimeSpan.Zero);

        Assert.True(File.Exists(Path.Combine(newDir, "visits.json")));   // moved anyway
        Assert.True(File.Exists(Path.Combine(oldDir, "read.txt")));      // left for next launch

        Paths.MigrateLocalData(oldDir, newDir, TimeSpan.Zero);           // next launch
        Assert.True(File.Exists(Path.Combine(newDir, "read.txt")));
    }

    [Fact]
    public void Nothing_to_do_without_an_old_folder()
    {
        var newDir = Path.Combine(Dir, "SkeinData");
        Paths.MigrateLocalData(Path.Combine(Dir, "missing"), newDir, TimeSpan.Zero);
        Assert.False(Directory.Exists(newDir));
    }
}

using System.IO;
using System.Text.Json;

namespace Readdit.Tests;

public class AlertMatcherTests
{
    static AlertRule Rule(string keywords, string subs = "", bool text = false) =>
        new() { Name = "test", Keywords = keywords, Subreddits = subs, SearchText = text };

    [Fact]
    public void Splits_wanted_and_excluded_terms()
    {
        var (wanted, excluded) = AlertMatcher.ParseTerms(" RTX, 4090 ,-broken, \"for parts\", - ");
        Assert.Equal(["RTX", "4090", "for parts"], wanted);
        Assert.Equal(["broken"], excluded);
    }

    [Theory]
    [InlineData("RTX, 4090", "[USA-CA] [H] RTX 3080 [W] PayPal", true)]
    [InlineData("RTX, 4090", "[H] rtx 4090 FE", true)]      // case doesn't matter
    [InlineData("RTX, 4090", "[H] Radeon 7900", false)]
    [InlineData("RTX, -broken", "[H] Broken RTX 3080", false)]  // an excluded word wins
    [InlineData("-broken", "[H] Working GPU", true)]        // only exclusions: everything else matches
    public void Matches_titles(string keywords, string title, bool expected) =>
        Assert.Equal(expected, AlertMatcher.Matches(Rule(keywords), title, ""));

    [Fact]
    public void Post_text_only_counts_when_asked()
    {
        Assert.False(AlertMatcher.Matches(Rule("seiko"), "Watch for sale", "A lovely Seiko diver"));
        Assert.True(AlertMatcher.Matches(Rule("seiko", text: true), "Watch for sale", "A lovely Seiko diver"));
    }

    [Fact]
    public void Rules_need_words_or_communities()
    {
        Assert.False(AlertMatcher.IsUsable(Rule("")));
        Assert.False(AlertMatcher.IsUsable(Rule("-broken")));
        Assert.True(AlertMatcher.IsUsable(Rule("RTX")));
        Assert.True(AlertMatcher.IsUsable(Rule("", "hardwareswap")));
    }

    [Fact]
    public void Watches_communities_or_searches_reddit()
    {
        Assert.Equal("/r/hardwareswap+buildapcsales/new.json?",
            AlertMatcher.ListingPath(Rule("RTX", "r/hardwareswap, /r/buildapcsales")));
        var search = AlertMatcher.ListingPath(Rule("RTX, for parts, -broken"));
        Assert.StartsWith("/search.json?q=", search);
        Assert.Contains(Uri.EscapeDataString("RTX OR \"for parts\""), search);
        Assert.DoesNotContain("broken", search);
        Assert.Contains("sort=new", search);
    }
}

public class AlertStoreTests : TempFolderTest
{
    static Post MakePost(string id, string title) =>
        Post.FromLink(JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            name = "t3_" + id,
            id,
            title,
            subreddit = "hardwareswap",
            author = "seller",
            permalink = $"/r/hardwareswap/comments/{id}/x/",
            created_utc = 1_700_000_000,
        })).RootElement);

    [Fact]
    public void Keeps_matches_newest_first_without_duplicates()
    {
        var file = Path.Combine(Dir, "alerts.json");
        var store = new AlertStore(file);
        Assert.True(store.Add("GPUs", MakePost("a1", "RTX 3080")));
        Assert.True(store.Add("GPUs", MakePost("b2", "RTX 4090")));
        Assert.False(store.Add("Other rule", MakePost("a1", "RTX 3080")));
        store.Save();

        var posts = new AlertStore(file).List();
        Assert.Equal(["RTX 4090", "RTX 3080"], posts.Select(p => p.Title));
        Assert.All(posts, p => Assert.Equal("GPUs", p.AlertName));
        Assert.Contains("🔔 GPUs", posts[0].Tags);
    }

    [Fact]
    public void Clear_empties_it()
    {
        var file = Path.Combine(Dir, "alerts.json");
        var store = new AlertStore(file);
        store.Add("GPUs", MakePost("a1", "RTX"));
        store.Clear();
        Assert.Empty(new AlertStore(file).List());
    }
}

public class PaletteMatcherTests
{
    [Fact]
    public void Ranks_exact_then_prefix_then_word_then_substring_then_letters()
    {
        var exact = PaletteMatcher.Score("pics", "r/pics");
        var prefix = PaletteMatcher.Score("pics", "r/picsofdogs");
        var word = PaletteMatcher.Score("pics", "Layout pics view");
        var inside = PaletteMatcher.Score("pics", "r/earthpics");
        var letters = PaletteMatcher.Score("rpi", "r/raspberry_pi");
        Assert.True(exact > prefix, "exact beats prefix");
        Assert.True(prefix > word, "prefix beats word start");
        Assert.True(word > inside, "word start beats mid-word");
        Assert.True(inside > letters, "substring beats scattered letters");
        Assert.True(letters > 0);
        Assert.Equal(-1, PaletteMatcher.Score("xyz", "r/pics"));
    }

    [Fact]
    public void Filter_keeps_order_with_nothing_typed_and_sorts_by_score_otherwise()
    {
        var items = new[] { "r/aww", "r/pics", "r/earthpics", "Preferences" }
            .Select(l => new PaletteItem("", l, "", () => { })).ToList();
        Assert.Equal(["r/aww", "r/pics"], PaletteMatcher.Filter(items, " ", 2).Select(i => i.Label));
        Assert.Equal(["r/pics", "r/earthpics"], PaletteMatcher.Filter(items, "pics", 10).Select(i => i.Label));
    }
}

public class LinkHandlerTests
{
    [Theory]
    [InlineData("readdit://r/pics", "https://www.reddit.com/r/pics")]
    [InlineData("readdit:r/pics", "https://www.reddit.com/r/pics")]
    [InlineData("readdit://", "https://www.reddit.com/")]
    [InlineData("readdit://https://www.reddit.com/r/aww/comments/abc/x/", "https://www.reddit.com/r/aww/comments/abc/x/")]
    [InlineData("readdit://https:/www.reddit.com/r/aww", "https://www.reddit.com/r/aww")]
    [InlineData("\"https://redd.it/abc\"", "https://redd.it/abc")]
    [InlineData("https://example.com/page", "https://example.com/page")]
    [InlineData("--restarted", null)]
    [InlineData("file:///C:/Windows/notepad.exe", null)]
    [InlineData("javascript:alert(1)", null)]
    public void Normalizes_incoming_links(string arg, string? expected) =>
        Assert.Equal(expected, LinkHandler.NormalizeIncoming(arg));

    [Theory]
    [InlineData("https://www.reddit.com/r/pics", true)]
    [InlineData("https://old.reddit.com/r/pics", true)]
    [InlineData("https://reddit.com/r/pics", true)]
    [InlineData("https://redd.it/abc", true)]
    [InlineData("https://notreddit.com/r/pics", false)]
    [InlineData("https://reddit.com.evil.example/r/pics", false)]
    [InlineData("https://example.com/?u=reddit.com", false)]
    public void Recognizes_reddit_links(string url, bool expected) =>
        Assert.Equal(expected, LinkHandler.IsRedditUrl(url));

    [Fact]
    public void Splits_browser_commands()
    {
        var chrome = LinkHandler.TrySplitCommand(
            "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --single-argument %1", "https://example.com/a b");
        Assert.Equal(("C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", "--single-argument https://example.com/a b"), chrome);

        var bare = LinkHandler.TrySplitCommand("C:\\Browsers\\firefox.exe -osint -url \"%1\"", "https://example.com/\"x");
        Assert.Equal(("C:\\Browsers\\firefox.exe", "-osint -url \"https://example.com/%22x\""), bare);

        var noPlaceholder = LinkHandler.TrySplitCommand("\"C:\\b.exe\"", "https://example.com");
        Assert.Equal(("C:\\b.exe", "\"https://example.com\""), noPlaceholder);

        Assert.Null(LinkHandler.TrySplitCommand("", "https://example.com"));
        Assert.Null(LinkHandler.TrySplitCommand("\"unterminated", "https://example.com"));
    }
}

public class FolderTests
{
    [Fact]
    public void Parses_member_lists_loosely()
    {
        Assert.Equal(["linux", "linux_gaming", "archlinux"],
            FolderWindow.ParseMembers("r/linux\n /r/linux_gaming, archlinux\r\nLINUX\n\n"));
    }

    [Fact]
    public void Only_single_communities_go_in_folders()
    {
        Assert.True(new SidebarItem("r/linux", "r/linux").CanAddToFolder);
        Assert.False(new SidebarItem("🔥 Popular", "r/popular", IsFeed: true).CanAddToFolder);
        Assert.False(new SidebarItem("r/a+b", "r/a+b").CanAddToFolder);
        Assert.False(new SidebarItem("r/linux", "mod:linux").CanAddToFolder);
        Assert.True(new SidebarItem("r/linux", "r/linux", Folder: "Linux").InFolder);
    }
}

public class LibraryExportTests
{
    static Post MakePost(object data) => Post.FromLink(JsonDocument.Parse(JsonSerializer.Serialize(data)).RootElement);

    static readonly Post[] Posts =
    [
        MakePost(new
        {
            name = "t3_a", id = "a", title = "=HYPERLINK(\"evil\") [brackets]", subreddit = "csharp", author = "alice",
            permalink = "/r/csharp/comments/a/x/", url = "https://example.com/page", is_self = false, domain = "example.com",
            created_utc = 1_700_000_000,
        }),
        MakePost(new
        {
            name = "t3_b", id = "b", title = "<script>alert(1)</script>", subreddit = "pics", author = "bob",
            permalink = "/r/pics/comments/b/y/", is_self = true, selftext = "body text", created_utc = 1_700_000_000,
        }),
    ];

    static IReadOnlyList<string> Tags(Post p) => p.Id == "a" ? ["to read", "dotnet"] : [];

    [Fact]
    public void Csv_quotes_cells_and_defuses_formulas()
    {
        var csv = LibraryExport.Render(Posts, Tags, ".csv");
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("title,subreddit,author,created,url,permalink,tags", lines[0]);
        Assert.StartsWith("\"'=HYPERLINK(\"\"evil\"\") [brackets]\",\"csharp\"", lines[1]);
        Assert.Contains("\"to read; dotnet\"", lines[1]);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Markdown_links_each_post_with_tags()
    {
        var md = LibraryExport.Render(Posts, Tags, ".md");
        Assert.StartsWith("# Saved posts (2)", md);
        Assert.Contains("- [=HYPERLINK(\"evil\") \\[brackets\\]](https://www.reddit.com/r/csharp/comments/a/x/)", md);
        Assert.Contains("[link](https://example.com/page)", md);
        Assert.Contains("#to-read #dotnet", md);
    }

    [Fact]
    public void Html_escapes_titles()
    {
        var html = LibraryExport.Render(Posts, Tags, ".html");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("class=\"tag\">to read<", html);
    }

    [Fact]
    public void Json_round_trips()
    {
        using var doc = JsonDocument.Parse(LibraryExport.Render(Posts, Tags, ".json"));
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("https://example.com/page", doc.RootElement[0].GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement[1].GetProperty("url").ValueKind); // self post
        Assert.Equal("body text", doc.RootElement[1].GetProperty("text").GetString());
        Assert.Equal(["to read", "dotnet"], doc.RootElement[0].GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }
}

public class PicturePostTests
{
    static Post Link(string json) => Post.FromLink(JsonDocument.Parse(json).RootElement);

    [Theory]
    [InlineData("""{"name":"t3_a","post_hint":"image","url":"https://i.redd.it/a.jpg"}""", true)]
    [InlineData("""{"name":"t3_a","url":"https://i.imgur.com/a.PNG?x=1"}""", true)]
    [InlineData("""{"name":"t3_a","is_gallery":true,"media_metadata":{"x":{}},"url":"https://www.reddit.com/gallery/a"}""", true)]
    [InlineData("""{"name":"t3_a","is_video":true,"post_hint":"hosted:video","url":"https://v.redd.it/a"}""", false)]
    [InlineData("""{"name":"t3_a","is_self":true,"url":"https://www.reddit.com/r/x/comments/a/"}""", false)]
    [InlineData("""{"name":"t3_a","post_hint":"link","url":"https://example.com/article"}""", false)]
    [InlineData("""{"name":"t3_a","url":"https://www.reddit.com/r/x/comments/a/","crosspost_parent_list":[{"post_hint":"image","url":"https://i.redd.it/b.jpg"}]}""", true)]
    public void Knows_which_posts_the_viewer_can_show(string json, bool expected) =>
        Assert.Equal(expected, Link(json).HasPictures);
}

public class PostingTests : TempFolderTest
{
    [Theory]
    [InlineData("photo.JPG", "image/jpeg")]
    [InlineData("anim.gif", "image/gif")]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("clip.MOV", "video/quicktime")]
    [InlineData("doc.pdf", null)]
    [InlineData("noextension", null)]
    public void Knows_what_reddit_accepts(string file, string? type)
    {
        Assert.Equal(type, RedditApi.MediaTypeFor(file));
        Assert.Equal(type?.StartsWith("video/") == true, RedditApi.IsVideoFile(file));
    }

    [Fact]
    public void Drafts_survive_and_drop_missing_files()
    {
        var store = new PostDraftStore(Path.Combine(Dir, "draft.json"));
        var picture = Path.Combine(Dir, "a.png");
        File.WriteAllBytes(picture, [1, 2, 3]);
        store.Save(new PostDraft("pics", "My cat", "media", "", true, false,
            [new(picture, "first"), new(Path.Combine(Dir, "gone.png"), "")]));

        var draft = new PostDraftStore(Path.Combine(Dir, "draft.json")).Load();
        Assert.NotNull(draft);
        Assert.Equal(("pics", "My cat", "media", true), (draft.Subreddit, draft.Title, draft.Kind, draft.Nsfw));
        Assert.Equal(new PostDraft.MediaFile(picture, "first"), Assert.Single(draft.Media));
    }

    [Fact]
    public void Empty_drafts_are_not_kept()
    {
        var file = Path.Combine(Dir, "draft.json");
        var store = new PostDraftStore(file);
        store.Save(new PostDraft("pics", "Title", "text", "", false, false, []));
        Assert.True(File.Exists(file));
        store.Save(new PostDraft("pics", "  ", "text", "", false, false, [])); // only the community left
        Assert.False(File.Exists(file));
        Assert.Null(store.Load());
    }
}

public class SidebarStoreTests : TempFolderTest
{
    [Fact]
    public void Subscription_cache_is_per_account()
    {
        var cache = new SubscriptionCache(Dir);
        cache.Save("", [new SubredditRef("pics", "https://icon", 100, IsFavorite: true, Type: "public")]);
        cache.Save("acct-1", [new SubredditRef("aww", null)]);

        var main = new SubscriptionCache(Dir).Load("");
        Assert.Equal("pics", Assert.Single(main).Name);
        Assert.True(main[0].IsFavorite);
        Assert.Equal("aww", Assert.Single(new SubscriptionCache(Dir).Load("acct-1")).Name);
        Assert.Empty(new SubscriptionCache(Dir).Load("acct-unknown"));
        // Profile names can't escape the folder.
        cache.Save("..\\..\\evil", [new SubredditRef("x", null)]);
        Assert.All(Directory.GetFiles(Dir), f => Assert.Equal(Dir, Path.GetDirectoryName(f)));
    }

    [Fact]
    public void Counts_community_visits_case_insensitively()
    {
        var file = Path.Combine(Dir, "visits.json");
        var visits = new CommunityVisits(file);
        visits.Record("Linux");
        visits.Record("linux");
        visits.Record("aww");
        var reloaded = new CommunityVisits(file);
        Assert.Equal(2, reloaded.CountFor("LINUX"));
        Assert.Equal(1, reloaded.CountFor("aww"));
        Assert.Equal(0, reloaded.CountFor("pics"));
    }

    [Fact]
    public void User_notes_save_trim_and_clear()
    {
        var file = Path.Combine(Dir, "notes.json");
        var notes = new UserNotes(file);
        notes.Set("Alice", "  helpful  ", " Knows WPF ");
        notes.Set("bob", "", "");
        var reloaded = new UserNotes(file);
        Assert.Equal(new UserNotes.UserNote("helpful", "Knows WPF"), reloaded.Get("alice"));
        Assert.Null(reloaded.Get("bob"));

        reloaded.Set("ALICE", "", "");
        Assert.Null(new UserNotes(file).Get("alice"));
    }
}

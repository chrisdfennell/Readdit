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

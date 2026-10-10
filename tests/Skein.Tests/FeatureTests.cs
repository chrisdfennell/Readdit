using System.IO;
using System.Text.Json;

namespace Skein.Tests;

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
    [InlineData("skein://r/pics", "https://www.reddit.com/r/pics")]
    [InlineData("skein:r/pics", "https://www.reddit.com/r/pics")]
    [InlineData("skein://", "https://www.reddit.com/")]
    [InlineData("skein://https://www.reddit.com/r/aww/comments/abc/x/", "https://www.reddit.com/r/aww/comments/abc/x/")]
    [InlineData("skein://https:/www.reddit.com/r/aww", "https://www.reddit.com/r/aww")]
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

public class SyncTests : TempFolderTest
{
    /// <summary>A PC: its own settings and notes, sharing the sync folder.</summary>
    (AppSettings Settings, UserNotes Notes, SettingsSync Sync) Pc(string name)
    {
        var settings = new AppSettings { SyncEnabled = true, SyncFolder = Path.Combine(Dir, "OneDrive") };
        var notes = new UserNotes(Path.Combine(Dir, name + "-notes.json"));
        return (settings, notes, new SettingsSync(settings, notes, save: () => { }));
    }

    [Fact]
    public void Changes_on_one_pc_arrive_on_the_other()
    {
        var a = Pc("a");
        var b = Pc("b");
        a.Settings.SubscriptionFolders.Add(new SubscriptionFolder { Name = "Tech", Subreddits = ["linux", "dotnet"] });
        a.Settings.AlertRules.Add(new AlertRule { Name = "GPUs", Keywords = "RTX", LastChecked = 123 });
        a.Settings.MutedUsers.Add("spammer");
        a.Notes.Set("alice", "friend", "met at a conference");
        a.Settings.Theme = "Dark";

        Assert.Empty(a.Sync.RunOnce()!); // writes everything out
        Assert.True(File.Exists(Path.Combine(Dir, "OneDrive", SettingsSync.FileName)));

        b.Sync.Reset(preferFile: true);
        var applied = b.Sync.RunOnce()!;
        Assert.Contains("folders", applied);
        Assert.Equal(["linux", "dotnet"], Assert.Single(b.Settings.SubscriptionFolders).Subreddits);
        var rule = Assert.Single(b.Settings.AlertRules);
        Assert.Equal("RTX", rule.Keywords);
        Assert.Equal(0, rule.LastChecked); // "checked up to" belongs to each PC
        Assert.Equal(["spammer"], b.Settings.MutedUsers);
        Assert.Equal("friend", b.Notes.Get("alice")?.Tag);
        Assert.Equal("Dark", b.Settings.Theme);

        // Nothing changed: a second round does nothing either way.
        Assert.Empty(b.Sync.RunOnce()!);
        Assert.Empty(a.Sync.RunOnce()!);
    }

    [Fact]
    public void Edits_go_back_and_forth_and_only_changed_sections_move()
    {
        var a = Pc("a");
        var b = Pc("b");
        a.Sync.RunOnce();
        b.Sync.Reset(preferFile: true);
        b.Sync.RunOnce();

        b.Settings.KeywordFilters.Add("spoilers");
        Thread.Sleep(5); // distinct timestamps
        b.Sync.RunOnce();
        Assert.Equal(["filters"], a.Sync.RunOnce());
        Assert.Equal(["spoilers"], a.Settings.KeywordFilters);

        // Alerts running (their "checked up to" moving on) isn't a change to sync.
        a.Settings.AlertRules.Add(new AlertRule { Name = "x", Keywords = "y" });
        a.Sync.RunOnce();
        b.Sync.RunOnce();
        a.Settings.AlertRules[0].LastChecked = 999;
        Assert.Empty(a.Sync.RunOnce()!);
    }

    [Fact]
    public void Starting_with_this_pcs_settings_overwrites_the_file()
    {
        var a = Pc("a");
        a.Settings.Theme = "Light";
        a.Sync.RunOnce();

        var b = Pc("b");
        b.Settings.Theme = "Black";
        b.Sync.Reset(preferFile: false);
        b.Sync.RunOnce();
        Assert.Equal("Black", b.Settings.Theme);
        a.Sync.RunOnce();
        Assert.Equal("Black", a.Settings.Theme);
    }

    [Fact]
    public void Plan_prefers_edits_made_here()
    {
        var local = new Dictionary<string, string> { ["folders"] = "[1]", ["notes"] = "{}" };
        var hashes = new Dictionary<string, string> { ["folders"] = SyncEngine.Hash("[0]"), ["notes"] = SyncEngine.Hash("{}") };
        var stamps = new Dictionary<string, long> { ["folders"] = 10, ["notes"] = 10 };
        var remote = new SyncDocument
        {
            Sections = { ["folders"] = new SyncEntry { Updated = 20 }, ["notes"] = new SyncEntry { Updated = 20 } },
        };
        var plan = SyncEngine.Plan(local, hashes, stamps, remote);
        Assert.Equal(["folders"], plan.Push);  // changed here, and newer there: this PC's edit wins
        Assert.Equal(["notes"], plan.Apply);   // unchanged here, newer there
    }
}

public class ProblemReportTests
{
    [Fact]
    public void Removes_names_paths_and_secrets()
    {
        var text = "user=Fennellc path C:\\Users\\chris.f\\AppData\\x \"modhash\": \"abc123\" Bearer xyz.789 fennellcool stays";
        var clean = ProblemReport.Sanitize(text, "fennellc", "C:\\Users\\chris.f");
        Assert.Equal("user=<you> path %USERPROFILE%\\AppData\\x \"modhash\": \"<removed>\" Bearer <removed> fennellcool stays", clean);
    }

    [Fact]
    public void Api_log_keeps_requests_but_not_what_reddit_sent()
    {
        var log = "[2026-10-10 10:00:00] POST /api/comment cookie route -> HTTP 200: {\"json\":{\"data\":{\"body\":\"secret\"}}}\n" +
                  "[2026-10-10 10:00:01] identity: user=fennellc, modhash present";
        var clean = ProblemReport.SanitizeApiLog(log, "fennellc", "C:\\Users\\x");
        Assert.Equal("[2026-10-10 10:00:00] POST /api/comment cookie route -> HTTP 200\n[2026-10-10 10:00:01] identity: user=<you>, modhash present", clean);
    }

    [Fact]
    public void Issue_link_goes_to_the_repository()
    {
        var url = ProblemReport.IssueUrl("https://github.com/chrisdfennell/Skein/", "It crashed\nwhen I clicked", "Skein 0.5.0");
        Assert.StartsWith("https://github.com/chrisdfennell/Skein/issues/new?title=It%20crashed%20when%20I%20clicked&body=", url);
        Assert.Null(ProblemReport.IssueUrl("https://evil.example/x", "a", "b"));
    }
}

public class SearchQueryTests
{
    [Fact]
    public void Builds_reddit_search_operators()
    {
        Assert.Equal("rtx 4090 author:seller flair:\"For Sale\" site:ebay.com self:no",
            new SearchQuery("rtx 4090", Author: "u/seller", Flair: "For Sale", Site: "ebay.com", Type: "links").Build());
        Assert.Equal("title:\"best laptop\" self:yes", new SearchQuery("best laptop", Type: "text", TitleOnly: true).Build());
        Assert.Equal("cats", new SearchQuery("cats").Build());
    }

    [Fact]
    public void Parses_what_it_builds()
    {
        var original = new SearchQuery("rtx 4090", Author: "seller", Flair: "For Sale", Site: "ebay.com", Type: "links");
        Assert.Equal(original, SearchQuery.Parse(original.Build()));
        var titled = new SearchQuery("best laptop", Type: "text", TitleOnly: true);
        Assert.Equal(titled, SearchQuery.Parse(titled.Build()));
        Assert.Equal(new SearchQuery("plain words"), SearchQuery.Parse("  plain   words "));
        Assert.Equal("any", SearchQuery.Parse("x self:maybe").Type);
        Assert.Equal("text", SearchQuery.Parse("x SELF:TRUE").Type);
        Assert.False(SearchQuery.Parse("just words").IsRefined);
        Assert.True(SearchQuery.Parse("words author:bob").IsRefined);
    }
}

public class MarkdownEditTests
{
    [Fact]
    public void Wraps_unwraps_and_inserts_examples()
    {
        var bold = MarkdownEdits.Wrap("hello world", 0, 5, "**", "**", "bold");
        Assert.Equal(new MarkdownEdits.Result("**hello** world", 2, 5), bold);
        Assert.Equal(new MarkdownEdits.Result("hello world", 0, 5),
            MarkdownEdits.Wrap(bold.Text, bold.SelectionStart, bold.SelectionLength, "**", "**", "bold"));
        Assert.Equal(new MarkdownEdits.Result("hi >!spoiler!<", 5, 7), MarkdownEdits.Wrap("hi ", 3, 0, ">!", "!<", "spoiler"));
        Assert.Equal("```\na\nb\n```", MarkdownEdits.Wrap("a\nb", 0, 3, "`", "`", "code").Text);
    }

    [Fact]
    public void Prefixes_every_selected_line()
    {
        Assert.Equal("> one\n> two\nthree", MarkdownEdits.PrefixLines("one\ntwo\nthree", 1, 5, "> ").Text);
        Assert.Equal("1. one\n2. two", MarkdownEdits.PrefixLines("one\ntwo", 0, 7, "1. ").Text);
        Assert.Equal("one\ntwo", MarkdownEdits.PrefixLines("- one\n- two", 0, 11, "- ").Text); // toggles off
        Assert.Equal("## title", MarkdownEdits.PrefixLines("- title", 3, 0, "## ").Text); // replaces another prefix
    }
}

public class DuplicateTests
{
    static Post Link(string id, string url, string sub = "pics", string crosspostParent = "") =>
        Post.FromLink(JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            name = "t3_" + id,
            id,
            url,
            subreddit = sub,
            is_self = false,
            crosspost_parent = crosspostParent,
            title = "t",
        })).RootElement);

    [Fact]
    public void The_same_link_matches_without_tracking_or_www()
    {
        var a = Link("a", "https://www.example.com/story/?utm_source=x&id=5");
        var b = Link("b", "https://example.com/story?id=5&ref=home");
        Assert.Equal("example.com/story?id=5", a.LinkKey);
        Assert.Equal(a.LinkKey, b.LinkKey);
        Assert.NotEqual(a.LinkKey, Link("c", "https://example.com/other").LinkKey);
    }

    [Fact]
    public void Crossposts_match_their_original_and_reddit_links_never_match()
    {
        var original = Link("orig", "https://i.redd.it/x.jpg");
        var crosspost = Link("xp", "https://www.reddit.com/r/pics/comments/orig/", crosspostParent: "t3_orig");
        Assert.Contains("t3_orig", crosspost.DuplicateKeys);
        Assert.Contains("t3_orig", original.DuplicateKeys);
        Assert.Null(Link("v", "https://v.redd.it/abc").LinkKey);
        Assert.Null(Link("r", "https://www.reddit.com/r/x/comments/1/").LinkKey);
    }

    [Fact]
    public void Duplicates_are_tagged_with_the_other_communities()
    {
        var post = Link("a", "https://example.com/x");
        post.AlsoIn = "r/news, r/worldnews";
        Assert.Contains("🔁 also in r/news, r/worldnews", post.Tags);
    }
}

public class BackupTests : TempFolderTest
{
    [Fact]
    public void Backs_up_and_restores_settings_and_data_but_not_the_sign_in()
    {
        var roaming = Path.Combine(Dir, "roaming");
        var local = Path.Combine(Dir, "local");
        Directory.CreateDirectory(Path.Combine(local, "WebView2"));
        Directory.CreateDirectory(Path.Combine(local, "offline", "media", "abc"));
        Directory.CreateDirectory(roaming);
        File.WriteAllText(Path.Combine(roaming, "settings.json"), "{\"Theme\":\"Dark\"}");
        File.WriteAllText(Path.Combine(local, "user-notes.json"), "{}");
        File.WriteAllText(Path.Combine(local, "subscriptions.json"), "[]");
        File.WriteAllText(Path.Combine(local, "WebView2", "Cookies"), "secret");
        File.WriteAllText(Path.Combine(local, "offline", "abc.json"), "[]");
        File.WriteAllText(Path.Combine(local, "offline", "media", "abc", "0.jpg"), "jpg");

        var zip = Path.Combine(Dir, "backup.zip");
        Backup.Create(zip, roaming, local, includeLibrary: true, "0.5.3");
        var manifest = Backup.ReadManifest(zip);
        Assert.NotNull(manifest);
        Assert.True(manifest.IncludesLibrary);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
            Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains("WebView2") || e.FullName.Contains("Cookies"));

        // Restore onto a "new PC".
        var roaming2 = Path.Combine(Dir, "roaming2");
        var local2 = Path.Combine(Dir, "local2");
        Backup.Stage(zip, local2);
        Assert.True(Backup.ApplyPending(roaming2, local2));
        Assert.Equal("{\"Theme\":\"Dark\"}", File.ReadAllText(Path.Combine(roaming2, "settings.json")));
        Assert.True(File.Exists(Path.Combine(local2, "subscriptions.json")));
        Assert.True(File.Exists(Path.Combine(local2, "offline", "media", "abc", "0.jpg")));
        Assert.False(Directory.Exists(Path.Combine(local2, "restore-pending")));
        Assert.False(Backup.ApplyPending(roaming2, local2)); // nothing left to apply
    }

    [Fact]
    public void Ignores_files_that_try_to_escape_the_restore_folder()
    {
        var zip = Path.Combine(Dir, "evil.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "skein-backup.json", "local/../../escaped.txt", "other/x.txt", "local/ok.txt" })
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open()))
                    writer.Write(name == "skein-backup.json" ? "{\"Version\":1}" : "x");
        }
        var local = Path.Combine(Dir, "local");
        Backup.Stage(zip, local);
        Backup.ApplyPending(Path.Combine(Dir, "roaming"), local);
        Assert.True(File.Exists(Path.Combine(local, "ok.txt")));
        Assert.False(File.Exists(Path.Combine(Dir, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(local, "x.txt")));
    }

    [Fact]
    public void Not_a_backup_has_no_manifest()
    {
        var zip = Path.Combine(Dir, "random.zip");
        using (System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create)) { }
        Assert.Null(Backup.ReadManifest(zip));
        Assert.Null(Backup.ReadManifest(Path.Combine(Dir, "missing.zip")));
    }
}

public class PostingFlairTests
{
    [Fact]
    public void Flair_goes_in_the_form_only_when_chosen()
    {
        var form = new Dictionary<string, string>();
        FlairChoice.None.AddTo(form);
        Assert.Empty(form);
        new FlairChoice("abc-123", "").AddTo(form);
        Assert.Equal("abc-123", form["flair_id"]);
        Assert.False(form.ContainsKey("flair_text"));
        new FlairChoice("abc-123", "Custom").AddTo(form);
        Assert.Equal("Custom", form["flair_text"]);
    }
}

public class WatchSyncTests : TempFolderTest
{
    [Fact]
    public void Watched_threads_and_followed_people_sync_keeping_each_pcs_counts()
    {
        var folder = Path.Combine(Dir, "OneDrive");
        var a = new AppSettings { SyncEnabled = true, SyncFolder = folder };
        var b = new AppSettings { SyncEnabled = true, SyncFolder = folder };
        a.WatchedThreads.Add(new WatchedThread { PostId = "abc", Title = "A thread", Subreddit = "aww", SeenComments = 40 });
        a.FollowedUsers.Add(new FollowedUser { Name = "alice", LastChecked = 1000 });
        new SettingsSync(a, new UserNotes(Path.Combine(Dir, "a.json")), save: () => { }).RunOnce();

        var syncB = new SettingsSync(b, new UserNotes(Path.Combine(Dir, "b.json")), save: () => { });
        syncB.Reset(preferFile: true);
        syncB.RunOnce();
        var thread = Assert.Single(b.WatchedThreads);
        Assert.Equal(("abc", "A thread"), (thread.PostId, thread.Title));
        Assert.Equal(-1, thread.SeenComments); // counted fresh on this PC, without announcing old comments
        var user = Assert.Single(b.FollowedUsers);
        Assert.Equal(("alice", 0L), (user.Name, user.LastChecked));
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

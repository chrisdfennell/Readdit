namespace Readdit.Tests;

/// <summary>Saved tab identities ("specs") and how links/typed text open.</summary>
public class FeedSpecTests
{
    [Theory]
    [InlineData("home", FeedKind.Home, "", null)]
    [InlineData("saved", FeedKind.Saved, "", null)]
    [InlineData("inbox", FeedKind.Inbox, "", null)]
    [InlineData("offline", FeedKind.Offline, "", null)]
    [InlineData("r/csharp", FeedKind.Subreddit, "csharp", null)]
    [InlineData("R/CSharp", FeedKind.Subreddit, "CSharp", null)]
    [InlineData("u/alice", FeedKind.User, "alice", null)]
    [InlineData("mod:mod", FeedKind.ModQueue, "mod", null)]
    [InlineData("mod:dotnet", FeedKind.ModQueue, "dotnet", null)]
    [InlineData("search::cats", FeedKind.Search, "cats", null)]
    [InlineData("search:aww:cute cats", FeedKind.Search, "cute cats", "aww")]
    [InlineData("search::a:b", FeedKind.Search, "a:b", null)]
    public void Parses(string spec, FeedKind kind, string target, string? scope)
    {
        var parsed = FeedSpec.Parse(spec);
        Assert.NotNull(parsed);
        Assert.Equal(new FeedSpec(kind, target, scope), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("r/")]
    [InlineData("u/")]
    [InlineData("mod:")]
    [InlineData("search:")]
    [InlineData("search:aww")]
    [InlineData("search:aww:")]
    public void Rejects_invalid_specs(string spec) => Assert.Null(FeedSpec.Parse(spec));

    [Theory]
    [InlineData("home")]
    [InlineData("r/csharp")]
    [InlineData("u/alice")]
    [InlineData("search::cats")]
    [InlineData("search:aww:cute cats")]
    [InlineData("saved")]
    [InlineData("inbox")]
    [InlineData("offline")]
    [InlineData("mod:mod")]
    public void Round_trips(string spec) => Assert.Equal(spec, FeedSpec.Parse(spec)!.Value.ToString());

    [Theory]
    [InlineData("all", true)]
    [InlineData("All", true)]
    [InlineData("POPULAR", true)]
    [InlineData("all-politics", true)]
    [InlineData("csharp+dotnet", true)]
    [InlineData("csharp", false)]
    [InlineData("allergies", false)]
    public void Aggregate_feeds(string name, bool aggregate) => Assert.Equal(aggregate, FeedViewModel.IsAggregateName(name));
}

public class RedditLinkTests
{
    [Theory]
    [InlineData("https://www.reddit.com/r/csharp/comments/abc123/some_title/", "abc123", null)]
    [InlineData("https://old.reddit.com/r/csharp/comments/abc123/some_title/def456/", "abc123", "def456")]
    [InlineData("https://reddit.com/comments/abc123", "abc123", null)]
    [InlineData("https://redd.it/abc123", "abc123", null)]
    public void Threads(string url, string postId, string? commentId) =>
        Assert.Equal(new ThreadLink(postId, commentId), RedditLinks.Classify(url));

    [Theory]
    [InlineData("https://www.reddit.com/r/csharp/", "r/csharp")]
    [InlineData("https://www.reddit.com/r/csharp/top/", "r/csharp")]
    [InlineData("https://www.reddit.com/user/alice", "u/alice")]
    [InlineData("https://www.reddit.com/user/alice/submitted/", "u/alice")]
    [InlineData("https://www.reddit.com/u/alice", "u/alice")]
    public void Communities_and_profiles(string url, string spec) =>
        Assert.Equal(new FeedLink(spec), RedditLinks.Classify(url));

    [Theory]
    [InlineData("https://example.com/r/csharp")]          // not Reddit
    [InlineData("https://www.reddit.com/r/csharp/wiki/index")]
    [InlineData("https://www.reddit.com/settings")]
    [InlineData("https://notreddit.com/r/x")]
    public void Everything_else_is_a_web_page(string url) => Assert.Equal(new WebLink(url), RedditLinks.Classify(url));

    [Fact]
    public void Relative_or_garbage_urls_are_ignored() => Assert.Null(RedditLinks.Classify("/r/csharp"));

    [Theory]
    [InlineData("r/csharp", "r/csharp")]
    [InlineData("/r/csharp/", "r/csharp")]
    [InlineData("R/CSharp", "r/CSharp")]
    [InlineData("u/alice", "u/alice")]
    [InlineData("user/alice", "u/alice")]
    [InlineData("r/csharp+dotnet", "r/csharp+dotnet")]
    public void Search_box_shortcuts(string input, string spec) => Assert.Equal(spec, RedditLinks.ParseFeedInput(input));

    [Theory]
    [InlineData("csharp")]
    [InlineData("r/csharp tips")]
    [InlineData("how do i r/csharp")]
    public void Other_text_is_searched(string input) => Assert.Null(RedditLinks.ParseFeedInput(input));
}

public class UpdateVersionTests
{
    [Theory]
    [InlineData("0.4.1", "0.4.0", true)]
    [InlineData("v0.5.0", "0.4.9", true)]
    [InlineData("1.0.0", "0.9.9+abc123", true)]
    [InlineData("0.4.0", "0.4.0", false)]
    [InlineData("0.4.0", "0.4.0+abc123", false)]      // build metadata doesn't count
    [InlineData("0.3.9", "0.4.0", false)]
    [InlineData("1.0.0", "1.0.0-beta.1", true)]        // final release beats its prerelease
    [InlineData("1.0.0-beta.2", "1.0.0", false)]       // a prerelease never replaces the final
    [InlineData("1.0.0", "1.0.0.0", false)]            // assembly versions have four parts
    public void Compares_like_semver(string latest, string current, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(latest, current));

    [Fact]
    public void Parses_versions()
    {
        Assert.Equal((new Version(1, 2, 3), "beta.1"), UpdateService.ParseVersion("v1.2.3-beta.1+sha"));
        Assert.Equal((new Version(0, 0, 0), ""), UpdateService.ParseVersion("garbage"));
    }
}

namespace Skein.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1k")]
    [InlineData(1234, "1.2k")]
    [InlineData(12345, "12k")]
    [InlineData(1_500_000, "1.5m")]
    [InlineData(-2500, "-2.5k")]
    public void Counts_are_abbreviated(long n, string expected) => Assert.Equal(expected, Format.Count(n));

    [Fact]
    public void Ages_use_the_largest_sensible_unit()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("just now", Format.Age(now.AddSeconds(-20)));
        Assert.Equal("5m", Format.Age(now.AddMinutes(-5)));
        Assert.Equal("3h", Format.Age(now.AddHours(-3)));
        Assert.Equal("2d", Format.Age(now.AddDays(-2)));
        Assert.Equal("2mo", Format.Age(now.AddDays(-65)));
        Assert.Equal("3y", Format.Age(now.AddDays(-365 * 3 - 10)));
    }
}

/// <summary>Reddit's JSON is loose (null, "", false where you expect objects); the helpers must cope.</summary>
public class JsonExtensionTests
{
    readonly System.Text.Json.JsonElement _data = TestJson.Parse("""
        {"s":"text","n":12.7,"t":true,"f":false,"nul":null,"obj":{"x":1},"emptystr":"","arr":[1]}
        """);

    [Fact]
    public void Strings() { Assert.Equal("text", _data.Str("s")); Assert.Equal("", _data.Str("n")); Assert.Equal("", _data.Str("missing")); }

    [Fact]
    public void Numbers() { Assert.Equal(12, _data.Long("n")); Assert.Equal(0, _data.Long("s")); Assert.Equal(12.7, _data.Double("n")); }

    [Fact]
    public void Booleans()
    {
        Assert.True(_data.Bool("t"));
        Assert.False(_data.Bool("f"));
        Assert.False(_data.Bool("s"));
        Assert.True(_data.NullableBool("t"));
        Assert.False(_data.NullableBool("f"));
        Assert.Null(_data.NullableBool("nul"));
        Assert.Null(_data.NullableBool("missing"));
    }

    [Fact]
    public void Child_objects_only()
    {
        Assert.NotNull(_data.Child("obj"));
        Assert.Null(_data.Child("nul"));
        Assert.Null(_data.Child("emptystr"));
        Assert.Null(_data.Child("arr"));
        Assert.Null(_data.Child("missing"));
    }

    [Fact]
    public void Helpers_tolerate_non_objects()
    {
        var array = TestJson.Parse("[1,2]");
        Assert.Equal("", array.Str("x"));
        Assert.Equal(0, array.Long("x"));
        Assert.False(array.Bool("x"));
        Assert.Null(array.Child("x"));
    }
}

public class SidebarItemTests
{
    [Fact]
    public void Communities_get_a_badge_feeds_do_not()
    {
        Assert.True(new SidebarItem("r/csharp", "r/csharp").IsCommunity);
        Assert.True(new SidebarItem("r/csharp", "mod:csharp").IsCommunity);
        Assert.False(new SidebarItem("🛡 Mod queue", "mod:mod").IsCommunity);
        Assert.False(new SidebarItem("🔥 Popular", "r/popular", IsFeed: true).IsCommunity);
        Assert.False(new SidebarItem("🏠 Home", "home").IsCommunity);
    }

    [Theory]
    [InlineData("r/csharp", "C")]
    [InlineData("mod:dotnet", "D")]
    [InlineData("u/alice", "A")]
    public void Letter_badge_uses_the_name(string spec, string initial) =>
        Assert.Equal(initial, new SidebarItem("label", spec).Initial);
}

public class ModActionTests
{
    [Theory]
    [InlineData("approve")]
    [InlineData("remove")]
    [InlineData("spam")]
    [InlineData("lock")]
    [InlineData("sticky_comment")]
    [InlineData("ignore_reports")]
    public void Known_actions_are_allowed(string action) => Assert.True(RedditApi.IsModAction(action));

    [Theory]
    [InlineData("")]
    [InlineData("delete")]
    [InlineData("/api/remove")]
    [InlineData("APPROVE")]
    public void Anything_else_is_refused(string action) => Assert.False(RedditApi.IsModAction(action));
}

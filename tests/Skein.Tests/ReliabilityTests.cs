namespace Skein.Tests;

public class RateLimiterTests
{
    DateTime _now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    RateLimiter Limiter() => new(() => _now);

    [Fact]
    public void Plenty_left_means_no_waiting()
    {
        var limiter = Limiter();
        limiter.Observe("95", "300", "", 200);
        Assert.Equal(TimeSpan.Zero, limiter.Delay());
    }

    [Fact]
    public void Nearly_used_up_waits_for_the_reset()
    {
        var limiter = Limiter();
        limiter.Observe("2", "12", "", 200);
        Assert.Equal(TimeSpan.FromSeconds(12), limiter.Delay());
        _now = _now.AddSeconds(13);
        Assert.Equal(TimeSpan.Zero, limiter.Delay()); // reset has passed
    }

    [Fact]
    public void Waits_are_capped()
    {
        var limiter = Limiter();
        limiter.Observe("0", "500", "", 200);
        Assert.Equal(RateLimiter.MaxWait, limiter.Delay());
    }

    [Fact]
    public void Too_many_requests_waits_as_asked_or_five_seconds()
    {
        var limiter = Limiter();
        limiter.Observe("", "", "8", 429);
        Assert.Equal(TimeSpan.FromSeconds(8), limiter.Delay());
        var other = Limiter();
        other.Observe("", "", "", 429);
        Assert.Equal(TimeSpan.FromSeconds(5), other.Delay());
    }

    [Fact]
    public void Missing_headers_change_nothing()
    {
        var limiter = Limiter();
        limiter.Observe("", "", "", 200);
        Assert.Equal(TimeSpan.Zero, limiter.Delay());
    }
}

public class SummaryTextTests
{
    const string Thread = """
        [{"data":{"children":[{"kind":"t3","data":{"title":"Best budget keyboard?","subreddit":"keyboards","author":"op_person",
          "score":120,"num_comments":3,"selftext":"Under $50 please.","is_self":true}}]}},
         {"data":{"children":[
           {"kind":"t1","data":{"author":"alice","score":40,"body":"Get the Keychron C1.\nGreat value.","is_submitter":false,
             "replies":{"data":{"children":[
               {"kind":"t1","data":{"author":"op_person","score":10,"body":"Thanks!","is_submitter":true,"replies":""}}]}}}},
           {"kind":"t1","data":{"author":"bob","score":0,"body":"[deleted]","replies":""}},
           {"kind":"t1","data":{"author":"carol","score_hidden":true,"score":1,"body":"Avoid membrane ones.","replies":""}},
           {"kind":"more","data":{"count":20}}]}}]
        """;

    [Fact]
    public void A_thread_becomes_readable_indented_text()
    {
        var text = ThreadSummarizer.ThreadText(Thread);
        Assert.Contains("Title: Best budget keyboard?", text);
        Assert.Contains("r/keyboards · Posted by u/op_person", text);
        Assert.Contains("Under $50 please.", text);
        Assert.Contains("- u/alice (40 points): Get the Keychron C1. Great value.", text); // one line per comment
        Assert.Contains("  - u/op_person (OP) (10 points): Thanks!", text);                // replies indented
        Assert.Contains("- u/carol (score hidden): Avoid membrane ones.", text);
        Assert.DoesNotContain("[deleted]", text);
    }

    [Fact]
    public void Summaries_need_a_key()
    {
        var summarizer = new ThreadSummarizer(new AppSettings());
        if (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 }) return; // a key in the environment counts
        Assert.False(summarizer.IsReady);
    }

    [Fact]
    public void Saved_keys_are_encrypted_for_this_account()
    {
        var stored = KeyStore.Protect("sk-ant-test-123");
        Assert.DoesNotContain("sk-ant", stored);
        Assert.Equal("sk-ant-test-123", KeyStore.Unprotect(stored));
        Assert.Null(KeyStore.Unprotect("not a protected value"));
        Assert.Equal("", KeyStore.Protect(""));
    }
}

public class AccessibilityTests
{
    [Theory]
    [InlineData("✕", true)]
    [InlineData("⟳", true)]
    [InlineData("📋", true)]
    [InlineData("▲", true)]
    [InlineData("Reply", false)]
    [InlineData("AZ", false)]
    [InlineData("", false)]
    [InlineData("✉ 3", false)]
    public void Symbol_labels_are_told_apart_from_words(string label, bool symbol) =>
        Assert.Equal(symbol, Accessibility.IsSymbol(label));
}

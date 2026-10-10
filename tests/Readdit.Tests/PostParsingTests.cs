namespace Readdit.Tests;

/// <summary>Turning Reddit's listing JSON into Post rows.</summary>
public class PostParsingTests
{
    const string Link = """
        {
          "name": "t3_abc", "id": "abc", "title": "Hello", "author": "alice", "subreddit": "csharp",
          "url": "https://example.com/page", "permalink": "/r/csharp/comments/abc/hello/", "domain": "example.com",
          "score": 1234, "likes": true, "saved": true, "num_comments": 42, "created_utc": 1700000000.0,
          "is_self": false, "over_18": true, "spoiler": false, "stickied": true, "link_flair_text": "Help",
          "thumbnail": "https://b.thumbs.redditmedia.com/t.jpg",
          "preview": { "images": [ {
            "source": { "url": "https://preview.redd.it/full.jpg", "width": 3000 },
            "resolutions": [
              { "url": "https://preview.redd.it/320.jpg", "width": 320 },
              { "url": "https://preview.redd.it/640.jpg", "width": 640 },
              { "url": "https://preview.redd.it/1080.jpg", "width": 1080 } ] } ] }
        }
        """;

    [Fact]
    public void Link_fields_are_read()
    {
        var post = Post.FromLink(TestJson.Parse(Link));

        Assert.Equal(ItemKind.Link, post.Kind);
        Assert.Equal("t3_abc", post.FullName);
        Assert.Equal("abc", post.Id);
        Assert.Equal("Hello", post.Title);
        Assert.Equal("alice", post.Author);
        Assert.Equal("csharp", post.Subreddit);
        Assert.Equal("https://example.com/page", post.Url);
        Assert.Equal("https://www.reddit.com/r/csharp/comments/abc/hello/", post.PermalinkUrl);
        Assert.Equal(1234, post.Score);
        Assert.True(post.Likes);
        Assert.True(post.Saved);
        Assert.Equal(42, post.NumComments);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), post.Created);
        Assert.True(post.Over18);
        Assert.True(post.Stickied);
        Assert.True(post.HasLink);
    }

    [Fact]
    public void Preview_picks_the_largest_resolution_that_fits_640()
    {
        var post = Post.FromLink(TestJson.Parse(Link));
        Assert.Equal("https://preview.redd.it/640.jpg", post.PreviewImage);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("default")]
    [InlineData("nsfw")]
    [InlineData("spoiler")]
    [InlineData("")]
    public void Placeholder_thumbnails_are_ignored(string thumbnail)
    {
        var post = Post.FromLink(TestJson.Parse($$"""{"name":"t3_x","id":"x","thumbnail":"{{thumbnail}}"}"""));
        Assert.Null(post.Thumbnail);
    }

    [Fact]
    public void Self_post_links_to_its_own_permalink()
    {
        var post = Post.FromLink(TestJson.Parse("""{"name":"t3_x","id":"x","is_self":true,"url":"","permalink":"/r/a/comments/x/t/"}"""));
        Assert.Equal("https://www.reddit.com/r/a/comments/x/t/", post.Url);
        Assert.False(post.HasLink);
    }

    [Fact]
    public void Removed_galleries_with_null_metadata_do_not_throw()
    {
        // Reddit sends null for these on removed galleries; this used to break a whole feed page.
        var post = Post.FromLink(TestJson.Parse(
            """{"name":"t3_x","id":"x","is_gallery":true,"gallery_data":null,"media_metadata":null,"preview":null}"""));
        Assert.Null(post.PreviewImage);
    }

    [Fact]
    public void Gallery_preview_comes_from_media_metadata()
    {
        var post = Post.FromLink(TestJson.Parse("""
            {"name":"t3_g","id":"g","is_gallery":true,
             "gallery_data":{"items":[{"media_id":"m1"},{"media_id":"m2"}]},
             "media_metadata":{"m1":{"status":"valid","e":"Image","s":{"u":"https://i.redd.it/m1.jpg"},
               "p":[{"x":320,"u":"https://preview.redd.it/m1-320.jpg"},{"x":960,"u":"https://preview.redd.it/m1-960.jpg"}]}}}
            """));
        Assert.Equal("https://preview.redd.it/m1-320.jpg", post.PreviewImage);
    }

    [Fact]
    public void Direct_image_url_is_its_own_preview()
    {
        var post = Post.FromLink(TestJson.Parse("""{"name":"t3_x","id":"x","url":"https://i.redd.it/pic.PNG?x=1"}"""));
        Assert.Equal("https://i.redd.it/pic.PNG?x=1", post.PreviewImage);
    }

    [Fact]
    public void Moderator_fields_mark_reports_and_status()
    {
        var removed = Post.FromLink(TestJson.Parse("""{"name":"t3_r","id":"r","num_reports":3,"removed_by_category":"moderator"}"""));
        Assert.Equal(3, removed.NumReports);
        Assert.True(removed.IsRemoved);
        Assert.False(removed.IsApproved);

        var approved = Post.FromLink(TestJson.Parse("""{"name":"t3_a","id":"a","approved_by":"somemod"}"""));
        Assert.True(approved.IsApproved);

        var deletedByAuthor = Post.FromLink(TestJson.Parse("""{"name":"t3_d","id":"d","removed_by_category":"deleted"}"""));
        Assert.False(deletedByAuthor.IsRemoved);
    }

    [Fact]
    public void Profile_comment_opens_its_thread_focused_on_the_comment()
    {
        var post = Post.FromThing(TestJson.Thing("t1", """
            {"name":"t1_c9","id":"c9","link_id":"t3_abc","link_title":"The thread","author":"bob",
             "subreddit":"csharp","body":"A reply","permalink":"/r/csharp/comments/abc/t/c9/","score":5}
            """))!;
        Assert.Equal(ItemKind.Comment, post.Kind);
        Assert.Equal("abc", post.Id);
        Assert.Equal("c9", post.CommentId);
        Assert.Equal("The thread", post.Title);
        Assert.Equal("A reply", post.Snippet);
    }

    [Fact]
    public void Inbox_reply_without_link_id_uses_its_context_url()
    {
        var post = Post.FromThing(TestJson.Thing("t1", """
            {"name":"t1_r1","id":"r1","context":"/r/csharp/comments/xyz/title/r1/?context=3","new":true,
             "subject":"comment reply","body":"hi"}
            """))!;
        Assert.Equal("xyz", post.Id);
        Assert.True(post.IsUnread);
        Assert.Contains("COMMENT REPLY", post.Tags);
    }

    [Fact]
    public void Modmail_message_without_author_shows_the_subreddit()
    {
        var post = Post.FromThing(TestJson.Thing("t4", """
            {"name":"t4_m1","id":"m1","subject":"Welcome","author":"","subreddit_name_prefixed":"r/csharp","body":"Hi"}
            """))!;
        Assert.Equal(ItemKind.Message, post.Kind);
        Assert.Equal("r/csharp", post.Author);
        Assert.Equal("", post.Id);
        Assert.False(post.CanVote);
    }

    [Fact]
    public void Unknown_kinds_are_skipped()
    {
        Assert.Null(Post.FromThing(TestJson.Thing("more", """{"count":3}""")));
    }

    [Theory]
    [InlineData(null, true, 11)]   // upvote from neutral
    [InlineData(true, null, 9)]    // undo an upvote
    [InlineData(true, false, 8)]   // switch up → down
    [InlineData(false, true, 12)]  // switch down → up
    public void Votes_adjust_the_score_like_reddit(bool? before, bool? after, long expected)
    {
        var post = Post.FromLink(TestJson.Parse("""{"name":"t3_v","id":"v"}"""));
        post.Likes = before;
        post.Score = before switch { true => 10, false => 10, _ => 10 };
        post.ApplyVote(after);
        Assert.Equal(expected, post.Score);
        Assert.Equal(after, post.Likes);
    }

    [Fact]
    public void Long_comment_snippets_are_trimmed()
    {
        var body = new string('x', 500);
        var post = Post.FromThing(TestJson.Thing("t1", $$"""{"name":"t1_l","id":"l","body":"{{body}}"}"""))!;
        Assert.Equal(281, post.Snippet.Length); // 280 + "…"
        Assert.EndsWith("…", post.Snippet);
    }

    [Fact]
    public void Tags_reflect_state_changes()
    {
        var post = Post.FromThing(TestJson.Thing("t1", """{"name":"t1_n","id":"n","new":true}"""))!;
        Assert.Contains("NEW", post.Tags);
        post.IsUnread = false;
        Assert.DoesNotContain("NEW", post.Tags);
    }
}

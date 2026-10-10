namespace Readdit.Tests;

/// <summary>Finding the downloadable media in a post (also used for the offline library).</summary>
public class MediaExtractorTests
{
    [Fact]
    public void Gallery_images_and_animations_in_order_skipping_failed_items()
    {
        var items = MediaExtractor.Extract(TestJson.Parse("""
            {"is_gallery":true,
             "gallery_data":{"items":[{"media_id":"a"},{"media_id":"b"},{"media_id":"broken"},{"media_id":"c"}]},
             "media_metadata":{
               "a":{"status":"valid","e":"Image","s":{"u":"https://i.redd.it/a.jpg"}},
               "b":{"status":"valid","e":"AnimatedImage","s":{"gif":"https://i.redd.it/b.gif","mp4":"https://i.redd.it/b.mp4"}},
               "broken":{"status":"failed"},
               "c":{"status":"valid","e":"Image","s":{"u":"https://i.redd.it/c.png"}}}}
            """));

        Assert.Equal(["https://i.redd.it/a.jpg", "https://i.redd.it/b.mp4", "https://i.redd.it/c.png"], items.Select(i => i.Url));
        Assert.Equal([false, true, false], items.Select(i => i.IsVideo));
    }

    [Fact]
    public void Reddit_video_tries_current_audio_names_first()
    {
        var items = MediaExtractor.Extract(TestJson.Parse("""
            {"secure_media":{"reddit_video":{"fallback_url":"https://v.redd.it/vid1/CMAF_720.mp4?source=fallback","is_gif":false}}}
            """));

        var video = Assert.Single(items);
        Assert.True(video.IsVideo);
        Assert.Equal("https://v.redd.it/vid1/CMAF_720.mp4?source=fallback", video.Url);
        Assert.Equal("https://v.redd.it/vid1/CMAF_AUDIO_128.mp4", video.AudioCandidates[0]);
        Assert.Contains("https://v.redd.it/vid1/DASH_AUDIO_128.mp4", video.AudioCandidates);
    }

    [Fact]
    public void Gif_style_videos_have_no_audio_to_look_for()
    {
        var items = MediaExtractor.Extract(TestJson.Parse("""
            {"preview":{"reddit_video_preview":{"fallback_url":"https://v.redd.it/g/CMAF_480.mp4","is_gif":true}}}
            """));
        Assert.Empty(Assert.Single(items).AudioCandidates);
    }

    [Theory]
    [InlineData("https://i.imgur.com/abc.gifv", "https://i.imgur.com/abc.mp4", true)]
    [InlineData("https://example.com/clip.mp4", "https://example.com/clip.mp4", true)]
    [InlineData("https://i.redd.it/pic.jpg", "https://i.redd.it/pic.jpg", false)]
    [InlineData("https://example.com/anim.gif?x=1", "https://example.com/anim.gif?x=1", false)]
    public void Direct_media_links(string url, string expected, bool isVideo)
    {
        var item = Assert.Single(MediaExtractor.Extract(TestJson.Parse($$"""{"url":"{{url}}"}""")));
        Assert.Equal(expected, item.Url);
        Assert.Equal(isVideo, item.IsVideo);
    }

    [Fact]
    public void Ordinary_links_have_nothing_to_download()
    {
        Assert.Empty(MediaExtractor.Extract(TestJson.Parse("""{"url":"https://example.com/article"}""")));
    }

    [Fact]
    public void Crossposts_use_the_original_posts_media()
    {
        var items = MediaExtractor.Extract(TestJson.Parse("""
            {"url":"/r/x/comments/1/","crosspost_parent_list":[{"url":"https://i.redd.it/original.jpg","post_hint":"image"}]}
            """));
        Assert.Equal("https://i.redd.it/original.jpg", Assert.Single(items).Url);
    }

    [Fact]
    public void Null_media_fields_do_not_throw()
    {
        Assert.Empty(MediaExtractor.Extract(TestJson.Parse(
            """{"is_gallery":true,"gallery_data":null,"media_metadata":null,"secure_media":null,"media":null,"preview":null}""")));
    }

    [Fact]
    public void Stills_match_the_post_views_preview_choice()
    {
        // The offline library maps these URLs to local files; detail.js picks the same one (≤1080 wide).
        var stills = MediaExtractor.Stills(TestJson.Parse("""
            {"preview":{"images":[{"source":{"url":"https://p/full.jpg"},
              "resolutions":[{"url":"https://p/640.jpg","width":640},{"url":"https://p/1080.jpg","width":1080},{"url":"https://p/2000.jpg","width":2000}]}]}}
            """));
        Assert.Equal(["https://p/1080.jpg"], stills);
    }
}

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Skein.Tests;

public class LaterTimesTests
{
    // Friday 10 October 2025, 14:30.
    static readonly DateTime Friday = new(2025, 10, 10, 14, 30, 0);

    [Theory]
    [InlineData("1h", "2025-10-10 15:30")]
    [InlineData("3h", "2025-10-10 17:30")]
    [InlineData("evening", "2025-10-10 19:00")]
    [InlineData("tomorrow", "2025-10-11 09:00")]
    [InlineData("weekend", "2025-10-11 09:00")]
    [InlineData("week", "2025-10-17 09:00")]
    public void Quick_choices_mean_sensible_times(string key, string expected) =>
        Assert.Equal(expected, LaterTimes.For(key, Friday).ToString("yyyy-MM-dd HH:mm"));

    [Fact]
    public void Evening_after_six_is_tomorrow_evening_and_weekends_roll_on()
    {
        Assert.Equal(new DateTime(2025, 10, 11, 19, 0, 0), LaterTimes.For("evening", Friday.Date.AddHours(20)));
        Assert.Equal(new DateTime(2025, 10, 12, 9, 0, 0), LaterTimes.For("weekend", new DateTime(2025, 10, 11, 8, 0, 0))); // Saturday → Sunday
        Assert.Equal(new DateTime(2025, 10, 18, 9, 0, 0), LaterTimes.For("weekend", new DateTime(2025, 10, 12, 8, 0, 0))); // Sunday → next Saturday
    }

    [Theory]
    [InlineData("9:30", 9, 30)]
    [InlineData("21:15", 21, 15)]
    [InlineData("7pm", 19, 0)]
    [InlineData("12am", 0, 0)]
    [InlineData("12:45 PM", 12, 45)]
    [InlineData("8.05", 8, 5)]
    public void Times_are_read_the_way_people_type_them(string text, int hour, int minute) =>
        Assert.Equal(new TimeSpan(hour, minute, 0), LaterTimes.ParseTime(text));

    [Theory]
    [InlineData("25:00")]
    [InlineData("9:75")]
    [InlineData("13pm")]
    [InlineData("soon")]
    [InlineData("")]
    public void Nonsense_times_are_refused(string text) => Assert.Null(LaterTimes.ParseTime(text));

    [Fact]
    public void Times_are_described_relative_to_today()
    {
        Assert.StartsWith("Today", LaterTimes.Describe(Friday.AddHours(2), Friday));
        Assert.StartsWith("Tomorrow", LaterTimes.Describe(Friday.AddDays(1), Friday));
        Assert.DoesNotContain("Today", LaterTimes.Describe(Friday.AddDays(20), Friday));
    }
}

public class SavedReplyAndKarmaTests
{
    [Fact]
    public void Saved_replies_are_kept_by_name_in_order()
    {
        var list = new List<SavedReply>();
        SavedReplyBook.Add(list, "Welcome", "Hi!");
        SavedReplyBook.Add(list, "approved", "Approved.");
        SavedReplyBook.Add(list, "welcome", "Hello there!"); // same name replaces
        Assert.Equal(["approved", "welcome"], list.Select(r => r.Name));
        Assert.Equal("Hello there!", list[1].Text);
        Assert.True(SavedReplyBook.Remove(list, "APPROVED"));
        Assert.Single(list);
        Assert.Throws<InvalidOperationException>(() => SavedReplyBook.Add(list, "  ", "text"));
        Assert.Throws<InvalidOperationException>(() => SavedReplyBook.Add(list, "Empty", "   "));
    }

    [Fact]
    public void Karma_is_recorded_once_a_day_with_the_latest_value()
    {
        var history = new Dictionary<string, List<KarmaPoint>>();
        var day = new DateTime(2026, 10, 1, 9, 0, 0);
        Assert.True(KarmaHistory.Record(history, "dave", day, 100));
        Assert.False(KarmaHistory.Record(history, "dave", day.AddHours(1), 100)); // unchanged: nothing to save
        Assert.True(KarmaHistory.Record(history, "dave", day.AddHours(5), 120));
        Assert.True(KarmaHistory.Record(history, "dave", day.AddDays(1), 130));
        Assert.Equal([new KarmaPoint("2026-10-01", 120), new KarmaPoint("2026-10-02", 130)], history["dave"]);
        Assert.False(KarmaHistory.Record(history, "", day, 5));
    }

    [Fact]
    public void Karma_history_keeps_two_years()
    {
        var history = new Dictionary<string, List<KarmaPoint>>();
        var start = new DateTime(2024, 1, 1);
        for (var i = 0; i < 800; i++) KarmaHistory.Record(history, "dave", start.AddDays(i), i);
        Assert.Equal(730, history["dave"].Count);
        Assert.Equal(799, history["dave"][^1].Karma);
    }
}

public class LaterSyncTests : TempFolderTest
{
    [Fact]
    public void Reminders_and_saved_replies_sync_but_scheduled_posts_stay_on_this_pc()
    {
        var folder = Path.Combine(Dir, "OneDrive");
        var a = new AppSettings { SyncEnabled = true, SyncFolder = folder };
        var b = new AppSettings { SyncEnabled = true, SyncFolder = folder };
        a.Reminders.Add(new Reminder { PostId = "abc", Title = "Read later", Due = 1000 });
        a.SavedReplies.Add(new SavedReply { Name = "Welcome", Text = "Hi!" });
        a.ScheduledPosts.Add(new ScheduledPost { Title = "Goes out from PC A", Due = 2000 });
        new SettingsSync(a, new UserNotes(Path.Combine(Dir, "a.json")), save: () => { }).RunOnce();

        var syncB = new SettingsSync(b, new UserNotes(Path.Combine(Dir, "b.json")), save: () => { });
        syncB.Reset(preferFile: true);
        syncB.RunOnce();
        Assert.Equal("abc", Assert.Single(b.Reminders).PostId);
        Assert.Equal("Hi!", Assert.Single(b.SavedReplies).Text);
        Assert.Empty(b.ScheduledPosts); // posting it from two PCs would post it twice
    }
}

public class TranslatorTests
{
    [Fact]
    public void Long_text_is_split_into_pieces_that_join_back_up()
    {
        var text = string.Join(" ", Enumerable.Range(0, 60).Select(i => $"This is sentence number {i}.")) + "\n\nShort.\n" +
                   new string('x', 1200) + " end";
        var pieces = Translator.Pieces(text);
        Assert.All(pieces, p => Assert.True(System.Text.Encoding.UTF8.GetByteCount(p) <= 450, p));
        Assert.Equal(text, string.Concat(pieces));
        Assert.True(pieces.Count > 4);
    }

    [Fact]
    public void Multibyte_text_is_measured_in_bytes()
    {
        var text = string.Concat(Enumerable.Repeat("これは日本語の文です。", 40));
        var pieces = Translator.Pieces(text);
        Assert.All(pieces, p => Assert.True(System.Text.Encoding.UTF8.GetByteCount(p) <= 450));
        Assert.Equal(text, string.Concat(pieces));
    }

    [Fact]
    public async Task Nothing_is_sent_while_translation_is_off()
    {
        var translator = new Translator(new AppSettings { TranslateEnabled = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() => translator.TranslateAsync("Bonjour", default));
    }

    [Fact]
    public void The_target_language_is_the_chosen_one_or_windows()
    {
        Assert.Equal("de", new Translator(new AppSettings { TranslateTo = "DE" }).TargetLanguage);
        Assert.Equal(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            new Translator(new AppSettings()).TargetLanguage);
    }
}

public class AppCommandTests : TempFolderTest
{
    [Fact]
    public void Jump_list_and_bookmark_links_are_commands()
    {
        var inbox = AppCommands.Parse(LinkHandler.NormalizeIncoming("skein://_skein/inbox")!);
        Assert.Equal("inbox", inbox?.Command);

        var url = LinkHandler.NormalizeIncoming(
            "skein://_skein/submit?url=https%3A%2F%2Fexample.com%2Fa%3Fb%3D1&title=A%20great%20page")!;
        var submit = AppCommands.Parse(url);
        Assert.Equal("submit", submit?.Command);
        var prefill = AppCommands.PrefillFrom(submit!.Value.Args);
        Assert.Equal(("https://example.com/a?b=1", "A great page"), (prefill.Url, prefill.Title));
    }

    [Theory]
    [InlineData("https://www.reddit.com/r/aww/")]
    [InlineData("https://www.reddit.com/_skein/format-c")]
    [InlineData("https://example.com/_skein/inbox")]
    public void Ordinary_links_and_unknown_commands_are_not_commands(string url) => Assert.Null(AppCommands.Parse(url));

    [Fact]
    public void Only_web_links_are_taken_as_a_posts_link()
    {
        var args = new Dictionary<string, string> { ["url"] = "javascript:alert(1)", ["title"] = new string('t', 400) };
        var prefill = AppCommands.PrefillFrom(args);
        Assert.Equal("", prefill.Url);
        Assert.Equal(300, prefill.Title.Length);
    }

    [Fact]
    public void Send_to_passes_pictures_and_videos_only()
    {
        var picture = Path.Combine(Dir, "cat.jpg");
        var video = Path.Combine(Dir, "clip.mp4");
        var document = Path.Combine(Dir, "notes.txt");
        foreach (var file in new[] { picture, video, document }) File.WriteAllText(file, "x");

        var message = AppCommands.ForFiles([picture, document, video, "--restarted", Path.Combine(Dir, "missing.png")]);
        Assert.NotNull(message);
        Assert.Equal([picture, video], AppCommands.FilesFrom(message!));
        Assert.Null(AppCommands.ForFiles([document]));
        Assert.Empty(AppCommands.FilesFrom("https://www.reddit.com/"));
    }

    [Fact]
    public void A_web_link_cant_pretend_to_be_shared_files()
    {
        // skein:// links always become web links, never the files form.
        var fromLink = LinkHandler.NormalizeIncoming("skein://" + AppCommands.FilesPrefix + "C:\\Windows\\win.ini");
        Assert.True(fromLink == null || !fromLink.StartsWith(AppCommands.FilesPrefix));
    }
}

public class ImageEditTests : TempFolderTest
{
    /// <summary>A 40×20 picture: left half red, right half blue.</summary>
    static BitmapSource Sample()
    {
        var pixels = new byte[40 * 20 * 4];
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 40; x++)
            {
                var i = (y * 40 + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = x < 20 ? ((byte)0, (byte)0, (byte)255, (byte)255) : ((byte)255, (byte)0, (byte)0, (byte)255);
            }
        var image = BitmapSource.Create(40, 20, 96, 96, PixelFormats.Bgra32, null, pixels, 40 * 4);
        image.Freeze();
        return image;
    }

    static byte[] PixelAt(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    [Fact]
    public void Crop_keeps_the_chosen_part()
    {
        var cropped = ImageEditWindow.Crop(Sample(), new Int32Rect(20, 0, 10, 5));
        Assert.Equal((10, 5), (cropped.PixelWidth, cropped.PixelHeight));
        Assert.Equal(255, PixelAt(cropped, 0, 0)[0]); // blue side
    }

    [Fact]
    public void Blur_mixes_the_area_and_leaves_the_rest()
    {
        var blurred = ImageEditWindow.Pixelate(Sample(), new Int32Rect(15, 0, 20, 20));
        // One block spans the red/blue edge, so it comes out a blend of both.
        var mixed = PixelAt(blurred, 18, 5);
        Assert.InRange(mixed[0], 1, 254);
        Assert.InRange(mixed[2], 1, 254);
        Assert.Equal([0, 0, 255, 255], PixelAt(blurred, 2, 2)); // outside the area: untouched
    }

    [Fact]
    public void Black_box_covers_the_area()
    {
        var covered = ImageEditWindow.BlackOut(Sample(), new Int32Rect(0, 0, 5, 5));
        Assert.Equal([0, 0, 0, 255], PixelAt(covered, 2, 2));
        Assert.Equal([0, 0, 255, 255], PixelAt(covered, 8, 8));
    }

    [Theory]
    [InlineData(1, 40, 20)]
    [InlineData(3, 40, 20)]
    [InlineData(6, 20, 40)]
    [InlineData(8, 20, 40)]
    public void Phone_photos_are_turned_the_right_way_up(int orientation, int width, int height)
    {
        var turned = ImageEditWindow.Orient(Sample(), orientation);
        Assert.Equal((width, height), (turned.PixelWidth, turned.PixelHeight));
    }

    [Fact]
    public void Turning_a_quarter_puts_the_left_side_on_top()
    {
        // Orientation 6 means "turn 90° clockwise to view": the left (red) half ends up on top.
        var turned = ImageEditWindow.Orient(Sample(), 6);
        Assert.Equal(255, PixelAt(turned, 10, 2)[2]);  // red at the top
        Assert.Equal(255, PixelAt(turned, 10, 37)[0]); // blue at the bottom
    }

    [Fact]
    public void Saved_copies_are_new_files_and_photos_stay_jpeg()
    {
        var jpeg = ImageEditWindow.SaveCopy(Sample(), @"C:\photos\holiday.JPG", Dir);
        var png = ImageEditWindow.SaveCopy(Sample(), @"C:\shots\screen.webp", Dir);
        Assert.EndsWith(".jpg", jpeg);
        Assert.EndsWith(".png", png);
        Assert.Equal((40, 20), (ImageEditWindow.Load(jpeg).PixelWidth, ImageEditWindow.Load(jpeg).PixelHeight));
        Assert.True(ImageEditWindow.CanEdit("a.jpeg") && ImageEditWindow.CanEdit("b.PNG"));
        Assert.False(ImageEditWindow.CanEdit("c.gif") || ImageEditWindow.CanEdit("d.mp4"));
    }
}

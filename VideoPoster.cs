using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Readdit;

/// <summary>
/// Reddit wants a still picture ("poster") with every uploaded video. This grabs a frame from the
/// video with Windows' own player, or draws a plain placeholder if Windows can't decode it.
/// </summary>
static class VideoPoster
{
    /// <summary>Writes a PNG poster for <paramref name="videoPath"/> into a temporary file and returns its path.</summary>
    public static async Task<string> CreateAsync(string videoPath)
    {
        var output = Path.Combine(Path.GetTempPath(), $"readdit-poster-{Guid.NewGuid():N}.png");
        var frame = await TryGrabFrameAsync(videoPath) ?? Placeholder();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        await using var file = File.Create(output);
        encoder.Save(file);
        return output;
    }

    static async Task<BitmapSource?> TryGrabFrameAsync(string videoPath)
    {
        var player = new MediaPlayer { ScrubbingEnabled = true, IsMuted = true };
        try
        {
            var opened = new TaskCompletionSource<bool>();
            player.MediaOpened += (_, _) => opened.TrySetResult(true);
            player.MediaFailed += (_, _) => opened.TrySetResult(false);
            player.Open(new Uri(Path.GetFullPath(videoPath)));
            if (await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(10))) != opened.Task || !opened.Task.Result)
                return null;

            var width = player.NaturalVideoWidth;
            var height = player.NaturalVideoHeight;
            if (width <= 0 || height <= 0) return null;
            // A second in (or the middle of a very short clip), so it isn't a black first frame.
            var length = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
            player.Position = length > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(1) : length / 2;
            player.Pause();
            await Task.Delay(700); // let the scrubbed frame arrive

            var scale = Math.Min(1.0, 1280.0 / Math.Max(width, height));
            var (w, h) = ((int)(width * scale), (int)(height * scale));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawVideo(player, new Rect(0, 0, w, h));
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            player.Close();
        }
    }

    /// <summary>A dark 16:9 frame with a play symbol.</summary>
    static BitmapSource Placeholder()
    {
        const int w = 1280, h = 720;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1B)), null, new Rect(0, 0, w, h));
            var play = new StreamGeometry();
            using (var g = play.Open())
            {
                g.BeginFigure(new Point(w / 2.0 - 60, h / 2.0 - 80), isFilled: true, isClosed: true);
                g.LineTo(new Point(w / 2.0 + 90, h / 2.0), isStroked: false, isSmoothJoin: false);
                g.LineTo(new Point(w / 2.0 - 60, h / 2.0 + 80), isStroked: false, isSmoothJoin: false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), null, play);
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

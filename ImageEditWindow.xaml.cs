using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Skein;

/// <summary>
/// Crop, rotate, blur or black out parts of a picture before posting it. Works on a copy: the
/// result is saved under Skein's data folder, without the original's hidden details (location…).
/// </summary>
public partial class ImageEditWindow : Window
{
    static readonly string[] Editable = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"];

    public static bool CanEdit(string path) => Editable.Contains(Path.GetExtension(path).ToLowerInvariant());

    readonly string _source;
    readonly BitmapSource _original;
    readonly Stack<BitmapSource> _undo = new();
    BitmapSource _current;
    Point? _start;

    public ImageEditWindow(string path)
    {
        InitializeComponent();
        _source = path;
        _original = _current = Load(path);
        Show(_current);
        OnToolChanged(this, new RoutedEventArgs());
    }

    /// <summary>Where the edited copy was saved.</summary>
    public string? SavedPath { get; private set; }

    // ---- Loading ----------------------------------------------------------------------------------

    /// <summary>Loads a picture the right way up (phones store photos sideways with a note to turn them).</summary>
    public static BitmapSource Load(string path)
    {
        BitmapFrame frame;
        using (var stream = File.OpenRead(path))
            frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        ushort orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort o) orientation = o;
        }
        catch
        {
            // No readable metadata: take it as it is.
        }
        BitmapSource image = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        image = Orient(image, orientation);
        image.Freeze();
        return image;
    }

    /// <summary>Applies an EXIF orientation (1–8).</summary>
    public static BitmapSource Orient(BitmapSource image, int orientation)
    {
        var (angle, flip) = orientation switch
        {
            2 => (0, true),
            3 => (180, false),
            4 => (180, true),
            5 => (90, true),
            6 => (90, false),
            7 => (270, true),
            8 => (270, false),
            _ => (0, false),
        };
        if (angle == 0 && !flip) return image;
        var transform = new TransformGroup();
        if (flip) transform.Children.Add(new ScaleTransform(-1, 1));
        if (angle != 0) transform.Children.Add(new RotateTransform(angle));
        return Flatten(new TransformedBitmap(image, transform));
    }

    /// <summary>A plain in-memory copy (so edits don't stack up chains of transforms).</summary>
    static BitmapSource Flatten(BitmapSource image)
    {
        var converted = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        copy.Freeze();
        return copy;
    }

    void Show(BitmapSource image)
    {
        _current = image;
        Picture.Source = image;
        UndoButton.IsEnabled = _undo.Count > 0;
        Title = $"Crop or blur · {image.PixelWidth} × {image.PixelHeight}";
    }

    void Apply(BitmapSource next)
    {
        _undo.Push(_current);
        Show(next);
    }

    // ---- Tools ------------------------------------------------------------------------------------

    void OnToolChanged(object sender, RoutedEventArgs e)
    {
        if (Hint == null) return;
        Hint.Text = CropTool.IsChecked == true ? "Drag over the part of the picture to keep."
            : BlurTool.IsChecked == true ? "Drag over faces, plates or names to blur them. Repeat for each one."
            : "Drag over anything to cover it with a black box.";
    }

    void OnRotate(object sender, RoutedEventArgs e) => Apply(Flatten(new TransformedBitmap(_current, new RotateTransform(90))));

    void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo.Count > 0) Show(_undo.Pop());
    }

    void OnReset(object sender, RoutedEventArgs e)
    {
        if (_current == _original) return;
        Apply(_original);
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Picture);
        Surface.CaptureMouse();
        UpdateSelection(_start.Value);
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_start != null) UpdateSelection(e.GetPosition(Picture));
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start) return;
        _start = null;
        Surface.ReleaseMouseCapture();
        Selection.Visibility = Visibility.Collapsed;
        var area = ToPixels(start, e.GetPosition(Picture));
        if (area.Width < 4 || area.Height < 4) return;
        Apply(CropTool.IsChecked == true ? Crop(_current, area)
            : BlurTool.IsChecked == true ? Pixelate(_current, area)
            : BlackOut(_current, area));
        if (CropTool.IsChecked == true) BlurTool.IsChecked = true; // cropping is usually done once
    }

    void UpdateSelection(Point to)
    {
        var from = _start!.Value;
        var x = Math.Clamp(Math.Min(from.X, to.X), 0, Picture.ActualWidth);
        var y = Math.Clamp(Math.Min(from.Y, to.Y), 0, Picture.ActualHeight);
        Canvas.SetLeft(Selection, x);
        Canvas.SetTop(Selection, y);
        Selection.Width = Math.Clamp(Math.Max(from.X, to.X), 0, Picture.ActualWidth) - x;
        Selection.Height = Math.Clamp(Math.Max(from.Y, to.Y), 0, Picture.ActualHeight) - y;
        Selection.Visibility = Visibility.Visible;
    }

    /// <summary>Two points on screen → the picture's pixels between them.</summary>
    Int32Rect ToPixels(Point a, Point b)
    {
        if (Picture.ActualWidth <= 0 || Picture.ActualHeight <= 0) return Int32Rect.Empty;
        var sx = _current.PixelWidth / Picture.ActualWidth;
        var sy = _current.PixelHeight / Picture.ActualHeight;
        var left = (int)Math.Clamp(Math.Min(a.X, b.X) * sx, 0, _current.PixelWidth);
        var top = (int)Math.Clamp(Math.Min(a.Y, b.Y) * sy, 0, _current.PixelHeight);
        var right = (int)Math.Clamp(Math.Ceiling(Math.Max(a.X, b.X) * sx), 0, _current.PixelWidth);
        var bottom = (int)Math.Clamp(Math.Ceiling(Math.Max(a.Y, b.Y) * sy), 0, _current.PixelHeight);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    public static BitmapSource Crop(BitmapSource image, Int32Rect area) => Flatten(new CroppedBitmap(image, area));

    /// <summary>
    /// Blurs an area beyond recognition: big blocks of averaged colour (about 8 across the shorter
    /// side, never under 10 pixels), so faces and text can't be made out or sharpened back.
    /// </summary>
    public static BitmapSource Pixelate(BitmapSource image, Int32Rect area)
    {
        var (pixels, stride) = Pixels(image);
        var block = Math.Max(10, Math.Min(area.Width, area.Height) / 8);
        for (var by = area.Y; by < area.Y + area.Height; by += block)
            for (var bx = area.X; bx < area.X + area.Width; bx += block)
            {
                var w = Math.Min(block, area.X + area.Width - bx);
                var h = Math.Min(block, area.Y + area.Height - by);
                long b = 0, g = 0, r = 0, alpha = 0;
                for (var y = by; y < by + h; y++)
                    for (var x = bx; x < bx + w; x++)
                    {
                        var i = y * stride + x * 4;
                        b += pixels[i];
                        g += pixels[i + 1];
                        r += pixels[i + 2];
                        alpha += pixels[i + 3];
                    }
                var n = w * h;
                for (var y = by; y < by + h; y++)
                    for (var x = bx; x < bx + w; x++)
                    {
                        var i = y * stride + x * 4;
                        pixels[i] = (byte)(b / n);
                        pixels[i + 1] = (byte)(g / n);
                        pixels[i + 2] = (byte)(r / n);
                        pixels[i + 3] = (byte)(alpha / n);
                    }
            }
        return FromPixels(image, pixels, stride);
    }

    public static BitmapSource BlackOut(BitmapSource image, Int32Rect area)
    {
        var (pixels, stride) = Pixels(image);
        for (var y = area.Y; y < area.Y + area.Height; y++)
            for (var x = area.X; x < area.X + area.Width; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
                pixels[i + 3] = 255;
            }
        return FromPixels(image, pixels, stride);
    }

    static (byte[] Pixels, int Stride) Pixels(BitmapSource image)
    {
        var flat = Flatten(image);
        var stride = flat.PixelWidth * 4;
        var pixels = new byte[stride * flat.PixelHeight];
        flat.CopyPixels(pixels, stride, 0);
        return (pixels, stride);
    }

    static BitmapSource FromPixels(BitmapSource like, byte[] pixels, int stride)
    {
        var result = BitmapSource.Create(like.PixelWidth, like.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    // ---- Saving -----------------------------------------------------------------------------------

    void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            SavedPath = SaveCopy(_current, _source, Path.Combine(Paths.Local, "edited"));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Info.Text = $"Couldn't save the picture: {ex.Message}";
        }
    }

    /// <summary>Saves as JPEG (photos) or PNG (everything else) in <paramref name="folder"/>; returns the file.</summary>
    public static string SaveCopy(BitmapSource image, string source, string folder)
    {
        Directory.CreateDirectory(folder);
        var jpeg = Path.GetExtension(source).ToLowerInvariant() is ".jpg" or ".jpeg";
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        // JPEG has no transparency: put it on white rather than black.
        var frame = jpeg ? new FormatConvertedBitmap(OnWhite(image), PixelFormats.Bgr24, null, 0) : image;
        encoder.Frames.Add(BitmapFrame.Create(frame));
        var name = Path.GetFileNameWithoutExtension(source);
        var file = Path.Combine(folder, $"{name}-edited-{DateTime.Now:yyyyMMdd-HHmmss}{(jpeg ? ".jpg" : ".png")}");
        using var stream = File.Create(file);
        encoder.Save(stream);
        return file;
    }

    static BitmapSource OnWhite(BitmapSource image)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            dc.DrawRectangle(Brushes.White, null, rect);
            dc.DrawImage(image, rect);
        }
        var target = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}

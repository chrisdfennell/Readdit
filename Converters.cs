using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Skein;

/// <summary>Collapsed for null / empty string / false; Visible otherwise. Invert flips it.</summary>
public sealed class VisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true,
        };
        return visible ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the value is non-null (and, for strings, non-empty).</summary>
public sealed class HasValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? s.Length > 0 : value != null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Loads a remote image URL; ConverterParameter optionally sets the decode width.</summary>
public sealed class UrlToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || !Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (parameter is string p && int.TryParse(p, out var width)) bitmap.DecodePixelWidth = width;
            bitmap.EndInit();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

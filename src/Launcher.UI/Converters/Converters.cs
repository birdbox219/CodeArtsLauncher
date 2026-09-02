using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Launcher.UI.Converters;

/// <summary>
/// Turns a hex string from the view model into a brush. The view models stay free of WPF types,
/// and colour choices stay in one place instead of being spread through DataTriggers.
/// </summary>
public sealed class StringToBrushConverter : IValueConverter
{
    private static readonly BrushConverter Inner = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            try { return Inner.ConvertFromString(s) ?? Brushes.Transparent; }
            catch (FormatException) { /* fall through */ }
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// Loads itch.io cover art from a URL.
///
/// Handlers are attached for the two failure paths (a dead URL, a corrupt image) because an
/// unhandled BitmapImage failure surfaces as an exception on the dispatcher — a broken thumbnail
/// should leave a blank tile, not take the window down. Decoding is capped at the display width
/// so a wall of covers does not hold full-size bitmaps in memory.
/// </summary>
public sealed class CoverImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        int decodeWidth = parameter is string p && int.TryParse(p, out int w) ? w : 0;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.UriSource = uri;
            image.DownloadFailed += (_, _) => { };
            image.DecodeFailed += (_, _) => { };
            image.EndInit();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound string is null or empty — used for search placeholders.</summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Inverts a bool, for "enabled while not busy" bindings.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : true;
}

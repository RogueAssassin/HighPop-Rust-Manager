using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace HighPop.Desktop.Converters;

/// <summary>
/// Turns a server's GameImageUrl into a bitmap for the server list. A custom image is a file path;
/// anything else (the WPF build's pack:// default) falls back to the bundled HighPop mark.
/// </summary>
public sealed class GameImageConverter : IValueConverter
{
    public static readonly GameImageConverter Instance = new();

    private static Bitmap? _default;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path && File.Exists(path))
        {
            try { return new Bitmap(path); }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { }
        }
        return _default ??= new Bitmap(AssetLoader.Open(new Uri("avares://HighPop.Desktop/Assets/highpop.png")));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

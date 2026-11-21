using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using HighPop.Models;

namespace HighPop.Desktop.Converters;

/// <summary>Colours a console line by its level.</summary>
public sealed class ConsoleTypeBrushConverter : IValueConverter
{
    private static readonly IBrush Default = new SolidColorBrush(Color.Parse("#EEF1F8"));
    private static readonly IBrush Input = new SolidColorBrush(Color.Parse("#34C77B"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#FFB020"));
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#F06A6A"));
    private static readonly IBrush System = new SolidColorBrush(Color.Parse("#38BDF8"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConsoleMessageType type ? type.ToString() switch
        {
            "Warning" => Warning,
            "Error" => Error,
            "System" => System,
            "Input" => Input,
            _ => Default,
        } : Default;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"Connected" or "Disconnected".</summary>
public sealed class ConnectedTextConverter : IValueConverter
{
    public static readonly ConnectedTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Connected" : "Disconnected";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Green when connected, grey when not.</summary>
public sealed class ConnectedBrushConverter : IValueConverter
{
    public static readonly ConnectedBrushConverter Instance = new();
    private static readonly IBrush On = new SolidColorBrush(Color.Parse("#34C77B"));
    private static readonly IBrush Off = new SolidColorBrush(Color.Parse("#7A8398"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? On : Off;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Backup kind label.</summary>
public sealed class BoolToIncrementalLabelConverter : IValueConverter
{
    public static readonly BoolToIncrementalLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "Incremental" : "Full";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps a 0..1 fraction to a bar height out of 80 px (minimum 2 so empty hours stay visible).</summary>
public sealed class FractionToHeightConverter : IValueConverter
{
    public static readonly FractionToHeightConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double f ? Math.Max(2, Math.Min(1, f) * 80) : 2d;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

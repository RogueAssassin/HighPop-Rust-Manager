using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HighPop.Desktop.Converters;

/// <summary>Green below 70 %, amber from 70 %, red from 90 %.</summary>
public sealed class PercentBrushConverter : IValueConverter
{
    public static readonly PercentBrushConverter Instance = new();
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#34C77B"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#FFB020"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F06A6A"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var pct = value switch
        {
            float f => f,
            double d => d,
            _ => 0d,
        };
        return pct >= 90 ? Bad : pct >= 70 ? Warn : Ok;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colours a remote server's status text (Running, Starting, Error ...).</summary>
public sealed class RemoteStatusBrushConverter : IValueConverter
{
    public static readonly RemoteStatusBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(Color.Parse((value as string) switch
        {
            "Running" => "#34C77B",
            "Starting" or "Stopping" => "#FFB020",
            "Installing" => "#22D3EE",
            "Updating" => "#A855F7",
            "Error" => "#F06A6A",
            _ => "#7A8398",
        }));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Whether a remote server action is allowed for a status. The parameter is "start", "stop" or "restart".
/// </summary>
public sealed class RemoteActionEnabledConverter : IValueConverter
{
    public static readonly RemoteActionEnabledConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string ?? string.Empty;
        return (parameter as string) switch
        {
            "start" => status is not ("Running" or "Starting" or "Stopping" or "Installing" or "Updating"),
            "stop" => status is "Running" or "Starting",
            "restart" => status is "Running",
            _ => false,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

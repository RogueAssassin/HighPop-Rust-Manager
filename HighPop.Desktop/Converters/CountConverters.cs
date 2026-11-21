using System.Globalization;
using Avalonia.Data.Converters;

namespace HighPop.Desktop.Converters;

/// <summary>True when an item count is above zero; used to hide a list's heading while it is empty.</summary>
public sealed class HasItemsConverter : IValueConverter
{
    public static readonly HasItemsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when an item count is zero; shows an empty-list message.</summary>
public sealed class NoItemsConverter : IValueConverter
{
    public static readonly NoItemsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count == 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
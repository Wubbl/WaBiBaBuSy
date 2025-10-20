using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace WaBiBaBuSy.UI.Views;

/// <summary>
/// Converts boolean values to "ON"/"OFF" text
/// </summary>
public class BoolToTextConverter : IValueConverter
{
    public static readonly BoolToTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? "ON" : "OFF";
        }
        return "OFF";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string stringValue)
        {
            return stringValue.Equals("ON", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}

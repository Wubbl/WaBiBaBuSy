using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace WaBiBaBuSy.UI.Views;

/// <summary>
/// Converts a hex color string (e.g. "#FF0000") to an Avalonia Color.
/// </summary>
public class HexToColorConverter : IValueConverter
{
    public static readonly HexToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try
            {
                return Color.Parse(hex);
            }
            catch
            {
                return Colors.Black;
            }
        }
        return Colors.Black;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color color)
            return color.ToString();
        return "#000000";
    }
}

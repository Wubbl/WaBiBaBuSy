using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;

namespace WaBiBaBuSy.UI.Views;

/// <summary>Converts a file path to its file name (for chips and captions).</summary>
public sealed class FileNameConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> use in XAML.</summary>
    public static readonly FileNameConverter Instance = new();

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string path ? Path.GetFileName(path) : value;

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

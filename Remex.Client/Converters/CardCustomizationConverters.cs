using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Remex.Client.Converters;

/// <summary>
/// Converts a hex color string (e.g. "#C0C0FF") to a <see cref="SolidColorBrush"/>.
/// </summary>
public class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                return new SolidColorBrush(Color.Parse(hex));
            }
            catch
            {
                // Fallback for invalid hex
            }
        }

        return new SolidColorBrush(Color.Parse("#E0E0FF"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a hex color string to an Avalonia <see cref="Color"/> (not a brush).
/// Used for SparklineControl.AccentColor binding.
/// </summary>
public class HexToColorConverter : IValueConverter
{
    public static readonly HexToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                return Color.Parse(hex);
            }
            catch
            {
                // Fallback
            }
        }

        return Color.Parse("#C0C0FF");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

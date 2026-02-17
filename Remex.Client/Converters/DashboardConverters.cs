using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Client.Converters;

/// <summary>
/// Converts a boolean value to one of two colors.
/// Pass "TrueColor|FalseColor" as the parameter (e.g. "#4ADE80|#FF6B6B").
/// </summary>
public class BoolToColorConverter : IValueConverter
{
    public static readonly BoolToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string s)
        {
            var parts = s.Split('|');
            if (parts.Length == 2)
            {
                return Avalonia.Media.Color.Parse(b ? parts[0] : parts[1]);
            }
        }
        return Avalonia.Media.Colors.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a double (latency ms) to a bar height for the mini chart.
/// Clamps to a reasonable pixel range (2–60px).
/// </summary>
public class LatencyToHeightConverter : IValueConverter
{
    public static readonly LatencyToHeightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double ms)
        {
            // Scale: 0ms → 2px, 100ms → 60px (clamped)
            var height = Math.Clamp(ms / 100.0 * 60.0, 2.0, 60.0);
            return height;
        }
        return 2.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

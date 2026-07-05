using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Desktop.Converters;

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
/// Converts an integer index to an opacity. If the value equals the parameter, returns 1.0; otherwise 0.3.
/// Used for page indicator dots.
/// </summary>
public class IndexToOpacityConverter : IValueConverter
{
    public static readonly IndexToOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index && parameter is string s && int.TryParse(s, out var target))
        {
            return index == target ? 1.0 : 0.3;
        }
        return 0.3;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts an integer to a boolean: true when the value equals the integer parsed from
/// the string ConverterParameter. Fixes the type-mismatch bug where ObjectConverters.Equal
/// compares int to string and always returns false.
/// </summary>
public class IntEqualConverter : IValueConverter
{
    public static readonly IntEqualConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index && parameter is string s && int.TryParse(s, out var target))
            return index == target;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Inverse of <see cref="IntEqualConverter"/>: true when the value does NOT equal the parameter.
/// </summary>
public class IntNotEqualConverter : IValueConverter
{
    public static readonly IntNotEqualConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index && parameter is string s && int.TryParse(s, out var target))
            return index != target;
        return true;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a boolean to one of two strings.
/// Pass "TrueString|FalseString" as the parameter (e.g. "✓|✕").
/// </summary>
public class BoolToStringConverter : IValueConverter
{
    public static readonly BoolToStringConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string s)
        {
            var parts = s.Split('|');
            return b ? parts[0] : (parts.Length > 1 ? parts[1] : string.Empty);
        }
        return string.Empty;
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

/// <summary>
/// Converts a CornerRadius to a Thickness (padding/margin) that scales with the corner arc,
/// ensuring card content never visually crowds the rounded edges.
/// Formula: max(16, maxCorner * 0.7) so large radii get proportionally more breathing room.
/// </summary>
public class CornerRadiusToMarginConverter : IValueConverter
{
    public static readonly CornerRadiusToMarginConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Avalonia.CornerRadius cr)
        {
            var max = Math.Max(cr.TopLeft, Math.Max(cr.TopRight, Math.Max(cr.BottomLeft, cr.BottomRight)));
            var margin = Math.Max(16.0, max * 0.7);
            return new Avalonia.Thickness(margin);
        }
        return new Avalonia.Thickness(16);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

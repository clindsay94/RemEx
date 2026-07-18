using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Remex.Desktop.Converters;

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
/// Converts a hex color string to a semi-opaque <see cref="SolidColorBrush"/>, overriding the
/// alpha channel with the <c>ConverterParameter</c> (a 0.0–1.0 fraction, default 0.8).
/// Used for the sensor-card title/value backing plate so text reads clearly over the
/// full-card sparkline regardless of the active app theme (the card background hex is dark in
/// every <see cref="Remex.Core.Models.SensorCardTheme"/> preset).
/// </summary>
public class HexToTranslucentBrushConverter : IValueConverter
{
    public static readonly HexToTranslucentBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double alpha = 0.8;
        if (parameter is string p && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            alpha = Math.Clamp(parsed, 0.0, 1.0);

        var baseColor = Color.Parse("#0A0A16");
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try { baseColor = Color.Parse(hex); }
            catch { /* keep dark fallback */ }
        }

        return new SolidColorBrush(new Color((byte)(alpha * 255), baseColor.R, baseColor.G, baseColor.B));
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

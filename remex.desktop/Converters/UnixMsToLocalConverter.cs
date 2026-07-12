using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Desktop.Converters;

/// <summary>
/// Converts a Unix-milliseconds UTC timestamp (as used by the file-transfer metadata contracts) to a
/// short local date-time string for display in the properties pane. A non-positive value renders as an
/// em dash so "unknown" reads cleanly.
/// </summary>
public sealed class UnixMsToLocalConverter : IValueConverter
{
    public static readonly UnixMsToLocalConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ms = value switch
        {
            long l => l,
            int i => i,
            _ => 0L,
        };

        if (ms <= 0)
            return "—";

        return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().DateTime.ToString("g", culture);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

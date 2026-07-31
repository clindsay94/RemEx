using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Desktop.Converters;

/// <summary>
/// Converts a raw byte count (long) to a human-readable string.
/// Precision:
///   &lt; 1 KB  → "N B"
///   1 KB–1 MB → "N.N KB"
///   1 MB–1 GB → "N.N MB"
///   ≥ 1 GB   → "N.N GB"
/// </summary>
public class BytesToHumanReadableConverter : IValueConverter
{
    public static readonly BytesToHumanReadableConverter Instance = new();

    private const long KB = 1024L;
    private const long MB = 1024L * KB;
    private const long GB = 1024L * MB;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        long bytes = value switch
        {
            long l => l,
            int i => i,
            ulong u => (long)u,
            uint ui => ui,
            _ => -1L
        };

        if (bytes < 0)
            return "—";

        if (bytes < KB)
            return $"{bytes:N0} B";

        if (bytes < MB)
            return $"{bytes / (double)KB:F1} KB";

        if (bytes < GB)
            return $"{bytes / (double)MB:F1} MB";

        return $"{bytes / (double)GB:F1} GB";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Remex.Desktop.Converters;

/// <summary>
/// Decodes a base64 image (app icons, a transfer thumbnail) into a <see cref="Bitmap"/>.
/// </summary>
/// <remarks>
/// DECODED ONCE PER DISTINCT STRING, NOT PER BINDING EVALUATION (perf audit P3-62). Every realization
/// of an app tile, flyout icon or launcher row re-ran the base64 decode and a full image decode, and
/// the old bitmap was never disposed. Bitmaps are now cached by their base64 string and shared across
/// every Image showing the same icon, which Avalonia supports. Evicted entries are dropped, NOT
/// disposed: an Image may still be painting one, and disposing it under the Image would blank it.
/// Oversized strings (a large transfer thumbnail) are not cached, so the cache stays small.
/// </remarks>
public class Base64ToImageConverter : IValueConverter
{
    public static readonly Base64ToImageConverter Instance = new();

    internal const int MaxCachedEntries = 256;
    internal const int MaxCachedLength = 256 * 1024;

    private readonly Func<string, object?> _decode;
    private readonly Dictionary<string, object?> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Base64ToImageConverter() : this(DecodeBitmap)
    {
    }

    /// <summary>Test seam: the decode step, so the cache can be exercised without a render platform.</summary>
    internal Base64ToImageConverter(Func<string, object?> decode) => _decode = decode;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string base64 || string.IsNullOrWhiteSpace(base64))
            return null;

        if (base64.Length > MaxCachedLength)
            return _decode(base64);

        lock (_gate)
        {
            if (_cache.TryGetValue(base64, out var cached))
                return cached;
        }

        var decoded = _decode(base64);

        lock (_gate)
        {
            if (_cache.Count >= MaxCachedEntries) _cache.Clear();
            // A failed decode caches as null too: the same bad string fails the same way every time.
            _cache[base64] = decoded;
        }
        return decoded;
    }

    private static object? DecodeBitmap(string base64)
    {
        try
        {
            byte[] bytes = System.Convert.FromBase64String(base64);
            var stream = new MemoryStream(bytes, writable: false);
            // Avalonia Bitmap takes ownership of the stream; do NOT dispose it here.
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

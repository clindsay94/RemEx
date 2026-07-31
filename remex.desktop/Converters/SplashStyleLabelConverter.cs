using System.Globalization;
using Avalonia.Data.Converters;
using Remex.Desktop.Localization;
using Remex.Desktop.Services;

namespace Remex.Desktop.Converters;

/// <summary>
/// Maps a splash-style id (RemexCommand / CosmicZoom / Pong) to its localized display label
/// (Original Scan / Cosmic Zoom / Signal Pong) via the "Splash_Style_&lt;id&gt;" resource key.
/// The picker keeps binding SelectedItem to the raw id (compatible with saved profiles); only the
/// shown text is friendly. Falls back to the raw id if no label resource exists.
/// </summary>
public sealed class SplashStyleLabelConverter : IValueConverter
{
    public static readonly SplashStyleLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string id = value as string ?? string.Empty;
        try
        {
            var label = LocalizationService.Instance["Splash_Style_" + id];
            if (!string.IsNullOrEmpty(label)) return label;
        }
        catch { /* fall through to the raw id */ }
        return id;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value;
}

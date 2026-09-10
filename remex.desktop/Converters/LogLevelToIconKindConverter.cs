using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;
using Microsoft.Extensions.Logging;

namespace Remex.Desktop.Converters;

/// <summary>
/// Maps a <see cref="LogLevel"/> to a <see cref="MaterialIconKind"/> for the diagnostic log list, so
/// severity reads from shape as well as colour. <see cref="LogLevelToBrushConverter"/> already handles
/// the colour half of this pairing; this converter exists because colour alone is not an accessible way
/// to convey severity (contrast varies per theme, and colour-blind viewers cannot rely on hue), so every
/// row also carries an icon whose silhouette differs level to level. (RemEx-peagx)
/// </summary>
public sealed class LogLevelToIconKindConverter : IValueConverter
{
    public static readonly LogLevelToIconKindConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevel.Trace => MaterialIconKind.DotsHorizontal,
        LogLevel.Debug => MaterialIconKind.BugOutline,
        LogLevel.Information => MaterialIconKind.InformationOutline,
        LogLevel.Warning => MaterialIconKind.AlertOutline,
        LogLevel.Error => MaterialIconKind.CloseCircleOutline,
        LogLevel.Critical => MaterialIconKind.AlertOctagonOutline,
        _ => MaterialIconKind.InformationOutline,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;
using Remex.Desktop.Services;

namespace Remex.Desktop.Converters;

/// <summary>
/// Maps an <see cref="ActivityKind"/> to a <see cref="MaterialIconKind"/> for the Home
/// "Recent activity" feed, replacing the emoji <c>ActivityEntry.Glyph</c> used to compute
/// (RemEx-1ufoa.4).
/// </summary>
public sealed class ActivityKindToIconKindConverter : IValueConverter
{
    public static readonly ActivityKindToIconKindConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ActivityKind.FileReceived => MaterialIconKind.TrayArrowDown,
        ActivityKind.FileSent => MaterialIconKind.TrayArrowUp,
        ActivityKind.FileUploaded => MaterialIconKind.CloudUploadOutline,
        ActivityKind.FileDownloaded => MaterialIconKind.CloudDownloadOutline,
        ActivityKind.AppLaunched => MaterialIconKind.RocketLaunchOutline,
        ActivityKind.CommandRun => MaterialIconKind.Flash,
        ActivityKind.DeviceConnected => MaterialIconKind.CellphoneLink,
        ActivityKind.DeviceDisconnected => MaterialIconKind.CellphoneOff,
        _ => MaterialIconKind.CircleSmall,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

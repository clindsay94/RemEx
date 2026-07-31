using System.Globalization;
using Avalonia.Data.Converters;
using Remex.Core.Models;

namespace Remex.Desktop.Converters;

/// <summary>
/// Formats a target FPS slider value for display. Values above <see cref="DesktopConfig.PacedMaxFps"/>
/// render as the localized "Unlimited" string instead of a number — the encoder is throughput-bound
/// well below <see cref="DesktopConfig.MaxTargetFps"/> at real resolutions, so the exact value in that
/// band is immaterial to the user.
/// </summary>
public class FpsDisplayConverter : IValueConverter
{
    public static readonly FpsDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int fps)
            return value;

        return fps > DesktopConfig.PacedMaxFps
            ? Services.LocalizationService.Instance["RemoteDesktop_FpsUnlimited"]
            : string.Format(culture, Services.LocalizationService.Instance["RemoteDesktop_FpsValue"], fps);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

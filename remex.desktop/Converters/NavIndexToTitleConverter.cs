using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Desktop.Converters;

/// <summary>
/// Maps <c>ShellViewModel.ActiveNavIndex</c> to the localized destination title, for the ColorZone
/// app bar's "current page" label (RemEx-a3prn).
/// </summary>
/// <remarks>
/// Reuses the same resource keys the drawer's <c>ListBoxItem</c>s already localize with
/// (<c>Nav_Home</c>, <c>Nav_Sensors</c>, ...) instead of adding new ones — every one of those keys
/// is already translated in all 9 locales, so they needed no localization work of their own.
///
/// Index 5 (<c>RemoteDesktopViewModel</c>) IS a real new string, <c>Nav_RemoteDesktop</c> (Opus
/// review of 6522b12, RemEx-a3prn): it has no drawer entry (it is reached from inside the
/// Commands/Remote flow, not the nav list), but it is NOT always hidden — <c>NavigateToRemoteDesktop</c>
/// never sets <c>IsShellChromeHidden</c>, only <c>RemoteDesktopViewModel.ToggleFullScreen</c> /
/// <c>NavigateBack</c> do, so windowed remote desktop shows this app bar with a real page to name.
/// Falling back to empty for anything genuinely unmapped (an out-of-range or non-int value) stays,
/// so this converter is still honest about the difference between "no page" and "this page has no
/// name yet".
/// </remarks>
public class NavIndexToTitleConverter : IValueConverter
{
    public static readonly NavIndexToTitleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int index) return string.Empty;

        var key = index switch
        {
            0 => "Nav_Home",
            1 => "Nav_Sensors",
            2 => "Nav_Commands",
            3 => "Nav_Launcher",
            4 => "Nav_Processes",
            5 => "Nav_RemoteDesktop",
            6 => "Shell_About",
            7 => "Nav_Files",
            8 => "Shell_LogsDiagnostics",
            9 => "Nav_Settings",
            _ => null,
        };

        return key is null ? string.Empty : Services.LocalizationService.Instance[key] ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

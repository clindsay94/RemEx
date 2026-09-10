namespace Remex.Desktop.Services;

/// <summary>
/// Shared formatting for a sensor reading or threshold: value to one decimal place (in the
/// current UI culture) plus one space and the unit, no trailing space when the unit is empty.
/// Lifted out of <c>ShellViewModel.FormatReading</c> (RemEx-8wpvr.4) so the Canvas alert bell
/// tooltip composes readings identically to the tray/toast notification text instead of
/// duplicating the "{0:F1} {1}" logic.
/// </summary>
public static class SensorReadingFormat
{
    public static string FormatReading(double value, string? unit)
    {
        var formattedValue = value.ToString("F1", LocalizationService.Instance.Culture);
        return string.IsNullOrEmpty(unit) ? formattedValue : $"{formattedValue} {unit}";
    }
}

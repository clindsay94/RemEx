using System.Globalization;
using Avalonia.Data.Converters;
using Remex.Desktop.Services;

namespace Remex.Desktop.Converters;

/// <summary>
/// Converts a staged card's <c>IsStale</c> flag to the localized AutomationProperties.HelpText a
/// screen reader gets for it, or null when the card is live (RemEx-lki2r).
/// </summary>
/// <remarks>
/// ON THE LIST BOX ITEM CONTAINER, NOT THE INNER material:Card (review). Verified on a live host:
/// Material.Styles.Controls.Card does not route AutomationProperties.HelpText to its automation
/// peer at all - a hardcoded HelpText set directly on a Card never surfaced via UIA, on the Card
/// element or any of its descendants, while the same value set on the containing ListBoxItem read
/// back correctly. UIA also reads HelpText from the FOCUSED element, which for a ListBox item is
/// the container, not a descendant - a second, independent reason this belongs on the item.
/// <para>
/// READS THE LOCALIZER AT BIND TIME AND DOES NOT REFRESH ON A LATER LANGUAGE SWITCH, and that is a
/// deliberate, accepted trade rather than an oversight (review). The alternative - a per-card
/// subscription to <c>LocalizationService.Instance.PropertyChanged</c> - is exactly what this bead
/// removed from <c>CanvasCardViewModel</c>: with roughly 470 staged cards in a session and no
/// removal path that disposes one, that subscription rooted every removed card (and its
/// <see cref="Remex.Desktop.ViewModels.SensorViewModel"/> history buffers) for the process
/// lifetime. A stale mark is transient - the row un-stales the moment the sensor reports again, or
/// the app is restarted - so a screen reader hearing the pre-switch language for a stale row until
/// its staleness next toggles is an acceptable, self-healing gap; a leaked view-model graph is not.
/// </para>
/// </remarks>
public sealed class StaleSensorHelpTextConverter : IValueConverter
{
    public static readonly StaleSensorHelpTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? LocalizationService.Instance["A11y_StagedSensorStale"] : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

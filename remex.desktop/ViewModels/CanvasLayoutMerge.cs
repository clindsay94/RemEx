using Remex.Core.Models;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// Pure merge helpers for persisting the canvas layout without losing sensor cards that haven't
/// been restored yet.
/// </summary>
/// <remarks>
/// Sensor cards are materialized into the live canvas <b>lazily</b> — one is only recreated once
/// its sensor's telemetry arrives. Non-sensor cards restore eagerly at init, but a sensor card sits
/// dormant in the persisted profile until its reading shows up. A naive "save the current in-memory
/// cards" therefore <b>deletes every sensor card whose sensor hasn't reported yet</b> on the first
/// save that fires during startup (a drag, a snap toggle, a resize, a pin, a colour change…). Over a
/// few sessions the layout erodes to only whatever happened to be live — the RemEx-jwvg data-loss bug.
///
/// These helpers keep persisted entries for sensors that have <i>not</i> been materialized this
/// session, while still letting the live canvas be authoritative for sensors that <i>have</i> been
/// seen (so genuine user deletions/unpins stick). "Materialized" is decided by the caller — the set
/// of sensor names that currently have a <see cref="SensorViewModel"/> on the canvas or in staging.
/// </remarks>
public static class CanvasLayoutMerge
{
    private const string SensorCardType = "Sensor";

    /// <summary>
    /// Combines the live card snapshot with the persisted sensor cards whose sensor hasn't been
    /// materialized this session, so an early/partial save can't delete not-yet-restored cards.
    /// </summary>
    /// <param name="persistedCards">The cards from the profile currently on disk (the merge base).</param>
    /// <param name="liveCardStates">Snapshot of the cards currently present on the canvas.</param>
    /// <param name="materializedSensorNames">
    /// Sensor names that currently have a live <see cref="SensorViewModel"/> (canvas or staging) —
    /// i.e. sensors that have reported this session and whose live cards are authoritative.
    /// </param>
    public static List<CardState> MergeCards(
        IEnumerable<CardState>? persistedCards,
        IReadOnlyList<CardState> liveCardStates,
        ISet<string> materializedSensorNames)
    {
        var liveCardIds = new HashSet<string>(liveCardStates.Select(s => s.CardId), StringComparer.Ordinal);

        var preserved = (persistedCards ?? Enumerable.Empty<CardState>())
            .Where(cs => string.Equals(cs.CardType, SensorCardType, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(cs.SensorId)
                && !materializedSensorNames.Contains(cs.SensorId!)   // sensor hasn't reported yet
                && !liveCardIds.Contains(cs.CardId));                 // and isn't already live

        return liveCardStates.Concat(preserved).ToList();
    }

    /// <summary>
    /// Combines the live pinned-sensor set with persisted pins for sensors not yet materialized this
    /// session, so pins for dormant sensor cards aren't wiped by an early save.
    /// </summary>
    public static List<string> MergePinnedSensors(
        IEnumerable<string>? persistedPinned,
        IEnumerable<string> livePinned,
        ISet<string> materializedSensorNames)
    {
        var preserved = (persistedPinned ?? Enumerable.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name) && !materializedSensorNames.Contains(name));

        return livePinned
            .Concat(preserved)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

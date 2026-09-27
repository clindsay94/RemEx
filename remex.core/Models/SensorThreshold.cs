namespace Remex.Core.Models;

/// <summary>
/// The one threshold rule shared by the PC's sensor alerts and the <c>pc.sensor</c> routine trigger
/// (routines spec §8.5.1). Moved here from <c>SensorViewModel.IsAlertLive</c> so both evaluate a
/// reading identically: a routine that fires where the dashboard alert does not (or the reverse) would
/// look like a bug in whichever one the person trusted.
/// </summary>
public static class SensorThreshold
{
    /// <summary>Fraction of |threshold| a value must retreat past it before a live state clears.</summary>
    public const double ClearDeadbandFraction = 0.02;

    /// <summary>
    /// HYSTERESIS (perf audit P3-57). Crossing the threshold goes live at once; clearing needs the value
    /// to retreat <see cref="ClearDeadbandFraction"/> of the threshold past it. Without the deadband a
    /// sensor sitting on its threshold flipped live/clear every other tick. Relative, not absolute,
    /// because thresholds span volts to RPM; a zero threshold has no band.
    /// </summary>
    public static bool IsLive(AlertDirection direction, double threshold, double value, bool wasLive)
    {
        var band = wasLive ? Math.Abs(threshold) * ClearDeadbandFraction : 0;
        return direction == AlertDirection.Above
            ? value > threshold - band
            : value < threshold + band;
    }
}

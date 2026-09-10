namespace Remex.Desktop.Services.FileTransfer;

/// <summary>The unit a throughput figure is expressed in.</summary>
public enum TransferRateUnit
{
    BytesPerSecond,
    KilobytesPerSecond,
    MegabytesPerSecond,
    GigabytesPerSecond,
}

/// <summary>The unit a time-remaining figure is expressed in.</summary>
public enum TransferEtaUnit
{
    Seconds,
    Minutes,
    Hours,
}

/// <summary>A throughput figure, or the honest absence of one.</summary>
public abstract record TransferRate
{
    /// <summary>Nothing meaningful to show — no estimate yet, or an unusable one.</summary>
    public sealed record Unknown : TransferRate;

    /// <summary>A figure and the unit it is in. The caller formats and localizes it.</summary>
    public sealed record Known(double Amount, TransferRateUnit Unit) : TransferRate;
}

/// <summary>A time-remaining figure, or the honest absence of one.</summary>
public abstract record TransferEta
{
    /// <summary>No usable estimate.</summary>
    public sealed record Unknown : TransferEta;

    /// <summary>Under a second — say so rather than showing a number.</summary>
    public sealed record Finishing : TransferEta;

    /// <summary>A rounded-up amount and its unit.</summary>
    public sealed record Remaining(int Amount, TransferEtaUnit Unit) : TransferEta;
}

/// <summary>
/// Classifies throughput and time-remaining for display (RemEx-kh6p4).
/// </summary>
/// <remarks>
/// <para>
/// **PORTED AS A SHAPE, NOT AS SHARED CODE**, the same way <c>VolumesResponseClassifier</c> was
/// (RemEx-jc4q). The reference is <c>remex.android</c>'s <c>TransferProgressFormat.kt</c> and there
/// is nothing to reuse from it: different language, different resource system, different screen.
/// What transfers is the ORDER and the THRESHOLDS, and RemEx-4lcq is explicit that they should be
/// reused rather than re-derived because "none of them was obvious".
/// </para>
/// <para>
/// THIS DELIBERATELY STOPS SHORT OF WORDS. Turning <c>Remaining(3, Minutes)</c> into a sentence needs
/// plural forms, and the PC's resources have no plural infrastructure at all while Polish and
/// Ukrainian take four forms for duration words. Choosing that mechanism is a decision and it stays
/// on RemEx-4lcq; this is the half that has no decisions left in it.
/// </para>
/// <para>
/// Nothing on the PC displays speed or ETA today, so this is pre-built arithmetic rather than a
/// second definition of a rule that already runs somewhere — the distinction recorded on
/// RemEx-thwlr, and the reason it is safe to land ahead of its UI.
/// </para>
/// </remarks>
public static class TransferProgressFormat
{
    /// <summary>Beyond this, an estimate stops being information.</summary>
    /// <remarks>
    /// TWENTY-THREE HOURS RATHER THAN TWENTY-FOUR, carried over with its reasoning: a 24-hour cap
    /// makes "24 hours" the top bucket, which is a quantity nobody writes — at that size a person
    /// says "a day" — and it would sit exactly on the refusal boundary, so an estimate jittering
    /// across it would alternate between a number and nothing.
    /// </remarks>
    public const double MaximumMeaningfulSeconds = 23.0 * 60.0 * 60.0;

    private const double BytesPerKilobyte = 1024.0;
    private const double BytesPerMegabyte = 1024.0 * 1024.0;
    private const double BytesPerGigabyte = 1024.0 * 1024.0 * 1024.0;

    private const int SecondsPerMinute = 60;
    private const int MinutesPerHour = 60;
    private const int SecondsPerHour = SecondsPerMinute * MinutesPerHour;

    /// <summary>Picks the unit for a throughput figure.</summary>
    /// <param name="bytesPerSecond">Null when there is no estimate yet.</param>
    /// <remarks>
    /// NOT FINITE MEANS NOT KNOWN. This is fed from a division, and "NaN MB/s" would be a defect
    /// visible to the user in the one place they are already anxious.
    /// </remarks>
    public static TransferRate Rate(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } value || !double.IsFinite(value) || value < 0.0)
        {
            return new TransferRate.Unknown();
        }

        return value switch
        {
            >= BytesPerGigabyte => new TransferRate.Known(value / BytesPerGigabyte, TransferRateUnit.GigabytesPerSecond),
            >= BytesPerMegabyte => new TransferRate.Known(value / BytesPerMegabyte, TransferRateUnit.MegabytesPerSecond),
            >= BytesPerKilobyte => new TransferRate.Known(value / BytesPerKilobyte, TransferRateUnit.KilobytesPerSecond),
            _ => new TransferRate.Known(value, TransferRateUnit.BytesPerSecond),
        };
    }

    /// <summary>Picks the unit and the rounded amount for a time-remaining figure.</summary>
    /// <param name="secondsRemaining">Null when stalled, size unknown, or too little observed.</param>
    /// <remarks>
    /// **ROUNDED UP, THEN BUCKETED — IN THAT ORDER, AND THE ORDER IS THE WHOLE SUBTLETY.** Bucketing
    /// first gives "60 seconds left" for 59.6s, because the value is under a minute before rounding
    /// and over one afterwards. Rounding up rather than to nearest keeps the figure from ever
    /// claiming less time than remains, so the display never counts to zero and then waits — which
    /// reads as a hang.
    /// </remarks>
    public static TransferEta Eta(double? secondsRemaining)
    {
        if (secondsRemaining is not { } value || !double.IsFinite(value) || value < 0.0)
        {
            return new TransferEta.Unknown();
        }

        // Under a second there is no honest number: "0 seconds left" reads as finished, and
        // "1 second" is a rounding-up of something smaller.
        if (value < 1.0) return new TransferEta.Finishing();

        // An absurd ETA is not an ETA. A very large file at a rate just above the estimator's floor
        // still yields days, and a number that large is fiction the user would plan around.
        if (value > MaximumMeaningfulSeconds) return new TransferEta.Unknown();

        var seconds = (int)Math.Ceiling(value);
        if (seconds < SecondsPerMinute) return new TransferEta.Remaining(seconds, TransferEtaUnit.Seconds);

        var minutes = (int)Math.Ceiling(value / SecondsPerMinute);
        if (minutes < MinutesPerHour) return new TransferEta.Remaining(minutes, TransferEtaUnit.Minutes);

        return new TransferEta.Remaining((int)Math.Ceiling(value / SecondsPerHour), TransferEtaUnit.Hours);
    }
}

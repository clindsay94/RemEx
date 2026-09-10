namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// Smooths observed progress into a throughput figure and a time-remaining figure (RemEx-x5qd1).
/// </summary>
/// <remarks>
/// <para>
/// **PORTED AS A SHAPE, NOT AS SHARED CODE**, from <c>remex.android</c>'s
/// <c>TransferRateEstimator.kt</c> — the same approach as <see cref="TransferProgressFormat"/> and
/// as <c>VolumesResponseClassifier</c> before it. What transfers is the arithmetic and the refusals,
/// each of which the Kotlin records a reason for.
/// </para>
/// <para>
/// THE PRODUCER FOR A CLASSIFIER THAT HAD NONE. RemEx-kh6p4 landed the half that turns a number into
/// a unit and an amount; nothing on the PC produced the number. Together they are the whole
/// arithmetic of RemEx-4lcq, which keeps the words.
/// </para>
/// <para>
/// **THE ANDROID VOLATILE FIELDS ARE DELIBERATELY NOT PORTED.** There they answer a specific
/// arrangement — the transfer engine writes on an IO dispatcher while the notification and the queue
/// row read from Main. The PC's threading is not that, and inventing a concurrency story for a type
/// nothing calls yet would be guessing. Whoever wires this up owns that question; if it ends up read
/// and written from different threads, this needs the same treatment.
/// </para>
/// </remarks>
public sealed class TransferRateEstimator
{
    /// <summary>A rate this low is indistinguishable from stalled.</summary>
    /// <remarks>
    /// Slow enough that a genuinely crawling transfer still reports, fast enough that a dead one does
    /// not produce a number measured in days: one byte per second puts a 4 GB transfer at 136 years.
    /// </remarks>
    public const double MinimumMeaningfulBytesPerSecond = 1024.0;

    /// <summary>Time constants of silence after which the estimate stops describing anything.</summary>
    /// <remarks>
    /// DERIVED RATHER THAN PICKED: after four of them the exponential weighting has forgotten about
    /// 98% of what the figure was built from. At the default time constant that is twenty seconds —
    /// long enough that an ordinary gap between chunks does not blank the display, short enough that
    /// a dead transfer stops claiming a speed while the user is still watching.
    /// </remarks>
    public const double StaleTimeConstants = 4.0;

    private readonly double _timeConstantSeconds;

    private long? _lastTimestampMillis;
    private long _lastBytes;
    private double? _smoothedBytesPerSecond;

    /// <param name="timeConstantSeconds">
    /// How quickly the estimate forgets. Larger is smoother and slower to react. Five seconds is
    /// chosen so a momentary stall does not blank the display while a genuine slowdown still shows
    /// within a few seconds, and it is the number most worth tuning against a real transfer.
    /// </param>
    public TransferRateEstimator(double timeConstantSeconds = 5.0) =>
        _timeConstantSeconds = timeConstantSeconds;

    private double StaleAfterMillis => StaleTimeConstants * _timeConstantSeconds * 1000.0;

    /// <summary>Feeds a progress observation.</summary>
    /// <param name="transferredBytes">Total transferred so far, NOT a delta.</param>
    /// <param name="timestampMillis">
    /// A MONOTONIC clock reading, not wall-clock time: a wall clock can step backwards or jump on a
    /// time sync, and a negative interval turns into a negative or infinite rate.
    /// </param>
    public void Update(long transferredBytes, long timestampMillis)
    {
        var previousTime = _lastTimestampMillis;
        var previousBytes = _lastBytes;

        _lastTimestampMillis = timestampMillis;
        _lastBytes = transferredBytes;

        // A first observation establishes a baseline and nothing else — there is no interval to
        // measure a rate over, and inventing one from zero would report an absurd initial speed.
        if (previousTime is not { } previous) return;

        var elapsedSeconds = (timestampMillis - previous) / 1000.0;

        // Two samples in the same millisecond, or a clock that went backwards. Either way there is no
        // rate to compute and dividing would produce infinity.
        if (elapsedSeconds <= 0.0) return;

        var deltaBytes = transferredBytes - previousBytes;

        // BYTES GOING BACKWARDS MEANS A RESTART, NOT NEGATIVE THROUGHPUT. A retried transfer resets
        // its counter, and carrying the old average across that boundary would describe a transfer
        // that no longer exists. Drop the estimate and re-establish from here.
        if (deltaBytes < 0)
        {
            _smoothedBytesPerSecond = null;
            return;
        }

        var instantRate = deltaBytes / elapsedSeconds;

        _smoothedBytesPerSecond = _smoothedBytesPerSecond is not { } previousRate
            ? instantRate
            : previousRate + ((1.0 - Math.Exp(-elapsedSeconds / _timeConstantSeconds)) * (instantRate - previousRate));
    }

    /// <summary>The throughput as of <paramref name="nowMillis"/>, or null when nothing recent is known.</summary>
    /// <remarks>
    /// **TAKES A CLOCK BECAUSE A STALL IS SILENCE, AND SILENCE CANNOT CALL <see cref="Update"/>.**
    /// The estimator only advances when a progress observation arrives, so when bytes stop the last
    /// figure would otherwise sit on screen indefinitely while the ETA quietly became fiction. A user
    /// watching "12.4 MB/s, 38 seconds left" on a transfer that died five minutes ago is being
    /// actively misinformed, which is worse than the percentage-only display this replaces.
    /// </remarks>
    public double? BytesPerSecondAt(long nowMillis)
    {
        if (_lastTimestampMillis is not { } last) return null;
        if (_smoothedBytesPerSecond is not { } rate) return null;

        return nowMillis - last > StaleAfterMillis ? null : rate;
    }

    /// <summary>Seconds until the transfer finishes as of <paramref name="nowMillis"/>, or null.</summary>
    /// <remarks>
    /// Reads THROUGH <see cref="BytesPerSecondAt"/>, so speed and time-remaining go blank together.
    /// Two figures that disagreed about whether the transfer was still alive would be worse than
    /// either alone.
    /// </remarks>
    public double? SecondsRemainingAt(long transferredBytes, long? totalBytes, long nowMillis) =>
        BytesPerSecondAt(nowMillis) is null ? null : SecondsRemaining(transferredBytes, totalBytes);

    /// <summary>Seconds until the transfer finishes, ignoring staleness.</summary>
    /// <remarks>
    /// NULL IS A REAL ANSWER AND MUST BE SHOWN AS ONE. It means "not known yet" or "stalled", and the
    /// caller has to say something like "calculating" rather than render a placeholder number. A
    /// stalled transfer with a rate approaching zero yields an ETA approaching infinity, and showing
    /// "14 hours remaining" that then jumps to "30 seconds" is worse than showing nothing — it is a
    /// number the user will plan around.
    /// </remarks>
    internal double? SecondsRemaining(long transferredBytes, long? totalBytes)
    {
        if (_smoothedBytesPerSecond is not { } rate) return null;
        if (totalBytes is not { } total || total <= 0L) return null;

        var remaining = total - transferredBytes;
        if (remaining <= 0L) return 0.0;

        if (rate < MinimumMeaningfulBytesPerSecond) return null;

        return remaining / rate;
    }

    /// <summary>Forgets the estimate without discarding the byte baseline — used when a transfer pauses.</summary>
    public void ResetRate()
    {
        _smoothedBytesPerSecond = null;
        _lastTimestampMillis = null;
    }
}

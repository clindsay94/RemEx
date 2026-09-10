namespace Remex.Core.Services;

/// <summary>
/// Smooths ping/pong round trips into a latency figure (RemEx-s2ksi).
/// </summary>
/// <remarks>
/// <para>
/// **THE MEASUREMENT EXISTED ON THE WIRE AND NOTHING READ IT.** The client stamps a ping and the
/// host echoes the stamp back with a comment saying so — for a consumer that did not exist, so the
/// round trip the timestamp was put there for was never computed.
/// </para>
/// <para>
/// EXPONENTIAL SMOOTHING, THE SAME SHAPE THE TRANSFER RATE ESTIMATOR USES, so a single slow reply
/// does not spike the figure and a genuine change still shows within a few samples. The time
/// constant is expressed in SAMPLES rather than seconds because pings arrive on their own schedule
/// and a latency figure has no meaning between them.
/// </para>
/// <para>
/// **BOTH ENDS OF THIS MEASUREMENT ARE TAKEN ON THE SAME MACHINE FROM A WALL CLOCK**, which is what
/// makes the refusals below necessary rather than defensive. <c>DateTime.UtcNow</c> can step
/// backwards on an NTP correction or a manual change, and a step landing between send and receive
/// corrupts exactly one sample — possibly into a negative number. Averaging that in would move the
/// figure for every later sample. Rejecting it costs one ping.
/// </para>
/// </remarks>
public sealed class RoundTripTracker
{
    /// <summary>Beyond this a sample is a clock artefact rather than a slow network.</summary>
    /// <remarks>
    /// A real round trip over a bad link is seconds, not minutes. Sixty seconds is far enough above
    /// anything a working connection produces that refusing it cannot discard a genuine reading, and
    /// low enough to catch a clock that jumped.
    /// </remarks>
    public const double MaximumPlausibleMilliseconds = 60_000.0;

    private readonly double _smoothingSamples;

    private double? _smoothedMilliseconds;

    /// <param name="smoothingSamples">
    /// How many samples the average effectively remembers. Larger is steadier and slower to react.
    /// </param>
    public RoundTripTracker(double smoothingSamples = 5.0) => _smoothingSamples = smoothingSamples;

    /// <summary>The smoothed round-trip time, or null until a usable sample has arrived.</summary>
    /// <remarks>
    /// NULL IS A REAL ANSWER. It means nothing has been measured yet, and a caller must say so
    /// rather than render a zero — "0 ms" reads as an impossibly good connection.
    /// </remarks>
    public double? RoundTripMilliseconds => _smoothedMilliseconds;

    /// <summary>The most recent accepted sample, for callers that want the raw figure.</summary>
    public double? LastSampleMilliseconds { get; private set; }

    /// <summary>Number of samples refused as implausible. Diagnostic only.</summary>
    /// <remarks>
    /// Counted rather than logged, because the interesting signal is a RATE of refusals — a clock
    /// stepping once is noise, and a machine whose clock is being corrected constantly is a reason
    /// the latency figure should not be trusted at all.
    /// </remarks>
    public int RefusedSamples { get; private set; }

    /// <summary>Feeds one completed round trip.</summary>
    /// <param name="milliseconds">Receive time minus the echoed send stamp.</param>
    /// <returns>True when the sample was used.</returns>
    public bool Observe(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0.0 || milliseconds > MaximumPlausibleMilliseconds)
        {
            RefusedSamples++;
            return false;
        }

        LastSampleMilliseconds = milliseconds;

        _smoothedMilliseconds = _smoothedMilliseconds is not { } previous
            ? milliseconds
            : previous + ((milliseconds - previous) / _smoothingSamples);

        return true;
    }

    /// <summary>Forgets the average, used when the connection is replaced.</summary>
    /// <remarks>
    /// Carrying a latency figure across a reconnect would describe a link that no longer exists —
    /// the same reasoning that makes the transfer rate estimator drop its average when a transfer's
    /// byte counter goes backwards.
    /// </remarks>
    public void Reset()
    {
        _smoothedMilliseconds = null;
        LastSampleMilliseconds = null;
    }
}

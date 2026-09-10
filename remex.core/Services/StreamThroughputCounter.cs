namespace Remex.Core.Services;

/// <summary>
/// Counts encoded frames and bytes, and reports the rate since the last sample (RemEx-93n2).
/// </summary>
/// <remarks>
/// <para>
/// **COUNTING ONLY. NOTHING HERE DECIDES WHEN ANYONE IS TOLD.** That separation is the finding this
/// type exists because of: the bead's scope line reads "count encoded bytes/frames per second ... and
/// include them in desktop_meta", and desktop_meta is sent from exactly two places, both
/// EVENT-DRIVEN — once at stream bootstrap and once on a geometry change. Nothing sends it on a
/// timer. So "per second, in desktop_meta" resolves to either making desktop_meta periodic — a
/// second timed emitter beside the frame pacer, which is precisely the shape
/// <c>docs/REGRESSION-GUARDS.md</c> warns away from — or attaching the numbers to messages nobody is
/// looking at. How often a quality meter wants to sample is a property of the meter (RemEx-grc5),
/// not of the counter, and it fell through the gap between the two beads.
/// </para>
/// <para>
/// **THE RATE IS MEASURED, NOT ASSUMED.** Dividing by a nominal frame interval would report the rate
/// the stream was CONFIGURED for rather than the one it achieved, which is the opposite of useful in
/// a quality meter — the whole point is to notice when they differ. Elapsed time comes from the
/// caller's clock so a test can advance it exactly.
/// </para>
/// <para>
/// Sampling RESETS the window. A caller that samples twice in quick succession gets the second
/// interval's rate, not a running average dominated by whatever came before — which is what a meter
/// wants and what a total would not give it.
/// </para>
/// </remarks>
public sealed class StreamThroughputCounter
{
    private long _framesSinceSample;
    private long _bytesSinceSample;
    private DateTime _windowStart;

    // INTERLOCKED BECAUSE THE SAMPLER WILL NOT BE ON THE PRODUCER'S THREAD. Add runs on the capture
    // task; the obvious place to sample is the existing five-second metrics block, which runs on the
    // SEND loop. Sample is a read-then-reset, so an unsynchronised version loses every frame counted
    // between the read and the store - intermittently, unreproducibly, and with no log line. The
    // enclosing handler already has that latent race on its own frame totals; copying it here would
    // have been inheriting a bug rather than a precedent.

    private long _totalFrames;
    private long _totalBytes;

    /// <summary>Frames counted since construction, across every window.</summary>
    public long TotalFrames => Interlocked.Read(ref _totalFrames);

    /// <summary>Encoded bytes counted since construction, across every window.</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public StreamThroughputCounter(DateTime start) => _windowStart = start;

    /// <summary>Records one encoded frame of <paramref name="byteCount"/> bytes.</summary>
    /// <remarks>
    /// Zero is accepted and counted as a frame, but **THE CURRENT CALLER CANNOT PRODUCE ONE** - it
    /// counts inside its own <c>!frameBytes.IsEmpty</c> guard, so encoder-warmup frames that produce
    /// no output are excluded. That is a known limitation rather than a decision: a rate computed
    /// only over frames that produced bytes reads NORMAL across a warmup gap, which is the one
    /// moment a quality meter should be showing something. RemEx-grc5 should decide whether it wants
    /// those frames before relying on the rate; this type is ready either way.
    /// </remarks>
    public void Add(int byteCount)
    {
        if (byteCount < 0) return;

        Interlocked.Increment(ref _framesSinceSample);
        Interlocked.Add(ref _bytesSinceSample, byteCount);
        Interlocked.Increment(ref _totalFrames);
        Interlocked.Add(ref _totalBytes, byteCount);
    }

    /// <summary>
    /// The rate over the window that just ended, and starts a new one.
    /// </summary>
    /// <returns>Frames and bytes per second, both zero when no time has passed.</returns>
    /// <param name="now">
    /// **MUST COME FROM A MONOTONIC SOURCE.** Nothing here needs calendar semantics, and wall clock
    /// has a failure this bead has already been bitten by once: the RTT half of RemEx-93n2 records
    /// that an NTP step can make a wall-clock interval negative. A forward step reports a near-zero
    /// bitrate for one window; a backward step trips the guard above. No test can catch it, because
    /// an injected clock is ideal by construction.
    /// </param>
    /// <remarks>
    /// **A NON-ADVANCING CLOCK RETURNS ZERO RATHER THAN DIVIDING BY IT.** Two samples inside the same
    /// tick of a coarse clock is not exotic — it is what a caller polling faster than the timer
    /// resolution does — and an infinity propagated into a meter is worse than a zero, because a
    /// zero reads as "no data yet" and an infinity reads as a measurement.
    /// </remarks>
    public (double FramesPerSecond, double BytesPerSecond) Sample(DateTime now)
    {
        // THE GUARD COMES FIRST, WHICH IS NOT WHERE IT STARTED. Draining before it threw the
        // accumulated counts away on a sub-tick sample - correct return value, silently lost data.
        // At 120 FPS a 15 ms Windows tick holds one or two frames, and they belonged to the next
        // window, not to the bin. The original test asserted the (0,0) and never that the counts
        // survived, so it passed either way.
        var seconds = (now - _windowStart).TotalSeconds;
        if (seconds <= 0) return (0, 0);

        _windowStart = now;
        var frames = Interlocked.Exchange(ref _framesSinceSample, 0);
        var bytes = Interlocked.Exchange(ref _bytesSinceSample, 0);

        return (frames / seconds, bytes / seconds);
    }
}

using System.Diagnostics;

namespace Remex.Agent.Services.RemoteDesktop;

/// <summary>
/// High-accuracy tick pacer that beats the Windows OS timer floor (~15.6 ms).
///
/// REGRESSION GUARD — single source of truth for remote-desktop stream/cursor pacing. A bare
/// <c>Task.Delay</c> rounds UP to the OS timer resolution (~15.6 ms on Windows): an 8.33 ms wait
/// (120 FPS) oversleeps to ~15.6 ms (capping near 60 FPS), and an 11 ms wait (90 Hz cursor)
/// oversleeps to ~15.6 ms (capping near 64 Hz). To hit the real target we coarse-sleep the bulk of
/// the interval with <c>Task.Delay</c>, then busy-spin the final few ms with <c>Thread.SpinWait</c>
/// for sub-millisecond accuracy. Fully localized — no global <c>timeBeginPeriod</c> — so it carries
/// to Linux pacing too. If you reintroduce a plain <c>Task.Delay</c> in a stream/cursor loop, the
/// frame rate / cursor Hz will SILENTLY regress; keep the timing here.
/// (See docs/REMOTE_DESKTOP_PERFORMANCE.md.)
///
/// Uses an absolute timeline so a per-tick overrun shortens the FOLLOWING wait instead of
/// accumulating drift. Call <see cref="Reset"/> after a pause/backoff so the loop doesn't burst
/// through a backlog of "missed" ticks on recovery.
/// </summary>
internal sealed class PrecisionPacer
{
    // Coarse-sleep everything except the last ~16 ms, then spin. 16 ms safely exceeds the OS timer
    // granularity so the Task.Delay portion never oversleeps past the target.
    private const double SpinMarginMs = 16.0;

    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private double _nextTickMs;

    /// <summary>
    /// Waits until the next tick, <paramref name="intervalMs"/> after the previous target. Returns
    /// <c>false</c> if cancellation was requested during the wait (caller should break), <c>true</c>
    /// otherwise. Never throws on cancellation.
    /// </summary>
    public async Task<bool> WaitForNextTickAsync(double intervalMs, CancellationToken ct)
    {
        _nextTickMs += intervalMs;

        double remainingMs = _nextTickMs - _timer.Elapsed.TotalMilliseconds;
        if (remainingMs > SpinMarginMs)
        {
            try { await Task.Delay((int)(remainingMs - SpinMarginMs), ct); }
            catch (OperationCanceledException) { return false; }
        }

        // SpinWait avoids both the OS sleep floor and a hot 100% loop. If the loop already overran
        // (remaining <= 0), the condition is false and we return immediately without spinning.
        while (!ct.IsCancellationRequested && _timer.Elapsed.TotalMilliseconds < _nextTickMs)
        {
            Thread.SpinWait(64);
        }

        return !ct.IsCancellationRequested;
    }

    /// <summary>
    /// Re-anchors the timeline to "now" so the next tick is a full interval away. Call after a
    /// backoff/pause to avoid bursting through missed ticks on recovery.
    /// </summary>
    public void Reset() => _nextTickMs = _timer.Elapsed.TotalMilliseconds;
}

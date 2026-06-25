using System;

namespace Remex.Agent.Services.ScreenCapture;

/// <summary>
/// Rate-limits DXGI Desktop Duplication re-initialization (the <c>DuplicateOutput</c> call).
///
/// <para>
/// When a display powers off (idle sleep), a fullscreen exclusive app toggles, or the session switches
/// to a secure desktop, <c>AcquireNextFrame</c> returns <c>DXGI_ERROR_ACCESS_LOST</c> — often on every
/// frame for the whole duration of the transition. Re-creating the duplication on each of those losses
/// calls <c>DuplicateOutput</c> at the stream's full frame rate (up to 120 Hz) against a display that is
/// mid power-state transition. On some NVIDIA drivers that storm wedges DWM (<c>dwmredir.dll</c>) and the
/// kernel display driver, hard-locking the host to a black screen with only the mouse cursor visible
/// (RemEx-crk).
/// </para>
///
/// <para>
/// This throttle caps re-initialization to at most one attempt per backoff window and escalates the
/// window on each consecutive loss, so a long display-off period pokes the driver a handful of times
/// rather than thousands. A confirmed-healthy frame (a real frame, or a no-change <c>WAIT_TIMEOUT</c> —
/// both prove the output is alive) resets it, so a one-off loss (UAC prompt, resolution change) still
/// recovers within the base interval.
/// </para>
///
/// <para>
/// Pure and clock-injected (callers pass <see cref="DateTime.UtcNow"/>) so the policy is unit-testable
/// without a GPU or a real display. Not internally synchronized: callers serialize access via the
/// capture lock, exactly as they already do around <c>DuplicateOutput</c> itself.
/// </para>
/// </summary>
internal sealed class DuplicationReinitThrottle
{
    private readonly TimeSpan _baseBackoff;
    private readonly TimeSpan _maxBackoff;

    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private int _consecutiveReinits;

    /// <param name="baseBackoff">Minimum interval between re-init attempts. Applied to the first loss.</param>
    /// <param name="maxBackoff">Ceiling the escalating backoff saturates at. Must be ≥ <paramref name="baseBackoff"/>.</param>
    public DuplicationReinitThrottle(TimeSpan baseBackoff, TimeSpan maxBackoff)
    {
        if (baseBackoff <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseBackoff), "Base backoff must be positive.");
        if (maxBackoff < baseBackoff)
            throw new ArgumentOutOfRangeException(nameof(maxBackoff), "Max backoff must be >= base backoff.");

        _baseBackoff = baseBackoff;
        _maxBackoff = maxBackoff;
    }

    /// <summary>Consecutive re-init attempts since the last confirmed-healthy frame (diagnostics/tests).</summary>
    public int ConsecutiveReinits => _consecutiveReinits;

    /// <summary>True once the current backoff window has elapsed and another attempt is allowed.</summary>
    public bool ShouldAttempt(DateTime nowUtc) => nowUtc >= _nextAttemptUtc;

    /// <summary>
    /// Record that a re-init attempt is being made now. Advances the next-allowed time by the
    /// (escalating) backoff <em>regardless of whether the attempt ultimately succeeds</em> — a re-init
    /// that succeeds but whose output is immediately lost again on the next frame must NOT be allowed to
    /// retry at full rate. Call only after <see cref="ShouldAttempt"/> has returned true.
    /// </summary>
    public void RecordAttempt(DateTime nowUtc)
    {
        _consecutiveReinits++;
        _nextAttemptUtc = nowUtc + BackoffFor(_consecutiveReinits);
    }

    /// <summary>
    /// Record that the duplication produced a healthy result (a real frame or a no-change timeout),
    /// proving the output is back. Clears the escalation and the backoff window so the next genuine loss
    /// recovers promptly.
    /// </summary>
    public void RecordHealthyFrame()
    {
        _consecutiveReinits = 0;
        _nextAttemptUtc = DateTime.MinValue;
    }

    private TimeSpan BackoffFor(int consecutive)
    {
        if (consecutive <= 1)
            return _baseBackoff;

        // Exponential: base * 2^(consecutive-1), saturating at _maxBackoff. Computed in ticks with the
        // saturation checked before the cast so a large shift can't overflow a long en route to the clamp.
        double factor = Math.Pow(2, consecutive - 1);
        double ticks = _baseBackoff.Ticks * factor;
        return ticks >= _maxBackoff.Ticks ? _maxBackoff : TimeSpan.FromTicks((long)ticks);
    }
}

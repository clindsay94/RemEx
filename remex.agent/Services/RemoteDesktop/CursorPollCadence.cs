namespace Remex.Agent.Services.RemoteDesktop;

/// <summary>
/// Tick cadence for the remote-desktop cursor loop: 90 Hz while the cursor is live, 30 Hz once it
/// has sat still for about half a second, and a wall-clock ~10 Hz "slow tick" for the ClipCursor
/// re-apply and animated-cursor shape sync that runs the same at either rate.
/// </summary>
/// <remarks>
/// <para>
/// WHY SLOW THE TICK RATHER THAN THIN THE BODY (P1-11). The idle loop body is two cheap polls
/// (<c>GetCursorPosition</c>, the hCursor handle) that already skip the send and the shape capture
/// when nothing changed. What the cursor loop actually costs is <see cref="PrecisionPacer"/>'s ~2 ms
/// busy-spin tail, paid once per tick no matter what the body did: ~18% of a core at 90 Hz for as
/// long as a stream is open. Only fewer ticks reduce that; at 30 Hz it is a third of the cost.
/// </para>
/// <para>
/// NO <c>Reset()</c> ON A RATE CHANGE. <see cref="PrecisionPacer.WaitForNextTickAsync"/> takes the
/// interval per call and steps its absolute timeline by exactly that much, and the loop never
/// pauses between ticks here, so the timeline is always current: switching 90 Hz -> 30 Hz just puts
/// the next target 33 ms out, and 30 Hz -> 90 Hz puts it 11 ms out. There is no backlog to burst
/// through, and a Reset() here would not sit under an awaited backoff (see PacerResetOrderingTests).
/// </para>
/// <para>
/// SNAP-BACK. Any activity (movement, a new cursor handle, a shape change) returns the very next
/// interval to 90 Hz, so the worst case is the FIRST movement after a still period being seen up to
/// one idle interval (33 ms) late; everything after it streams at full rate.
/// </para>
/// <para>
/// The slow tick is wall-clock, not a tick count, because a count would drop the ClipCursor
/// re-apply to ~3 Hz whenever the cursor idles — and Windows releases the clip on
/// display/desktop/foreground changes whether or not the pointer is moving.
/// </para>
/// </remarks>
internal sealed class CursorPollCadence
{
    /// <summary>Tick interval while the cursor is moving or recently moved (~90 Hz).</summary>
    public const double ActiveIntervalMs = 1000.0 / 90.0;

    /// <summary>Tick interval once the cursor has been idle (~30 Hz).</summary>
    public const double IdleIntervalMs = 1000.0 / 30.0;

    /// <summary>Consecutive quiet active-rate ticks before slowing down (~0.5 s at 90 Hz).</summary>
    public const int IdleTicksBeforeSlowdown = 45;

    /// <summary>Wall-clock period of the ClipCursor re-apply / animated-shape sync (~10 Hz).</summary>
    public const double SlowTickIntervalMs = 100.0;

    /// <summary>
    /// Half an active tick of slack, so a slow tick falls on the 9th 90 Hz tick (99.99 ms) or the 3rd
    /// 30 Hz tick despite floating-point and sub-millisecond pacing jitter, and never a tick early.
    /// </summary>
    private const double SlowTickToleranceMs = ActiveIntervalMs / 2.0;

    private int _quietTicks;
    private double _lastSlowTickMs = double.NegativeInfinity;

    /// <summary>Whether the loop is currently ticking at the idle rate.</summary>
    public bool IsIdle => _quietTicks >= IdleTicksBeforeSlowdown;

    /// <summary>
    /// Returns <c>true</c> (and consumes it) when a slow tick is due at <paramref name="nowMs"/>.
    /// The first call always returns <c>true</c>.
    /// </summary>
    public bool TryConsumeSlowTick(double nowMs)
    {
        if (nowMs - _lastSlowTickMs < SlowTickIntervalMs - SlowTickToleranceMs) return false;

        _lastSlowTickMs = nowMs;
        return true;
    }

    /// <summary>Makes the next <see cref="TryConsumeSlowTick"/> fire, e.g. after a display-off pause.</summary>
    public void ForceSlowTick() => _lastSlowTickMs = double.NegativeInfinity;

    /// <summary>
    /// Records whether this tick saw activity and returns the interval to wait before the next one.
    /// </summary>
    public double NextIntervalMs(bool activity)
    {
        if (activity)
        {
            _quietTicks = 0;
        }
        else if (_quietTicks < IdleTicksBeforeSlowdown)
        {
            _quietTicks++;
        }

        return IsIdle ? IdleIntervalMs : ActiveIntervalMs;
    }
}

namespace Remex.Core.Validation;

/// <summary>
/// Numeric guards for untrusted pointer/coordinate samples arriving from a network client.
/// Pure math, so it is safe to use from the NativeAOT-compiled <c>Remex.Core</c> surface.
///
/// Network-facing <see cref="float"/> samples can be NaN, ±Infinity, or wildly out of range. Casting
/// such a value straight to <see cref="int"/> wraps to an arbitrary integer (e.g. <c>(int)float.NaN</c>
/// is <see cref="int.MinValue"/> on most runtimes), which would let a hostile sample drive the mouse to
/// an arbitrary coordinate. These helpers reject non-finite values and clamp to a sane range before the
/// cast (RD-8 / RemEx-q6u).
/// </summary>
public static class CoordinateValidation
{
    /// <summary>
    /// Clamps an absolute coordinate to the pixel range <c>[0, maxExclusive - 1]</c>. A non-finite
    /// value (NaN/±Infinity) or a non-positive bound maps to <c>0</c> (the top-left origin).
    /// </summary>
    public static int ClampAbsolute(float value, int maxExclusive)
    {
        if (!float.IsFinite(value) || maxExclusive <= 0)
        {
            return 0;
        }

        return (int)Math.Clamp(value, 0f, maxExclusive - 1);
    }

    /// <summary>
    /// Clamps an absolute coordinate to <c>[minInclusive, maxExclusive - 1]</c>. Unlike
    /// <see cref="ClampAbsolute(float,int)"/> this supports a non-zero — possibly NEGATIVE — origin, so
    /// a monitor positioned left of / above the primary (which has negative virtual-desktop
    /// coordinates) stays reachable. A non-finite value (NaN/±Infinity) or an empty range maps to
    /// <paramref name="minInclusive"/> (a safe in-range default).
    ///
    /// REGRESSION GUARD (RD-D): negative virtual-desktop coordinates are VALID. Do not "simplify" this
    /// back to a 0-floored clamp — that silently strands the cursor at x=0 and makes left/top monitors
    /// unreachable. See docs/REMOTE_DESKTOP_PERFORMANCE.md.
    /// </summary>
    public static int ClampToRange(float value, int minInclusive, int maxExclusive)
    {
        if (!float.IsFinite(value) || maxExclusive <= minInclusive)
        {
            return minInclusive;
        }

        return (int)Math.Clamp(value, minInclusive, maxExclusive - 1);
    }

    /// <summary>
    /// Clamps a relative delta to <c>[-maxMagnitude, maxMagnitude]</c>. A non-finite value maps to
    /// <c>0</c> (no movement). A negative <paramref name="maxMagnitude"/> is treated as <c>0</c>.
    /// </summary>
    public static int ClampDelta(float value, int maxMagnitude)
    {
        if (!float.IsFinite(value) || maxMagnitude <= 0)
        {
            return 0;
        }

        return (int)Math.Clamp(value, -maxMagnitude, (float)maxMagnitude);
    }

    /// <summary>
    /// The largest scroll magnitude a single <c>mouseScroll</c> event may carry: ten wheel detents,
    /// in the protocol's 120-units-per-detent encoding.
    /// </summary>
    /// <remarks>
    /// Not an arbitrary number — it is what <c>LinuxInputSimulationService</c>'s two scroll branches
    /// already saturate at, so on those this bound is invisible. It exists for the two that did NOT
    /// bound anything: Windows passes the value straight to <c>MOUSEEVENTF_WHEEL</c>, and the Linux
    /// backend router's xdotool fallback spawned one process per detent with no ceiling at all.
    /// Real gestures are nowhere near it — the Android mouse pad sends ±100 per tap at default
    /// sensitivity and ±500 at its maximum, and the remote-desktop surface sends an accumulated
    /// per-frame remainder that needs more than 160px of two-finger travel in a single event to
    /// reach this bound.
    /// </remarks>
    public const int MaxScrollDelta = 120 * 10;

    /// <summary>
    /// Clamps an untrusted <c>mouseScroll</c> delta to <see cref="MaxScrollDelta"/> in either
    /// direction. An absent value is <c>0</c> — no scrolling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS EXISTS BECAUSE AN UNCLAMPED DELTA COULD PERMANENTLY DISABLE INPUT (RemEx-hnin). A client
    /// sending <see cref="int.MinValue"/> reached <c>Math.Abs</c> in two of the Linux scroll paths,
    /// which has no representable result and throws rather than saturating. What made that fatal is
    /// specific to the REMOTE-DESKTOP dispatcher: <c>RemoteDesktopHandler.DispatchInput</c> catches
    /// <c>Win32Exception</c>, <c>InvalidOperationException</c> and <c>ArgumentException</c> — not
    /// <c>OverflowException</c> — so it escaped into the session's single long-running input thread
    /// and ended its consuming loop. That thread is started once in the handler's constructor and is
    /// never restarted, so every subsequent mouse and keyboard event for that session was dropped on
    /// the floor while the video kept streaming: a desktop that looks live and ignores you. The
    /// faulted task is then swallowed at teardown by a <c>catch (AggregateException)</c> commented
    /// "expected", so nothing named the cause either. <c>PingPongHandler</c> dispatches inline behind
    /// a <c>catch (Exception)</c>, so there the same throw cost one event rather than the session —
    /// which is why the containment gap is filed separately as RemEx-q4wm.
    /// </para>
    /// <para>
    /// A magnitude bound rather than an <c>int.MinValue</c> special case, because the overflow was
    /// only the loudest symptom. A merely huge delta made the Linux backend router's xdotool
    /// fallback — <c>Math.Max(1, Math.Abs(delta) / 120)</c>, with no upper clamp — spawn one
    /// <c>xdotool</c> process per detent, so a single message could ask for millions of them.
    /// </para>
    /// <para>
    /// Deliberately NOT applied to the relative-move use of the same <c>deltaX</c>/<c>deltaY</c>
    /// fields: no backend takes the absolute value of those, so nothing there overflows, and pixels
    /// are not detents so this bound would be meaningless for them.
    /// </para>
    /// </remarks>
    public static int ClampScrollDelta(int? value) =>
        value is null ? 0 : Math.Clamp(value.Value, -MaxScrollDelta, MaxScrollDelta);
}

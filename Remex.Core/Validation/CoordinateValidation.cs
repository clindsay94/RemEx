using System;

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
}

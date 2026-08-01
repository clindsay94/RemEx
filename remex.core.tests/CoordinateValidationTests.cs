using Remex.Core.Validation;

namespace Remex.Core.Tests;

// Regression tests for RD-8 (RemEx-q6u): untrusted pointer-sample floats must be rejected/clamped
// before any (int) cast so a hostile NaN/Infinity/out-of-range value cannot wrap into an arbitrary
// MoveMouse coordinate.
public class CoordinateValidationTests
{
    [Theory]
    [InlineData(float.NaN, 1920, 0)]
    [InlineData(float.PositiveInfinity, 1920, 0)]
    [InlineData(float.NegativeInfinity, 1920, 0)]
    [InlineData(-50f, 1920, 0)]      // below origin clamps to 0
    [InlineData(0f, 1920, 0)]        // legitimate top-left origin is preserved
    [InlineData(960f, 1920, 960)]    // in-range passes through
    [InlineData(5000f, 1920, 1919)]  // above bound clamps to max-exclusive - 1
    [InlineData(1920f, 1920, 1919)]  // exactly the width clamps to the last valid pixel
    public void ClampAbsolute_RejectsNonFiniteAndClampsToBounds(float value, int maxExclusive, int expected)
    {
        Assert.Equal(expected, CoordinateValidation.ClampAbsolute(value, maxExclusive));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ClampAbsolute_NonPositiveBound_ReturnsZero(int maxExclusive)
    {
        Assert.Equal(0, CoordinateValidation.ClampAbsolute(123f, maxExclusive));
    }

    [Theory]
    [InlineData(float.NaN, 200, 0)]
    [InlineData(float.PositiveInfinity, 200, 0)]
    [InlineData(float.NegativeInfinity, 200, 0)]
    [InlineData(50f, 200, 50)]       // in-range passes through
    [InlineData(-50f, 200, -50)]     // negative deltas are valid
    [InlineData(5000f, 200, 200)]    // clamps to +maxMagnitude
    [InlineData(-5000f, 200, -200)]  // clamps to -maxMagnitude
    public void ClampDelta_RejectsNonFiniteAndClampsToMagnitude(float value, int maxMagnitude, int expected)
    {
        Assert.Equal(expected, CoordinateValidation.ClampDelta(value, maxMagnitude));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ClampDelta_NonPositiveMagnitude_ReturnsZero(int maxMagnitude)
    {
        Assert.Equal(0, CoordinateValidation.ClampDelta(123f, maxMagnitude));
    }

    // ── Scroll deltas (RemEx-hnin) ──────────────────────────────────────────────────────────────
    //
    // int.MinValue is the case with teeth, and its consequence is out of all proportion to its
    // size. Unclamped it reached Math.Abs in the Linux scroll backends, which throws rather than
    // saturating; OverflowException is not in the dispatcher's catch list, so it escaped into the
    // session's single input thread and ended its consuming loop for good. Nothing restarts that
    // thread, so one message disabled mouse and keyboard for the whole session while the video kept
    // streaming — and the faulted task is swallowed at teardown, so nothing named the cause.

    [Theory]
    [InlineData(int.MinValue, -CoordinateValidation.MaxScrollDelta)]
    [InlineData(int.MaxValue, CoordinateValidation.MaxScrollDelta)]
    [InlineData(-2_000_000_000, -CoordinateValidation.MaxScrollDelta)]
    public void ClampScrollDelta_PathologicalMagnitudesAreBounded(int value, int expected)
    {
        Assert.Equal(expected, CoordinateValidation.ClampScrollDelta(value));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(120, 120)]
    [InlineData(-120, -120)]
    [InlineData(500, 500)]
    [InlineData(-1199, -1199)]
    public void ClampScrollDelta_LeavesRealisticGesturesExactlyAlone(int value, int expected)
    {
        // The Android mouse pad sends ±100 per tap and the remote-desktop surface sends an
        // accumulated per-frame remainder; a bound that altered these would be a behaviour change
        // rather than a guard.
        Assert.Equal(expected, CoordinateValidation.ClampScrollDelta(value));
    }

    [Fact]
    public void ClampScrollDelta_AbsentMeansNoScrollRatherThanSomeDefault()
    {
        Assert.Equal(0, CoordinateValidation.ClampScrollDelta(null));
    }

    [Fact]
    public void ClampScrollDelta_BoundIsTenDetentsWhichIsWhatTheBackendsAlreadySaturateAt()
    {
        // Pinned as a number rather than left implicit: both Linux shell backends clamp to ten
        // detents internally, so this bound is what makes Windows — which passes the value straight
        // to MOUSEEVENTF_WHEEL — agree with them instead of scrolling proportionally to whatever
        // arrives. Raising it silently reintroduces that divergence.
        Assert.Equal(1200, CoordinateValidation.MaxScrollDelta);
        Assert.Equal(10, CoordinateValidation.MaxScrollDelta / 120);
    }
}

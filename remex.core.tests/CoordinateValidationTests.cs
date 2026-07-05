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
}

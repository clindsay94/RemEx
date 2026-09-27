using Remex.Core.Models;

namespace Remex.Core.Tests.Routines;

/// <summary>
/// The threshold rule moved to core for routines S5 (§8.5.1): the PC's sensor alerts and the
/// <c>pc.sensor</c> trigger share it, so they must agree to the decimal.
/// </summary>
public sealed class SensorThresholdTests
{
    [Theory]
    [InlineData(AlertDirection.Above, 85, 85.1, false, true)]
    [InlineData(AlertDirection.Above, 85, 85, false, false)]
    [InlineData(AlertDirection.Above, 85, 84, true, true)] // inside the 2% band (1.7) of a live breach
    [InlineData(AlertDirection.Above, 85, 83, true, false)]
    [InlineData(AlertDirection.Below, 10, 9.9, false, true)]
    [InlineData(AlertDirection.Below, 10, 10.1, true, true)]
    [InlineData(AlertDirection.Below, 10, 10.3, true, false)]
    [InlineData(AlertDirection.Above, 0, 0.1, true, true)] // a zero threshold has no band
    [InlineData(AlertDirection.Above, 0, 0, true, false)]
    public void IsLiveAppliesTheClearBandOnlyToALiveBreach(AlertDirection direction, double threshold, double value, bool wasLive, bool expected)
    {
        Assert.Equal(expected, SensorThreshold.IsLive(direction, threshold, value, wasLive));
    }

    [Fact]
    public void NaNIsNeverLive()
    {
        Assert.False(SensorThreshold.IsLive(AlertDirection.Above, 85, double.NaN, wasLive: true));
        Assert.False(SensorThreshold.IsLive(AlertDirection.Below, 85, double.NaN, wasLive: true));
    }
}

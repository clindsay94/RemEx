using System.Globalization;
using System.Text.RegularExpressions;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The PC estimates throughput and time-remaining the way the phone already does (RemEx-x5qd1).
/// </summary>
/// <remarks>
/// Ported as a shape from <c>remex.android</c>'s <c>TransferRateEstimator.kt</c>. These assertions
/// are its recorded decisions, not its implementation — every one of them is a case where the
/// obvious behaviour is the wrong one.
/// </remarks>
public class TransferRateEstimatorTests
{
    [Fact]
    public void AFirstObservationEstablishesABaselineAndNothingElse()
    {
        // There is no interval to measure a rate over, and inventing one from zero would report an
        // absurd initial speed on the first frame of every transfer.
        var estimator = new TransferRateEstimator();

        estimator.Update(transferredBytes: 1_000_000, timestampMillis: 0);

        Assert.Null(estimator.BytesPerSecondAt(0));
    }

    [Fact]
    public void ASteadyTransferConvergesOnItsActualRate()
    {
        // The first interval seeds the average outright, so a constant rate reports itself exactly
        // rather than easing in from zero.
        var estimator = new TransferRateEstimator();

        estimator.Update(0, 0);
        estimator.Update(1024 * 1024, 1000);

        Assert.Equal(1024 * 1024, estimator.BytesPerSecondAt(1000)!.Value, precision: 3);
    }

    [Fact]
    public void ASameMillisecondSampleIsRefusedRatherThanDividedBy()
    {
        // A non-positive interval yields infinity, which would render as an absurd speed.
        var estimator = new TransferRateEstimator();

        estimator.Update(0, 500);
        estimator.Update(5_000_000, 500);

        Assert.Null(estimator.BytesPerSecondAt(500));
    }

    [Fact]
    public void BytesGoingBACKWARDSDropTheEstimateRatherThanGoingNegative()
    {
        // A retried transfer resets its counter. Carrying the old average across that boundary would
        // describe a transfer that no longer exists, and a negative delta would render as a negative
        // speed.
        var estimator = new TransferRateEstimator();

        estimator.Update(0, 0);
        estimator.Update(2_000_000, 1000);
        Assert.NotNull(estimator.BytesPerSecondAt(1000));

        estimator.Update(0, 2000);

        Assert.Null(estimator.BytesPerSecondAt(2000));
    }

    [Fact]
    public void SILENCEBlanksTheRateRatherThanLeavingItOnScreen()
    {
        // THE DECISION THIS TYPE EXISTS FOR. The estimator only advances when an observation
        // arrives, so a dead transfer would otherwise keep reporting the last figure indefinitely.
        // "12.4 MB/s, 38 seconds left" on a transfer that died five minutes ago is worse than the
        // percentage-only display this replaces - it is actively misinforming.
        var estimator = new TransferRateEstimator(timeConstantSeconds: 5.0);

        estimator.Update(0, 0);
        estimator.Update(10_000_000, 1000);

        // Four time constants is twenty seconds; just inside it still reports.
        Assert.NotNull(estimator.BytesPerSecondAt(1000 + 20_000));
        Assert.Null(estimator.BytesPerSecondAt(1000 + 20_001));
    }

    [Fact]
    public void TimeRemainingGoesBlankWithTheRateRatherThanSeparately()
    {
        // Two figures disagreeing about whether the transfer is still alive would be worse than
        // either alone, so seconds-remaining reads THROUGH the clock-aware rate.
        var estimator = new TransferRateEstimator(timeConstantSeconds: 5.0);

        estimator.Update(0, 0);
        estimator.Update(10_000_000, 1000);

        Assert.NotNull(estimator.SecondsRemainingAt(10_000_000, 20_000_000, 1000));
        Assert.Null(estimator.SecondsRemainingAt(10_000_000, 20_000_000, 1000 + 20_001));
    }

    [Fact]
    public void AnUnknownTotalHasNoEta()
    {
        // A streamed source has no length, and no rate can produce an ETA without one.
        var estimator = new TransferRateEstimator();

        estimator.Update(0, 0);
        estimator.Update(1_000_000, 1000);

        Assert.Null(estimator.SecondsRemainingAt(1_000_000, null, 1000));
        Assert.Null(estimator.SecondsRemainingAt(1_000_000, 0, 1000));
    }

    [Fact]
    public void ACrawlingRateIsRefusedRatherThanTurnedIntoYears()
    {
        // One byte per second puts a 4 GB transfer at 136 years. A rate that low is indistinguishable
        // from stalled and any ETA derived from it is fiction.
        var estimator = new TransferRateEstimator();

        estimator.Update(0, 0);
        estimator.Update(100, 1000); // 100 B/s, under the 1 KB/s floor

        Assert.NotNull(estimator.BytesPerSecondAt(1000));
        Assert.Null(estimator.SecondsRemainingAt(100, 4_000_000_000, 1000));
    }

    [Fact]
    public void AFinishedTransferReportsZeroRatherThanNothing()
    {
        // Zero is a true answer here and null would read as "unknown", which is a different claim.
        var estimator = new TransferRateEstimator();

        estimator.Update(0, 0);
        estimator.Update(5_000_000, 1000);

        Assert.Equal(0.0, estimator.SecondsRemainingAt(5_000_000, 5_000_000, 1000));
    }

    [Fact]
    public void ResettingForgetsTheRateButNotThatItIsPaused()
    {
        var estimator = new TransferRateEstimator();

        estimator.Update(0, 0);
        estimator.Update(5_000_000, 1000);
        estimator.ResetRate();

        Assert.Null(estimator.BytesPerSecondAt(1000));
    }

    [Theory]
    [InlineData("MinimumMeaningfulBytesPerSecond", TransferRateEstimator.MinimumMeaningfulBytesPerSecond)]
    [InlineData("StaleTimeConstants", TransferRateEstimator.StaleTimeConstants)]
    public void TheTwoPlatformsAgreeOnTheConstantsTheyShare(string name, double expected)
    {
        // THE PARITY THAT MATTERS FOR A PORT, because a port drifts silently. If the phone lowers its
        // stall floor and the PC does not, the same transfer shows a time on one device and nothing
        // on the other, and neither side is obviously the wrong one to whoever is looking.
        var kotlin = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "remex.android", "app", "src", "main", "java", "com", "clindsay94", "remex",
            "service", "TransferRateEstimator.kt"));

        var declared = Regex.Match(kotlin, name + @":\s*Double\s*=\s*([0-9.]+)");

        Assert.True(declared.Success, $"the Kotlin {name} moved or changed shape; re-check the parity by hand");
        Assert.Equal(expected, double.Parse(declared.Groups[1].Value, CultureInfo.InvariantCulture));
    }
}

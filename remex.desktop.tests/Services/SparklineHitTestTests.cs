using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins the sparkline hover crosshair's sample lookup (RemEx-hf90).
/// </summary>
/// <remarks>
/// The failure mode here is not a wrong number - it is a chart that feels like it is lagging the
/// mouse, which reads as the app being slow rather than as an off-by-one.
/// </remarks>
public class SparklineHitTestTests
{
    private const int Samples = 11;   // 10 gaps
    private const double Width = 100; // step = 10

    [Fact]
    public void APointerOnASampleSelectsThatSample()
    {
        Assert.Equal(0, SparklineHitTest.NearestIndex(0, Samples, Width));
        Assert.Equal(5, SparklineHitTest.NearestIndex(50, Samples, Width));
        Assert.Equal(10, SparklineHitTest.NearestIndex(100, Samples, Width));
    }

    [Fact]
    public void ItSnapsToTheNEARESTSampleRatherThanTheOneOnTheLeft()
    {
        // THE BUG THIS EXISTS TO AVOID. The obvious (int)(x / step) truncates, biasing every lookup
        // LEFT - so the crosshair snaps to the sample the pointer has just passed rather than the
        // one it is closest to, and the user reads it as the chart trailing their mouse.
        //
        // At x=49 the nearest gridline is 50, not 40.
        Assert.Equal(5, SparklineHitTest.NearestIndex(49, Samples, Width));
        Assert.Equal(5, SparklineHitTest.NearestIndex(46, Samples, Width));
        Assert.Equal(4, SparklineHitTest.NearestIndex(44, Samples, Width));
    }

    [Fact]
    public void AnExactMidpointResolvesTheSameWayEveryTime()
    {
        // Banker's rounding would alternate between neighbours depending on parity, making the
        // crosshair jitter back and forth on a slow drag across the midpoint.
        Assert.Equal(SparklineHitTest.NearestIndex(45, Samples, Width),
                     SparklineHitTest.NearestIndex(45, Samples, Width));
        Assert.Equal(5, SparklineHitTest.NearestIndex(45, Samples, Width));
        Assert.Equal(6, SparklineHitTest.NearestIndex(55, Samples, Width));
    }

    [Fact]
    public void APointerPastTheEdgeClampsRatherThanBlankingTheReadout()
    {
        // Hovering a pixel beyond the right edge should read the last value. A readout that
        // flickers empty at the edges looks like the chart has holes in it.
        Assert.Equal(10, SparklineHitTest.NearestIndex(9999, Samples, Width));
        Assert.Equal(0, SparklineHitTest.NearestIndex(-9999, Samples, Width));
    }

    [Fact]
    public void NoSamplesMeansNothingToPointAt()
    {
        Assert.Equal(-1, SparklineHitTest.NearestIndex(50, 0, Width));
        Assert.Equal(-1, SparklineHitTest.NearestIndex(50, -3, Width));
    }

    [Fact]
    public void ASingleSampleIsAlwaysTheAnswer()
    {
        // Not a divide-by-zero: step is width / (count - 1), which is division by zero here.
        Assert.Equal(0, SparklineHitTest.NearestIndex(0, 1, Width));
        Assert.Equal(0, SparklineHitTest.NearestIndex(9999, 1, Width));
    }

    [Fact]
    public void AZeroWidthPlotDoesNotProduceAnUndefinedIndex()
    {
        // A control can be measured at zero during layout. Dividing by it yields infinity, and the
        // cast that follows is undefined behaviour rather than merely a wrong number.
        var index = SparklineHitTest.NearestIndex(50, Samples, 0);

        Assert.InRange(index, 0, Samples - 1);
    }

    [Fact]
    public void TheReturnedIndexIsAlwaysInRangeAcrossTheWholePointerSweep()
    {
        // Swept rather than sampled: an index one past the end throws when the caller reads
        // samples[index], and it only happens at one pixel.
        for (double x = -50; x <= 150; x += 0.5)
        {
            var index = SparklineHitTest.NearestIndex(x, Samples, Width);

            Assert.InRange(index, 0, Samples - 1);
        }
    }

    [Fact]
    public void TheCrosshairIsDrawnOnTheSampleItReports()
    {
        // Drawing at the pointer would put the line between two points while the readout names one
        // of them, inviting the user to believe the value belongs where the line is.
        Assert.Equal(50, SparklineHitTest.SnappedX(5, Samples, Width), 3);
        Assert.Equal(0, SparklineHitTest.SnappedX(0, Samples, Width), 3);
        Assert.Equal(100, SparklineHitTest.SnappedX(10, Samples, Width), 3);
    }

    [Fact]
    public void SnappedXAgreesWithNearestIndexAcrossTheSweep()
    {
        // The two are used together on every pointer move - the readout comes from the index and
        // the line from the x. If they can disagree, the chart shows one sample and names another.
        for (double x = 0; x <= Width; x += 0.5)
        {
            var index = SparklineHitTest.NearestIndex(x, Samples, Width);
            var snapped = SparklineHitTest.SnappedX(index, Samples, Width);

            Assert.InRange(snapped, 0, Width);
            Assert.True(Math.Abs(snapped - x) <= (Width / (Samples - 1) / 2) + 0.001,
                $"x={x} snapped to {snapped}, further than half a step away");
        }
    }
}

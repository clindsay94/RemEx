using Remex.Core.Services;

namespace Remex.Core.Tests;

/// <summary>
/// The stream throughput counter measures what happened, not what was configured (RemEx-93n2).
/// </summary>
/// <remarks>
/// The clock is injected so elapsed time is exact rather than slept for. A test that sleeps to
/// produce a duration is a test that fails on a loaded machine, and this repo has no timing-based
/// assertions for that reason.
/// </remarks>
public class StreamThroughputCounterTests
{
    private static readonly DateTime T0 = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RatesAreMeasuredOverTheElapsedWindow()
    {
        var counter = new StreamThroughputCounter(T0);
        counter.Add(1000);
        counter.Add(3000);

        var (fps, bps) = counter.Sample(T0.AddSeconds(2));

        Assert.Equal(1.0, fps, precision: 6);
        Assert.Equal(2000.0, bps, precision: 6);
    }

    [Fact]
    public void SamplingResetsTheWindowSoTheSecondReadingIsNotDilutedByTheFirst()
    {
        // THE PROPERTY A METER ACTUALLY WANTS. A running total would let a busy first second hold the
        // reported rate up through a stalled second - which is exactly the condition the meter exists
        // to make visible.
        var counter = new StreamThroughputCounter(T0);
        counter.Add(10_000);
        counter.Sample(T0.AddSeconds(1));

        var (fps, bps) = counter.Sample(T0.AddSeconds(2));

        Assert.Equal(0.0, fps);
        Assert.Equal(0.0, bps);
    }

    [Fact]
    public void ANonAdvancingClockReportsZeroRatherThanInfinity()
    {
        // Two samples inside one tick of a coarse clock is what a caller polling faster than the
        // timer resolution does. An infinity propagated into a meter reads as a measurement; a zero
        // reads as "no data yet", which is the truth.
        var counter = new StreamThroughputCounter(T0);
        counter.Add(500);

        var (fps, bps) = counter.Sample(T0);

        Assert.Equal(0.0, fps);
        Assert.Equal(0.0, bps);
        Assert.False(double.IsInfinity(bps));

        // AND THE COUNTS SURVIVE INTO THE NEXT WINDOW. The original version asserted only the (0,0)
        // and passed just as happily while the counts were being DRAINED and discarded - which is
        // what the code did until a reviewer read it. At 120 FPS a 15 ms Windows tick holds a frame
        // or two, and they belong to the next window, not to the bin.
        var (fps2, bps2) = counter.Sample(T0.AddSeconds(1));

        Assert.Equal(1.0, fps2);
        Assert.Equal(500.0, bps2);
    }

    [Fact]
    public void AnEmptyFrameCountsAsAFrameEvenThoughTheCurrentCallerNeverSendsOne()
    {
        // The TYPE accepts a zero-byte frame; the CALLER cannot produce one, because it counts
        // inside its own !IsEmpty guard. That asymmetry is documented on both sides rather than left
        // for someone to discover - a reviewer found this test and the wiring claiming opposite
        // things, which is exactly the shape of a comment that outlives its code.
        var counter = new StreamThroughputCounter(T0);
        counter.Add(0);
        counter.Add(0);

        var (fps, bps) = counter.Sample(T0.AddSeconds(1));

        Assert.Equal(2.0, fps);
        Assert.Equal(0.0, bps);
        Assert.Equal(2, counter.TotalFrames);
    }

    [Fact]
    public void ANegativeByteCountIsIgnoredEntirelyRatherThanCountedAsAFrame()
    {
        // Not reachable from the encoder, but a counter that can be driven negative would report a
        // NEGATIVE bitrate, and a meter has no sensible way to render that.
        var counter = new StreamThroughputCounter(T0);
        counter.Add(-1);

        Assert.Equal(0, counter.TotalFrames);
        Assert.Equal(0, counter.TotalBytes);
    }

    [Fact]
    public void TotalsAccumulateAcrossWindowsEvenThoughRatesDoNot()
    {
        var counter = new StreamThroughputCounter(T0);
        counter.Add(100);
        counter.Sample(T0.AddSeconds(1));
        counter.Add(200);
        counter.Sample(T0.AddSeconds(2));

        Assert.Equal(2, counter.TotalFrames);
        Assert.Equal(300, counter.TotalBytes);
    }
}

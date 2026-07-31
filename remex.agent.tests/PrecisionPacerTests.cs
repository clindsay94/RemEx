using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Remex.Agent.Services.RemoteDesktop;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins both halves of what <see cref="PrecisionPacer"/> has to do at once: hit the target interval
/// accurately, and do it without burning a core.
/// </summary>
/// <remarks>
/// Those two pull against each other, which is how the bug happened. The pacer coarse-sleeps the
/// bulk of an interval and busy-spins the tail for sub-millisecond accuracy — but the margin was
/// 16 ms, larger than a whole tick at 90 Hz (11.1 ms) or 120 Hz (8.3 ms), so the coarse sleep never
/// ran and the tail WAS the interval. The cursor loop spun ~100% of a core for as long as a stream
/// was open (RemEx-ccen). A test that only checked timing would have passed against that code — the
/// old pacer hit 90 Hz perfectly, it just burned a core doing it — which is why
/// <see cref="PacingSleepsMostOfTheInterval"/> exists alongside the accuracy test.
/// </remarks>
[Collection(nameof(PrecisionPacerTests))]
[CollectionDefinition(nameof(PrecisionPacerTests), DisableParallelization = true)]
public sealed class PrecisionPacerTests
{
    private const double CursorIntervalMs = 1000.0 / 90.0;   // the 90 Hz cursor loop
    private const double FrameIntervalMs = 1000.0 / 120.0;   // the 120 FPS frame loop

    [Fact]
    public void SpinMargin_IsSmall_WhenTheCoarseSleepIsAccurate()
    {
        using var pacer = new PrecisionPacer();

        if (OperatingSystem.IsWindows() && !pacer.UsesHighResolutionTimer)
        {
            // Pre-1803 Windows: the pacer MUST keep the old wide margin, because a small margin over
            // a 15.6 ms-granular sleep would overshoot the tick and drop the frame rate — worse than
            // the CPU cost this bead is about.
            Assert.Equal(16.0, pacer.SpinMarginMs);
            return;
        }

        Assert.Equal(2.0, pacer.SpinMarginMs);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    public async Task PacesCloseToTheTargetRate(int hz)
    {
        using var pacer = new PrecisionPacer();
        double intervalMs = 1000.0 / hz;
        const int ticks = 30;

        pacer.Reset();
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < ticks; i++)
            Assert.True(await pacer.WaitForNextTickAsync(intervalMs, CancellationToken.None));
        clock.Stop();

        // The absolute timeline means the total is what matters, not any single tick. A generous
        // band: the point is that it neither oversleeps to the OS floor (which would show up as
        // ~15.6 ms per tick) nor returns early.
        double expectedMs = ticks * intervalMs;
        Assert.InRange(clock.Elapsed.TotalMilliseconds, expectedMs * 0.85, expectedMs * 1.15);
    }

    /// <summary>
    /// The regression this bead exists for: pacing must SLEEP most of each interval, not spin it.
    /// </summary>
    /// <remarks>
    /// Asserted on the pacer's own accumulated spin time rather than on
    /// <c>Process.TotalProcessorTime</c>. The CPU-time version of this test was written first and had
    /// to be abandoned: that counter is process-WIDE, so with the rest of the suite running it read
    /// ~105% whether the pacer spun or not — it measured xUnit, not the code under test. Spin time is
    /// the mechanism itself and is unaffected by anything else on the box.
    /// <para>
    /// For the record, measured manually on an otherwise-idle run: 99% of a core before the fix,
    /// 18% after — which is the 2 ms margin over an 11.1 ms interval, exactly what this asserts.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    public async Task PacingSleepsMostOfTheInterval(int hz)
    {
        double intervalMs = 1000.0 / hz;

        using var pacer = new PrecisionPacer();
        if (OperatingSystem.IsWindows() && !pacer.UsesHighResolutionTimer)
            return;   // pre-1803: the wide margin is deliberate, see SpinMargin_IsSmall_...

        pacer.Reset();
        var clock = Stopwatch.StartNew();

        int ticks = (int)(600 / intervalMs);
        for (int i = 0; i < ticks; i++)
            await pacer.WaitForNextTickAsync(intervalMs, CancellationToken.None);

        clock.Stop();

        double spunFraction = pacer.SpinMsAccumulated / clock.Elapsed.TotalMilliseconds;
        Assert.True(spunFraction < 0.40,
            $"at {hz} Hz the pacer spun {spunFraction:P0} of the wall time " +
            $"({pacer.SpinMsAccumulated:F0} ms of {clock.Elapsed.TotalMilliseconds:F0} ms) — it is " +
            "burning the interval instead of sleeping it");
    }

    [Fact]
    public async Task ReturnsFalse_WhenCancelledDuringTheCoarseSleep()
    {
        using var pacer = new PrecisionPacer();
        using var cts = new CancellationTokenSource();

        // An interval far longer than the spin margin, so the wait is definitely in the coarse sleep
        // when the cancel lands.
        var wait = pacer.WaitForNextTickAsync(5_000, cts.Token);
        cts.CancelAfter(50);

        var clock = Stopwatch.StartNew();
        Assert.False(await wait);
        clock.Stop();

        // Must abandon the wait rather than sleep out the full interval — a stream teardown blocked
        // for five seconds would look like a hang.
        Assert.True(clock.Elapsed.TotalMilliseconds < 2_000,
            $"cancellation took {clock.ElapsedMilliseconds} ms to be observed");
    }

    [Fact]
    public async Task ReturnsFalse_WhenAlreadyCancelled()
    {
        using var pacer = new PrecisionPacer();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.False(await pacer.WaitForNextTickAsync(FrameIntervalMs, cts.Token));
    }

    [Fact]
    public async Task Reset_ReanchorsSoARecoveryDoesNotBurstThroughMissedTicks()
    {
        using var pacer = new PrecisionPacer();

        // Simulate a stall: the timeline is now well behind "now".
        await pacer.WaitForNextTickAsync(FrameIntervalMs, CancellationToken.None);
        await Task.Delay(120);

        pacer.Reset();

        // Without Reset the next tick would return instantly (it is already "due"); with it, a full
        // interval is waited.
        var clock = Stopwatch.StartNew();
        Assert.True(await pacer.WaitForNextTickAsync(FrameIntervalMs, CancellationToken.None));
        clock.Stop();

        Assert.True(clock.Elapsed.TotalMilliseconds > FrameIntervalMs * 0.5,
            $"tick returned after {clock.Elapsed.TotalMilliseconds:F1} ms — Reset did not re-anchor");
    }
}

using FluentAssertions;
using Remex.Desktop.Controls.Splash;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// The splash host's clock rule (RemEx-8g6n0.2 fix round, HIGH, from a screen recording): a Debug
/// window became visible ~3.6 s after the splash attached, so a clock started at attach put the first
/// presented Live Handshake frame at the hand-off. The live clock starts at the first rendered frame
/// and clamps each step; the fixed films keep their old from-attach, unclamped timing.
/// </summary>
public class SplashClockTests
{
    [Fact]
    public void ALiveClockStaysAtZeroUntilTheFirstFrameRenders()
    {
        var clock = new SplashClock();
        clock.Reset(live: true);

        for (int i = 0; i < 200; i++) clock.Advance(0.016); // 3.2 s of an invisible window

        clock.Elapsed.Should().Be(0, "no frame has been presented, so the splash has not started");
        clock.Running.Should().BeFalse();

        clock.MarkFirstFrame();
        clock.Advance(0.016);
        clock.Elapsed.Should().BeApproximately(0.016, 1e-9);
        clock.Running.Should().BeTrue();
    }

    [Fact]
    public void ALiveStepIsClampedSoAStallNeverJumpsTheTimeline()
    {
        var clock = new SplashClock();
        clock.Reset(live: true);
        clock.MarkFirstFrame();

        clock.Advance(0.9).Should().Be(SplashClock.LiveMaxStep, "a shader compile or GC stall slows the splash, it does not skip it");
        clock.LastDt.Should().Be(SplashClock.LiveMaxStep);
        clock.Advance(0.010).Should().BeApproximately(0.010, 1e-12, "normal frames are not touched");
        clock.Elapsed.Should().BeApproximately(SplashClock.LiveMaxStep + 0.010, 1e-12);
    }

    [Fact]
    public void AFixedFilmRunsFromAttachUnclampedExactlyAsBefore()
    {
        var clock = new SplashClock();
        clock.Reset(live: false);

        clock.Running.Should().BeTrue();
        clock.Advance(0.9).Should().Be(0.9);
        clock.Advance(0.016);
        clock.Elapsed.Should().BeApproximately(0.916, 1e-12);
        clock.LastDt.Should().BeApproximately(0.016, 1e-12);
    }

    [Fact]
    public void AWindowThatNeverRendersStillStartsTheClockEventually()
    {
        // Started hidden (to the tray): no frame is ever composed, and the splash must still be able
        // to complete on its own rather than wait forever.
        var clock = new SplashClock();
        clock.Reset(live: true);

        double waited = 0;
        while (!clock.Running && waited < SplashClock.FirstFrameTimeout + 1)
        {
            clock.Advance(0.05);
            waited += 0.05;
        }

        clock.Running.Should().BeTrue();
        waited.Should().BeApproximately(SplashClock.FirstFrameTimeout, 0.06);
        clock.Elapsed.Should().Be(0, "the wait itself is not splash time");
    }

    [Fact]
    public void ResetReturnsToWaitingForAFreshFirstFrame()
    {
        var clock = new SplashClock();
        clock.Reset(live: true);
        clock.MarkFirstFrame();
        clock.Advance(0.02);

        clock.Reset(live: true); // Preview / a style change replays from 0

        clock.Elapsed.Should().Be(0);
        clock.Running.Should().BeFalse();
    }
}

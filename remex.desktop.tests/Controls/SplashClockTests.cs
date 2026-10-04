using FluentAssertions;
using Remex.Desktop.Controls.Splash;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// The splash host's clock rule (RemEx-8g6n0.2 fix round, HIGH, from a screen recording): a Debug
/// window became visible ~3.6 s after the splash attached, so a clock started at attach put the first
/// presented Live Handshake frame at the hand-off. The clock starts at the first rendered frame and
/// clamps each step, for every style (the fixed films too, since RemEx-pp4cm.11).
/// </summary>
public class SplashClockTests
{
    [Fact]
    public void ALiveClockStaysAtZeroUntilTheFirstFrameRenders()
    {
        var clock = new SplashClock();
        clock.Reset();

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
        clock.Reset();
        clock.MarkFirstFrame();

        clock.Advance(0.9).Should().Be(SplashClock.LiveMaxStep, "a shader compile or GC stall slows the splash, it does not skip it");
        clock.LastDt.Should().Be(SplashClock.LiveMaxStep);
        clock.Advance(0.010).Should().BeApproximately(0.010, 1e-12, "normal frames are not touched");
        clock.Elapsed.Should().BeApproximately(SplashClock.LiveMaxStep + 0.010, 1e-12);
    }

    [Fact]
    public void AFreshClockHasNotStartedBeforeAnyFrame()
    {
        // RemEx-pp4cm.11: the fixed films used to run from attach (Running was true at once), so a
        // cold start's ~3 s first present ate most of a ~3 s film. There is one clock rule now.
        var clock = new SplashClock();
        clock.Reset();

        clock.Running.Should().BeFalse("every style waits for its first rendered frame");
        clock.Advance(0.9).Should().Be(0, "time does not move before the first frame");
        clock.Elapsed.Should().Be(0);
    }

    [Fact]
    public void AWindowThatNeverRendersStillStartsTheClockEventually()
    {
        // Started hidden (to the tray): no frame is ever composed, and the splash must still be able
        // to complete on its own rather than wait forever.
        var clock = new SplashClock();
        clock.Reset();

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
        clock.Reset();
        clock.MarkFirstFrame();
        clock.Advance(0.02);

        clock.Reset(); // Preview / a style change replays from 0

        clock.Elapsed.Should().Be(0);
        clock.Running.Should().BeFalse();
    }
}

using Remex.Agent.Services.ScreenCapture;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Tests for <see cref="DuplicationReinitThrottle"/> — the gate that stops a lost DXGI duplication from
/// being re-created at the stream frame rate during a display power-transition (RemEx-crk). Clock is
/// injected, so the escalation schedule is verified deterministically without a GPU or real display.
/// </summary>
public class DuplicationReinitThrottleTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DuplicationReinitThrottle NewThrottle() =>
        new(baseBackoff: TimeSpan.FromSeconds(1), maxBackoff: TimeSpan.FromSeconds(8));

    [Fact]
    public void FirstLoss_IsAllowedImmediately()
    {
        var throttle = NewThrottle();
        Assert.True(throttle.ShouldAttempt(T0));
    }

    [Fact]
    public void AfterAttempt_BlockedUntilBaseWindowElapses()
    {
        var throttle = NewThrottle();
        throttle.RecordAttempt(T0);

        Assert.False(throttle.ShouldAttempt(T0.AddMilliseconds(999)));
        Assert.True(throttle.ShouldAttempt(T0.AddSeconds(1)));
    }

    [Fact]
    public void ConsecutiveLosses_EscalateTheBackoffWindow()
    {
        var throttle = NewThrottle();

        // 1st attempt → 1s window
        throttle.RecordAttempt(T0);
        var t = T0.AddSeconds(1);
        Assert.True(throttle.ShouldAttempt(t));

        // 2nd attempt → 2s window
        throttle.RecordAttempt(t);
        Assert.False(throttle.ShouldAttempt(t.AddMilliseconds(1999)));
        Assert.True(throttle.ShouldAttempt(t.AddSeconds(2)));

        // 3rd attempt → 4s window
        t = t.AddSeconds(2);
        throttle.RecordAttempt(t);
        Assert.False(throttle.ShouldAttempt(t.AddMilliseconds(3999)));
        Assert.True(throttle.ShouldAttempt(t.AddSeconds(4)));
    }

    [Fact]
    public void Backoff_SaturatesAtMax()
    {
        var throttle = NewThrottle();
        var t = T0;

        // Drive escalation well past the cap: 1s, 2s, 4s, 8s, then it must stay at 8s (not 16s+).
        for (int i = 0; i < 6; i++)
        {
            throttle.RecordAttempt(t);
            t = t.AddSeconds(8); // always wait the max so each next attempt is permitted
            Assert.True(throttle.ShouldAttempt(t));
        }

        // One more attempt, then confirm the window never exceeds the 8s ceiling.
        throttle.RecordAttempt(t);
        Assert.False(throttle.ShouldAttempt(t.AddMilliseconds(7999)));
        Assert.True(throttle.ShouldAttempt(t.AddSeconds(8)));
    }

    [Fact]
    public void HealthyFrame_ResetsWindowAndCounter()
    {
        var throttle = NewThrottle();
        throttle.RecordAttempt(T0);
        throttle.RecordAttempt(T0.AddSeconds(1)); // escalate so the reset is meaningful
        Assert.True(throttle.ConsecutiveReinits > 0);

        throttle.RecordHealthyFrame();

        Assert.Equal(0, throttle.ConsecutiveReinits);
        Assert.True(throttle.ShouldAttempt(T0.AddSeconds(1))); // recovers immediately after a healthy frame
    }

    [Fact]
    public void StormScenario_BoundsAttemptsInsteadOfHammeringEveryFrame()
    {
        // Regression for RemEx-crk: a display powering off makes AcquireNextFrame return ACCESS_LOST on
        // EVERY frame. Simulate a 125 FPS stream (8ms/frame) losing access continuously for 10 seconds —
        // 1250 frames. Without throttling that is 1250 DuplicateOutput calls against a transitioning
        // display (the storm that wedges the GPU driver). With the throttle it must be a small handful.
        var throttle = NewThrottle();
        int attempts = 0;

        for (int frame = 0; frame < 1250; frame++)
        {
            var now = T0.AddMilliseconds(frame * 8);
            if (throttle.ShouldAttempt(now))
            {
                throttle.RecordAttempt(now); // never followed by a healthy frame — the display stays off
                attempts++;
            }
        }

        // Escalation 1s,2s,4s,8s → attempts land at t=0,1,3,7s; next would be 15s (outside the window).
        Assert.Equal(4, attempts);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveBaseBackoff()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DuplicationReinitThrottle(TimeSpan.Zero, TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public void Constructor_RejectsMaxBelowBase()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DuplicationReinitThrottle(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
    }
}

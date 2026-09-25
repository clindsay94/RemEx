using Remex.Agent.Handlers;
using Remex.Agent.Services.RemoteDesktop;

namespace Remex.Agent.Tests;

/// <summary>
/// The adaptive-scale controller must survive the encoder rebuild its own step triggers
/// (perf audit P3-38).
/// </summary>
/// <remarks>
/// Every controller decision bumps the encoder config version, which rebuilds the encoder, and the
/// capture loop used to build a fresh controller on every encoder swap. So the state a decision had
/// just set — the change cooldown and the 60 s hold after a failed step-up — was thrown away by the
/// rebuild that decision caused, and a host that could not hold a rung oscillated on it.
/// </remarks>
public class AdaptiveScaleReseedTests
{
    [Fact]
    public void NoControllerYet_Reseeds()
        => Assert.True(RemoteDesktopHandler.ShouldReseedAdaptiveController(null, double.NaN, 0.5));

    [Fact]
    public void RebuildCausedByTheControllersOwnStep_KeepsIt()
    {
        var controller = new AdaptiveScaleController(0.5);
        // The loop records the scale the controller chose, then _scale holds that same value.
        Assert.False(RemoteDesktopHandler.ShouldReseedAdaptiveController(controller, 0.65, 0.65));
    }

    [Fact]
    public void ScaleChangedFromOutside_Reseeds()
    {
        // A client preset change moves _scale away from what the controller last produced.
        var controller = new AdaptiveScaleController(0.5);
        Assert.True(RemoteDesktopHandler.ShouldReseedAdaptiveController(controller, 0.65, 0.4));
    }

    [Fact]
    public void KeptController_HoldsAfterAFailedStepUp_WhereAFreshOneWouldNot()
    {
        // Why keeping it matters: after a step-up that immediately fails, the kept controller holds
        // the lower rung; a fresh controller seeded at that rung would step straight back up after
        // five stable windows, which is the oscillation this row removes.
        var t = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var kept = new AdaptiveScaleController(0.5);

        // Five stable windows -> step up to 0.65.
        AdaptiveScaleDecision? up = null;
        for (var i = 0; i < 5; i++)
        {
            t = t.AddSeconds(2);
            up = kept.Report(60, 60, outputOverflowed: false, t) ?? up;
        }
        Assert.Equal(0.65, up!.Scale);

        // The new rung overflows once the cooldown has passed -> step back down to 0.5.
        t = t.AddSeconds(6);
        var down = kept.Report(60, 60, outputOverflowed: true, t);
        Assert.Equal(0.5, down!.Scale);

        var fresh = new AdaptiveScaleController(down.Scale);

        AdaptiveScaleDecision? keptDecision = null;
        AdaptiveScaleDecision? freshDecision = null;
        for (var i = 0; i < 8; i++)
        {
            t = t.AddSeconds(2);
            keptDecision ??= kept.Report(60, 60, outputOverflowed: false, t);
            freshDecision ??= fresh.Report(60, 60, outputOverflowed: false, t);
        }

        Assert.Null(keptDecision);              // inside the 60 s failed-step-up hold
        Assert.Equal(0.65, freshDecision!.Scale); // the old behaviour: straight back up
    }
}

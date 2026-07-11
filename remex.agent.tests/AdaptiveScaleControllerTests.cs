using System;
using Remex.Agent.Services.RemoteDesktop;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Tests for <see cref="AdaptiveScaleController"/> — the Phase 5 (RemEx-eo0f) state machine that
/// steps H.264 capture scale up/down to hold the sharpest resolution the encoder can sustain at the
/// target FPS. Clock is injected, so the escalation/hysteresis schedule is verified deterministically.
/// </summary>
public sealed class AdaptiveScaleControllerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    [Fact]
    public void StartsAtNearestRungToConfiguredScale()
    {
        var controller = new AdaptiveScaleController(startingScale: 0.5);
        Assert.Equal(0.5, controller.CurrentScale);
    }

    [Fact]
    public void StartingScaleBelowFloor_ClampsToFloor()
    {
        var controller = new AdaptiveScaleController(startingScale: 0.4, floorScale: 0.5);
        Assert.Equal(0.5, controller.CurrentScale);
    }

    [Fact]
    public void HealthyStableWindow_DoesNotChangeBeforeFiveWindows()
    {
        var controller = new AdaptiveScaleController(startingScale: 0.5);
        var t = T0;

        for (int i = 0; i < 4; i++)
        {
            t += Window;
            var decision = controller.Report(targetFps: 90, achievedFps: 90, outputOverflowed: false, t);
            Assert.Null(decision);
        }

        Assert.Equal(0.5, controller.CurrentScale);
    }

    [Fact]
    public void FiveConsecutiveStableWindows_StepsUpOneRung()
    {
        var controller = new AdaptiveScaleController(startingScale: 0.5);
        var t = T0;
        AdaptiveScaleDecision? decision = null;

        for (int i = 0; i < 5; i++)
        {
            t += Window;
            decision = controller.Report(targetFps: 90, achievedFps: 90, outputOverflowed: false, t);
        }

        Assert.NotNull(decision);
        Assert.Equal(0.65, decision!.Scale);
        Assert.Equal(0.65, controller.CurrentScale);
        Assert.Equal("fps-stable", decision.Reason);
    }

    [Fact]
    public void TwoConsecutiveLowFpsWindows_StepsDownOneRung()
    {
        var controller = new AdaptiveScaleController(startingScale: 0.65);
        var t = T0;

        t += Window;
        Assert.Null(controller.Report(targetFps: 90, achievedFps: 70, outputOverflowed: false, t)); // 78% < 90%

        t += Window;
        var decision = controller.Report(targetFps: 90, achievedFps: 70, outputOverflowed: false, t);

        Assert.NotNull(decision);
        Assert.Equal(0.5, decision!.Scale);
        Assert.Equal("low-fps", decision.Reason);
    }

    [Fact]
    public void SingleLowFpsWindow_DoesNotStepDownYet()
    {
        var controller = new AdaptiveScaleController(startingScale: 0.65);
        var decision = controller.Report(targetFps: 90, achievedFps: 70, outputOverflowed: false, T0 + Window);

        Assert.Null(decision);
        Assert.Equal(0.65, controller.CurrentScale);
    }

    [Fact]
    public void OutputOverflow_StepsDownImmediatelyEvenOnFirstWindow()
    {
        var controller = new AdaptiveScaleController(startingScale: 0.65);
        var decision = controller.Report(targetFps: 90, achievedFps: 90, outputOverflowed: true, T0 + Window);

        Assert.NotNull(decision);
        Assert.Equal(0.5, decision!.Scale);
        Assert.Equal("output-overflow", decision.Reason);
    }

    [Fact]
    public void CannotStepDownBelowFloor()
    {
        var controller = new AdaptiveScaleController(startingScale: 0.4, floorScale: 0.4);
        var t = T0;

        for (int i = 0; i < 5; i++)
        {
            t += Window;
            controller.Report(targetFps: 90, achievedFps: 10, outputOverflowed: true, t);
        }

        Assert.Equal(0.4, controller.CurrentScale);
    }

    [Fact]
    public void CannotStepUpAboveCeiling()
    {
        var controller = new AdaptiveScaleController(startingScale: 1.0);
        var t = T0;

        for (int i = 0; i < 20; i++)
        {
            t += Window;
            controller.Report(targetFps: 90, achievedFps: 90, outputOverflowed: false, t);
        }

        Assert.Equal(1.0, controller.CurrentScale);
    }

    [Fact]
    public void MinimumFiveSecondsBetweenChanges()
    {
        var controller = new AdaptiveScaleController(startingScale: 0.5);
        var t = T0;

        // Step up to 0.65 after 5 stable windows.
        for (int i = 0; i < 5; i++)
        {
            t += Window;
            controller.Report(90, 90, false, t);
        }
        Assert.Equal(0.65, controller.CurrentScale);
        var changeTime = t;

        // An overflow 2s later would otherwise force an immediate step-down (overflow bypasses the
        // consecutive-window requirement), but the 5s minimum-change cooldown must still hold.
        t = changeTime + TimeSpan.FromSeconds(2);
        var blocked = controller.Report(90, 90, outputOverflowed: true, t);
        Assert.Null(blocked);
        Assert.Equal(0.65, controller.CurrentScale);

        // Once the cooldown elapses, the still-failing window steps down.
        t = changeTime + TimeSpan.FromSeconds(6);
        var stepDown = controller.Report(90, 90, outputOverflowed: true, t);
        Assert.NotNull(stepDown);
        Assert.Equal(0.5, stepDown!.Scale);
    }

    [Fact]
    public void FailedStepUp_HoldsLowerRungForSixtySeconds()
    {
        var controller = new AdaptiveScaleController(startingScale: 0.5);
        var t = T0;

        // Step up to 0.65 after 5 stable windows.
        for (int i = 0; i < 5; i++)
        {
            t += Window;
            controller.Report(90, 90, false, t);
        }
        Assert.Equal(0.65, controller.CurrentScale);

        // Immediately fails: 2 consecutive low-fps windows (past the 5s min-change cooldown) step it
        // back down to 0.5. That step-down followed a step-up, so it triggers the 60s failed-step-up hold.
        t += TimeSpan.FromSeconds(5);
        controller.Report(90, 40, false, t);
        t += Window;
        var stepDown = controller.Report(90, 40, false, t);
        Assert.NotNull(stepDown);
        Assert.Equal(0.5, stepDown!.Scale);
        var failedAt = t;

        // Even once 5 fresh consecutive stable windows accumulate (which alone would normally step
        // up), the 60s failed-step-up hold must still block it — this is the oscillation guard.
        AdaptiveScaleDecision? stillHeld = null;
        for (int i = 0; i < 5; i++)
        {
            t += Window;
            stillHeld = controller.Report(90, 90, false, t);
        }
        Assert.Null(stillHeld);
        Assert.Equal(0.5, controller.CurrentScale);

        // Once the 60s hold fully elapses, the step back up to 0.65 is allowed again.
        t = failedAt + TimeSpan.FromSeconds(61);
        var afterHold = controller.Report(90, 90, false, t);
        Assert.NotNull(afterHold);
        Assert.Equal(0.65, afterHold!.Scale);
    }

    [Fact]
    public void NonPositiveTargetFps_ReturnsNullWithoutThrowing()
    {
        var controller = new AdaptiveScaleController(startingScale: 0.5);
        Assert.Null(controller.Report(targetFps: 0, achievedFps: 90, outputOverflowed: false, T0));
    }
}

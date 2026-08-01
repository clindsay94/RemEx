using Remex.Agent.Services.Input;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the wheel-detent conversion behind the ydotool scroll path (RemEx-nb7c).
///
/// THE SCROLL PATH HAD THE SAME DEFECT AS THE CLICK PATH and was found only when the click fix was
/// reviewed. It sent X11's wheel buttons 4/5/6/7 to <c>ydotool click</c>. ydotool has no wheel
/// button: <c>Client/tool_click.c</c> does <c>keycode = (key &amp; 0xf) | 0x110</c>, so those
/// numbers selected EXTR/FORWARD/BACK/TASK, and because the argument set neither the 0x40 nor the
/// 0x80 action bit, the tool's two guards both failed and it emitted nothing. Scrolling spawned up
/// to ten processes per gesture to do nothing at all.
///
/// The replacement is <c>mousemove --wheel</c>, which emits REL_HWHEEL/REL_WHEEL with the argument
/// values used verbatim as detent counts — so the count now has to be a signed number rather than a
/// repeat count, which is what this converts and what these tests pin.
/// </summary>
public sealed class YdotoolWheelDetentTests
{
    [Theory]
    [InlineData(120, 1)]
    [InlineData(240, 2)]
    [InlineData(-120, -1)]
    [InlineData(-360, -3)]
    public void AWholeNumberOfNotchesConvertsOneForOne(int delta, int expected)
    {
        // 120 units per notch is the protocol's convention, inherited from WHEEL_DELTA.
        Assert.Equal(expected, LinuxInputSimulationService.WheelDetents(delta));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(119)]
    [InlineData(-1)]
    [InlineData(-119)]
    public void ASubNotchScrollStillMovesTheWheelInsteadOfVanishing(int delta)
    {
        // Integer division alone would round these to zero and drop the gesture. The loop this
        // replaced clamped to a minimum of one click for the same reason, so the behaviour carries
        // over rather than being newly invented here.
        Assert.Equal(System.Math.Sign(delta), LinuxInputSimulationService.WheelDetents(delta));
    }

    [Theory]
    [InlineData(120_000, 10)]
    [InlineData(-120_000, -10)]
    public void ARunawayDeltaIsCappedSoOneMessageCannotFlingThePage(int delta, int expected)
    {
        Assert.Equal(expected, LinuxInputSimulationService.WheelDetents(delta));
    }

    [Fact]
    public void ZeroMeansZeroSoNoProcessIsSpawnedForAnAxisThatDidNotMove()
    {
        // The call site skips the invocation entirely when both axes come back zero. Were this to
        // return the clamped minimum of 1 instead, every horizontal-only scroll would also nudge the
        // page vertically.
        Assert.Equal(0, LinuxInputSimulationService.WheelDetents(0));
    }

    [Fact]
    public void TheSignConventionMatchesTheButtonsItReplaces()
    {
        // The old code chose xdotool button 4 for deltaY > 0 (scroll up) and 7 for deltaX > 0
        // (scroll right); REL_WHEEL is positive up and REL_HWHEEL positive right, so a positive
        // delta must stay positive. Getting this backwards would invert scrolling on this backend
        // only — silent, and exactly the class of failure RemEx-kie3 already cost this project.
        Assert.True(LinuxInputSimulationService.WheelDetents(240) > 0);
        Assert.True(LinuxInputSimulationService.WheelDetents(-240) < 0);
    }
}

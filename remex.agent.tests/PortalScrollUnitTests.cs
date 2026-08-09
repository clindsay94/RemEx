using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.Input.Linux;
using Remex.Agent.Services.RemoteDesktop.Linux;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins what <see cref="LinuxInputSimulationService.MouseScroll"/> actually hands the portal
/// (RemEx-y45x).
///
/// THE BUG WAS A UNIT MISMATCH BETWEEN TWO APIS THAT BOTH TAKE AN INT. The wire delta is in Windows'
/// <c>WHEEL_DELTA</c> units, 120 per notch — <c>WindowsInputSimulationService</c> passes it straight
/// to <c>MOUSEEVENTF_WHEEL</c>, which is what defines it, and <c>CoordinateValidation</c> caps it at
/// <c>120 * 10</c>. The portal's parameter is documented as "the number of steps scrolled":
///
///   NotifyPointerAxisDiscrete(IN session_handle o, IN options a{sv}, IN axis u, IN steps i)
///
/// The portal branch passed the raw delta into <c>steps</c>, so one notch from the phone asked the
/// compositor for 120. Both shell branches already divided by 120, which is why the defect existed
/// only on the branch nothing could observe.
///
/// THAT IS ALSO WHY THESE TESTS DRIVE THE SERVICE RATHER THAN THE HELPER. <c>WheelDetents</c> was
/// already correct and already covered; the mistake was at the call site. The constructor remark on
/// the service states the rule these follow — "the defect class is not 'the mapping is wrong', it is
/// 'the argv is wrong', and only the argv is worth pinning" — and the shell backends had a seam for
/// exactly that while the portal backend did not.
/// </summary>
public sealed class PortalScrollUnitTests
{
    /// <summary>Records what the service sends instead of talking to D-Bus.</summary>
    private sealed class RecordingSink : IPortalInputSink
    {
        public List<(int Dx, int Dy)> Scrolls { get; } = [];

        // True so the service's gate short-circuits before EnsureStartedAsync, which is what lets this
        // fake stay this small: the portal session is never started, only observed.
        public bool IsActive => true;

        public Task<bool> EnsureStartedAsync(CancellationToken ct = default) => Task.FromResult(true);
        public void NotifyPointerScrollDiscrete(int dx, int dy) => Scrolls.Add((dx, dy));
        public void NotifyPointerMotionRelative(double dx, double dy) { }
        public void NotifyPointerButton(int linuxButtonCode, bool pressed) { }
        public void NotifyKeyboardKeycode(int keycode, bool pressed) { }
        public void NotifyKeyboardKeysym(int keysym, bool pressed) { }
    }

    private static (LinuxInputSimulationService Service, RecordingSink Sink) Build()
    {
        var sink = new RecordingSink();
        var service = new LinuxInputSimulationService(
            NullLogger<LinuxInputSimulationService>.Instance,
            // No input tool and no display server: if the portal branch were ever skipped, the shell
            // branches below it would have nothing to run and the assertion would fail on an empty
            // recording rather than pass on the wrong path.
            new LinuxDesktopBackendStatus(
                DesktopEnvironment: "test",
                IsWaylandSession: true,
                IsKdePlasma: false,
                HasDisplayServer: false,
                InputTool: LinuxDesktopTool.None,
                InputToolPath: null,
                CursorQueryTool: LinuxDesktopTool.None,
                CursorQueryToolPath: null,
                WindowControlTool: LinuxDesktopTool.None,
                WindowControlToolPath: null),
            launcher: null,
            captureLifetime: null,
            virtualDesktopOrigin: null,
            portalInjector: sink);
        return (service, sink);
    }

    [Fact]
    public void OneNotchAsksTheCompositorForOneStepRatherThanOneHundredAndTwenty()
    {
        // The regression itself. 120 is one notch on the wire; before this fix the portal was told to
        // scroll 120 steps, which is not a subtle mis-scaling — it flings the page on a single notch.
        var (service, sink) = Build();

        service.MouseScroll(0, 120);

        // NEGATIVE FOR AN UPWARD NOTCH, and that is the fix rather than a typo (RemEx-e0b2). The
        // wire's positive is up; the portal's positive on axis 0 is DOWN, per mutter's
        // discrete_steps_to_scroll_direction and KDE's pass-through to wl_pointer.
        Assert.Equal((0, -1), Assert.Single(sink.Scrolls));
    }

    [Fact]
    public void TwoNotchesAskForTwoSteps()
    {
        // Proves the conversion is a division rather than a clamp to 1, which would also have passed
        // the single-notch assertion above.
        var (service, sink) = Build();

        service.MouseScroll(0, 240);

        Assert.Equal((0, -2), Assert.Single(sink.Scrolls));
    }

    [Fact]
    public void ScrollingBackwardsKeepsItsSignAndItsMagnitude()
    {
        // Sign is carried by the same helper, so a fix that took Math.Abs would break the direction
        // while leaving the two assertions above green.
        var (service, sink) = Build();

        service.MouseScroll(0, -240);

        Assert.Equal((0, 2), Assert.Single(sink.Scrolls));
    }

    [Fact]
    public void HorizontalAndVerticalAreCarriedInThatOrderAndConvertedIndependently()
    {
        // dx and dy are two separate conversions, and the portal splits them into two D-Bus messages,
        // so a fix applied to one argument only would leave a half-broken axis.
        var (service, sink) = Build();

        service.MouseScroll(-360, 120);

        // THE ASYMMETRY, AND IT IS THE POINT OF THIS TEST NOW. dx is carried through unchanged and
        // dy is negated, because the portal agrees with the wire on horizontal (axis 1 positive is
        // RIGHT, same as MOUSEEVENTF_HWHEEL) and disagrees on vertical. Negating both would have
        // fixed one axis and broken the other, and only this assertion would have noticed.
        Assert.Equal((-3, -1), Assert.Single(sink.Scrolls));
    }

    [Fact]
    public void ASubNotchScrollStillMovesOneStepInsteadOfVanishing()
    {
        // The reason the conversion is not a bare integer division: a phone reporting a partial notch
        // would otherwise scroll nothing at all, which reads as a dead gesture rather than a small one.
        var (service, sink) = Build();

        service.MouseScroll(0, 40);

        Assert.Equal((0, -1), Assert.Single(sink.Scrolls));
    }

    [Fact]
    public void ARunawayDeltaIsCappedRatherThanPassedThrough()
    {
        // The other end of the same helper. CoordinateValidation already caps the wire value at
        // 120 * 10, so this is defence in depth rather than the only guard — but the portal is the one
        // backend where an uncapped value would be handed to the compositor in a single call.
        var (service, sink) = Build();

        service.MouseScroll(0, 120 * 50);

        Assert.Equal((0, -10), Assert.Single(sink.Scrolls));
    }

    [Theory]
    [InlineData(120, -1)]   // wire says UP   -> portal needs a NEGATIVE step count
    [InlineData(-120, 1)]   // wire says DOWN -> portal needs a POSITIVE step count
    public void TheVerticalSignIsInvertedBecauseTheCompositorsSayPositiveIsDown(int wireDeltaY, int expectedSteps)
    {
        // THE ASSERTION RemEx-y45x DELIBERATELY DID NOT WRITE, and it could not have: it fixed the
        // magnitude against the spec and refused to touch the sign, because the portal documents
        // `steps` as "the number of steps scrolled" and never says which way positive goes. Guessing
        // from "Wayland is usually positive-down" is the assumption RemEx-nb7c exists to stop.
        //
        // Settled from the two implementations that actually receive the call:
        //
        //   GNOME — mutter, discrete_steps_to_scroll_direction() in meta-remote-desktop-session.c:
        //   axis 0 with steps < 0 is CLUTTER_SCROLL_UP, steps > 0 is CLUTTER_SCROLL_DOWN.
        //
        //   KDE — xdg-desktop-portal-kde forwards `steps` unchanged to
        //   fakeInput->axis(WL_POINTER_AXIS_VERTICAL_SCROLL, ...), where positive vertical is down.
        //
        // The wire is the Windows convention — MOUSEEVENTF_WHEEL positive is away from the user — so
        // the two disagree and this branch had scrolled backwards on every Wayland desktop since it
        // was written. Both rows matter: a fix that dropped the sign entirely, or applied Math.Abs,
        // would satisfy one of them.
        var (service, sink) = Build();

        service.MouseScroll(0, wireDeltaY);

        Assert.Equal((0, expectedSteps), Assert.Single(sink.Scrolls));
    }

    [Fact]
    public void TheHorizontalSignIsNotInverted()
    {
        // THE HALF THAT MUST NOT MOVE. mutter maps axis 1 with steps > 0 to CLUTTER_SCROLL_RIGHT, and
        // the wire's positive dx is also right (MOUSEEVENTF_HWHEEL; xdotool's button 7). Without this,
        // "invert the portal scroll" reads as a whole-call instruction and the next person negates
        // both arguments — fixing vertical and breaking horizontal, with every other test still green.
        var (service, sink) = Build();

        service.MouseScroll(240, 0);

        Assert.Equal((2, 0), Assert.Single(sink.Scrolls));
    }

    [Fact]
    public void AZeroScrollIsStillForwarded()
    {
        // Characterization, not a requirement: the service does not filter, and the injector's own
        // early-return drops (0,0) before it reaches D-Bus. Asserted so that moving the guard from one
        // side to the other is a visible decision rather than a silent one.
        var (service, sink) = Build();

        service.MouseScroll(0, 0);

        Assert.Equal((0, 0), Assert.Single(sink.Scrolls));
    }
}

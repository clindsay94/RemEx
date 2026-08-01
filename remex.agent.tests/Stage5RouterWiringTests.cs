using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.Input.Linux;
using Remex.Agent.Services.RemoteDesktop.Linux;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins what setting a Stage 5 router actually does, which is less than its own documentation used to
/// claim (RemEx-7tkg).
///
/// NOTHING CONSTRUCTS <see cref="LinuxInputBackendRouter"/> IN PRODUCTION. DI registers
/// <see cref="LinuxInputSimulationService"/> as the input service, and the only way to reach the
/// router is <c>SetRouter</c>, which has no callers anywhere — <c>git log -S"SetRouter("</c> finds
/// exactly one commit, the one that added it. So the wiring was never written rather than written and
/// lost, and the rest of the codebase agrees: <c>RemoteDesktopHandler</c> still says stylus data "will
/// be preserved end-to-end once Stage 5 lands".
///
/// THE PART THAT WOULD BITE WHOEVER WIRES IT UP is not the missing call, which is obvious once looked
/// for, but the asymmetry underneath it: the router is consulted by four members only, so setting one
/// routes the KEYBOARD through it while every mouse event keeps going to the shell path. Silent, and
/// exactly the kind of half-connected state that reads as working. These tests exist so that
/// discovery happens here rather than on a Wayland machine.
///
/// They deliberately assert the CURRENT split rather than the desired one. When Stage 5 is wired,
/// they must fail — that is the handover, and the alternative is a comment, which cannot.
/// </summary>
public sealed class Stage5RouterWiringTests
{
    private sealed class Recorder
    {
        public List<string[]> Calls { get; } = [];
    }

    /// <summary>
    /// A service on the xdotool shell path, plus a router whose own launcher is separately observable.
    /// </summary>
    /// <remarks>
    /// Two recorders is the whole design: which one receives the call is the assertion. A single
    /// shared one could not tell "the router handled it" from "the shell path handled it", which is
    /// the only question these tests ask.
    /// </remarks>
    private static (LinuxInputSimulationService Service, Recorder ViaShell, Recorder ViaRouter) New()
    {
        var viaShell = new Recorder();
        var viaRouter = new Recorder();

        var service = new LinuxInputSimulationService(
            NullLogger<LinuxInputSimulationService>.Instance,
            new LinuxDesktopBackendStatus(
                DesktopEnvironment: "test",
                IsWaylandSession: false,
                IsKdePlasma: false,
                HasDisplayServer: true,
                InputTool: LinuxDesktopTool.Xdotool,
                InputToolPath: "/usr/bin/xdotool",
                CursorQueryTool: LinuxDesktopTool.None,
                CursorQueryToolPath: null,
                WindowControlTool: LinuxDesktopTool.None,
                WindowControlToolPath: null),
            (_, _, arguments) => { viaShell.Calls.Add(arguments); return string.Empty; });

        var router = new LinuxInputBackendRouter(
            new LinuxInputCapabilitySet
            {
                Tier = LinuxRemoteDesktopTier.X11Degraded,
                EisAvailable = false,
                PortalNotifyAvailable = false,
                UinputTabletAvailable = false,
                XdotoolPath = "/usr/bin/xdotool",
            },
            logger: null,
            arguments => viaRouter.Calls.Add(arguments));

        service.SetRouter(router);
        return (service, viaShell, viaRouter);
    }

    [Fact]
    public void AKeyPressDoesGoThroughTheRouterOnceOneIsSet()
    {
        var (service, viaShell, viaRouter) = New();

        service.KeyDown(0x1B);

        // The half that works. Worth asserting first, because it is what makes the other half
        // convincing: the router IS connected, so a reader cannot dismiss the mouse result below as
        // "the router simply is not hooked up in this test".
        Assert.Single(viaRouter.Calls);
        Assert.Empty(viaShell.Calls);
    }

    [Fact]
    public void AMouseButtonBYPASSESTheRouterEvenThoughOneIsSet()
    {
        var (service, viaShell, viaRouter) = New();

        service.MouseDown(MouseButtons.Left);

        // THE TRAP, in one assertion. Same service, same router, same instant — and the pointer goes
        // somewhere else entirely. MouseDown checks the portal injector and then the shell tool; it
        // never looks at _router. Whoever wires Stage 5 up will set a router, watch the keyboard
        // start flowing through libei, and reasonably conclude the pointer does too.
        Assert.Single(viaShell.Calls);
        Assert.Empty(viaRouter.Calls);
        Assert.Equal(["mousedown", "1"], viaShell.Calls[0]);
    }

    [Fact]
    public void EveryMousePathBypassesIt_NotJustTheButtons()
    {
        // Scoped deliberately: it is the whole pointer surface, not one overlooked method. Listed
        // individually rather than asserted as a count so a failure names which one moved.
        var (service, viaShell, viaRouter) = New();

        service.MoveMouse(10, 20);
        service.MouseMoveRelative(3, 4);
        service.MouseUp(MouseButtons.Right);
        service.MouseScroll(0, 120);

        Assert.Empty(viaRouter.Calls);
        Assert.Equal(4, viaShell.Calls.Count);
        Assert.Equal(["mousemove", "10", "20"], viaShell.Calls[0]);
        Assert.Equal(["mousemove_relative", "--", "3", "4"], viaShell.Calls[1]);
        Assert.Equal(["mouseup", "3"], viaShell.Calls[2]);
        Assert.Equal(["click", "4"], viaShell.Calls[3]);
    }

    [Fact]
    public void TypedTextAlsoBypassesIt()
    {
        // Separate from the pointer test because TypeText is neither mouse nor one of the four routed
        // members, and its payload is the attacker-chosen one — so if any single method were going to
        // be quietly rerouted later, this is the one whose behaviour change would matter most.
        var (service, viaShell, viaRouter) = New();

        service.TypeText("hello");

        Assert.Empty(viaRouter.Calls);
        Assert.Equal(["type", "--", "hello"], Assert.Single(viaShell.Calls));
    }

    [Fact]
    public void ClearingTheRouterPutsTheKeyboardBackOnTheShellPath()
    {
        // The null path is a documented part of SetRouter's contract and the only one with a
        // production-shaped precondition, since production never sets a router at all.
        var (service, viaShell, viaRouter) = New();

        service.SetRouter(null);
        service.KeyDown(0x1B);

        Assert.Empty(viaRouter.Calls);
        Assert.Equal(["keydown", "Escape"], Assert.Single(viaShell.Calls));
    }
}

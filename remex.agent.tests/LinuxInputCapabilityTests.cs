using Remex.Agent.Services;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.RemoteDesktop.Linux;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that the host only advertises input it can actually perform on Linux (RemEx-jvme).
///
/// STREAMING AND INJECTING ARE DIFFERENT QUESTIONS, and the bug was answering the second with the
/// first. <c>SupportsInputSimulation</c> returned <c>prereqReport.CanStream</c>, which is
/// <c>SelectedTier &gt;= X11Degraded</c> — and <c>DetermineTier</c> never probes xdotool or ydotool.
/// Any X11 session with a D-Bus session bus reaches <c>X11Degraded</c>, so a machine with neither
/// tool installed advertised input it could not perform. That is RemEx-is52's shape, and the check
/// that would have caught it existed but was unreachable, because <c>prereqReport</c> is assigned on
/// every Linux run.
///
/// WHAT THIS DOES NOT DO IS FIX A USER-VISIBLE SYMPTOM, and the first draft of this file said it did.
/// Nothing reads <c>SupportsInputSimulation</c> anywhere; the Android client gates on
/// <c>supportsRemoteDesktop</c>. So the phone behaves the same before and after, and these tests pin
/// an advertisement rather than a behaviour. That is still worth pinning: it is the precondition for
/// wiring a consumer (RemEx-q9zw) without shipping the same wrong answer to a client that now acts
/// on it.
///
/// THE FIX IS NOT SIMPLY ANDING WITH <c>SupportsBasicInput</c>, which is what makes these tests worth
/// having rather than one assertion. Three tiers, three different injectors: the Wayland tiers use
/// the portal injector and would be wrongly denied by a shell-tool requirement, while X11Degraded has
/// no portal injector and a shell tool is the only thing that can inject there. A fix that treated
/// them alike would trade this bug for its mirror image.
///
/// These run on any platform because the Linux decision was split out of the OS dispatch into a pure
/// function of its inputs. Left where it was, a test here would have hit the Windows branch and
/// answered a different question, so the only possible coverage was a Linux runner — and this is
/// exactly the decision that was wrong for months without anyone noticing.
/// </summary>
public sealed class LinuxInputCapabilityTests
{
    private static LinuxPrerequisiteReport Report(LinuxRemoteDesktopTier tier) =>
        new() { SelectedTier = tier };

    /// <summary>A probe result that reports an input tool, or none.</summary>
    private static LinuxDesktopBackendStatus Backend(bool hasTool) => new(
        DesktopEnvironment: "test",
        IsWaylandSession: false,
        IsKdePlasma: false,
        HasDisplayServer: true,
        InputTool: hasTool ? LinuxDesktopTool.Xdotool : LinuxDesktopTool.None,
        InputToolPath: hasTool ? "/usr/bin/xdotool" : null,
        CursorQueryTool: LinuxDesktopTool.None,
        CursorQueryToolPath: null,
        WindowControlTool: LinuxDesktopTool.None,
        WindowControlToolPath: null);

    private static bool Supports(LinuxRemoteDesktopTier tier, bool hasTool) =>
        HostCapabilitiesProvider.SupportsInputSimulationOnLinux(
            isInteractiveSession: true,
            linuxBackend: Backend(hasTool),
            prereqReport: Report(tier));

    [Fact]
    public void X11WithNoInputToolNoLongerClaimsItCanInject()
    {
        // THE BUG, in one assertion. This returned true before, on the strength of being able to
        // stream. The enum's own documentation agrees with the fix: X11Degraded is "shell-tool
        // capture and xdotool input only", so with no shell tool there is no input.
        Assert.False(Supports(LinuxRemoteDesktopTier.X11Degraded, hasTool: false));
    }

    [Fact]
    public void X11WithAnInputToolStillDoes()
    {
        // The other half, and the one that keeps this from being a blanket refusal: X11 with xdotool
        // installed is a perfectly good input host and must not have been broken by the fix.
        Assert.True(Supports(LinuxRemoteDesktopTier.X11Degraded, hasTool: true));
    }

    [Fact]
    public void TheWaylandTiersDoNotNeedAShellToolAtAll()
    {
        // THE MIRROR-IMAGE BUG THIS AVOIDS. The obvious fix — AND the tier with SupportsBasicInput —
        // would deny input on a working Wayland host that simply has no xdotool installed, because
        // there the injector is the portal, not a shell tool. DetermineTier already required the
        // RemoteDesktop portal to reach either of these tiers, which is the same thing the injector
        // needs, so the tier itself is the evidence.
        Assert.True(Supports(LinuxRemoteDesktopTier.PortalNoPen, hasTool: false));
        Assert.True(Supports(LinuxRemoteDesktopTier.WaylandNative, hasTool: false));
    }

    [Fact]
    public void AnUnsupportedTierIsStillNo()
    {
        // Unchanged behaviour, asserted so the new branch ordering cannot accidentally answer this
        // one from the tool instead of the tier: a machine that cannot stream has nothing to inject
        // into, whatever it has installed.
        Assert.False(Supports(LinuxRemoteDesktopTier.Unsupported, hasTool: true));
    }

    [Fact]
    public void ANonInteractiveSessionIsNoRegardlessOfEverythingElse()
    {
        Assert.False(HostCapabilitiesProvider.SupportsInputSimulationOnLinux(
            isInteractiveSession: false,
            linuxBackend: Backend(hasTool: true),
            prereqReport: Report(LinuxRemoteDesktopTier.WaylandNative)));
    }

    [Fact]
    public void WithNoPrereqReportItFallsBackToTheToolProbe()
    {
        // The pre-existing path, kept reachable and now actually reached by a test. It is the branch
        // the original bug hid behind: correct all along, and unreachable in production because
        // prereqReport is assigned on every Linux run.
        Assert.True(HostCapabilitiesProvider.SupportsInputSimulationOnLinux(
            true, Backend(hasTool: true), prereqReport: null));
        Assert.False(HostCapabilitiesProvider.SupportsInputSimulationOnLinux(
            true, Backend(hasTool: false), prereqReport: null));
    }
}

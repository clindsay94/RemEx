using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.Input.Linux;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// A refused portal prompt is reported instead of silently eating every event (RemEx-iaxc).
/// </summary>
/// <remarks>
/// <para>
/// <c>SupportsInputSimulation</c> is computed once at startup and answers true on Wayland, correctly:
/// the host CAN inject through the desktop portal. The injector is started lazily on the first input
/// event, deliberately, so the permission dialog appears only once a remote session actually begins
/// sending input. Decline it with no xdotool or ydotool installed and every click and keystroke is
/// discarded while the advertised capability still says input works — the stream keeps running, the
/// cursor never moves, and nothing anywhere says why.
/// </para>
/// <para>
/// **NO CAPABILITY FLAG CAN CARRY THIS, WHICH IS THE ENTIRE REASON FOR A RUNTIME SIGNAL.** At
/// capability time the prediction was right; the answer only changes once a user has answered a
/// dialog. So the property under test here is discovered by HAVING TRIED, and every test below drives
/// a real input event rather than inspecting startup state.
/// </para>
/// </remarks>
public sealed class LinuxInputSilentlyDroppedTests
{
    /// <summary>A portal that refuses to start, standing in for a declined permission dialog.</summary>
    private sealed class RefusingSink : IPortalInputSink
    {
        public int StartAttempts { get; private set; }

        public bool IsActive => false;

        public Task<bool> EnsureStartedAsync(CancellationToken ct = default)
        {
            StartAttempts++;
            return Task.FromResult(false);
        }

        public void NotifyPointerScrollDiscrete(int dx, int dy) { }
        public void NotifyPointerMotionRelative(double dx, double dy) { }
        public void NotifyPointerButton(int linuxButtonCode, bool pressed) { }
        public void NotifyKeyboardKeycode(int keycode, bool pressed) { }
        public void NotifyKeyboardKeysym(int keysym, bool pressed) { }
    }

    /// <summary>A portal that starts normally.</summary>
    private sealed class WorkingSink : IPortalInputSink
    {
        public bool IsActive => true;

        public Task<bool> EnsureStartedAsync(CancellationToken ct = default) => Task.FromResult(true);
        public void NotifyPointerScrollDiscrete(int dx, int dy) { }
        public void NotifyPointerMotionRelative(double dx, double dy) { }
        public void NotifyPointerButton(int linuxButtonCode, bool pressed) { }
        public void NotifyKeyboardKeycode(int keycode, bool pressed) { }
        public void NotifyKeyboardKeysym(int keysym, bool pressed) { }
    }

    private static LinuxDesktopBackendStatus Backend(LinuxDesktopTool tool) =>
        new(
            DesktopEnvironment: "test",
            IsWaylandSession: true,
            IsKdePlasma: false,
            HasDisplayServer: false,
            InputTool: tool,
            InputToolPath: tool == LinuxDesktopTool.None ? null : "/usr/bin/fake",
            CursorQueryTool: LinuxDesktopTool.None,
            CursorQueryToolPath: null,
            WindowControlTool: LinuxDesktopTool.None,
            WindowControlToolPath: null);

    private static LinuxInputSimulationService Build(IPortalInputSink sink, LinuxDesktopTool tool) =>
        new(
            NullLogger<LinuxInputSimulationService>.Instance,
            Backend(tool),
            // Never the real launcher: with a tool path present the shell path would otherwise try to
            // execute /usr/bin/fake once per event.
            launcher: (_, _, _) => string.Empty,
            captureLifetime: null,
            virtualDesktopOrigin: null,
            portalInjector: sink);

    [Fact]
    public void ARefusedPortalWithNoFallbackToolIsReported()
    {
        // THE BEAD. Portal declined, nothing to fall back to: every event from here is discarded, and
        // this is the only thing that will ever say so.
        var service = Build(new RefusingSink(), LinuxDesktopTool.None);

        service.MouseClick(1);

        Assert.NotNull(service.InputSilentlyDroppedReason);
    }

    [Fact]
    public void NothingIsReportedBeforeAnyInputHasBeenTried()
    {
        // **THE PROPERTY IS DISCOVERED BY TRYING, AND THIS IS WHAT PINS THAT.** Reporting at
        // construction would be a startup prediction again - the exact thing that cannot express this
        // failure - and would also fire on hosts where the user is about to APPROVE the dialog.
        var service = Build(new RefusingSink(), LinuxDesktopTool.None);

        Assert.Null(service.InputSilentlyDroppedReason);
    }

    [Fact]
    public void ARefusedPortalWithAShellToolAvailableIsNotReported()
    {
        // **A DECLINED PORTAL IS ONLY FATAL WHEN THERE IS NOTHING BEHIND IT.** With xdotool or ydotool
        // installed the events still reach the desktop, so this is a degradation and not a failure -
        // and telling the user their input is dead while it demonstrably works would be worse than
        // saying nothing. This is the assertion that stops the fix over-firing.
        var service = Build(new RefusingSink(), LinuxDesktopTool.Xdotool);

        service.MouseClick(1);

        Assert.Null(service.InputSilentlyDroppedReason);
    }

    [Fact]
    public void AWorkingPortalIsNotReported()
    {
        // A healthy session says nothing. Honest about its weight: this path SHORT-CIRCUITS on
        // IsActive before any of this bead's code runs, so it kills no mutation the two tests above
        // do not already kill - over-firing inside NotePortalStartFailed is caught by the shell-tool
        // test, and setting the reason at construction by the not-yet-tried test. Kept as a readable
        // statement of the normal case, not counted as coverage.
        var service = Build(new WorkingSink(), LinuxDesktopTool.None);

        service.MouseClick(1);

        Assert.Null(service.InputSilentlyDroppedReason);
    }

    [Fact]
    public void TheWireCodeMatchesTheLiteralTheAndroidClientMatchesOn()
    {
        // **THIS STRING IS A CROSS-LANGUAGE CONTRACT AND NOTHING ELSE ENFORCES IT.** The Kotlin side
        // hardcodes it independently (DESKTOP_ERR_INPUT_UNAVAILABLE in RemoteDesktopViewModel.kt) to
        // pull this message OFF the fatal error path. Rename the constant here and neither build
        // breaks - the client stops matching, the advisory falls through to the handler that clears
        // isStreaming and reconnects, and a declined permission prompt becomes a reconnect loop that
        // blanks a healthy picture once per input event. That is a worse failure than the silent one
        // this bead fixes, and it is one refactor away, so the literal is pinned rather than trusted.
        Assert.Equal("input_unavailable", DesktopErrorCodes.InputUnavailable);
    }

    [Fact]
    public void ThePortalIsOnlyAskedOnceEvenAfterItRefuses()
    {
        // The lazy start is gated by a one-shot flag, so a refusal must not re-prompt. Without this a
        // declined dialog would reappear on every mouse move, which is unusable - and it is the
        // failure mode a naive "retry until it works" fix introduces.
        var sink = new RefusingSink();
        var service = Build(sink, LinuxDesktopTool.None);

        service.MouseClick(1);
        service.MouseClick(1);
        service.MouseMoveRelative(5, 5);

        Assert.Equal(1, sink.StartAttempts);
    }
}

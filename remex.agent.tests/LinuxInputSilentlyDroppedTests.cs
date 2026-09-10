using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.Input.Linux;
using Remex.Core.Messages;
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
    private sealed class RefusingSink : IPortalInputSink, IDisposable
    {
        public int StartAttempts { get; private set; }

        /// <summary>
        /// Held open by default. A test that needs to observe state WHILE a start is in flight
        /// (RemEx-5bwpv's in-flight bail, and the "reason clears synchronously" retry test) calls
        /// <see cref="Gate"/>.Reset() before triggering the attempt and Set()s it to let the fake
        /// "dialog" resolve — always from a <c>finally</c>, so a failed assertion in between cannot
        /// leave the fake dialog open forever.
        /// </summary>
        public ManualResetEventSlim Gate { get; } = new(initialState: true);

        public bool IsActive => false;

        public Task<bool> EnsureStartedAsync(CancellationToken ct = default)
        {
            StartAttempts++;
            // Bounded even though production passes ct = default (which never cancels): a test that
            // forgets to Set() the gate degrades to a slow, clearly-attributed failure instead of an
            // indefinite hang (RemEx-5bwpv fix-round finding).
            Gate.Wait(TimeSpan.FromSeconds(10), ct);
            return Task.FromResult(false);
        }

        public void NotifyPointerScrollDiscrete(int dx, int dy) { }
        public void NotifyPointerMotionRelative(double dx, double dy) { }
        public void NotifyPointerButton(int linuxButtonCode, bool pressed) { }
        public void NotifyKeyboardKeycode(int keycode, bool pressed) { }
        public void NotifyKeyboardKeysym(int keysym, bool pressed) { }

        public void Dispose() => Gate.Dispose();
    }

    /// <summary>A portal that starts normally.</summary>
    private sealed class WorkingSink : IPortalInputSink
    {
        public int StartAttempts { get; private set; }

        public bool IsActive => true;

        public Task<bool> EnsureStartedAsync(CancellationToken ct = default)
        {
            StartAttempts++;
            return Task.FromResult(true);
        }

        public void NotifyPointerScrollDiscrete(int dx, int dy) { }
        public void NotifyPointerMotionRelative(double dx, double dy) { }
        public void NotifyPointerButton(int linuxButtonCode, bool pressed) { }
        public void NotifyKeyboardKeycode(int keycode, bool pressed) { }
        public void NotifyKeyboardKeysym(int keysym, bool pressed) { }
    }

    /// <summary>Refuses on its first start attempt, then reports active from the second attempt on.</summary>
    private sealed class FlipFlopSink : IPortalInputSink
    {
        public int StartAttempts { get; private set; }

        public bool IsActive => StartAttempts >= 2;

        public Task<bool> EnsureStartedAsync(CancellationToken ct = default)
        {
            StartAttempts++;
            return Task.FromResult(IsActive);
        }

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

    private static LinuxInputSimulationService Build(IPortalInputSink? sink, LinuxDesktopTool tool) =>
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
    public void ThePortalIsOnlyAskedOnceUntilExplicitlyRearmed()
    {
        // The lazy start is gated by a one-shot flag, so a refusal must not re-prompt on its own.
        // Without this a declined dialog would reappear on every mouse move, which is unusable - and
        // it is the failure mode a naive "retry until it works" fix introduces. Events alone still
        // never re-prompt (RemEx-5bwpv did not touch this); only an explicit re-arm does.
        using var sink = new RefusingSink();
        var service = Build(sink, LinuxDesktopTool.None);

        service.MouseClick(1);
        service.MouseClick(1);
        service.MouseMoveRelative(5, 5);

        Assert.Equal(1, sink.StartAttempts);
    }

    [Fact]
    public void ARearmAfterRefusalRetriesEagerlyWithNoFurtherInputEvent()
    {
        // Proves the retry is eager: the dialog must reappear on the tap itself, not on the next
        // touch/click reaching the input path.
        using var sink = new RefusingSink();
        var service = Build(sink, LinuxDesktopTool.None);
        service.MouseClick(1);
        Assert.Equal(1, sink.StartAttempts);

        var active = service.RearmPortalPermissionAndRetry();

        Assert.False(active);
        Assert.Equal(2, sink.StartAttempts);
    }

    [Fact]
    public void RetryInputPermissionClearsTheReasonSynchronouslyBeforeTheBackgroundRetryResolves()
    {
        // The handler orders RetryInputPermission() before it resets its own reported-once guard
        // specifically because this clears synchronously - if it did not, the frame loop could re-send
        // the stale reason before the retry has a chance to run (RemEx-5bwpv). The gate holds the
        // second attempt open so this is observed deterministically rather than raced.
        using var sink = new RefusingSink();
        var service = Build(sink, LinuxDesktopTool.None);
        service.MouseClick(1);
        Assert.NotNull(service.InputSilentlyDroppedReason);

        sink.Gate.Reset();
        try
        {
            service.RetryInputPermission();

            Assert.Null(service.InputSilentlyDroppedReason);
        }
        finally
        {
            // ALWAYS release the fake dialog, even if the assertion above just failed - otherwise the
            // background retry's thread stays blocked on the gate for the rest of the run instead of
            // the test failing cleanly (RemEx-5bwpv fix-round finding: "gated tests hang instead of
            // failing").
            sink.Gate.Set();
        }

        Assert.True(SpinWait.SpinUntil(() => service.InputSilentlyDroppedReason is not null, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ARearmWhileAStartIsAlreadyInFlightIsIgnored()
    {
        // LOAD-BEARING (per the handoff): while the fake "dialog" is open, _portalStartAttempted == 1
        // and !IsActive looks identical to "already refused". A naive reset here would queue a second
        // dialog the instant the first resolves.
        using var sink = new RefusingSink();
        var service = Build(sink, LinuxDesktopTool.None);
        sink.Gate.Reset();
        var inFlight = Task.Run(() => service.RearmPortalPermissionAndRetry());
        try
        {
            // Give the background attempt a chance to actually enter EnsureStartedAsync and block on
            // the gate before the foreground call races it.
            Assert.True(SpinWait.SpinUntil(() => sink.StartAttempts >= 1, TimeSpan.FromSeconds(5)));

            // BOUNDED, NOT INDEFINITE (RemEx-5bwpv fix-round finding). With the in-flight guard intact
            // this second call returns near-instantly (false): Monitor.TryEnter(_portalStartLock, 0)
            // fails immediately rather than waiting. If the guard regresses, this second call would
            // itself try to run EnsurePortalStarted and block on the still-held lock/dialog for as
            // long as the gate stays closed - Task.WhenAny turns that into a clean, named assertion
            // failure in 2 seconds instead of hanging the whole test run.
            var second = Task.Run(() => service.RearmPortalPermissionAndRetry());
            var winner = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(second, winner);
            Assert.False(await second);
            Assert.Equal(1, sink.StartAttempts);
        }
        finally
        {
            // ALWAYS release the fake dialog so the first (still in-flight) attempt can finish and no
            // thread leaks past this test, win or lose above.
            sink.Gate.Set();
            await Task.WhenAny(inFlight, Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void ARearmWithAFlipFlopSinkSucceedsOnTheSecondAttempt()
    {
        var sink = new FlipFlopSink();
        var service = Build(sink, LinuxDesktopTool.None);
        service.MouseClick(1);
        Assert.NotNull(service.InputSilentlyDroppedReason);

        var active = service.RearmPortalPermissionAndRetry();

        Assert.True(active);
        Assert.Null(service.InputSilentlyDroppedReason);
        Assert.Equal("portal-notify", service.BackendName);
    }

    [Fact]
    public void ARearmOfAnAlreadyWorkingPortalTouchesNothing()
    {
        var sink = new WorkingSink();
        var service = Build(sink, LinuxDesktopTool.None);
        service.MouseClick(1);

        var active = service.RearmPortalPermissionAndRetry();

        Assert.True(active);
        Assert.Equal(0, sink.StartAttempts);
    }

    [Fact]
    public void ARearmWithNoPortalInjectorReturnsFalseWithoutThrowing()
    {
        var service = Build(sink: null, LinuxDesktopTool.None);

        var active = service.RearmPortalPermissionAndRetry();

        Assert.False(active);
    }

    [Fact]
    public void ARearmWithAShellToolAvailableLeavesTheReasonNull()
    {
        using var sink = new RefusingSink();
        var service = Build(sink, LinuxDesktopTool.Xdotool);
        service.MouseClick(1);
        Assert.Null(service.InputSilentlyDroppedReason);

        var active = service.RearmPortalPermissionAndRetry();

        Assert.False(active);
        Assert.Null(service.InputSilentlyDroppedReason);
    }

    [Fact]
    public void TheRetryWireLiteralMatchesTheConstant()
    {
        // Same cross-language-contract reasoning as TheWireCodeMatchesTheLiteralTheAndroidClientMatchesOn
        // above: pin the literal so a rename here cannot silently desync from the Android sender.
        Assert.Equal("desktop_input_permission_retry", MessageTypes.DesktopInputPermissionRetry);
    }

    [Fact]
    public void TheHandlerCallsRetryBeforeResettingTheReportedGuard()
    {
        // NO RUNTIME SEAM REACHES THIS (RemEx-5bwpv fix-round finding): the WebSocket receive loop in
        // RemoteDesktopHandler has no test harness, so the two-part contract of its
        // DesktopInputPermissionRetry case - call RetryInputPermission(), THEN reset
        // _inputSilentlyDroppedReported - is pinned by reading the source instead. Dropping the reset,
        // or swapping the order, breaks the contract silently: the phone would never be told about a
        // second refusal, and no behavioural test anywhere would go red.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.agent", "Handlers", "RemoteDesktopHandler.cs"));

        var match = Regex.Match(
            source,
            @"case\s+MessageTypes\.DesktopInputPermissionRetry\s*:(?<body>.*?)break\s*;",
            RegexOptions.Singleline);

        Assert.True(match.Success, "Could not find a DesktopInputPermissionRetry case in RemoteDesktopHandler.cs.");

        var body = match.Groups["body"].Value;
        var retryIndex = body.IndexOf("_inputSimulation.RetryInputPermission();", StringComparison.Ordinal);
        var resetIndex = body.IndexOf(
            "Interlocked.Exchange(ref _inputSilentlyDroppedReported, 0)", StringComparison.Ordinal);

        Assert.True(retryIndex >= 0, "The case no longer calls _inputSimulation.RetryInputPermission().");
        Assert.True(resetIndex >= 0, "The case no longer resets _inputSilentlyDroppedReported to 0.");
        Assert.True(
            retryIndex < resetIndex,
            "RetryInputPermission() must run BEFORE the reported-once guard is reset - reversed, the "
            + "frame loop can re-send a stale reason before the retry has cleared it.");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}

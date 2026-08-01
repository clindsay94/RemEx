using System.Diagnostics;
using System.Runtime.Versioning;
using Remex.Core.Guards;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.RemoteDesktop.Linux;
using Remex.Agent.Services.RemoteDesktop.Windows;
using Remex.Agent.Services.ScreenCapture;

namespace Remex.Agent.Services;

public interface IHostCapabilitiesProvider
{
    HostCapabilities GetCurrent();

    /// <summary>
    /// Returns the last Linux prerequisite report, or null on non-Linux hosts.
    /// </summary>
    LinuxPrerequisiteReport? GetLinuxPrerequisiteReport();

    /// <summary>
    /// Returns a live Windows remote desktop diagnostics snapshot, or null on non-Windows hosts.
    /// </summary>
    WindowsRemoteDesktopDiagnosticReport? GetWindowsRemoteDesktopDiagnosticReport();
}

public sealed class HostCapabilitiesProvider : IHostCapabilitiesProvider
{
    // All inputs (OS, env vars, installed tools, session ID) are stable for the lifetime
    // of the host process. Cache the result to avoid spawning `which` subprocesses on
    // every health-check request or WebSocket handshake.
    private readonly IScreenCaptureService _screenCapture;
    private readonly IInputSimulationService _inputSimulation;
    private readonly Lazy<HostCapabilities> _cached;
    private LinuxPrerequisiteReport? _linuxReport;

    public HostCapabilitiesProvider(
        IScreenCaptureService screenCapture,
        IInputSimulationService inputSimulation)
    {
        _screenCapture = Guard.NotNull(screenCapture);
        _inputSimulation = Guard.NotNull(inputSimulation);
        _cached = new Lazy<HostCapabilities>(Build, isThreadSafe: true);
    }

    public HostCapabilities GetCurrent() => _cached.Value;

    public LinuxPrerequisiteReport? GetLinuxPrerequisiteReport()
        => OperatingSystem.IsLinux() ? _linuxReport : null;

    public WindowsRemoteDesktopDiagnosticReport? GetWindowsRemoteDesktopDiagnosticReport()
        => OperatingSystem.IsWindows() ? EvaluateWindowsDiagnostics() : null;

    private HostCapabilities Build()
    {
        var platform = GetPlatform();
        var isInteractiveSession = GetIsInteractiveSession();
        var runtimeMode = GetRuntimeMode(isInteractiveSession);
        var linuxBackend = OperatingSystem.IsLinux() ? LinuxDesktopBackendProbe.Probe() : null;
        var windowsReport = OperatingSystem.IsWindows() ? EvaluateWindowsDiagnostics() : null;

        // Stage 1: evaluate the full Linux prerequisites report and cache it.
        LinuxPrerequisiteReport? prereqReport = null;
        if (OperatingSystem.IsLinux())
        {
            prereqReport = EvaluateLinuxPrerequisites();
            _linuxReport = prereqReport;
        }

        var (supportsRemoteDesktop, remoteDesktopReason) = SupportsRemoteDesktop(isInteractiveSession, linuxBackend, prereqReport, windowsReport);
        var supportsInputSimulation = SupportsInputSimulation(isInteractiveSession, linuxBackend, prereqReport, windowsReport);
        var version = typeof(HostCapabilitiesProvider).Assembly.GetName().Version?.ToString() ?? "unknown";

        return new HostCapabilities
        {
            Version = version,
            Platform = platform,
            RuntimeMode = runtimeMode,
            IsInteractiveSession = isInteractiveSession,
            SupportsTelemetry = true,
            SupportsSystemCommands = true,
            SupportsWakeOnLan = true,
            SupportsProcessList = true,
            SupportsLauncherSync = true,
            SupportsRemoteDesktop = supportsRemoteDesktop,
            SupportsInputSimulation = supportsInputSimulation,
            SupportsCursorQuery = SupportsCursorQuery(isInteractiveSession, linuxBackend),
            SupportsAdvancedWindowControl = SupportsAdvancedWindowControl(isInteractiveSession, linuxBackend),
            SupportsInteractiveAppLaunch = !OperatingSystem.IsWindows() || isInteractiveSession,
            InputBackend = GetInputBackendName(linuxBackend, prereqReport, windowsReport),
            WindowControlBackend = linuxBackend?.WindowControlBackendName,
            RemoteDesktopUnavailableReason = remoteDesktopReason,
        };
    }

    private static string GetPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsAndroid())
        {
            return "android";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return "unknown";
    }

    private static bool GetIsInteractiveSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Environment.UserInteractive;
        }

        return Environment.UserInteractive && Process.GetCurrentProcess().SessionId != 0;
    }

    /// <summary>
    /// Classifies how this process is running. NOTE: the "service" identifier is legacy naming from
    /// the removed Windows-service design (RemEx-aep) — it does NOT mean RemEx is installed as a
    /// service, because no such install exists. It simply means Windows reported a non-interactive
    /// or Session-0 process, i.e. the same condition Linux reports as "headless". Kept as-is
    /// because the identifier is part of the serialized HostCapabilities contract; its consumers
    /// are PC-side only (ConnectionViewModel, AboutViewModel). The user-facing label was
    /// corrected to say "no desktop session" rather than "Service PC" (RemEx-9z0f).
    /// </summary>
    private static string GetRuntimeMode(bool isInteractiveSession)    {
        if (OperatingSystem.IsWindows())
        {
            return isInteractiveSession ? "interactive" : "service";
        }

        return isInteractiveSession ? "interactive" : "headless";
    }

    [SupportedOSPlatform("linux")]
    private static LinuxPrerequisiteReport EvaluateLinuxPrerequisites()
    {
        try
        {
            return new LinuxRemoteDesktopPrerequisites().Evaluate();
        }
        catch
        {
            // Never crash startup. Return an unsupported report on any evaluation failure.
            return new LinuxPrerequisiteReport
            {
                SelectedTier = LinuxRemoteDesktopTier.Unsupported,
                DegradedReason = "Prerequisite evaluation failed unexpectedly.",
            };
        }
    }

    private static (bool Supported, string? Reason) SupportsRemoteDesktop(
        bool isInteractiveSession,
        LinuxDesktopBackendStatus? linuxBackend,
        LinuxPrerequisiteReport? prereqReport,
        WindowsRemoteDesktopDiagnosticReport? windowsReport)
    {
        if (OperatingSystem.IsWindows())
        {
            return windowsReport?.SupportsRemoteDesktopSession ?? isInteractiveSession
                ? (true, windowsReport?.CaptureBackendDegradedReason)
                : (false, windowsReport?.RemoteDesktopUnavailableReason
                    ?? "Remote desktop needs RemEx to be running inside a signed-in Windows session. RemEx normally starts on its own when you sign in — if you are seeing this, sign in and start RemEx from the Start menu.");
        }

        if (OperatingSystem.IsLinux())
        {
            linuxBackend ??= LinuxDesktopBackendProbe.Probe();

            if (!isInteractiveSession)
                return (false, "Remote desktop requires an interactive logged-in user session. Start the host from a graphical session.");

            if (prereqReport is not null)
            {
                // Use the tier-aware report: any tier ≥ X11Degraded can stream.
                if (!prereqReport.CanStream)
                    return (false, prereqReport.DegradedReason ?? "Remote desktop is unavailable in the current Linux environment.");
                return (true, prereqReport.SelectedTier < LinuxRemoteDesktopTier.PortalNoPen
                    ? prereqReport.DegradedReason   // propagate degraded reason as informational
                    : null);
            }

            // Fallback when prerequisite evaluation was skipped.
            if (!linuxBackend.HasDisplayServer)
                return (false, "No display server detected. Start the host from inside an X11 or Wayland session ($DISPLAY or $WAYLAND_DISPLAY must be set).");

            if (!linuxBackend.SupportsBasicInput)
                return (false, "Remote desktop is unavailable: no input simulation tool found. Install xdotool (X11) or ydotool (Wayland).");

            return (true, null);
        }

        return (true, null);
    }

    /// <summary>
    /// Whether the host can actually inject input, as opposed to merely stream (RemEx-jvme).
    /// </summary>
    /// <remarks>
    /// <para>
    /// STREAMING AND INJECTING ARE DIFFERENT QUESTIONS ON LINUX, and answering the second with the
    /// first is what this fixes. It used to return <c>prereqReport.CanStream</c>, and
    /// <c>DetermineTier</c> never probes xdotool or ydotool — any X11 session with a session bus
    /// returns <c>X11Degraded</c> — so an X11 box with neither tool installed advertised input it
    /// could not perform, which is the shape of RemEx-is52.
    /// </para>
    /// <para>
    /// BE HONEST ABOUT WHAT THIS FIXES TODAY: nothing the user can see. A first draft of this comment
    /// said the client "enabled its input UI" on the strength of the flag; it does not. Nothing
    /// anywhere reads <c>SupportsInputSimulation</c> — the Android side gates every input and stream
    /// path on <c>supportsRemoteDesktop</c> instead — so the phone behaves identically before and
    /// after. The lost-taps symptom is real but is caused by the host having no injector, and this
    /// change does not by itself cure it. What it does is make the advertisement true, so that wiring
    /// a consumer to it (RemEx-q9zw) becomes correct rather than another way to be wrong.
    /// </para>
    /// <para>
    /// The three answers differ by what actually does the injecting, which is why this is not simply
    /// ANDed with <c>SupportsBasicInput</c>. The Wayland tiers inject through
    /// <c>LinuxInputSimulationService</c>'s portal injector, which is constructed for any Wayland
    /// session and needs the RemoteDesktop portal that <c>DetermineTier</c> already required to reach
    /// those tiers — so demanding a shell tool there would deny input on a working Wayland host that
    /// happens not to have xdotool installed. <c>X11Degraded</c> has no portal injector at all, so a
    /// shell tool is the only thing that can inject and its absence is decisive.
    /// </para>
    /// <para>
    /// Deliberately NOT expressed by making <c>DetermineTier</c> refuse the tier, and the real reason
    /// is stronger than the one first written here. "Capture and input are separate capabilities" is
    /// not supported by this codebase — nothing consumes the input flag at all. What rules it out is
    /// that <c>SelectedTier</c> drives things that ARE consumed: <c>SupportsRemoteDesktop</c>, which
    /// the Android client reads and gates streaming on, plus <c>GetInputBackendName</c> and
    /// <c>HostDoctor</c>'s exit codes. Probing for a shell tool inside <c>DetermineTier</c> would
    /// therefore have turned an X11 box with no xdotool from "streams, cannot inject" into "cannot
    /// stream at all", and made <c>--doctor</c> exit 1 on a machine that works.
    /// </para>
    /// <para>
    /// Internal rather than private so this can be tested as the pure function it is. Every input is
    /// already a parameter; the alternative is driving it through a probe of the machine the tests
    /// happen to run on, which would answer for that machine rather than for the three tiers.
    /// </para>
    /// </remarks>
    private static bool SupportsInputSimulation(
        bool isInteractiveSession,
        LinuxDesktopBackendStatus? linuxBackend,
        LinuxPrerequisiteReport? prereqReport,
        WindowsRemoteDesktopDiagnosticReport? windowsReport)
    {
        if (OperatingSystem.IsWindows()) return windowsReport?.SupportsRemoteDesktopSession ?? isInteractiveSession;
        if (OperatingSystem.IsLinux()) return SupportsInputSimulationOnLinux(isInteractiveSession, linuxBackend, prereqReport);
        return true;
    }

    /// <summary>
    /// The Linux half of <see cref="SupportsInputSimulation"/>, as a function of its inputs rather
    /// than of the machine it runs on.
    /// </summary>
    /// <remarks>
    /// Split out so it is testable from Windows, which is where this repo is usually built. Left
    /// inside <see cref="SupportsInputSimulation"/> it was unreachable from a test here — the OS
    /// dispatch above would take the Windows branch and answer a different question — so the only
    /// coverage possible would have been on a Linux runner, and this is precisely the decision that
    /// was wrong for months without anyone noticing.
    /// </remarks>
    internal static bool SupportsInputSimulationOnLinux(
        bool isInteractiveSession,
        LinuxDesktopBackendStatus? linuxBackend,
        LinuxPrerequisiteReport? prereqReport)
    {
        if (!isInteractiveSession) return false;

        if (prereqReport is not null)
        {
            // Cannot stream at all — no tier, nothing to inject into.
            if (!prereqReport.CanStream) return false;

            // PortalNoPen and WaylandNative: the portal injector is the mechanism, and the portal it
            // needs is the same one DetermineTier required to select these tiers.
            if (prereqReport.HasPortalCapture) return true;

            // X11Degraded: shell tools or nothing.
            linuxBackend ??= LinuxDesktopBackendProbe.Probe();
            return linuxBackend.SupportsBasicInput;
        }

        linuxBackend ??= LinuxDesktopBackendProbe.Probe();
        return linuxBackend.SupportsBasicInput;
    }

    private string? GetInputBackendName(
        LinuxDesktopBackendStatus? linuxBackend,
        LinuxPrerequisiteReport? prereqReport,
        WindowsRemoteDesktopDiagnosticReport? windowsReport)
    {
        if (OperatingSystem.IsWindows()) return windowsReport?.InputBackend ?? _inputSimulation.BackendName;
        if (!OperatingSystem.IsLinux()) return null;
        if (prereqReport is null) return linuxBackend?.InputBackendName;

        return prereqReport.SelectedTier switch
        {
            LinuxRemoteDesktopTier.WaylandNative => prereqReport.LibeiAvailable
                ? "libei" : "portal-notify",
            LinuxRemoteDesktopTier.PortalNoPen => "portal-notify",
            LinuxRemoteDesktopTier.X11Degraded => linuxBackend?.InputBackendName ?? "xdotool",
            _ => null,
        };
    }

    [SupportedOSPlatform("windows")]
    private WindowsRemoteDesktopDiagnosticReport EvaluateWindowsDiagnostics()
        => WindowsRemoteDesktopDiagnostics.Evaluate(
            _screenCapture as WindowsScreenCaptureService,
            _inputSimulation as WindowsInputSimulationService);

    private static bool SupportsCursorQuery(bool isInteractiveSession, LinuxDesktopBackendStatus? linuxBackend)
    {
        if (OperatingSystem.IsWindows())
        {
            return isInteractiveSession;
        }

        if (!OperatingSystem.IsLinux() || !isInteractiveSession)
        {
            return false;
        }

        linuxBackend ??= LinuxDesktopBackendProbe.Probe();
        return linuxBackend.SupportsCursorQuery;
    }

    /// <summary>
    /// Whether this host can list and manipulate desktop windows for the client's window panel.
    /// </summary>
    /// <remarks>
    /// WINDOWS WAS ANSWERING FALSE WHILE FULLY CAPABLE, which is the bug this method carried
    /// (RemEx-is52). <c>WindowsDesktopWindowControlService</c> is a complete Win32 implementation —
    /// list, activate, raise, minimize, close, resize — and <c>HostBootstrapper</c> registers it for
    /// <c>IDesktopWindowControlService</c> on Windows, overriding the unsupported default. The
    /// handler dispatches <c>desktop_window_query</c> straight to it with no capability check of its
    /// own, so the host would have served those queries perfectly well. Only this advertisement said
    /// otherwise, and the Android client hides its entire window-control section on it — so a
    /// working, registered, dispatched feature was invisible on the platform it was written for.
    /// The check simply was never updated when the Win32 backend landed.
    ///
    /// The interactive-session requirement is real on both platforms: enumerating and focusing
    /// windows means having a desktop to enumerate.
    ///
    /// Virtual-desktop MOVES remain unsupported on Windows (the virtual-desktop COM API is
    /// undocumented) and that one action returns a clear, non-fatal error — which is a property of
    /// the action, not a reason to withhold the whole capability.
    /// </remarks>
    private static bool SupportsAdvancedWindowControl(bool isInteractiveSession, LinuxDesktopBackendStatus? linuxBackend)
    {
        if (!isInteractiveSession)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        linuxBackend ??= LinuxDesktopBackendProbe.Probe();
        return linuxBackend.SupportsAdvancedWindowControl;
    }
}

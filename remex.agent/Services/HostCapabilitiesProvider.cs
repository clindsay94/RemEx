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

    private static string GetRuntimeMode(bool isInteractiveSession)
    {
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

    private static bool SupportsInputSimulation(
        bool isInteractiveSession,
        LinuxDesktopBackendStatus? linuxBackend,
        LinuxPrerequisiteReport? prereqReport,
        WindowsRemoteDesktopDiagnosticReport? windowsReport)
    {
        if (OperatingSystem.IsWindows()) return windowsReport?.SupportsRemoteDesktopSession ?? isInteractiveSession;
        if (OperatingSystem.IsLinux())
        {
            if (!isInteractiveSession) return false;

            // With Stage 5 input router: portal/EIS or legacy tools are all valid.
            if (prereqReport is not null)
                return prereqReport.CanStream; // any tier that can stream can inject basic input

            linuxBackend ??= LinuxDesktopBackendProbe.Probe();
            return linuxBackend.SupportsBasicInput;
        }
        return true;
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

    private static bool SupportsAdvancedWindowControl(bool isInteractiveSession, LinuxDesktopBackendStatus? linuxBackend)
    {
        if (!OperatingSystem.IsLinux() || !isInteractiveSession)
        {
            return false;
        }

        linuxBackend ??= LinuxDesktopBackendProbe.Probe();
        return linuxBackend.SupportsAdvancedWindowControl;
    }
}

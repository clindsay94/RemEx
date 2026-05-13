using System.Diagnostics;
using System.Runtime.Versioning;
using Remex.Core.Models;
using Remex.Host.Services.Input;
using Remex.Host.Services.RemoteDesktop.Linux;

namespace Remex.Host.Services;

public interface IHostCapabilitiesProvider
{
    HostCapabilities GetCurrent();

    /// <summary>
    /// Returns the last Linux prerequisite report, or null on non-Linux hosts.
    /// </summary>
    LinuxPrerequisiteReport? GetLinuxPrerequisiteReport();
}

public sealed class HostCapabilitiesProvider : IHostCapabilitiesProvider
{
    // All inputs (OS, env vars, installed tools, session ID) are stable for the lifetime
    // of the host process. Cache the result to avoid spawning `which` subprocesses on
    // every health-check request or WebSocket handshake.
    private readonly Lazy<HostCapabilities> _cached = new(Build, isThreadSafe: true);
    private static LinuxPrerequisiteReport? _linuxReport;

    public HostCapabilities GetCurrent() => _cached.Value;

    public LinuxPrerequisiteReport? GetLinuxPrerequisiteReport()
        => OperatingSystem.IsLinux() ? _linuxReport : null;

    private static HostCapabilities Build()
    {
        var platform = GetPlatform();
        var isInteractiveSession = GetIsInteractiveSession();
        var runtimeMode = GetRuntimeMode(isInteractiveSession);
        var linuxBackend = OperatingSystem.IsLinux() ? LinuxDesktopBackendProbe.Probe() : null;

        // Stage 1: evaluate the full Linux prerequisites report and cache it.
        LinuxPrerequisiteReport? prereqReport = null;
        if (OperatingSystem.IsLinux())
        {
            prereqReport = EvaluateLinuxPrerequisites();
            _linuxReport = prereqReport;
        }

        var (supportsRemoteDesktop, remoteDesktopReason) = SupportsRemoteDesktop(isInteractiveSession, linuxBackend, prereqReport);
        var supportsInputSimulation = SupportsInputSimulation(isInteractiveSession, linuxBackend, prereqReport);
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
            SupportsInteractiveAppLaunch = !OperatingSystem.IsWindows() || isInteractiveSession || Process.GetCurrentProcess().SessionId == 0,
            InputBackend = GetLinuxInputBackendName(linuxBackend, prereqReport),
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
        LinuxPrerequisiteReport? prereqReport)
    {
        if (OperatingSystem.IsWindows())
        {
            return isInteractiveSession
                ? (true, null)
                : (false, "Remote desktop requires an interactive logged-in user session. The service can stay online for commands, but a logged-in companion is required for screen streaming and input.");
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
        LinuxPrerequisiteReport? prereqReport)
    {
        if (OperatingSystem.IsWindows()) return isInteractiveSession;
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

    private static string? GetLinuxInputBackendName(
        LinuxDesktopBackendStatus? linuxBackend,
        LinuxPrerequisiteReport? prereqReport)
    {
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

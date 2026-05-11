using System.Diagnostics;
using Remex.Core.Models;
using Remex.Host.Services.Input;

namespace Remex.Host.Services;

public interface IHostCapabilitiesProvider
{
    HostCapabilities GetCurrent();
}

public sealed class HostCapabilitiesProvider : IHostCapabilitiesProvider
{
    // All inputs (OS, env vars, installed tools, session ID) are stable for the lifetime
    // of the host process. Cache the result to avoid spawning `which` subprocesses on
    // every health-check request or WebSocket handshake.
    private readonly Lazy<HostCapabilities> _cached = new(Build, isThreadSafe: true);

    public HostCapabilities GetCurrent() => _cached.Value;

    private static HostCapabilities Build()
    {
        var platform = GetPlatform();
        var isInteractiveSession = GetIsInteractiveSession();
        var runtimeMode = GetRuntimeMode(isInteractiveSession);
        var linuxBackend = OperatingSystem.IsLinux() ? LinuxDesktopBackendProbe.Probe() : null;
        var (supportsRemoteDesktop, remoteDesktopReason) = SupportsRemoteDesktop(isInteractiveSession, linuxBackend);
        var supportsInputSimulation = SupportsInputSimulation(isInteractiveSession, linuxBackend);
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
            InputBackend = linuxBackend?.InputBackendName,
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

    private static (bool Supported, string? Reason) SupportsRemoteDesktop(bool isInteractiveSession, LinuxDesktopBackendStatus? linuxBackend)
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
            {
                return (false, "Remote desktop requires an interactive logged-in user session. Start the host from a graphical session.");
            }

            if (!linuxBackend.HasDisplayServer)
            {
                return (false, "No display server detected. Start the host from inside an X11 or Wayland session ($DISPLAY or $WAYLAND_DISPLAY must be set).");
            }

            // Require at least one screenshot tool. The user-visible black-screen-on-RD bug
            // happens because LinuxScreenCaptureService falls back to spectacle/grim/scrot/ffmpeg
            // and silently returns empty bytes when none are present.
            var hasCaptureTool =
                LinuxDesktopBackendProbe.FindExecutable("spectacle") is not null ||
                LinuxDesktopBackendProbe.FindExecutable("grim") is not null ||
                LinuxDesktopBackendProbe.FindExecutable("scrot") is not null ||
                LinuxDesktopBackendProbe.FindExecutable("import") is not null ||
                LinuxDesktopBackendProbe.FindExecutable("gnome-screenshot") is not null ||
                LinuxDesktopBackendProbe.FindExecutable("ffmpeg") is not null;
            if (!hasCaptureTool)
            {
                return (false, "Remote desktop is unavailable: no screen capture tool found. Install one of: spectacle, grim (Wayland), scrot or ImageMagick (X11), or ffmpeg.");
            }

            // Input is required for a usable RD session. If only one of the two is missing,
            // the connection still streams but nothing the user does works — be explicit
            // rather than silent.
            if (!linuxBackend.SupportsBasicInput)
            {
                return (false, "Remote desktop is unavailable: no input simulation tool found. Install xdotool (X11) or ydotool (Wayland).");
            }

            return (true, null);
        }

        return (true, null);
    }

    private static bool SupportsInputSimulation(bool isInteractiveSession, LinuxDesktopBackendStatus? linuxBackend)
    {
        if (OperatingSystem.IsWindows()) return isInteractiveSession;
        if (OperatingSystem.IsLinux())
        {
            if (!isInteractiveSession) return false;
            linuxBackend ??= LinuxDesktopBackendProbe.Probe();
            return linuxBackend.SupportsBasicInput;
        }
        return true;
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

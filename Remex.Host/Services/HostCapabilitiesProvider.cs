using System.Diagnostics;
using Remex.Core.Models;

namespace Remex.Host.Services;

public interface IHostCapabilitiesProvider
{
    HostCapabilities GetCurrent();
}

public sealed class HostCapabilitiesProvider : IHostCapabilitiesProvider
{
    public HostCapabilities GetCurrent()
    {
        var platform = GetPlatform();
        var isInteractiveSession = GetIsInteractiveSession();
        var runtimeMode = GetRuntimeMode(isInteractiveSession);
        var (supportsRemoteDesktop, remoteDesktopReason) = SupportsRemoteDesktop(isInteractiveSession);
        var supportsInputSimulation = SupportsInputSimulation(isInteractiveSession);
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
            SupportsInteractiveAppLaunch = !OperatingSystem.IsWindows() || isInteractiveSession || Process.GetCurrentProcess().SessionId == 0,
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

    private static (bool Supported, string? Reason) SupportsRemoteDesktop(bool isInteractiveSession)
    {
        if (OperatingSystem.IsWindows())
        {
            return isInteractiveSession
                ? (true, null)
                : (false, "Remote desktop requires an interactive logged-in user session. The service can stay online for commands, but a logged-in companion is required for screen streaming and input.");
        }

        if (OperatingSystem.IsLinux())
        {
            if (!isInteractiveSession)
            {
                return (false, "Remote desktop requires an interactive logged-in user session. Start the host from a graphical session.");
            }

            // Require a display server (X11 or Wayland) to be available.
            var hasDisplay = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
            var hasWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            if (!hasDisplay && !hasWayland)
            {
                return (false, "No display server detected. Start the host from inside an X11 or Wayland session ($DISPLAY or $WAYLAND_DISPLAY must be set).");
            }

            // Require at least one screenshot tool. The user-visible black-screen-on-RD bug
            // happens because LinuxScreenCaptureService falls back to spectacle/grim/scrot/ffmpeg
            // and silently returns empty bytes when none are present.
            var hasCaptureTool =
                HasExecutable("spectacle") ||
                HasExecutable("grim") ||
                HasExecutable("scrot") ||
                HasExecutable("import") ||
                HasExecutable("gnome-screenshot") ||
                HasExecutable("ffmpeg");
            if (!hasCaptureTool)
            {
                return (false, "Remote desktop is unavailable: no screen capture tool found. Install one of: spectacle, grim (Wayland), scrot or ImageMagick (X11), or ffmpeg.");
            }

            // Input is required for a usable RD session. If only one of the two is missing,
            // the connection still streams but nothing the user does works — be explicit
            // rather than silent.
            if (!HasExecutable("xdotool") && !HasExecutable("ydotool"))
            {
                return (false, "Remote desktop is unavailable: no input simulation tool found. Install xdotool (X11) or ydotool (Wayland).");
            }

            return (true, null);
        }

        return (true, null);
    }

    private static bool SupportsInputSimulation(bool isInteractiveSession)
    {
        if (OperatingSystem.IsWindows()) return isInteractiveSession;
        if (OperatingSystem.IsLinux())
        {
            if (!isInteractiveSession) return false;
            return HasExecutable("xdotool") || HasExecutable("ydotool");
        }
        return true;
    }

    private static bool HasExecutable(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("which", name)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }
}

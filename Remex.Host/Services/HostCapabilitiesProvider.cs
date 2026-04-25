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
        var supportsRemoteDesktop = SupportsRemoteDesktop(isInteractiveSession);
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
            SupportsInputSimulation = supportsRemoteDesktop,
            SupportsInteractiveAppLaunch = !OperatingSystem.IsWindows() || isInteractiveSession || Process.GetCurrentProcess().SessionId == 0,
            RemoteDesktopUnavailableReason = supportsRemoteDesktop
                ? null
                : "Remote desktop requires an interactive logged-in user session. The service can stay online for commands, but a logged-in companion is required for screen streaming and input.",
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

    private static bool SupportsRemoteDesktop(bool isInteractiveSession)
    {
        if (OperatingSystem.IsWindows())
        {
            return isInteractiveSession;
        }

        if (OperatingSystem.IsLinux())
        {
            // On Linux, check for interactive session with display server
            if (!isInteractiveSession)
                return false;

            // Require a display server (X11 or Wayland) to be available
            var hasDisplay = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
            var hasWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            return hasDisplay || hasWayland;
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

namespace Remex.Core.Models;

/// <summary>
/// Describes what the currently connected host process can do in its active runtime context.
/// </summary>
public sealed record HostCapabilities
{
    /// <summary>Runtime mode for the active host process, such as interactive or service.</summary>
    public string RuntimeMode { get; init; } = "unknown";

    /// <summary>Operating system identifier for the host process.</summary>
    public string Platform { get; init; } = "unknown";

    /// <summary>Whether the host is currently running in an interactive user session.</summary>
    public bool IsInteractiveSession { get; init; }

    /// <summary>Whether the host can provide telemetry updates.</summary>
    public bool SupportsTelemetry { get; init; }

    /// <summary>Whether the host can execute system commands.</summary>
    public bool SupportsSystemCommands { get; init; }

    /// <summary>Whether the host can send Wake-on-LAN packets.</summary>
    public bool SupportsWakeOnLan { get; init; }

    /// <summary>Whether the host can return a process list.</summary>
    public bool SupportsProcessList { get; init; }

    /// <summary>Whether the host can synchronize launcher entries.</summary>
    public bool SupportsLauncherSync { get; init; }

    /// <summary>Whether the host can capture and stream the interactive desktop.</summary>
    public bool SupportsRemoteDesktop { get; init; }

    /// <summary>Whether the host can inject mouse and keyboard input into the active session.</summary>
    public bool SupportsInputSimulation { get; init; }

    /// <summary>Whether the host can perform interactive application launches.</summary>
    public bool SupportsInteractiveAppLaunch { get; init; }

    /// <summary>Human-readable reason explaining why remote desktop is unavailable.</summary>
    public string? RemoteDesktopUnavailableReason { get; init; }
}
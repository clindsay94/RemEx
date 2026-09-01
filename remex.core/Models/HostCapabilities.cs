namespace Remex.Core.Models;

/// <summary>
/// Describes what the currently connected host process can do in its active runtime context.
/// </summary>
public sealed record HostCapabilities
{
    /// <summary>Version of the host application.</summary>
    public string Version { get; init; } = "unknown";

    /// <summary>
    /// The host build's identity from <c>build/BuildId.targets</c>: git's short sha, with
    /// "+xxxx" appended when built from a dirty tree (RemEx-d9guj). Empty when the host predates
    /// the field or carries no stamp — receivers show nothing then, never "unknown".
    /// </summary>
    /// <remarks>
    /// ONLY THE SHA HALF COMPARES ACROSS PLATFORMS. MSBuild and Gradle hash the dirty suffix
    /// independently, so two dirty builds of the SAME tree carry different suffixes — a display
    /// that invites comparing them reports a difference that is not there. Renderers show the
    /// suffix as a bare '+'.
    /// </remarks>
    public string BuildId { get; init; } = "";

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

    /// <summary>Whether the host can report live cursor coordinates to remote clients.</summary>
    public bool SupportsCursorQuery { get; init; }

    /// <summary>Whether the host can perform advanced window-management actions for remote desktop.</summary>
    public bool SupportsAdvancedWindowControl { get; init; }

    /// <summary>Whether the host can perform interactive application launches.</summary>
    public bool SupportsInteractiveAppLaunch { get; init; }

    /// <summary>
    /// Whether the host can report what it is playing, as <c>media_state</c> (RemEx-xx6xf).
    /// </summary>
    /// <remarks>
    /// DEFAULTS TO FALSE, WHICH IS THE OPPOSITE OF <see cref="SupportsInputSimulation"/> AND
    /// DELIBERATE. That one defaults to true because an absent key means an older host that should
    /// keep working, and refusing to send it input would break a shipping feature. Here an absent key
    /// means a host that will never send a reading, and the honest UI for "no reading" is the neutral
    /// play triangle the phone drew before this existed. Defaulting true would make every older PC
    /// look like one that is about to report, and it never would.
    /// </remarks>
    public bool SupportsMediaState { get; init; }

    /// <summary>Name of the preferred mouse/keyboard input backend for the current runtime.</summary>
    public string? InputBackend { get; init; }

    /// <summary>Name of the backend used for advanced window-control features, if available.</summary>
    public string? WindowControlBackend { get; init; }

    /// <summary>Human-readable reason explaining why remote desktop is unavailable.</summary>
    public string? RemoteDesktopUnavailableReason { get; init; }

    /// <summary>
    /// This PC's primary MAC address, so a paired phone can wake it without the user typing one in.
    /// </summary>
    /// <remarks>
    /// ADDITIVE AND OPTIONAL (RemEx-izuj), so it needs no protocolVersion bump: an older client
    /// ignores the field, and a newer client treats its absence exactly as it treated every host
    /// before this existed - fall back to whatever the user entered manually.
    ///
    /// Empty when no suitable adapter was found. Empty means ASK THE USER; it must never be filled
    /// with a placeholder, because a wrong MAC fails to wake and says nothing about why.
    /// </remarks>
    public string? MacAddress { get; init; }
}

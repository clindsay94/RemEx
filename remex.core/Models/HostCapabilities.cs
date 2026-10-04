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

    /// <summary>
    /// This PC's machine name (<c>Environment.MachineName</c>), so the phone can label a known PC that
    /// has no nickname by its name instead of its IP address (RemEx-odqj5).
    /// </summary>
    /// <remarks>
    /// ADDITIVE AND OPTIONAL, so it needs no protocolVersion bump: an older phone ignores the field,
    /// and a newer phone treats null (an older PC) as "no name known" and falls back to the address.
    /// It travels on every connect rather than only in <see cref="PairingResponse"/>, so PCs paired
    /// before this existed learn their name too.
    /// </remarks>
    public string? MachineName { get; init; }

    /// <summary>
    /// Whether this host implements Routines (routines spec §7.5, RemEx-pp0rt.3).
    /// </summary>
    /// <remarks>
    /// Absent or false means a host that predates routines: the phone sends no <c>routines_sync</c>,
    /// greys out PC triggers with "Update RemEx on this PC", and fails a host-executed step of a
    /// phone-run routine with <c>pc_too_old</c> - it never falls back to the raw <c>command</c> verb,
    /// because that would bypass the destructive-step countdown. Static, so it belongs in this cached
    /// record; source availability (idle, session) is dynamic and travels in every
    /// <c>routine_sync_result</c> instead.
    /// </remarks>
    public bool SupportsRoutines { get; init; }

    /// <summary>The routine schema this host reads (<c>RoutineSchema.CurrentVersion</c>); 0 = none.</summary>
    public int RoutineSchemaVersion { get; init; }

    /// <summary>
    /// The power verbs this host can execute for a routine, never <c>WAKEONLAN</c> (D5). A verb that is
    /// missing is disabled in the phone editor and rejected at sync with <c>power_unsupported</c>.
    /// Null on a host that predates routines.
    /// </summary>
    public List<string>? RoutinePowerVerbs { get; init; }

    /// <summary>
    /// Whether this host shares the PC Home's pinned sensors with the phone, as <c>home_pins_sync</c>,
    /// and accepts <c>home_pins_change</c> (RemEx-wqo7a.5).
    /// </summary>
    /// <remarks>
    /// ADDITIVE, AND ABSENT MEANS FALSE ON PURPOSE: an older host would ignore a change, so a phone that
    /// sees false never sends one and keeps a phone-local pinned list instead, with no error shown.
    /// </remarks>
    public bool SupportsHomePinsSync { get; init; }

    /// <summary>
    /// Whether this host mirrors its sensor alert rules to the phone: sends <c>sensor_alert_fired</c> and
    /// <c>sensor_alert_rules</c>, and accepts <c>sensor_alerts_get</c>, <c>sensor_alert_set</c> and
    /// <c>sensor_alert_remove</c> (RemEx-pp4cm.12).
    /// </summary>
    /// <remarks>
    /// ADDITIVE, AND ABSENT MEANS FALSE ON PURPOSE: an older host would ignore every request, so a phone
    /// that sees false hides its alert controls instead of offering buttons that do nothing.
    /// </remarks>
    public bool SupportsSensorAlerts { get; init; }
}

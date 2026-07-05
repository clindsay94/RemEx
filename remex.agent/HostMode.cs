namespace Remex.Agent;

/// <summary>
/// How the consolidated Remex.Agent process runs.
/// </summary>
public enum HostMode
{
    /// <summary>
    /// Interactive PC host: the full command plane plus remote-desktop streaming (<c>/ws/desktop</c>).
    /// Used when launched with the GUI while a user is logged in.
    /// </summary>
    Full,

    /// <summary>
    /// Headless command agent (<c>--agent</c>): the command plane only — power commands, telemetry/status,
    /// pairing, mDNS, IPC. Remote-desktop streaming (<c>/ws/desktop</c>) is disabled, so no screen capture
    /// or portal session is started. Intended for the always-on background service that answers remote
    /// power commands while the machine is logged out.
    /// </summary>
    CommandAgent
}

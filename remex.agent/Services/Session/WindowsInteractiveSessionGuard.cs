using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;

namespace Remex.Agent.Services.Session;

/// <summary>
/// Windows interactive session guard. RemEx runs INSIDE the signed-in user's session, so it keeps that
/// session AWAKE — no idle sleep, no display-off — while one or more authenticated remote-control
/// clients are connected, and releases the hold when the last one disconnects. Ref-counted across
/// concurrent clients.
///
/// It deliberately does NOT reconnect/disconnect sessions: the old <c>tscon</c> + <c>WTSDisconnectSession</c>
/// dance only made sense for a Session-0 SYSTEM service resuming an orphaned RDP session. Living inside
/// the session, disconnecting it would lock the very desktop we capture and inject input into (Win32
/// error 5/6), so the guard never touches lock state — it only holds <c>SetThreadExecutionState</c>.
/// (RemEx-aep Phase 4)
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInteractiveSessionGuard : IInteractiveSessionGuard
{
    private readonly ILogger<WindowsInteractiveSessionGuard> _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _engaged = new();

    public WindowsInteractiveSessionGuard(ILogger<WindowsInteractiveSessionGuard> logger)
    {
        _logger = Guard.NotNull(logger);
    }

    public void EngageForRemoteControl(string clientId)
    {
        lock (_gate)
        {
            bool firstClient = _engaged.Count == 0;
            _engaged.Add(clientId);
            if (!firstClient)
            {
                return;
            }

            try
            {
                // Prevent idle sleep / display-off while a client is connected. ES_CONTINUOUS makes the
                // request sticky until we clear it on the last disconnect.
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
                _logger.LogInformation("Session guard: keeping the session awake for client {Client}.", Remex.Agent.Services.Security.LogRedaction.RedactClientId(clientId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session guard: failed to engage keep-awake for client {Client}.", Remex.Agent.Services.Security.LogRedaction.RedactClientId(clientId));
            }
        }
    }

    public void Disengage(string clientId)
    {
        lock (_gate)
        {
            if (!_engaged.Remove(clientId) || _engaged.Count > 0)
            {
                return;
            }

            try
            {
                // Drop the keep-awake hold; normal idle sleep / display-off resumes.
                SetThreadExecutionState(ES_CONTINUOUS);
                _logger.LogInformation("Session guard: released keep-awake after client {Client} disconnected.", Remex.Agent.Services.Security.LogRedaction.RedactClientId(clientId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session guard: failed to disengage keep-awake for client {Client}.", Remex.Agent.Services.Security.LogRedaction.RedactClientId(clientId));
            }
        }
    }

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}

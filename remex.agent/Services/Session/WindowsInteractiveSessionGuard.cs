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
/// error 5/6), so the guard never touches lock state — it only holds a keep-awake power request.
/// (RemEx-aep Phase 4)
///
/// The hold is a handle-based power request (<see cref="PowerRequestKeepAwakeBackend"/>), not
/// <c>SetThreadExecutionState</c>: engage and disengage arrive on different thread-pool threads, and
/// the per-thread execution state could not be cleared from the second one (PERF-TRACKER P1-10).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInteractiveSessionGuard : IInteractiveSessionGuard
{
    private readonly ILogger<WindowsInteractiveSessionGuard> _logger;
    private readonly IKeepAwakeBackend _backend;
    private readonly object _gate = new();
    private readonly HashSet<string> _engaged = new();

    // The live keep-awake hold, or null. Only touched under _gate.
    private IDisposable? _hold;

    public WindowsInteractiveSessionGuard(ILogger<WindowsInteractiveSessionGuard> logger)
        : this(logger, new PowerRequestKeepAwakeBackend())
    {
    }

    internal WindowsInteractiveSessionGuard(ILogger<WindowsInteractiveSessionGuard> logger, IKeepAwakeBackend backend)
    {
        _logger = Guard.NotNull(logger);
        _backend = Guard.NotNull(backend);
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
                // Prevent idle sleep / display-off while a client is connected. The hold stays until we
                // release it on the last disconnect, from whatever thread that happens on.
                _hold = _backend.Acquire();
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
                IDisposable? hold = _hold;
                _hold = null;
                hold?.Dispose();
                _logger.LogInformation("Session guard: released keep-awake after client {Client} disconnected.", Remex.Agent.Services.Security.LogRedaction.RedactClientId(clientId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session guard: failed to disengage keep-awake for client {Client}.", Remex.Agent.Services.Security.LogRedaction.RedactClientId(clientId));
            }
        }
    }
}

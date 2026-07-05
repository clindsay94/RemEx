namespace Remex.Agent.Services.Session;

/// <summary>
/// Keeps the signed-in interactive session AWAKE (no idle sleep / display-off) while one or more
/// authenticated remote-control clients are connected, and releases the hold when the last one
/// disconnects. Implementations are ref-counted: <see cref="EngageForRemoteControl"/> /
/// <see cref="Disengage"/> are balanced per client. No-op on platforms without this concept and
/// when the feature is disabled. (RemEx runs inside the session, so it never locks/unlocks it.)
/// </summary>
public interface IInteractiveSessionGuard
{
    /// <summary>Engage for one authenticated client. Idempotent per <paramref name="clientId"/>.</summary>
    void EngageForRemoteControl(string clientId);

    /// <summary>Disengage one client; the guard releases the keep-awake hold when the count reaches zero.</summary>
    void Disengage(string clientId);
}

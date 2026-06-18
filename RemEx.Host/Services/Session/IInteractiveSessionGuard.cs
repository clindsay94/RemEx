namespace Remex.Host.Services.Session;

/// <summary>
/// Keeps the signed-in interactive session usable (unlocked, on the console) while one or more
/// authenticated remote-control clients are connected, and restores the locked state when the last
/// one disconnects. Implementations are ref-counted: <see cref="EngageForRemoteControl"/> /
/// <see cref="Disengage"/> are balanced per client. No-op on platforms without this concept and
/// when the feature is disabled.
/// </summary>
public interface IInteractiveSessionGuard
{
    /// <summary>Engage for one authenticated client. Idempotent per <paramref name="clientId"/>.</summary>
    void EngageForRemoteControl(string clientId);

    /// <summary>Disengage one client; the guard restores the locked state when the count reaches zero.</summary>
    void Disengage(string clientId);
}

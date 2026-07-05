namespace Remex.Agent.Services.Session;

/// <summary>
/// Default guard used on non-Windows platforms and whenever the feature is disabled: does nothing,
/// so the session is never altered.
/// </summary>
public sealed class NoOpInteractiveSessionGuard : IInteractiveSessionGuard
{
    public void EngageForRemoteControl(string clientId) { }

    public void Disengage(string clientId) { }
}

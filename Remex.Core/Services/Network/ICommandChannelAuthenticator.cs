namespace Remex.Core.Services.Network;

/// <summary>
/// Authenticates inbound commands on the external TCP command channel (port 8338).
/// </summary>
/// <remarks>
/// The 8338 channel uses server-only TLS (no client certificate), so transport-level
/// authentication does not identify the caller. This abstraction lets <see cref="RemexNetworkListener"/>
/// — which lives in NativeAOT-safe <c>Remex.Core</c> and therefore cannot reference
/// <c>RemEx.Host</c> — gate commands on an application-layer paired identity. The host
/// implements this by delegating to its <c>PairedClientRegistry</c>, mirroring the
/// default-deny pairing gate enforced on the <c>/ws</c> WebSocket channel.
/// </remarks>
public interface ICommandChannelAuthenticator
{
    /// <summary>
    /// Returns <c>true</c> only when <paramref name="clientId"/> identifies a client that has
    /// completed PIN-based pairing. A null/blank/unknown identifier must return <c>false</c>
    /// (fail-closed, default-deny).
    /// </summary>
    /// <param name="clientId">The client identifier supplied on the command request.</param>
    bool IsClientAuthenticated(string? clientId);
}

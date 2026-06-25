using Remex.Core.Guards;
using Remex.Core.Services.Network;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Services.Network;

/// <summary>
/// Host implementation of <see cref="ICommandChannelAuthenticator"/>. Authenticates callers on the
/// external 8338 TCP command channel against the paired-client registry, mirroring the default-deny
/// pairing gate enforced on the <c>/ws</c> WebSocket channel (PROTO-1, RemEx-htt).
/// </summary>
/// <remarks>
/// The 8338 channel uses server-only TLS and therefore cannot identify the caller at the transport
/// layer; identity is asserted application-side via the <c>ClientId</c> on the command request and
/// validated here. An unknown, null, or unpaired identifier yields <c>false</c> (fail-closed).
/// </remarks>
public sealed class PairedClientChannelAuthenticator : ICommandChannelAuthenticator
{
    private readonly PairedClientRegistry _registry;

    public PairedClientChannelAuthenticator(PairedClientRegistry registry)
    {
        _registry = Guard.NotNull(registry);
    }

    /// <inheritdoc />
    public bool IsClientAuthenticated(string? clientId) => _registry.IsClientPaired(clientId);
}

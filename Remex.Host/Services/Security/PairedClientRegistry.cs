using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Remex.Host.Services.Security;

/// <summary>
/// Maintains a registry of clients that have successfully completed the PIN-based pairing handshake.
/// Allows pairing state to persist across WebSocket reconnections.
/// </summary>
public sealed class PairedClientRegistry(ILogger<PairedClientRegistry> logger)
{
    // In a production app, this would be backed by a persistent store (SQLite/File).
    // For 2.0, we use an in-memory registry.
    private readonly ConcurrentDictionary<string, byte> _pairedClientIds = new();

    public void RegisterClient(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;

        if (_pairedClientIds.TryAdd(clientId, 0))
        {
            logger.LogInformation("Client {ClientId} registered as paired.", clientId);
        }
    }

    public bool IsClientPaired(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        return _pairedClientIds.ContainsKey(clientId);
    }

    public void UnregisterClient(string clientId)
    {
        if (_pairedClientIds.TryRemove(clientId, out _))
        {
            logger.LogInformation("Client {ClientId} unregistered (pairing revoked).", clientId);
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Remex.Host.Services.Security;

/// <summary>
/// Maintains a registry of clients that have successfully completed the PIN-based pairing handshake.
/// Allows pairing state to persist across WebSocket reconnections.
/// </summary>
public sealed class PairedClientRegistry
{
    private readonly ILogger<PairedClientRegistry> _logger;
    private readonly ConcurrentDictionary<string, byte> _pairedClientIds = new(StringComparer.Ordinal);
    private readonly object _persistenceGate = new();
    private readonly string _storePath;

    public PairedClientRegistry(ILogger<PairedClientRegistry> logger)
        : this(logger, null)
    {
    }

    internal PairedClientRegistry(ILogger<PairedClientRegistry> logger, string? storePath)
    {
        _logger = logger;
        _storePath = storePath ?? GetDefaultStorePath();
        LoadFromDisk();
    }

    public void RegisterClient(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;

        if (_pairedClientIds.TryAdd(clientId, 0))
        {
            PersistToDisk();
            _logger.LogInformation("Client {ClientId} registered as paired.", clientId);
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
            PersistToDisk();
            _logger.LogInformation("Client {ClientId} unregistered (pairing revoked).", clientId);
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return;
            }

            var json = File.ReadAllText(_storePath);
            var clientIds = JsonSerializer.Deserialize<string[]>(json) ?? [];
            foreach (var clientId in clientIds.Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                _pairedClientIds[clientId] = 0;
            }

            _logger.LogInformation(
                "Loaded {Count} paired client IDs from {Path}.",
                _pairedClientIds.Count,
                _storePath);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse paired client registry at {Path}; starting with an empty registry.", _storePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to load paired client registry at {Path}; starting with an empty registry.", _storePath);
        }
    }

    private void PersistToDisk()
    {
        lock (_persistenceGate)
        {
            var directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _storePath + ".tmp";
            var clientIds = _pairedClientIds.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var json = JsonSerializer.Serialize(clientIds, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storePath, overwrite: true);
        }
    }

    private static string GetDefaultStorePath()
    {
        var baseFolder = OperatingSystem.IsAndroid()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Personal)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return Path.Combine(baseFolder, "Remex", "paired_clients.json");
    }
}

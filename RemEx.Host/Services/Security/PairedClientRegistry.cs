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

        // storePath is only supplied by tests; production resolves the default machine-wide path.
        // Only attempt legacy migration for the production path so tests stay hermetic.
        var useDefaultPath = storePath is null;
        _storePath = storePath ?? GetDefaultStorePath();

        if (useDefaultPath)
        {
            MigrateLegacyStoreIfNeeded();
        }

        LoadFromDisk();

        // Always log the resolved path + entry count. Previously, when the file did not exist
        // (the exact failure mode behind "commands rejected as unpaired after switching the host
        // to the LocalSystem service"), LoadFromDisk logged nothing, so the wrong store location
        // was invisible. This line makes the active registry path obvious at startup.
        _logger.LogInformation(
            "Paired client registry ready at {Path} ({Count} paired client(s)).",
            _storePath,
            _pairedClientIds.Count);
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
        if (OperatingSystem.IsAndroid())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                "Remex", "paired_clients.json");
        }

        if (OperatingSystem.IsWindows())
        {
            // Machine-wide store (C:\ProgramData\RemEx) so pairing state survives the host
            // running under a different account than the one that originally paired — most
            // importantly the LocalSystem Windows Service vs. an interactive session. The TLS
            // certificate already lives here (see CertificateService.GetCertificatePath), which
            // is why TLS/telemetry kept working after the switch to LocalSystem while the
            // per-user pairing registry was orphaned. Keeping the registry alongside the cert
            // means an account switch no longer makes previously-paired clients look unpaired.
            // ("RemEx" matches the cert's folder; on case-insensitive Windows it's the same dir.)
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RemEx", "paired_clients.json");
        }

        // Other desktop platforms (Linux/macOS) retain the per-user location.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Remex", "paired_clients.json");
    }

    /// <summary>
    /// Returns the legacy per-user store path used by earlier builds, or <c>null</c> when there is
    /// no distinct legacy location for the current platform/account.
    /// </summary>
    private static string? GetLegacyStorePath()
    {
        // Android never moved; nothing to migrate.
        if (OperatingSystem.IsAndroid())
        {
            return null;
        }

        var baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseFolder))
        {
            return null;
        }

        return Path.Combine(baseFolder, "Remex", "paired_clients.json");
    }

    /// <summary>
    /// One-time, best-effort migration: if the current (machine-wide) store does not yet exist but
    /// a legacy per-user store does and is readable by the current account, copy it forward so the
    /// operator does not have to re-pair. In the common LocalSystem case the legacy file lives in a
    /// user profile the service cannot see, so this is a no-op and a single re-pair is required.
    /// </summary>
    private void MigrateLegacyStoreIfNeeded()
        => TryMigrateLegacyStore(_storePath, GetLegacyStorePath(), _logger);

    /// <summary>
    /// Core migration logic, factored out for hermetic testing. Returns <c>true</c> when a legacy
    /// store was copied to <paramref name="targetPath"/>. No-op (returns <c>false</c>) when the
    /// target already exists, there is no distinct legacy path, or the legacy file is absent.
    /// </summary>
    internal static bool TryMigrateLegacyStore(string targetPath, string? legacyPath, ILogger logger)
    {
        try
        {
            if (File.Exists(targetPath))
            {
                return false;
            }

            if (legacyPath is null
                || string.Equals(legacyPath, targetPath, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(legacyPath))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(legacyPath, targetPath, overwrite: false);
            logger.LogInformation(
                "Migrated paired client registry from legacy path {Legacy} to {New}.",
                legacyPath,
                targetPath);
            return true;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to migrate legacy paired client registry; continuing with current store.");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "No access to migrate legacy paired client registry; continuing with current store.");
            return false;
        }
    }
}

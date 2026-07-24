using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Remex.Agent.Services.Security;

/// <summary>
/// Maintains a registry of clients that have successfully completed the PIN-based pairing handshake.
/// Allows pairing state to persist across WebSocket reconnections.
/// </summary>
/// <remarks>
/// Each paired client is bound to a 32-byte reconnect secret (the ECDH/HKDF-derived session key
/// from pairing). On reconnect the host challenges the client to prove possession of this secret
/// (HMAC over a fresh nonce) rather than trusting a bare clientId — a clientId alone is a guessable
/// bearer token and no longer authenticates. The secret is persisted (base64) keyed by clientId.
/// Legacy stores that listed only clientIds load as secret-less entries: such clients cannot answer
/// the challenge and must re-pair, which is the intended hardening.
/// </remarks>
public sealed class PairedClientRegistry
{
    private readonly ILogger<PairedClientRegistry> _logger;
    // clientId → base64 reconnect secret. An empty/whitespace value means a legacy, secret-less
    // entry that cannot satisfy proof-of-possession (the client must re-pair).
    private readonly ConcurrentDictionary<string, string> _pairedClients = new(StringComparer.Ordinal);
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
            _pairedClients.Count);
    }

    /// <summary>
    /// Legacy presence-only registration. Registers a clientId WITHOUT a reconnect secret, so the
    /// client cannot satisfy proof-of-possession and must re-pair to obtain one. Retained only for
    /// the desktop-stream presence gate and existing tests; the production pairing path must use
    /// <see cref="RegisterClient(string, byte[])"/> to bind the secret.
    /// </summary>
    public void RegisterClient(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;

        if (_pairedClients.TryAdd(clientId, string.Empty))
        {
            PersistToDisk();
            _logger.LogInformation("Client {ClientId} registered as paired (presence only, no reconnect secret).", LogRedaction.RedactClientId(clientId));
        }
    }

    /// <summary>
    /// Registers a paired client and binds it to its 32-byte reconnect secret (the pairing
    /// session key). The secret is what the client proves possession of on every reconnect.
    /// Overwrites any prior entry for the same clientId (a fresh pairing replaces the old secret).
    /// </summary>
    public void RegisterClient(string clientId, byte[] reconnectSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;
        ArgumentNullException.ThrowIfNull(reconnectSecret);
        if (reconnectSecret.Length == 0)
        {
            throw new ArgumentException("Reconnect secret must not be empty.", nameof(reconnectSecret));
        }

        var secretBase64 = Convert.ToBase64String(reconnectSecret);
        _pairedClients[clientId] = secretBase64;
        PersistToDisk();
        _logger.LogInformation("Client {ClientId} registered as paired with a reconnect secret.", LogRedaction.RedactClientId(clientId));
    }

    /// <summary>
    /// Returns whether a clientId has a paired entry. This is a presence check only — it does NOT
    /// authenticate. Reconnect authentication must use the proof-of-possession challenge
    /// (<see cref="TryGetReconnectSecret"/> + HMAC verification). Retained for the desktop-stream
    /// auth gate, which combines it with TLS cert pinning and (where applicable) the challenge.
    /// </summary>
    public bool IsClientPaired(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        return _pairedClients.ContainsKey(clientId);
    }

    /// <summary>
    /// Retrieves the reconnect secret bound to <paramref name="clientId"/> for proof-of-possession
    /// verification. Returns <c>false</c> when the client is unknown or is a legacy secret-less
    /// entry (which must re-pair). The returned bytes are a fresh copy the caller may zero.
    /// </summary>
    public bool TryGetReconnectSecret(string? clientId, out byte[] reconnectSecret)
    {
        reconnectSecret = [];
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        if (!_pairedClients.TryGetValue(clientId, out var secretBase64)
            || string.IsNullOrWhiteSpace(secretBase64))
        {
            return false;
        }

        try
        {
            reconnectSecret = Convert.FromBase64String(secretBase64);
            return reconnectSecret.Length > 0;
        }
        catch (FormatException)
        {
            reconnectSecret = [];
            return false;
        }
    }

    public void UnregisterClient(string clientId)
    {
        if (_pairedClients.TryRemove(clientId, out _))
        {
            PersistToDisk();
            _logger.LogInformation("Client {ClientId} unregistered (pairing revoked).", LogRedaction.RedactClientId(clientId));
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

            // New format: { "clientId": "base64Secret", ... }. Legacy format: ["clientId", ...].
            // Detect by the first non-whitespace token so we can migrate old stores in place.
            var trimmed = json.TrimStart();
            if (trimmed.StartsWith('['))
            {
                var clientIds = JsonSerializer.Deserialize<string[]>(json) ?? [];
                foreach (var clientId in clientIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                {
                    // Secret-less legacy entry: present but unable to satisfy proof-of-possession.
                    _pairedClients[clientId] = string.Empty;
                }

                _logger.LogWarning(
                    "Loaded {Count} legacy (secret-less) paired client IDs from {Path}. These clients must re-pair to obtain a reconnect secret.",
                    _pairedClients.Count,
                    _storePath);
            }
            else
            {
                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
                foreach (var kvp in entries.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)))
                {
                    _pairedClients[kvp.Key] = kvp.Value ?? string.Empty;
                }

                _logger.LogInformation(
                    "Loaded {Count} paired client(s) from {Path}.",
                    _pairedClients.Count,
                    _storePath);
            }
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
            var entries = _pairedClients
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storePath, overwrite: true);

            // PAIR-4: the store now holds per-client reconnect secrets, so it is sensitive key
            // material. Restrict it to the service identity (+ admins on Windows) on both OSes.
            RestrictStorePermissions(_storePath, _logger);
        }
    }

    /// <summary>
    /// Hardens the on-disk permissions of the registry store so only the service identity can read
    /// the reconnect secrets it now contains. Best-effort: a failure here is logged but never blocks
    /// pairing persistence. Cross-platform — Windows ACL vs. Unix 0600 owner-only.
    /// </summary>
    internal static void RestrictStorePermissions(string path, ILogger logger)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                RestrictStorePermissionsWindows(path);
            }
            else if (!OperatingSystem.IsAndroid())
            {
                // Owner read/write only (0600). Android keeps app-private storage semantics and has
                // no equivalent multi-user file ACL surface, so we skip it there.
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            // InvalidOperationException covers "The security identifier is not allowed to be the owner
            // of this object" — thrown by SetAccessControl when SetOwner targets a principal the
            // process can't assign without SeRestorePrivilege. Hardening is best-effort and must never
            // block pairing persistence, so any of these are logged and swallowed. (RemEx-sgj)
            logger.LogWarning(
                ex,
                "Failed to harden permissions on paired client registry at {Path}; the file may be readable by other local users.",
                path);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictStorePermissionsWindows(string path)
    {
        var fileInfo = new FileInfo(path);
        var security = new System.Security.AccessControl.FileSecurity();

        // Disable inheritance so the broad ProgramData ACL does not leak access to the reconnect
        // secrets; grant access only to the service identity, admins, and (when interactive) the user.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var localSystem = new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        var administrators = new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);

        // Owner: prefer LocalSystem (the production service identity), but only when the process can
        // actually assign it. Setting the owner to a principal other than yourself requires
        // SeRestorePrivilege, which the host lacks when it runs interactively as the signed-in user
        // rather than the LocalSystem service — there SetOwner(LocalSystem) throws
        // InvalidOperationException ("The security identifier is not allowed to be the owner of this
        // object"). The current user is always an assignable owner, so fall back to it. (RemEx-sgj)
        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        var ownerSid = current.IsSystem ? localSystem : (current.User ?? localSystem);
        security.SetOwner(ownerSid);

        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            localSystem,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            administrators,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));

        // When the host runs interactively, the signed-in user owns this instance and must keep access
        // to the store it just wrote; LocalSystem + Administrators still get full control so a later
        // run as the Windows service can read it. (No extra rule when already running as LocalSystem.)
        if (!current.IsSystem && current.User != null)
        {
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                current.User,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
        }

        fileInfo.SetAccessControl(security);
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

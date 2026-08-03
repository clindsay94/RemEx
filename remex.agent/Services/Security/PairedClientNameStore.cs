using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Desktop.Services;

namespace Remex.Agent.Services.Security;

/// <summary>
/// Remembers what each paired device called itself, so a reconnect is not nameless (RemEx-yzqs).
/// </summary>
/// <remarks>
/// <para>
/// A device name crosses the wire exactly once, on <c>pairing_request</c>, and initial pairing
/// happens once in a device's life. Every connection after that presents a client id and nothing
/// else — so without somewhere to keep the name, <c>ClientSessionRegistry</c> holds a named session
/// for the first connection and nameless ones forever after, and a presence display degrades to a
/// count for the entire life of the pairing.
/// </para>
/// <para>
/// **DELIBERATELY NOT STORED ALONGSIDE THE RECONNECT SECRET, WHICH IS WHAT THE BEAD ASKED FOR.**
/// <c>PairedClientRegistry</c> is the only authentication path in production; corrupting it bricks
/// every paired phone with no clear error on either end. Two things argued against putting a name in
/// it. Its file already carries two on-disk shapes (a legacy id array and the current id → secret
/// map) and an older build reading a third would fail the whole parse and start with an empty
/// registry — silently unpairing every device on a downgrade. And a name is attacker-influenced text
/// that would then share a parse with key material. <see cref="PairedDeviceDisplayName"/> (RemEx-9see) reached the
/// same conclusion for user-chosen names and states the rule plainly: renaming operates on a separate
/// map and the registry record stays byte-identical. This is that rule applied to the name the device
/// reports about itself.
/// </para>
/// <para>
/// Distinct from the user's own rename (RemEx-9see, surfaced by RemEx-nrsv): this is
/// what the DEVICE says it is, which a user override should outrank when both exist. Keeping them
/// apart is what lets a re-pair refresh the reported name without discarding a name the user chose.
/// </para>
/// </remarks>
public sealed class PairedClientNameStore
{
    private readonly ILogger<PairedClientNameStore> _logger;
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly object _persistenceGate = new();
    private readonly string _storePath;

    public PairedClientNameStore(ILogger<PairedClientNameStore> logger)
        : this(logger, null)
    {
    }

    internal PairedClientNameStore(ILogger<PairedClientNameStore> logger, string? storePath)
    {
        _logger = logger;
        _storePath = storePath ?? GetDefaultStorePath();
        LoadFromDisk();
    }

    /// <summary>
    /// Records what a device calls itself. Blank is ignored rather than stored.
    /// </summary>
    /// <remarks>
    /// CALL THIS ONLY ONCE PAIRING HAS VERIFIED. The name arrives on <c>pairing_request</c>, which is
    /// unauthenticated — anything on the network can send one, choosing both the client id and the
    /// name. Persisting at that point would let a stranger overwrite the stored name of a device it
    /// had merely overheard the id of, straight into the PC's UI, with no PIN.
    /// </remarks>
    public void Remember(string? clientId, string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;

        // Same normalization as a user-typed rename, and for the same reason: a card row is one line,
        // so an unbounded name pushes every other device's controls off screen. The device chooses
        // this string, so the cap is also what stops a hostile one from being unbounded on disk.
        var normalized = PairedDeviceDisplayName.Normalize(deviceName);
        if (normalized is null) return;

        if (_names.TryGetValue(clientId, out var existing) && string.Equals(existing, normalized, StringComparison.Ordinal))
        {
            // Re-pairing with an unchanged name is the common case; rewriting the file would be a
            // pointless disk write on every pairing.
            return;
        }

        _names[clientId] = normalized;
        PersistToDisk();
    }

    /// <summary>The name this device reported, or null if it never reported one.</summary>
    public string? Resolve(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;
        return _names.TryGetValue(clientId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : null;
    }

    /// <summary>Drops a device's name, for when its pairing is revoked.</summary>
    /// <remarks>
    /// NOTHING CALLS THIS YET because nothing revokes a pairing yet — <c>UnregisterClient</c> has no
    /// production caller either, and the unpair button is RemEx-nrsv. It exists so that whoever adds
    /// that button has the matching call available: a name left behind after an unpair would resurface
    /// against a re-paired device the user may have renamed in between.
    /// </remarks>
    public void Forget(string? clientId)
    {
        if (!string.IsNullOrWhiteSpace(clientId) && _names.TryRemove(clientId, out _))
        {
            PersistToDisk();
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_storePath)) return;

            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(_storePath)) ?? [];

            foreach (var kvp in entries.Where(kvp =>
                !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value)))
            {
                // Re-normalize on read: the cap could have changed, and the file is editable by
                // anyone who can reach it, so what is on disk is not necessarily what was written.
                var normalized = PairedDeviceDisplayName.Normalize(kvp.Value);
                if (normalized is not null) _names[kvp.Key] = normalized;
            }
        }
        catch (Exception ex)
        {
            // CATCHES EVERYTHING, ON PURPOSE. A name is cosmetic, and this runs in a CONSTRUCTOR that
            // DI resolves on the /ws path — so an escaping exception would not merely cost names, it
            // would kill the connection before the handler exists and block pairing outright. An
            // enumerated catch list means "cosmetic unless the failure is one I did not think of",
            // which is not the property this design is claiming.
            _logger.LogWarning(
                ex, "Failed to load paired client names at {Path}; devices will show without a name.", _storePath);
        }
    }

    private void PersistToDisk()
    {
        lock (_persistenceGate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                var ordered = _names
                    .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

                var tempPath = _storePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tempPath, _storePath, overwrite: true);

                // No secrets here, but the file sits beside the ones that are, and a device name is
                // personal enough ("Connor's Pixel") not to leave readable by every local account.
                PairedClientRegistry.RestrictStorePermissions(_storePath, _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex, "Failed to save paired client names to {Path}; the name will be lost on restart.", _storePath);
            }
        }
    }

    /// <summary>
    /// The names file, beside the pairing store it accompanies.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>PairedClientRegistry</c>'s directory choice deliberately — machine-wide on Windows
    /// so it survives a change of signed-in user exactly as the pairing it describes does, per-user
    /// elsewhere. A name that outlived its pairing, or died while the pairing lived, would be worse
    /// than no name. The two paths sharing a directory is pinned by a test so they cannot drift.
    /// </remarks>
    private static string GetDefaultStorePath()
    {
        if (OperatingSystem.IsAndroid())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                "Remex", "paired_client_names.json");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RemEx", "paired_client_names.json");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Remex", "paired_client_names.json");
    }

    /// <summary>Exposes the resolved store path so tests can pin it against the pairing store's.</summary>
    internal static string DefaultStorePathForTests => GetDefaultStorePath();
}

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Core.Services;
using Remex.Desktop.Services;

namespace Remex.Agent.Services.Security;

/// <summary>
/// The names the USER chose for their paired devices (RemEx-4gbp2).
/// </summary>
/// <remarks>
/// <para>
/// **SEPARATE FROM <see cref="PairedClientNameStore"/>, AND THAT SEPARATION IS THE WHOLE POINT.**
/// That store holds what the DEVICE says it is, learned once at pairing. This one holds what the
/// PERSON decided to call it. A first attempt at this bead wrote renames into the device store and
/// review caught it: one slot per device means a re-pair refreshes the reported name and silently
/// discards the user's choice, and clearing a rename deletes the reported name too, leaving the row
/// showing a raw client id with no way back from the UI. Both class docs already described the
/// split; the code was the thing that had drifted.
/// </para>
/// <para>
/// THE OVERRIDE OUTRANKS THE REPORTED NAME when both exist, which is what
/// <see cref="PairedDeviceDisplayName.Resolve"/> already implements — it takes the override map and
/// falls back. Keeping the two apart is what lets a re-pair update one without touching the other.
/// </para>
/// <para>
/// Its own file, beside the pairing store, for the reason the name store gives: a record that
/// outlived its pairing, or died while the pairing lived, is worse than no record. No secrets, and
/// nothing may ever gate a decision on these values — a name is for reading.
/// </para>
/// </remarks>
public sealed class PairedDeviceNameOverrideStore
{
    private readonly ILogger<PairedDeviceNameOverrideStore> _logger;
    private readonly ConcurrentDictionary<string, string> _overrides = new(StringComparer.Ordinal);
    private readonly object _persistenceGate = new();
    private readonly string _storePath;

    public PairedDeviceNameOverrideStore(ILogger<PairedDeviceNameOverrideStore> logger)
        : this(logger, null)
    {
    }

    internal PairedDeviceNameOverrideStore(ILogger<PairedDeviceNameOverrideStore> logger, string? storePath)
    {
        _logger = logger;
        _storePath = storePath ?? GetDefaultStorePath();
        LoadFromDisk();
    }

    /// <summary>Every override, as the map <see cref="PairedDeviceDisplayName.Resolve"/> expects.</summary>
    public IReadOnlyDictionary<string, string> Snapshot() =>
        new Dictionary<string, string>(_overrides, StringComparer.Ordinal);

    /// <summary>
    /// Sets the user's name for a device, or clears it when <paramref name="typed"/> is blank.
    /// </summary>
    /// <remarks>
    /// BLANK CLEARS THE OVERRIDE — it does NOT clear the device's reported name, which lives in the
    /// other store and comes back into view the moment the override goes. That is what makes a
    /// rename undoable: the user gets "Pixel 9" back, not a raw client id.
    /// </remarks>
    public void Set(string? clientId, string? typed)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;

        var normalized = PairedDeviceDisplayName.Normalize(typed);
        if (normalized is null) _overrides.TryRemove(clientId, out _);
        else _overrides[clientId] = normalized;

        PersistToDisk();
    }

    /// <summary>Drops a device's override, for when its pairing is revoked (RemEx-5lb90).</summary>
    public void Forget(string? clientId)
    {
        if (!string.IsNullOrWhiteSpace(clientId) && _overrides.TryRemove(clientId, out _))
        {
            PersistToDisk();
        }
    }

    private void LoadFromDisk()
    {
        // BEFORE THE EXISTENCE CHECK, for the reason PairedClientRegistry gives (RemEx-njzcx):
        // a first write killed between staging and rename leaves an orphan and NO store, so a sweep
        // below an early return is walked past on every startup of the machine that most needs it.
        RemexDataPaths.SweepStagingOrphans(_storePath);

        try
        {
            if (!File.Exists(_storePath)) return;

            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(_storePath)) ?? [];

            foreach (var kvp in entries.Where(kvp =>
                !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value)))
            {
                // Re-normalized on read: the cap could have changed, and the file is editable by
                // anyone who can reach it, so what is on disk is not necessarily what was written.
                var normalized = PairedDeviceDisplayName.Normalize(kvp.Value);
                if (normalized is not null) _overrides[kvp.Key] = normalized;
            }
        }
        catch (Exception ex)
        {
            // CATCHES EVERYTHING, ON PURPOSE, for the reason the sibling stores spell out: this runs
            // in a CONSTRUCTOR that DI resolves on the /ws path, so an escaping exception would not
            // merely cost names — it would kill the connection before the handler exists and block
            // pairing outright.
            _logger.LogWarning(
                ex, "Failed to load paired device name overrides at {Path}; devices will show their reported names.", _storePath);
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

                var ordered = _overrides
                    .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

                RemexDataPaths.WriteAllTextAtomic(
                    _storePath,
                    JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));

                PairedClientRegistry.RestrictStorePermissions(_storePath, _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to persist paired device name overrides to {Path}.", _storePath);
            }
        }
    }

    private static string GetDefaultStorePath()
    {
        if (RemexDataPaths.HostStateDirectoryOverride is { } stateDirectory)
        {
            return Path.Combine(stateDirectory, "paired_device_name_overrides.json");
        }

        if (OperatingSystem.IsAndroid())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                "Remex", "paired_device_name_overrides.json");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RemEx", "paired_device_name_overrides.json");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Remex", "paired_device_name_overrides.json");
    }

    /// <summary>Exposes the resolved store path so tests can pin it against the pairing store's.</summary>
    internal static string DefaultStorePathForTests => GetDefaultStorePath();
}

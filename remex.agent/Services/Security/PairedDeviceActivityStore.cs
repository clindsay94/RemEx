using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Core.Services;

namespace Remex.Agent.Services.Security;

/// <summary>
/// When each paired device first paired and when it was last seen (RemEx-nrsv).
/// </summary>
/// <remarks>
/// <para>
/// **A SEPARATE STORE, NOT TWO MORE FIELDS ON THE REGISTRY, AND THAT IS THE WHOLE DESIGN.**
/// <see cref="PairedClientRegistry"/> persists a flat <c>clientId → base64 secret</c> map and
/// <c>docs/REGRESSION-GUARDS.md</c> names it the ONLY authentication path in production: breaking it
/// silently bricks every device pairing, with no clear error on either end. Changing the shape of
/// that file is the single edit most likely to do it, and it would buy nothing — a timestamp is not
/// a credential and nothing authenticates with it. So the registry record stays byte-identical, and
/// this sits beside it exactly as <see cref="PairedClientNameStore"/> already does for names.
/// </para>
/// <para>
/// COSMETIC BY CONSTRUCTION. There are no secrets here, and nothing may ever gate a decision on
/// these values — they exist to be read out to a person. A missing or absurd timestamp must degrade
/// to "unknown" in the UI, never to a refusal, because this file is editable by anyone who can reach
/// it and its contents are therefore untrusted input.
/// </para>
/// <para>
/// It lives in the same directory as the registry it describes, for the reason the name store gives:
/// a record that outlived its pairing, or died while the pairing lived, is worse than no record.
/// </para>
/// </remarks>
public sealed class PairedDeviceActivityStore
{
    private readonly ILogger<PairedDeviceActivityStore> _logger;
    private readonly ConcurrentDictionary<string, PairedDeviceActivity> _activity = new(StringComparer.Ordinal);
    private readonly object _persistenceGate = new();
    private readonly string _storePath;

    public PairedDeviceActivityStore(ILogger<PairedDeviceActivityStore> logger)
        : this(logger, null)
    {
    }

    internal PairedDeviceActivityStore(ILogger<PairedDeviceActivityStore> logger, string? storePath)
    {
        _logger = logger;
        _storePath = storePath ?? GetDefaultStorePath();
        LoadFromDisk();
    }

    /// <summary>
    /// Records that <paramref name="clientId"/> paired now, if this is the first time we have seen it.
    /// </summary>
    /// <remarks>
    /// FIRST-PAIRED IS NEVER OVERWRITTEN, so a re-pair of a device that was never unpaired keeps the
    /// date the user actually remembers. WHEN an unpair drops the row (see <see cref="Forget"/>), a
    /// re-pair after a revoke will start a new one, which is the truthful answer there — but NOTHING
    /// CALLS Forget YET, because nothing revokes a pairing yet; the button is RemEx-kirdm. Until then
    /// a re-pair keeps the original date (review).
    /// <para>
    /// CALL THIS ONLY ONCE PAIRING HAS VERIFIED, for the same reason the name store says so: the
    /// client id arrives on an unauthenticated <c>pairing_request</c>, so recording at that point
    /// would let anything on the network mint rows for ids it had merely overheard.
    /// </para>
    /// </remarks>
    public void RecordPaired(string? clientId, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;

        _activity.AddOrUpdate(
            clientId,
            _ => new PairedDeviceActivity(nowUtc, nowUtc),
            // FIRST-PAIRED IS FILLED IF MISSING, never overwritten: a device whose only row came
            // from a reconnect has a null there, and this is the one moment we genuinely know it.
            (_, existing) => existing with { FirstPairedUtc = existing.FirstPairedUtc ?? nowUtc, LastSeenUtc = nowUtc });

        PersistToDisk();
    }

    /// <summary>Records that <paramref name="clientId"/> was seen now.</summary>
    /// <remarks>
    /// A device seen before it was ever recorded as paired gets a row with a LAST-SEEN and no
    /// first-paired, rather than being dropped or given an invented date. That is every device
    /// already paired when this store shipped: they have no row, and the first thing that happens to
    /// them is a reconnect.
    /// </remarks>
    public void RecordSeen(string? clientId, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;

        _activity.AddOrUpdate(
            clientId,
            // NO FIRST-PAIRED DATE INVENTED HERE (review). This is the only writer that runs for a
            // device paired before this store existed, and stamping "now" would report today as the
            // day every one of the user's phones was paired.
            _ => new PairedDeviceActivity(FirstPairedUtc: null, LastSeenUtc: nowUtc),
            (_, existing) => existing with { LastSeenUtc = nowUtc });

        PersistToDisk();
    }

    /// <summary>What is known about a device, or null when nothing is.</summary>
    public PairedDeviceActivity? Resolve(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;
        return _activity.TryGetValue(clientId, out var activity) ? activity : null;
    }

    /// <summary>Drops a device's row, for when its pairing is revoked.</summary>
    /// <remarks>
    /// The matching call to <see cref="PairedClientNameStore.Forget"/>, and it must be made from the
    /// same place for the same reason: a row left behind after an unpair resurfaces against a
    /// re-paired device, showing a "first paired" date from a pairing the user deliberately ended.
    /// </remarks>
    public void Forget(string? clientId)
    {
        if (!string.IsNullOrWhiteSpace(clientId) && _activity.TryRemove(clientId, out _))
        {
            PersistToDisk();
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_storePath)) return;

            var entries = JsonSerializer.Deserialize<Dictionary<string, PairedDeviceActivity>>(
                File.ReadAllText(_storePath)) ?? [];

            foreach (var kvp in entries.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)))
            {
                _activity[kvp.Key] = kvp.Value;
            }
        }
        catch (Exception ex)
        {
            // CATCHES EVERYTHING, ON PURPOSE, and for the reason the name store spells out: this runs
            // in a CONSTRUCTOR that DI resolves on the /ws path, so an escaping exception would not
            // merely cost timestamps — it would kill the connection before the handler exists and
            // block pairing outright. A timestamp is cosmetic; blocking pairing to protect one is not
            // a trade anybody would make deliberately.
            _logger.LogWarning(
                ex, "Failed to load paired device activity at {Path}; devices will show without dates.", _storePath);
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

                var ordered = _activity
                    .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

                // Per-write staging name, not a fixed <store>.tmp shared with every other writer of
                // this file — see RemexDataPaths.WriteAllTextAtomic (RemEx-kow1).
                RemexDataPaths.WriteAllTextAtomic(
                    _storePath,
                    JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));

                // No secrets here, but the file sits beside the ones that are, and when a device was
                // last connected is personal enough not to leave readable by every local account.
                PairedClientRegistry.RestrictStorePermissions(_storePath, _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to persist paired device activity to {Path}.", _storePath);
            }
        }
    }

    /// <summary>
    /// Resolves the store path, in the same directory as the pairing store it describes.
    /// </summary>
    /// <remarks>
    /// Deliberately a copy of <see cref="PairedClientNameStore"/>'s shape rather than a shared helper:
    /// the three stores must land in ONE directory and a test pins that they do, so the thing to keep
    /// identical is the answer, not the code. The redirect branch comes ahead of every platform
    /// branch for the reason RemEx-4u29 records.
    /// </remarks>
    private static string GetDefaultStorePath()
    {
        if (RemexDataPaths.HostStateDirectoryOverride is { } stateDirectory)
        {
            return Path.Combine(stateDirectory, "paired_device_activity.json");
        }

        if (OperatingSystem.IsAndroid())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                "Remex", "paired_device_activity.json");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RemEx", "paired_device_activity.json");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Remex", "paired_device_activity.json");
    }

    /// <summary>Exposes the resolved store path so tests can pin it against the pairing store's.</summary>
    internal static string DefaultStorePathForTests => GetDefaultStorePath();
}

/// <summary>When a paired device first paired, and when it was last seen. Both UTC.</summary>
/// <remarks>
/// <c>FirstPairedUtc</c> IS NULLABLE AND THAT IS LOAD-BEARING (review). Every device paired before
/// this store existed has no row, and the first thing that happens to such a device is a reconnect —
/// so if <see cref="PairedDeviceActivityStore.RecordSeen"/> minted a first-paired date, then on the
/// day this ships every phone a user owns would report "first paired: today", and the "unknown"
/// rendering the row contract was designed around would never fire once. A null says the true thing.
/// </remarks>
public sealed record PairedDeviceActivity(DateTimeOffset? FirstPairedUtc, DateTimeOffset LastSeenUtc);

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Agent.Handlers;
using Remex.Core.Guards;
using Remex.Core.Models;
using Remex.Core.Serialization;
using Remex.Core.Services;
using Remex.Core.Services.Theme;

namespace Remex.Agent.Services.Theme;

/// <summary>
/// The host's live implementation of <see cref="IPhoneThemeSnapshotStore"/> (RemEx-sudp8). One
/// instance per running agent, registered as a singleton so <c>PingPongHandler</c> (writer) and the
/// desktop's <c>CustomizationViewModel</c> (reader, via <c>App.EmbeddedHostServices</c>) share it.
/// </summary>
/// <remarks>
/// <para>
/// PERSISTED TO DISK (RemEx-qean1). The host's instance keeps the last accepted snapshot in
/// <c>phone_theme_snapshot.json</c> under <see cref="RemexDataPaths.PerUserDirectory"/> and loads it
/// in the constructor, so "Match phone" is offered straight after a PC restart instead of only once
/// the phone happens to change its theme again. RemEx-sudp8 kept this in memory because persistence
/// was out of that slice's scope; the phone only pushes on its own start or a theme change, so an
/// in-memory store lost the button on every host restart.
/// </para>
/// <para>
/// A MISSING, UNREADABLE OR CORRUPT FILE MEANS "NO SNAPSHOT", NEVER A CRASH. The loaded value goes
/// through the same <see cref="PingPongHandler.IsValidThemeSync"/> check as a live <c>theme_sync</c>,
/// so a hand-edited file cannot paint the sheet with a value the wire path would have rejected.
/// </para>
/// <para>
/// The parameterless constructor is memory-only and touches no disk. It exists for the many handler
/// tests that need a store but must never write to the real per-user directory.
/// </para>
/// <para>
/// Lock-guarded rather than a <c>ConcurrentDictionary</c>-style store like
/// <c>PairedClientNameStore</c>: there is exactly one field to protect, not a keyed collection.
/// </para>
/// </remarks>
public sealed class PhoneThemeSnapshotStore : IPhoneThemeSnapshotStore
{
    internal const string FileName = "phone_theme_snapshot.json";

    private readonly object _gate = new();
    private readonly ILogger<PhoneThemeSnapshotStore>? _logger;
    private readonly string? _storePath;
    private PhoneThemeSnapshot? _latest;
    private string? _lastPersistedJson;

    /// <summary>Memory-only store: nothing is loaded from or written to disk.</summary>
    public PhoneThemeSnapshotStore()
    {
    }

    /// <summary>The host's persisted store, backed by the per-user data directory.</summary>
    public PhoneThemeSnapshotStore(ILogger<PhoneThemeSnapshotStore> logger)
        : this(logger, Path.Combine(RemexDataPaths.PerUserDirectory, FileName))
    {
    }

    internal PhoneThemeSnapshotStore(ILogger<PhoneThemeSnapshotStore> logger, string storePath)
    {
        _logger = Guard.NotNull(logger);
        _storePath = Guard.NotNull(storePath);
        LoadFromDisk();
    }

    public PhoneThemeSnapshot? Latest
    {
        get { lock (_gate) return _latest; }
    }

    public event Action<PhoneThemeSnapshot?>? Changed;

    public void Set(PhoneThemeSnapshot snapshot)
    {
        lock (_gate)
        {
            _latest = snapshot;
            PersistLocked(snapshot);
        }

        Changed?.Invoke(snapshot);
    }

    private void LoadFromDisk()
    {
        if (_storePath is null || !File.Exists(_storePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_storePath);
            var loaded = JsonSerializer.Deserialize(json, RemexJsonSerializerContext.Default.PhoneThemeSnapshot);
            if (loaded is not null && PingPongHandler.IsValidThemeSync(loaded))
            {
                _latest = loaded;
                _lastPersistedJson = json;
            }
            else
            {
                _logger?.LogWarning(
                    "Ignoring the saved phone theme in {Path}: it is empty or not a valid theme.", _storePath);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            _logger?.LogWarning(ex, "Could not read the saved phone theme from {Path}; starting without one.", _storePath);
        }
    }

    private void PersistLocked(PhoneThemeSnapshot snapshot)
    {
        if (_storePath is null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(snapshot, RemexJsonSerializerContext.Default.PhoneThemeSnapshot);

            // The phone now resends its theme on every reconnect (RemEx-qean1); skip the write when
            // nothing on disk would change.
            if (string.Equals(json, _lastPersistedJson, StringComparison.Ordinal))
            {
                return;
            }

            if (Path.GetDirectoryName(_storePath) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            RemexDataPaths.WriteAllTextAtomic(_storePath, json);
            _lastPersistedJson = json;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Failed to save the phone theme to {Path}.", _storePath);
        }
    }
}

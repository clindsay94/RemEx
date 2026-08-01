using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Core.Serialization;

namespace Remex.Desktop.Services.Security;

/// <summary>
/// Persists host SPKI (Subject Public Key Info) SHA-256 hashes
/// for certificate pinning. Stored as JSON in LocalApplicationData.
/// </summary>
public sealed class PinnedCertStore
{
    private readonly ILogger<PinnedCertStore> _logger;
    private readonly string _storePath;
    private Dictionary<string, string> _pins = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loaded;

    public PinnedCertStore(ILogger<PinnedCertStore> logger)
    {
        _logger = logger;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _storePath = Path.Combine(appData, "Remex", "pinned_hosts.json");
    }

    internal PinnedCertStore(ILogger<PinnedCertStore> logger, string storePath)
    {
        _logger = logger;
        _storePath = storePath;
    }

    /// <summary>
    /// Returns true if the given hostId has a pinned SPKI hash.
    /// </summary>
    public async Task<bool> IsPinnedAsync(string hostId)
    {
        await EnsureLoadedAsync();
        return _pins.ContainsKey(hostId);
    }

    /// <summary>
    /// Gets the pinned SPKI hash for a host, or null if not pinned.
    /// </summary>
    public async Task<string?> GetPinAsync(string hostId)
    {
        await EnsureLoadedAsync();
        return _pins.TryGetValue(hostId, out var hash) ? hash : null;
    }

    /// <summary>
    /// Gets the pinned SPKI hash for a host synchronously.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NO PRODUCTION CALLERS — only <c>PinnedCertStoreTests.GetPin_Sync_ReturnsStoredHash</c>.
    /// Production reads pins through <see cref="GetAllPinsAsync"/>. DO NOT CALL THIS FROM THE UI
    /// THREAD if that changes.
    /// </para>
    /// <para>
    /// THE HAZARD, SCOPED EXACTLY. This is the only caller of the synchronous
    /// <see cref="EnsureLoaded"/>, which blocks on <c>_lock</c>. The constructible deadlock is
    /// against <see cref="EnsureLoadedAsync"/> alone: it holds the lock across
    /// <c>File.ReadAllTextAsync</c>, awaited without <c>ConfigureAwait(false)</c> (banned repo-wide),
    /// so an instance started on the UI thread posts its continuation back there. Block that thread
    /// here and the continuation never runs, the lock is never released, and the app hangs holding
    /// the certificate-pin store.
    /// </para>
    /// <para>
    /// AND ONLY DURING FIRST LOAD. <see cref="SetPinAsync"/> and <see cref="RemovePinAsync"/> also
    /// hold the lock across an await, but they <c>await EnsureLoadedAsync()</c> BEFORE taking it — so
    /// by then <c>_loaded</c> is set and the synchronous path returns at its own guard without ever
    /// reaching <c>_lock.Wait()</c>. A first draft of this note named those two as well, which
    /// overstated it. (That guard reads a non-volatile <c>bool</c> outside the lock, which is a
    /// separate small sin.)
    /// </para>
    /// <para>
    /// Audited under RemEx-r9tv, which found this the only one of the repo's sync-over-async sites
    /// that could deadlock at all. Documented rather than deleted because removing a public API — and
    /// its test — is the operator's call; if you need a synchronous read, make it not take the lock.
    /// </para>
    /// </remarks>
    public string? GetPin(string hostId)
    {
        EnsureLoaded();
        return _pins.TryGetValue(hostId, out var hash) ? hash : null;
    }

    /// <summary>
    /// Sets or updates the SPKI hash for a host.
    /// </summary>
    public async Task SetPinAsync(string hostId, string spkiHashBase64)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            _pins[hostId] = spkiHashBase64;
            await SaveAsync();
            _logger.LogInformation("Pinned host {HostId} with SPKI hash {Hash}", hostId, spkiHashBase64);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes the pin for a host.
    /// </summary>
    public async Task RemovePinAsync(string hostId)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            if (_pins.Remove(hostId))
            {
                await SaveAsync();
                _logger.LogInformation("Unpinned host {HostId}", hostId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns all pinned hosts.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAllPinsAsync()
    {
        await EnsureLoadedAsync();
        return new Dictionary<string, string>(_pins, StringComparer.OrdinalIgnoreCase);
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _lock.WaitAsync();
        try
        {
            if (_loaded) return;

            if (File.Exists(_storePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_storePath);
                    _pins = JsonSerializer.Deserialize<Dictionary<string, string>>(json,
                        RemexJsonSerializerContext.Default.DictionaryStringString) ?? new();
                    _logger.LogInformation("Loaded {Count} pinned hosts from {Path}", _pins.Count, _storePath);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse pinned_hosts.json, starting fresh.");
                    _pins = new(StringComparer.OrdinalIgnoreCase);
                }
            }

            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Synchronous load. See the warning on <see cref="GetPin"/>, its only caller: this blocks on a
    /// lock that the async paths hold across awaits, so calling it from the UI thread deadlocks.
    /// </summary>
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _lock.Wait();
        try
        {
            if (_loaded) return;

            if (File.Exists(_storePath))
            {
                try
                {
                    var json = File.ReadAllText(_storePath);
                    _pins = JsonSerializer.Deserialize<Dictionary<string, string>>(json,
                        RemexJsonSerializerContext.Default.DictionaryStringString) ?? new();
                    _logger.LogInformation("Loaded {Count} pinned hosts from {Path} (sync)", _pins.Count, _storePath);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse pinned_hosts.json, starting fresh.");
                    _pins = new(StringComparer.OrdinalIgnoreCase);
                }
            }

            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveAsync()
    {
        var dir = Path.GetDirectoryName(_storePath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_pins,
            RemexJsonSerializerContext.Default.DictionaryStringString);
        await File.WriteAllTextAsync(_storePath, json);
    }
}

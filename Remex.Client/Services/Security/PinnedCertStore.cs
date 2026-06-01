using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Serialization;

namespace Remex.Client.Services.Security;

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

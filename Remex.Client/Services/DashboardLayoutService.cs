using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Client.Services;

/// <summary>
/// JSON-file-based implementation of <see cref="IDashboardLayoutService"/>.
/// Writes are debounced so that rapid card movements don't cause excessive I/O.
/// </summary>
public sealed class DashboardLayoutService : IDashboardLayoutService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly ThemeService _themeService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _debounceTimer;
    private DashboardProfile? _pendingProfile;
    private const int DebounceMs = 2000;

    /// <summary>
    /// The currently loaded dashboard profile. 
    /// Updated after calling <see cref="LoadAsync"/>.
    /// </summary>
    public DashboardProfile CurrentProfile { get; private set; } = new();

    public DashboardLayoutService(ThemeService themeService)
    {
        _themeService = themeService;

        // Store alongside the application data — works on both Desktop and Android.
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Remex");

        Directory.CreateDirectory(appData);
        _filePath = Path.Combine(appData, "dashboard_layout.json");
    }

    /// <inheritdoc />
    public async Task<DashboardProfile> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DashboardProfile profile;
            if (!File.Exists(_filePath))
            {
                profile = new DashboardProfile();
            }
            else
            {
                var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
                profile = JsonSerializer.Deserialize<DashboardProfile>(json, JsonOptions)
                        ?? new DashboardProfile();
            }

            // Apply persisted theme settings to the UI.
            _themeService.ApplyCustomization(profile.Customization);

            CurrentProfile = profile;
            return profile;
        }
        catch
        {
            // If the file is corrupt, return defaults rather than crashing.
            var profile = new DashboardProfile();
            _themeService.ApplyCustomization(profile.Customization);
            CurrentProfile = profile;
            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(DashboardProfile profile)
    {
        return SaveInternalAsync(profile);
    }

    /// <summary>
    /// Debounced save — queues a write that fires after <see cref="DebounceMs"/>
    /// of inactivity. Safe to call on every card move/resize.
    /// </summary>
    public void RequestSave(DashboardProfile profile)
    {
        _pendingProfile = profile;
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(
            _ => _ = FlushAsync(),
            null,
            DebounceMs,
            Timeout.Infinite);
    }

    /// <summary>
    /// Forces any pending debounced write to disk immediately.
    /// Call on application shutdown.
    /// </summary>
    public async Task FlushAsync()
    {
        var profile = _pendingProfile;
        if (profile is null) return;

        _pendingProfile = null;
        _debounceTimer?.Dispose();
        _debounceTimer = null;

        await SaveInternalAsync(profile).ConfigureAwait(false);
    }

    private async Task SaveInternalAsync(DashboardProfile profile)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(profile, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
        _gate.Dispose();
    }
}

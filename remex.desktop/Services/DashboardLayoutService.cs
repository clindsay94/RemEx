using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Desktop.Services;

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

    /// <summary>
    /// Set when <see cref="LoadAsync"/> falls back to defaults due to a corrupt or unreadable file.
    /// <c>null</c> when the last load succeeded.
    /// </summary>
    public string? LoadFailureWarning { get; private set; }

    /// <summary>
    /// True when the most recent <see cref="LoadAsync"/> found no <c>dashboard_layout.json</c> on
    /// disk at all (as opposed to a corrupt file). Used by the first-run restore prompt to decide
    /// whether to offer restoring from the latest rolling auto-snapshot.
    /// </summary>
    public bool ProfileFileMissingOnLoad { get; private set; }

    /// <summary>Raised after a profile is successfully written to disk by <see cref="SaveInternalAsync"/> (i.e. after <see cref="SaveAsync"/> or a flushed <see cref="RequestSave"/>).</summary>
    public event Action? ProfileSaved;

    public DashboardLayoutService(ThemeService themeService)
    {
        _themeService = themeService;

        var baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var appData = Path.Combine(baseFolder, "Remex");

        Directory.CreateDirectory(appData);
        _filePath = Path.Combine(appData, "dashboard_layout.json");
    }

    /// <inheritdoc />
    public async Task<DashboardProfile> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DashboardProfile profile;
            ProfileFileMissingOnLoad = !File.Exists(_filePath);
            if (ProfileFileMissingOnLoad)
            {
                profile = new DashboardProfile();
            }
            else
            {
                var json = await File.ReadAllTextAsync(_filePath);
                profile = JsonSerializer.Deserialize<DashboardProfile>(json, JsonOptions)
                        ?? new DashboardProfile();
            }

            // Apply persisted theme settings to the UI.
            _themeService.ApplyCustomization(profile.Customization);

            CurrentProfile = profile;
            return profile;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RemexLayout] Failed to load profile from '{_filePath}': {ex.Message}");

            // Rename the corrupt file so it isn't loaded again, but is preserved for diagnostics.
            if (File.Exists(_filePath))
            {
                try
                {
                    var backupPath = _filePath + ".bak";
                    File.Move(_filePath, backupPath, overwrite: true);
                    Debug.WriteLine($"[RemexLayout] Corrupt profile renamed to '{backupPath}'");
                }
                catch (Exception moveEx)
                {
                    Debug.WriteLine($"[RemexLayout] Could not rename corrupt profile: {moveEx.Message}");
                }
            }

            LoadFailureWarning = $"Dashboard layout could not be loaded ({ex.GetType().Name}). Defaults have been applied.";
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
        CurrentProfile = profile;

        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(
            // A timer callback cannot await, and a dropped fault here means the debounced save
            // never happened and nothing said so - the user loses the layout they just edited.
            _ => FlushAsync().FireAndForget("flush the debounced dashboard layout save"),
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

        await SaveInternalAsync(profile);
    }

    private async Task SaveInternalAsync(DashboardProfile profile)
    {
        await _gate.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(profile, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);

            ProfileSaved?.Invoke();
        }
        catch (Exception ex)
        {
            // The Android guard that used to wrap this made it unreachable, so a failed save
            // has been reporting NOTHING. Deleting the guard and its body along with it would
            // have left a bare swallow here, which is worse: the user silently loses their
            // dashboard layout. Un-gated instead, so the diagnostic actually runs.
            //
            // Debug.WriteLine is a stopgap - it compiles out of Release. This service takes no
            // ILogger today, and giving it one is RemEx-t4tc's job (bare catch-swallow blocks),
            // not a dead-branch cleanup's. Recorded there rather than quietly widened here.
            System.Diagnostics.Debug.WriteLine($"[RemexPersistence] ERROR saving profile: {ex.Message}");
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

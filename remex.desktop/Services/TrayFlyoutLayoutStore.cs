using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Core.Services;

namespace Remex.Desktop.Services;

/// <summary>
/// Persists the tray flyout's pinned window state as a small JSON file.
/// </summary>
/// <remarks>
/// Follows <c>FileTransferRootSettingsService</c>: the path resolves through
/// <see cref="RemexDataPaths"/> rather than <c>SpecialFolder</c> directly, and writes are staged
/// rather than written over the live file.
/// <para>
/// DELIBERATELY DOES NOT VALIDATE. It returns whatever was on disk and lets the window judge it
/// against the screens that exist at the moment it is used — screens can be connected or
/// disconnected between this load and that use, so a verdict reached here would already be stale.
/// The rule lives in <see cref="TrayFlyoutGeometryValidator"/>.
/// </para>
/// </remarks>
public sealed class TrayFlyoutLayoutStore
{
    private const string FileName = "tray_flyout_layout.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _configPath;

    public TrayFlyoutLayoutStore()
    {
        var legacyFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");
        var baseFolder = RemexDataPaths.ResolveDirectory(legacyFolder);
        RemexDataPaths.TryMigrateWindowsFile(FileName);
        _configPath = Path.Combine(baseFolder, FileName);
        RemexDataPaths.SweepStagingOrphans(_configPath);
    }

    /// <summary>Test seam: point the store at an explicit file.</summary>
    internal TrayFlyoutLayoutStore(string configPath) => _configPath = configPath;

    /// <summary>Reads the saved state, or <c>null</c> if there is none or it is unreadable.</summary>
    public async Task<TrayFlyoutGeometry?> LoadRawAsync()
    {
        if (!File.Exists(_configPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            return JsonSerializer.Deserialize<TrayFlyoutGeometry>(json, JsonOptions);
        }
        catch
        {
            // A corrupt layout file must never be worse than no layout file. The caller falls back
            // to tray placement, which is always valid.
            return null;
        }
    }

    /// <summary>Writes the state, reporting rather than throwing if the write fails.</summary>
    /// <remarks>
    /// NEVER THROWS, and that is load-bearing rather than defensive. The only caller is a
    /// <c>DispatcherTimer</c> tick in <c>TrayFlyoutWindow</c>, which is an <c>async void</c> event
    /// handler by necessity — an exception escaping it is raised on the synchronization context and
    /// takes the process down (RemEx-ajk3). <see cref="RemexDataPaths.WriteAllTextAtomicAsync"/>
    /// deletes its staging file and RETHROWS, so a locked file, a full volume or a folder mid-sync
    /// would otherwise close RemEx while the user was dragging the window. Losing a remembered
    /// window position is an acceptable outcome; losing the application is not.
    /// </remarks>
    public async Task SaveAsync(TrayFlyoutGeometry geometry)
    {
        try
        {
            var json = JsonSerializer.Serialize(geometry, JsonOptions);
            await RemexDataPaths.WriteAllTextAtomicAsync(_configPath, json);
        }
        catch (Exception ex)
        {
            InMemoryLogSink.Append(
                LogLevel.Warning, "TrayFlyout", "Could not persist the tray flyout layout", ex);
        }
    }
}

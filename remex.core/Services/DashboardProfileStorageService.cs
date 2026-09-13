using Remex.Core.Models;
using Remex.Core.Serialization;

namespace Remex.Core.Services;

public interface IDashboardProfileStorageService
{
    Task<DashboardProfile> LoadProfileAsync();
    Task SaveProfileAsync(DashboardProfile profile);
}

/// <summary>
/// Service to manage persistent storage for the dashboard layout on the host.
/// </summary>
public class DashboardProfileStorageService : IDashboardProfileStorageService
{
    private readonly string _filePath;

    public DashboardProfileStorageService()
    {
        // Host-only store. Relocated to machine-wide ProgramData on Windows (unchanged elsewhere)
        // so the layout survives a change of signed-in user — originally, the host running as the
        // LocalSystem service vs. interactively.
        var legacyFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");
        var folder = RemexDataPaths.ResolveDirectory(legacyFolder);
        RemexDataPaths.TryMigrateWindowsFile("host_dashboard_layout.json");
        _filePath = Path.Combine(folder, "host_dashboard_layout.json");
        // Swept once at construction rather than on every read (RemEx-njzcx): the orphan is left by a
        // killed process, so once per process is the cadence that matches, and the read path here is
        // hot. See PairedClientRegistry for why the sweep must not sit behind an existence check.
        RemexDataPaths.SweepStagingOrphans(_filePath);
    }

    public async Task<DashboardProfile> LoadProfileAsync()
    {
        if (!File.Exists(_filePath))
            return new DashboardProfile();

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            return RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.DashboardProfile) ?? new DashboardProfile();
        }
        catch
        {
            return new DashboardProfile();
        }
    }

    public async Task SaveProfileAsync(DashboardProfile profile)
    {
        // The phone pushes its layout here with LayoutUpdate and its copy carries no card themes
        // (RemEx-4kv0g.3, the 2026-09-13 preset wipe): a themeless card keeps the theme the stored
        // copy already has for it, so a phone-side reorder cannot strip the PC's presets and hand
        // the stripped layout back on the next LayoutSync. A card that arrives WITH a theme wins.
        var existing = await LoadProfileAsync();
        profile = profile with { Cards = CardThemeMerge.PreserveThemes(profile.Cards, existing.Cards) };
        var json = RemexJson.SerializeIndented(profile, RemexJsonSerializerContext.Default.DashboardProfile);
        // Staged, not written over the live file (RemEx-fqzp): a crash mid-write truncated it.
        await RemexDataPaths.WriteAllTextAtomicAsync(_filePath, json);
    }
}

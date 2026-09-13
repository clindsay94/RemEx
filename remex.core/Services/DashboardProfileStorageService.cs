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
    // SaveProfileAsync is a read-merge-write; two LayoutUpdates in flight must not interleave or the
    // second merges against a base the first is still replacing. Static, not per-instance: the store
    // is not always a registered singleton (App.axaml.cs falls back to `new`, and RemexSavefileService
    // writes through its own instance on import), so a per-instance lock would not serialise an import
    // racing a LayoutUpdate. One process, one host file, one lock.
    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    /// <summary>Test seam: a store rooted at an explicit file, never the machine-wide one (RemEx-4u29).</summary>
    internal DashboardProfileStorageService(string filePath)
    {
        _filePath = filePath;
    }

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
        // A LayoutUpdate whose cards carry no colour information must not strip the themes this
        // store already holds (RemEx-4kv0g.3, the 2026-09-13 preset wipe: a theme-less profile was
        // handed back to the PC on LayoutSync and written over its presets; which writer produced
        // that theme-less copy is still unidentified - see REGRESSION-GUARDS.md). A card that arrives
        // with a theme wins; a card with none keeps the stored one. Serialised so two updates cannot
        // interleave their read-merge-write.
        await SaveLock.WaitAsync();
        try
        {
            var existing = await LoadProfileAsync();
            profile = profile with { Cards = CardThemeMerge.PreserveThemes(profile.Cards, existing.Cards) };
            var json = RemexJson.SerializeIndented(profile, RemexJsonSerializerContext.Default.DashboardProfile);
            // Staged, not written over the live file (RemEx-fqzp): a crash mid-write truncated it.
            await RemexDataPaths.WriteAllTextAtomicAsync(_filePath, json);
        }
        finally
        {
            SaveLock.Release();
        }
    }
}

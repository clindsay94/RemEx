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
        var json = RemexJson.SerializeIndented(profile, RemexJsonSerializerContext.Default.DashboardProfile);
        // Staged, not written over the live file (RemEx-fqzp): a crash mid-write truncated it.
        await RemexDataPaths.WriteAllTextAtomicAsync(_filePath, json);
    }
}

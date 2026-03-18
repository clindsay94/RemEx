using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Remex.Core.Models;

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
    private static readonly string DefaultAppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DashboardProfileStorageService()
    {
        Directory.CreateDirectory(DefaultAppDataFolder);
        _filePath = Path.Combine(DefaultAppDataFolder, "host_dashboard_layout.json");
    }

    public async Task<DashboardProfile> LoadProfileAsync()
    {
        if (!File.Exists(_filePath))
            return new DashboardProfile();

        try
        {
            var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<DashboardProfile>(json, JsonOptions) ?? new DashboardProfile();
        }
        catch
        {
            return new DashboardProfile();
        }
    }

    public async Task SaveProfileAsync(DashboardProfile profile)
    {
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
    }
}

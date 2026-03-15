using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Remex.Core.Models;

namespace Remex.Core.Services;

public interface ILauncherStorageService
{
    Task<List<AppEntry>> LoadEntriesAsync();
    Task SaveEntriesAsync(IEnumerable<AppEntry> entries);
}

/// <summary>
/// Service to manage persistent storage for launcher applications.
/// </summary>
public class LauncherStorageService : ILauncherStorageService
{
    private static readonly string AppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");
    private static readonly string ConfigFilePath = Path.Combine(AppDataFolder, "launchers.json");

    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public LauncherStorageService()
    {
        // Ensure the directory exists
        if (!Directory.Exists(AppDataFolder))
        {
            Directory.CreateDirectory(AppDataFolder);
        }
    }

    /// <summary>
    /// Loads the stored app entries. Returns an empty list if no file exists.
    /// </summary>
    public async Task<List<AppEntry>> LoadEntriesAsync()
    {
        if (!File.Exists(ConfigFilePath))
        {
            return new List<AppEntry>();
        }

        try
        {
            using var stream = File.OpenRead(ConfigFilePath);
            var entries = await JsonSerializer.DeserializeAsync<List<AppEntry>>(stream, _jsonOptions);
            return entries ?? new List<AppEntry>();
        }
        catch (Exception)
        {
            // Log or handle deserialization errors as needed.
            // Return empty on failure so we don't crash.
            return new List<AppEntry>();
        }
    }

    /// <summary>
    /// Saves the given entries to storage.
    /// </summary>
    public async Task SaveEntriesAsync(IEnumerable<AppEntry> entries)
    {
        try
        {
            using var stream = File.Create(ConfigFilePath);
            await JsonSerializer.SerializeAsync(stream, entries, _jsonOptions);
        }
        catch (Exception)
        {
            // Log or handle serialization errors
        }
    }
}

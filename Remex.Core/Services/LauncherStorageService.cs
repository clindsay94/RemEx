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
    private static readonly string DefaultAppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");

    private readonly string _configFilePath;

    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public LauncherStorageService() : this(null) { }

    public LauncherStorageService(string? storageFolderPath)
    {
        var folder = storageFolderPath ?? DefaultAppDataFolder;
        _configFilePath = Path.Combine(folder, "launchers.json");

        // Ensure the directory exists
        Directory.CreateDirectory(folder);
    }

    /// <summary>
    /// Loads the stored app entries. Returns an empty list if no file exists.
    /// </summary>
    public async Task<List<AppEntry>> LoadEntriesAsync()
    {
        if (!File.Exists(_configFilePath))
        {
            return new List<AppEntry>();
        }

        try
        {
            using var stream = File.OpenRead(_configFilePath);
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
            using var stream = File.Create(_configFilePath);
            await JsonSerializer.SerializeAsync(stream, entries, _jsonOptions);
        }
        catch (Exception)
        {
            // TODO: Log serialization errors (e.g. with ILogger)
            throw; // Re-throw the exception to allow the caller to handle it.
        }
    }
}

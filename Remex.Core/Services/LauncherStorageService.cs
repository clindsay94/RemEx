using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Remex.Core.Models;
using Remex.Core.Serialization;

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
    private static readonly string DefaultAppDataFolder = Path.Combine(
        OperatingSystem.IsAndroid()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Personal)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Remex");

    private readonly string _configFilePath;

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
            var entries = await RemexJson.DeserializeAsync(stream, RemexJson.TypeInfo<List<AppEntry>>()).ConfigureAwait(false);
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
            var entryList = entries as List<AppEntry> ?? new List<AppEntry>(entries);
            using var stream = File.Create(_configFilePath);
            await RemexJson.SerializeIndentedAsync(stream, entryList, RemexJson.TypeInfo<List<AppEntry>>()).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // TODO: Log serialization errors (e.g. with ILogger)
            throw; // Re-throw the exception to allow the caller to handle it.
        }
    }
}

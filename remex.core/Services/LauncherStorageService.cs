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
    private readonly string _configFilePath;

    public LauncherStorageService() : this(null) { }

    public LauncherStorageService(string? storageFolderPath)
    {
        string folder;
        if (storageFolderPath is not null)
        {
            folder = storageFolderPath;
            Directory.CreateDirectory(folder);
        }
        else
        {
            // Per-user folder used historically; ResolveDirectory relocates only Windows to the
            // machine-wide ProgramData location so the store survives a change of signed-in user
            // (originally, so the LocalSystem host service and the interactive session agreed).
            // Android keeps its app-private Personal folder unchanged.
            var legacyFolder = Path.Combine(
                OperatingSystem.IsAndroid()
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Personal)
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Remex");
            folder = RemexDataPaths.ResolveDirectory(legacyFolder);
            RemexDataPaths.TryMigrateWindowsFile("launchers.json");
        }

        _configFilePath = Path.Combine(folder, "launchers.json");
    }

    // Perf audit P4-3: the host re-read and re-deserialized launchers.json (icons included) on every
    // connect, sync request, add/remove and launch. The last parse is now kept and served again while
    // the file is unchanged. This list is the VULN-3 launch allowlist (AppLauncherService), so a
    // desktop removal must be seen on the very next load (RemEx-q6xt); two checks guarantee it:
    //  - s_saveGeneration: every in-process save, through ANY instance (the desktop UI and the host
    //    each hold one over the same file), bumps it before and after writing, which invalidates every
    //    instance's cache regardless of what the file timestamp says. This is the path desktop edits
    //    and savefile restores take.
    //  - the file's last-write time + length, for writers outside this process (hand edits).
    // Both are captured BEFORE the read, so a change that lands mid-read makes the next load miss.
    // AppEntry is an immutable record, so a shallow list copy per load keeps callers (which Add and
    // RemoveAll on the result) from mutating the cached list.
    private static int s_saveGeneration;
    private readonly object _cacheLock = new();
    private List<AppEntry>? _cachedEntries;
    private long _cachedWriteTicks;
    private long _cachedLength;
    private int _cachedGeneration;

    /// <summary>Deserialize attempts since construction; a test seam for the P4-3 cache.</summary>
    internal int DeserializeCount;

    /// <summary>
    /// Loads the stored app entries. Returns an empty list if no file exists.
    /// </summary>
    public async Task<List<AppEntry>> LoadEntriesAsync()
    {
        var generation = Volatile.Read(ref s_saveGeneration);
        var info = new FileInfo(_configFilePath);
        if (!info.Exists)
        {
            lock (_cacheLock) { _cachedEntries = null; }
            return new List<AppEntry>();
        }

        long writeTicks = info.LastWriteTimeUtc.Ticks;
        long length = info.Length;
        lock (_cacheLock)
        {
            if (_cachedEntries is not null
                && _cachedGeneration == generation
                && _cachedWriteTicks == writeTicks
                && _cachedLength == length)
            {
                return new List<AppEntry>(_cachedEntries);
            }
        }

        try
        {
            Interlocked.Increment(ref DeserializeCount);
            List<AppEntry> entries;
            using (var stream = File.OpenRead(_configFilePath))
            {
                entries = await RemexJson.DeserializeAsync(stream, RemexJson.TypeInfo<List<AppEntry>>())
                    ?? new List<AppEntry>();
            }

            lock (_cacheLock)
            {
                _cachedEntries = new List<AppEntry>(entries);
                _cachedGeneration = generation;
                _cachedWriteTicks = writeTicks;
                _cachedLength = length;
            }
            return entries;
        }
        catch (Exception)
        {
            // Log or handle deserialization errors as needed.
            // Return empty on failure so we don't crash. A failed read is never cached.
            return new List<AppEntry>();
        }
    }

    /// <summary>
    /// Saves the given entries to storage.
    /// </summary>
    public async Task SaveEntriesAsync(IEnumerable<AppEntry> entries)
    {
        // Before AND after the write (see s_saveGeneration): a load that starts mid-write cannot
        // cache what it read under the post-write generation.
        Interlocked.Increment(ref s_saveGeneration);
        try
        {
            var entryList = entries as List<AppEntry> ?? new List<AppEntry>(entries);
            using var stream = File.Create(_configFilePath);
            await RemexJson.SerializeIndentedAsync(stream, entryList, RemexJson.TypeInfo<List<AppEntry>>());
        }
        finally
        {
            Interlocked.Increment(ref s_saveGeneration);
        }
    }
}

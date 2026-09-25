using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Covers the parse cache on <see cref="LauncherStorageService"/> (perf audit P4-3).
/// </summary>
/// <remarks>
/// launchers.json is the VULN-3 launch allowlist (AppLauncherService), so the cache must never serve
/// a list the desktop has already changed: a removal has to be visible on the very next load
/// (RemEx-q6xt). These tests pin that, including the case a timestamp alone cannot see.
/// </remarks>
public class LauncherStorageCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "remex-launcher-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static AppEntry Entry(string name) =>
        new(Guid.NewGuid(), name, @"C:\Apps\" + name + ".exe", "#FF0000", "aWNvbg==");

    [Fact]
    public async Task RepeatedLoads_OfAnUnchangedFile_DeserializeOnce()
    {
        var storage = new LauncherStorageService(_dir);
        await storage.SaveEntriesAsync(new[] { Entry("Alpha"), Entry("Beta") });

        var first = await storage.LoadEntriesAsync();
        var second = await storage.LoadEntriesAsync();
        var third = await storage.LoadEntriesAsync();

        Assert.Equal(1, storage.DeserializeCount);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public async Task ReturnedList_IsACopy_CallerMutationDoesNotLeakIntoTheCache()
    {
        var storage = new LauncherStorageService(_dir);
        var alpha = Entry("Alpha");
        await storage.SaveEntriesAsync(new[] { alpha });

        var mutated = await storage.LoadEntriesAsync();
        mutated.Add(Entry("Injected"));
        mutated.RemoveAll(e => e.Id == alpha.Id);

        var reloaded = await storage.LoadEntriesAsync();
        Assert.Single(reloaded);
        Assert.Equal(alpha.Id, reloaded[0].Id);
    }

    [Fact]
    public async Task RemovalSavedThroughAnotherInstance_IsSeenAtOnce_EvenWithTheSameTimestampAndLength()
    {
        // The desktop UI and the host each hold their own LauncherStorageService over the same file.
        var host = new LauncherStorageService(_dir);
        var desktop = new LauncherStorageService(_dir);
        var removed = Entry("Alpha");
        await desktop.SaveEntriesAsync(new[] { removed });

        var before = await host.LoadEntriesAsync();
        Assert.Contains(before, e => e.Id == removed.Id);
        var path = Path.Combine(_dir, "launchers.json");
        var stamp = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;

        // Same serialized length (same name length, same GUID width), then the timestamp forced back:
        // only the in-process save generation can tell the host its cache is stale.
        var replacement = Entry("Gamma");
        await desktop.SaveEntriesAsync(new[] { replacement });
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(length, new FileInfo(path).Length);

        var after = await host.LoadEntriesAsync();
        Assert.DoesNotContain(after, e => e.Id == removed.Id);
        Assert.Contains(after, e => e.Id == replacement.Id);
    }

    [Fact]
    public async Task ExternalRewrite_OfTheFile_IsPickedUp()
    {
        var storage = new LauncherStorageService(_dir);
        await storage.SaveEntriesAsync(new[] { Entry("Alpha") });
        Assert.Single(await storage.LoadEntriesAsync());

        // A writer outside this process (hand edit, another tool) goes through neither instance.
        var path = Path.Combine(_dir, "launchers.json");
        await File.WriteAllTextAsync(path, "[]");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Empty(await storage.LoadEntriesAsync());
        Assert.Equal(2, storage.DeserializeCount);
    }

    [Fact]
    public async Task DeletedFile_LoadsEmpty_AndARecreatedFileIsReadAgain()
    {
        var storage = new LauncherStorageService(_dir);
        await storage.SaveEntriesAsync(new[] { Entry("Alpha") });
        Assert.Single(await storage.LoadEntriesAsync());

        File.Delete(Path.Combine(_dir, "launchers.json"));
        Assert.Empty(await storage.LoadEntriesAsync());

        var beta = Entry("Beta");
        await storage.SaveEntriesAsync(new[] { beta });
        var reloaded = await storage.LoadEntriesAsync();
        Assert.Equal(beta.Id, Assert.Single(reloaded).Id);
    }

    [Fact]
    public async Task CorruptFile_IsNotCached()
    {
        var storage = new LauncherStorageService(_dir);
        var path = Path.Combine(_dir, "launchers.json");
        await File.WriteAllTextAsync(path, "{ not json");

        Assert.Empty(await storage.LoadEntriesAsync());
        Assert.Empty(await storage.LoadEntriesAsync());
        Assert.Equal(2, storage.DeserializeCount);
    }
}

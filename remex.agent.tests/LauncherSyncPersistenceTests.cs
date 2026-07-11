using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Agent.Tests;

/// <summary>
/// Covers the host-side persistence bug fixed by the <see cref="Remex.Agent.Handlers.PingPongHandler"/>
/// inbound <c>MessageTypes.LauncherSync</c> case: reorders performed while a client is connected must be
/// written to disk by <see cref="LauncherStorageService"/>, with entry <c>Id</c>s preserved and
/// <c>Order</c> reflecting the caller-supplied sequence. The socket-level wiring itself is covered by
/// manual smoke testing (see WP-B), so these tests exercise the storage round-trip directly.
/// </summary>
public sealed class LauncherSyncPersistenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LauncherStorageService _storage;

    public LauncherSyncPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RemExLauncherSyncTests_" + Guid.NewGuid().ToString("N"));
        _storage = new LauncherStorageService(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; leftover temp dirs don't affect other tests.
        }
    }

    [Fact]
    public async Task SaveEntriesAsync_ThenLoad_RoundTripsReorderedList()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();

        var original = new List<AppEntry>
        {
            new(idA, "App A", "C:\\Apps\\a.exe", "#FF0000", null, Order: 0),
            new(idB, "App B", "C:\\Apps\\b.exe", "#00FF00", null, Order: 1),
            new(idC, "App C", "C:\\Apps\\c.exe", "#0000FF", null, Order: 2),
        };
        await _storage.SaveEntriesAsync(original);

        // Simulate a client-side drag-reorder: rewrite Order to reflect the new sequence
        // (B, C, A) exactly as AppLauncherViewModel.PersistOrderAsync does before sending
        // LauncherSync, then persist that as the host would on receiving the message.
        var reordered = new List<AppEntry>
        {
            original[1] with { Order = 0 },
            original[2] with { Order = 1 },
            original[0] with { Order = 2 },
        };
        await _storage.SaveEntriesAsync(reordered);

        var loaded = await _storage.LoadEntriesAsync();

        Assert.Equal(3, loaded.Count);

        Assert.Equal(idB, loaded[0].Id);
        Assert.Equal(0, loaded[0].Order);

        Assert.Equal(idC, loaded[1].Id);
        Assert.Equal(1, loaded[1].Order);

        Assert.Equal(idA, loaded[2].Id);
        Assert.Equal(2, loaded[2].Order);

        // Ids and other fields must survive the round trip unchanged.
        Assert.Equal("App A", loaded[2].DisplayName);
        Assert.Equal("C:\\Apps\\a.exe", loaded[2].TargetPath);
    }

    [Fact]
    public async Task SaveEntriesAsync_EmptyList_IsPersistedNotIgnored()
    {
        // Guard on the host-side handler is `is not null`, not `Count > 0` — an empty list is a
        // valid, deliberate "user removed everything" state and must be persisted, not skipped.
        var initial = new List<AppEntry>
        {
            new(Guid.NewGuid(), "App A", "C:\\Apps\\a.exe", "#FF0000", null, Order: 0),
        };
        await _storage.SaveEntriesAsync(initial);

        await _storage.SaveEntriesAsync(new List<AppEntry>());

        var loaded = await _storage.LoadEntriesAsync();

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task LoadEntriesAsync_NoFileYet_ReturnsEmptyList()
    {
        var loaded = await _storage.LoadEntriesAsync();

        Assert.NotNull(loaded);
        Assert.Empty(loaded);
    }
}

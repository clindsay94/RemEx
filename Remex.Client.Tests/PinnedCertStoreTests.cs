using Microsoft.Extensions.Logging.Abstractions;
using Remex.Client.Services.Security;

namespace Remex.Client.Tests;

public sealed class PinnedCertStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _storePath;
    private readonly PinnedCertStore _store;

    public PinnedCertStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "remex-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _storePath = Path.Combine(_dir, "pinned_hosts.json");
        _store = new PinnedCertStore(NullLogger<PinnedCertStore>.Instance, _storePath);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task SetPin_ThenGetPin_ReturnsSameHash()
    {
        await _store.SetPinAsync("host-a", "abc123==");

        var retrieved = await _store.GetPinAsync("host-a");

        Assert.Equal("abc123==", retrieved);
    }

    [Fact]
    public async Task IsPinned_ReturnsFalse_WhenNoPinExists()
    {
        var result = await _store.IsPinnedAsync("nonexistent-host");

        Assert.False(result);
    }

    [Fact]
    public async Task IsPinned_ReturnsTrue_AfterSetPin()
    {
        await _store.SetPinAsync("host-b", "xyz==");

        Assert.True(await _store.IsPinnedAsync("host-b"));
    }

    [Fact]
    public async Task RemovePin_MakesHostUnpinned()
    {
        await _store.SetPinAsync("host-c", "hash==");
        await _store.RemovePinAsync("host-c");

        Assert.False(await _store.IsPinnedAsync("host-c"));
        Assert.Null(await _store.GetPinAsync("host-c"));
    }

    [Fact]
    public async Task RemovePin_NonexistentHost_DoesNotThrow()
    {
        await _store.RemovePinAsync("ghost-host");
    }

    [Fact]
    public async Task GetAllPins_ReturnsAllStoredEntries()
    {
        await _store.SetPinAsync("host-1", "hash1==");
        await _store.SetPinAsync("host-2", "hash2==");

        var all = await _store.GetAllPinsAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal("hash1==", all["host-1"]);
        Assert.Equal("hash2==", all["host-2"]);
    }

    [Fact]
    public async Task SetPin_OverwritesExistingPin()
    {
        await _store.SetPinAsync("host-d", "old==");
        await _store.SetPinAsync("host-d", "new==");

        Assert.Equal("new==", await _store.GetPinAsync("host-d"));
    }

    [Fact]
    public async Task Pins_PersistedToDisk_LoadedByNewInstance()
    {
        await _store.SetPinAsync("host-e", "persisted==");

        var store2 = new PinnedCertStore(NullLogger<PinnedCertStore>.Instance, _storePath);
        var retrieved = await store2.GetPinAsync("host-e");

        Assert.Equal("persisted==", retrieved);
    }

    [Fact]
    public async Task GetPin_ReturnsNull_WhenHostNotPinned()
    {
        var result = await _store.GetPinAsync("missing");

        Assert.Null(result);
    }

    [Fact]
    public void GetPin_Sync_ReturnsStoredHash()
    {
        _store.SetPinAsync("host-f", "synchash==").GetAwaiter().GetResult();

        var result = _store.GetPin("host-f");

        Assert.Equal("synchash==", result);
    }

    [Fact]
    public async Task HostIdLookup_IsCaseInsensitive()
    {
        await _store.SetPinAsync("Host-G", "casehash==");

        Assert.Equal("casehash==", await _store.GetPinAsync("host-g"));
        Assert.Equal("casehash==", await _store.GetPinAsync("HOST-G"));
    }
}

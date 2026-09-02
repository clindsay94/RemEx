using System;
using System.Security.Cryptography;
using Remex.Agent.Services.Media;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the artwork store's LRU behaviour and its content-hash id scheme (RemEx-vtorl).
/// </summary>
public class MediaArtworkStoreTests
{
    [Fact]
    public void IdenticalBytesProduceTheSameIdAndCountAsOneEntryTowardCapacity()
    {
        var store = new MediaArtworkStore(capacity: 2);
        var bytes = new byte[] { 1, 2, 3 };

        var id1 = store.Put(bytes);
        var id2 = store.Put((byte[])bytes.Clone());

        Assert.NotNull(id1);
        Assert.Equal(id1, id2);

        // If the second Put had consumed a distinct second slot, a capacity-2 store would already be
        // full and this next distinct entry would evict the first instead of coexisting with it.
        var otherId = store.Put(new byte[] { 4, 5, 6 });

        Assert.Equal(bytes, store.TryGet(id1!));
        Assert.NotNull(store.TryGet(otherId!));
    }

    [Fact]
    public void ANinthDistinctPutEvictsTheLeastRecentlyUsed()
    {
        var store = new MediaArtworkStore(); // default capacity 8
        var ids = new string[8];
        for (var i = 0; i < 8; i++)
        {
            ids[i] = store.Put(new byte[] { (byte)i, 0xAA })!;
        }

        // Check the oldest FIRST: TryGet itself refreshes recency, so touching the others first would
        // change which entry is actually least-recently-used.
        var lru = ids[0];

        var ninthId = store.Put(new byte[] { 9, 0xAA });

        Assert.Null(store.TryGet(lru));
        Assert.NotNull(ninthId);
        Assert.NotNull(store.TryGet(ninthId!));
        for (var i = 1; i < 8; i++)
        {
            Assert.NotNull(store.TryGet(ids[i]));
        }
    }

    [Fact]
    public void TryGetRefreshesRecencySoATouchedEntrySurvivesTheNextEviction()
    {
        var store = new MediaArtworkStore(capacity: 3);
        var idA = store.Put(new byte[] { 1 });
        var idB = store.Put(new byte[] { 2 });
        var idC = store.Put(new byte[] { 3 });

        // Touch A, making B the least recently used instead of A.
        Assert.NotNull(store.TryGet(idA!));

        store.Put(new byte[] { 4 }); // capacity 3 -> evicts the LRU, which should now be B.

        Assert.NotNull(store.TryGet(idA!));
        Assert.Null(store.TryGet(idB!));
        Assert.NotNull(store.TryGet(idC!));
    }

    [Fact]
    public void EmptyBytesAreRejected()
    {
        var store = new MediaArtworkStore();

        Assert.Null(store.Put(Array.Empty<byte>()));
    }

    [Fact]
    public void OverCapBytesAreRejected()
    {
        var store = new MediaArtworkStore();
        var tooBig = new byte[MediaArtworkStore.MaxArtworkBytes + 1];

        Assert.Null(store.Put(tooBig));
    }

    [Fact]
    public void TheIdIsSixteenLowercaseHexCharacters()
    {
        var store = new MediaArtworkStore();

        var id = store.Put(new byte[] { 1, 2, 3, 4, 5 });

        Assert.NotNull(id);
        Assert.Matches("^[0-9a-f]{16}$", id);
    }

    [Fact]
    public void TheIdIsTheFirstSixteenHexCharactersOfTheSha256()
    {
        var bytes = new byte[] { 10, 20, 30, 40, 50 };
        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];

        Assert.Equal(expected, MediaArtworkStore.ComputeId(bytes));
    }

    [Fact]
    public void AnUnknownIdReturnsNull()
    {
        var store = new MediaArtworkStore();

        Assert.Null(store.TryGet("0000000000000000"));
    }
}

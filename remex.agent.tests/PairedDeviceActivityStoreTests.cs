using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Security;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The store holding when each paired device paired and was last seen (RemEx-nrsv).
/// </summary>
/// <remarks>
/// <para>
/// THESE ARE THE NAME STORE'S TESTS, PORTED, AND THAT IS THE POINT. This class's entire safety case
/// is "it behaves exactly like <c>PairedClientNameStore</c>" — same directory, same atomic write,
/// same permission restriction, same catch-everything constructor. The name store proves each of
/// those with a test; shipping the copy with none would have made that claim an assertion in a
/// comment, sitting beside the highest-risk file in the repo (review of the first pass, which
/// failed on exactly this).
/// </para>
/// <para>
/// The constructor property is the one that matters most: it is resolved by DI on the <c>/ws</c>
/// path, so an exception escaping it does not cost timestamps, it kills the connection before the
/// handler exists and blocks pairing outright.
/// </para>
/// </remarks>
public sealed class PairedDeviceActivityStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory();

    private string StorePath => Path.Combine(_root.FullName, "activity.json");

    private PairedDeviceActivityStore NewStore() =>
        new(NullLogger<PairedDeviceActivityStore>.Instance, StorePath);

    public void Dispose()
    {
        _root.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void DatesSurviveARestart()
    {
        var paired = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        NewStore().RecordPaired("phone-a", paired);

        var activity = NewStore().Resolve("phone-a");

        Assert.NotNull(activity);
        Assert.Equal(paired, activity!.FirstPairedUtc);
        Assert.Equal(paired, activity.LastSeenUtc);
    }

    [Fact]
    public void AReconnectMovesLastSeenAndLeavesFirstPairedAlone()
    {
        var paired = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var seen = new DateTimeOffset(2026, 8, 9, 10, 11, 12, TimeSpan.Zero);

        var store = NewStore();
        store.RecordPaired("phone-a", paired);
        store.RecordSeen("phone-a", seen);

        var activity = store.Resolve("phone-a")!;
        Assert.Equal(paired, activity.FirstPairedUtc);
        Assert.Equal(seen, activity.LastSeenUtc);
    }

    [Fact]
    public void ADeviceSeenBeforeItWasEverRecordedAsPairedGetsNoInventedPairingDate()
    {
        // THE RELEASE-DAY CASE, and the reason FirstPairedUtc is nullable (review). Every device
        // already paired when this store ships has no row, and the first thing that happens to it is
        // a reconnect. Stamping "now" there would tell the user that every phone they own was paired
        // today — and the "unknown" rendering the row contract was built around would never fire.
        var store = NewStore();
        store.RecordSeen("phone-a", new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));

        var activity = store.Resolve("phone-a")!;
        Assert.Null(activity.FirstPairedUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero), activity.LastSeenUtc);
    }

    [Fact]
    public void APairingLaterFillsInTheDateAReconnectCouldNotKnow()
    {
        // The other half: once the device genuinely pairs, that IS the moment we know the date.
        var seen = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
        var paired = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        var store = NewStore();
        store.RecordSeen("phone-a", seen);
        store.RecordPaired("phone-a", paired);

        Assert.Equal(paired, store.Resolve("phone-a")!.FirstPairedUtc);
    }

    [Fact]
    public void AnUnknownDeviceResolvesToNullRatherThanSomethingInvented()
        => Assert.Null(NewStore().Resolve("never-paired"));

    [Fact]
    public void ABlankClientIdIsIgnoredRatherThanStored()
    {
        var store = NewStore();
        store.RecordPaired("   ", DateTimeOffset.UtcNow);
        store.RecordSeen(null, DateTimeOffset.UtcNow);

        Assert.Null(store.Resolve("   "));
    }

    [Fact]
    public void ForgettingADeviceRemovesItsDatesForGood()
    {
        var store = NewStore();
        store.RecordPaired("phone-a", DateTimeOffset.UtcNow);
        store.Forget("phone-a");

        Assert.Null(store.Resolve("phone-a"));
        Assert.Null(NewStore().Resolve("phone-a"));
    }

    [Fact]
    public void ACorruptStoreCostsDatesAndNothingElse()
    {
        // THE PROPERTY THAT PROTECTS PAIRING. This constructor is resolved by DI on the /ws path, so
        // an exception escaping it would block pairing outright rather than merely lose dates. A hard
        // power-off mid-write is the realistic producer of a half-written file.
        Directory.CreateDirectory(_root.FullName);
        File.WriteAllText(StorePath, "{ this is not json");

        var store = NewStore();

        Assert.Null(store.Resolve("phone-a"));

        // And it still works afterwards, rather than being wedged by the bad file.
        store.RecordPaired("phone-a", DateTimeOffset.UtcNow);
        Assert.NotNull(store.Resolve("phone-a"));
    }

    [Fact]
    public void TheActivityFileSitsBesideThePairingStoreItDescribes()
    {
        // The class comment CLAIMS a test pins this. It did not exist until review pointed that out —
        // the claim was the assertion. A record that outlived its pairing, or died while the pairing
        // lived, is worse than no record, and that only holds while the two files move together.
        Assert.Equal(
            Path.GetDirectoryName(PairedClientRegistry.DefaultStorePathForTests),
            Path.GetDirectoryName(PairedDeviceActivityStore.DefaultStorePathForTests));

        Assert.Equal(
            Path.GetDirectoryName(PairedClientNameStore.DefaultStorePathForTests),
            Path.GetDirectoryName(PairedDeviceActivityStore.DefaultStorePathForTests));
    }

    [Fact]
    public void RecordingActivityNeverTouchesThePairingStore()
    {
        // THE BYTE-IDENTICAL PROPERTY THIS WHOLE DESIGN RESTS ON. Timestamps live in their own file
        // precisely so that writing one cannot alter the file that authenticates devices.
        var registryPath = Path.Combine(_root.FullName, "paired.json");
        var registry = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, registryPath);
        registry.RegisterClient("phone-a", [1, 2, 3, 4]);

        var before = File.ReadAllBytes(registryPath);

        var store = NewStore();
        store.RecordPaired("phone-a", DateTimeOffset.UtcNow);
        store.RecordSeen("phone-a", DateTimeOffset.UtcNow);
        store.Forget("phone-a");

        Assert.Equal(before, File.ReadAllBytes(registryPath));
    }
}

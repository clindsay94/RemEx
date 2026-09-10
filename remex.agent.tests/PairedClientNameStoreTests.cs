using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Security;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the store that stops a reconnecting device from being nameless (RemEx-yzqs).
/// </summary>
/// <remarks>
/// A device name crosses the wire exactly once, on <c>pairing_request</c>, and initial pairing
/// happens once in a device's life — so before this store the PC knew a phone's name for its first
/// connection and never again.
/// </remarks>
public sealed class PairedClientNameStoreTests : IDisposable
{
    private readonly string _storePath =
        Path.Combine(Path.GetTempPath(), $"remex-name-store-{Guid.NewGuid():N}.json");

    private PairedClientNameStore NewStore() =>
        new(NullLogger<PairedClientNameStore>.Instance, _storePath);

    public void Dispose()
    {
        if (File.Exists(_storePath)) File.Delete(_storePath);
    }

    [Fact]
    public void ANameSurvivesARestart()
    {
        // THE ENTIRE POINT. The name is learned once, at pairing, and every later connection has to
        // get it from disk — including connections that happen after the host has been rebooted,
        // which is most of them.
        NewStore().Remember("phone-1", "Connor's Pixel");

        Assert.Equal("Connor's Pixel", NewStore().Resolve("phone-1"));
    }

    [Fact]
    public void AnUnknownDeviceResolvesToNullRatherThanSomethingInvented()
    {
        // Null means "no name", which ClientSession already documents and PhonePresence already
        // degrades to a count for. Inventing a placeholder here would put a fake name on screen that
        // the user cannot match against anything their phone shows.
        var store = NewStore();

        Assert.Null(store.Resolve("never-paired"));
        Assert.Null(store.Resolve(null));
        Assert.Null(store.Resolve(""));
    }

    [Fact]
    public void ABlankNameIsIgnoredRatherThanStored()
    {
        // A client is free to send an empty clientName. Storing it would mean the device is recorded
        // as having a name that renders as nothing — worse than having none, because the fallback to
        // the id never gets a chance to run.
        var store = NewStore();
        store.Remember("phone-1", "Connor's Pixel");

        store.Remember("phone-1", "   ");
        store.Remember("phone-1", null);

        Assert.Equal("Connor's Pixel", store.Resolve("phone-1"));
    }

    [Fact]
    public void ARepairWithANewNameReplacesTheOldOne()
    {
        var store = NewStore();
        store.Remember("phone-1", "Old Phone");

        store.Remember("phone-1", "New Phone");

        Assert.Equal("New Phone", store.Resolve("phone-1"));
        Assert.Equal("New Phone", NewStore().Resolve("phone-1"));
    }

    [Fact]
    public void AnOverlongNameIsCappedTheSameWayATypedRenameIs()
    {
        // The DEVICE chooses this string, so nothing bounds it but this. A card row is one line: an
        // unbounded name pushes every other device's unpair button off screen, which turns a cosmetic
        // detail into a usability failure for the rows that matter. Sharing PairedDeviceDisplayName's
        // rule rather than restating it is what keeps a rename and a reported name the same width.
        var store = NewStore();

        store.Remember("phone-1", new string('x', PairedDeviceDisplayName.MaxLength + 40));

        Assert.Equal(PairedDeviceDisplayName.MaxLength, store.Resolve("phone-1")!.Length);
    }

    [Fact]
    public void ForgettingADeviceRemovesItsNameForGood()
    {
        var store = NewStore();
        store.Remember("phone-1", "Connor's Pixel");

        store.Forget("phone-1");

        Assert.Null(store.Resolve("phone-1"));
        Assert.Null(NewStore().Resolve("phone-1"));
    }

    [Fact]
    public void OneDevicesNameIsNotAnotherDevices()
    {
        var store = NewStore();
        store.Remember("phone-1", "Connor's Pixel");
        store.Remember("phone-2", "The Tablet");

        store.Forget("phone-1");

        Assert.Null(store.Resolve("phone-1"));
        Assert.Equal("The Tablet", store.Resolve("phone-2"));
    }

    [Fact]
    public void ACorruptStoreCostsNamesAndNothingElse()
    {
        // WHY THE NAMES ARE NOT IN THE PAIRING STORE. There, the same unparseable file is caught the
        // same way and the host continues with an EMPTY REGISTRY — silently unpairing every device.
        // Here the identical damage costs a display string, which is the whole argument for keeping
        // a cosmetic, attacker-influenced value out of the file that holds key material.
        File.WriteAllText(_storePath, "{ this is not json");

        var store = NewStore();

        Assert.Null(store.Resolve("phone-1"));

        // And it must still be usable afterwards, not wedged.
        store.Remember("phone-1", "Connor's Pixel");
        Assert.Equal("Connor's Pixel", NewStore().Resolve("phone-1"));
    }

    [Fact]
    public void ANameEditedOnDiskToSomethingAbsurdIsStillCappedOnRead()
    {
        // The file is editable by anyone who can reach it, and a build with a smaller cap has to cope
        // with what a larger one wrote. Normalizing on the way in means the cap is a property of what
        // is served, not merely of what was stored.
        var absurd = new string('y', 4000);
        File.WriteAllText(_storePath, $"{{ \"phone-1\": \"{absurd}\" }}");

        Assert.Equal(PairedDeviceDisplayName.MaxLength, NewStore().Resolve("phone-1")!.Length);
    }

    [Fact]
    public void TheNamesFileSitsBesideThePairingStoreItDescribes()
    {
        // A name that outlived its pairing, or died while the pairing lived, would be worse than no
        // name — and the pairing store's directory is a considered choice (machine-wide on Windows so
        // it survives a change of signed-in user). Pinning them together is what stops one being
        // moved later without the other.
        Assert.Equal(
            Path.GetDirectoryName(PairedClientRegistry.DefaultStorePathForTests),
            Path.GetDirectoryName(PairedClientNameStore.DefaultStorePathForTests));

        Assert.NotEqual(
            PairedClientRegistry.DefaultStorePathForTests,
            PairedClientNameStore.DefaultStorePathForTests);
    }

    [Fact]
    public void RememberingANameNeverTouchesThePairingStore()
    {
        // THE SAFETY PROPERTY THIS DESIGN EXISTS FOR. PairedClientRegistry is the only authentication
        // path in production; a write that corrupted it bricks every paired phone with no clear error
        // on either end. The bead asked for the name to live "alongside the secret" — this asserts the
        // deliberate deviation: the pairing file is not opened, let alone rewritten, when a name
        // changes.
        var pairingPath = Path.Combine(Path.GetTempPath(), $"remex-pairing-{Guid.NewGuid():N}.json");
        try
        {
            var registry = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, pairingPath);
            registry.RegisterClient("phone-1", [1, 2, 3, 4]);
            var pairingBytes = File.ReadAllBytes(pairingPath);

            NewStore().Remember("phone-1", "Connor's Pixel");

            Assert.Equal(pairingBytes, File.ReadAllBytes(pairingPath));

            // And the pairing itself still works, which is the thing that actually matters.
            Assert.True(new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, pairingPath)
                .TryGetReconnectSecret("phone-1", out var secret));
            Assert.Equal<byte[]>([1, 2, 3, 4], secret);
        }
        finally
        {
            if (File.Exists(pairingPath)) File.Delete(pairingPath);
        }
    }
}

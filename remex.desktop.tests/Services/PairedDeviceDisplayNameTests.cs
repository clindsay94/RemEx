using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins the friendly-name behaviour of the Paired Devices card (RemEx-9see).
/// </summary>
/// <remarks>
/// Renaming is DISPLAY ONLY. PairedClientRegistry is the only authentication path in production, so
/// a rename must not be able to reach it - these functions operate on a separate map and produce
/// new dictionaries rather than mutating anything.
/// </remarks>
public class PairedDeviceDisplayNameTests
{
    private const string DeviceId = "a1b2c3d4";

    [Fact]
    public void AFriendlyNameIsPreferredWhenSet()
    {
        var names = new Dictionary<string, string> { [DeviceId] = "Connor's Pixel" };

        Assert.Equal("Connor's Pixel", PairedDeviceDisplayName.Resolve(DeviceId, names));
    }

    [Fact]
    public void WithoutANameTheIdIsShownRatherThanNothing()
    {
        // A NAMELESS ROW WITH AN UNPAIR BUTTON BESIDE IT is a decision the user cannot make safely.
        // The existing File-Sharing Trust list already renders raw ShortIds and is described as
        // opaque - blank is strictly worse than opaque, because at least an id can be compared
        // against what the phone shows.
        Assert.Equal(DeviceId, PairedDeviceDisplayName.Resolve(DeviceId, null));
        Assert.Equal(DeviceId, PairedDeviceDisplayName.Resolve(DeviceId, new Dictionary<string, string>()));
    }

    [Fact]
    public void ResolveNeverReturnsBlank()
    {
        // The invariant behind the row: whatever goes wrong, there is something to click next to.
        Assert.False(string.IsNullOrWhiteSpace(PairedDeviceDisplayName.Resolve("", null)));
        Assert.False(string.IsNullOrWhiteSpace(PairedDeviceDisplayName.Resolve("   ", null)));
        Assert.Equal(PairedDeviceDisplayName.UnknownDevice, PairedDeviceDisplayName.Resolve("", null));
    }

    [Fact]
    public void AStoredBlankIsIgnoredRatherThanRenderedEmpty()
    {
        // Defence against a map that already holds a blank from an older build or a hand edit.
        var names = new Dictionary<string, string> { [DeviceId] = "   " };

        Assert.Equal(DeviceId, PairedDeviceDisplayName.Resolve(DeviceId, names));
    }

    [Fact]
    public void ClearingTheFieldRemovesTheOverrideInsteadOfStoringAnEmptyName()
    {
        // STORING AN EMPTY STRING WOULD BE A TRAP: the device renders nameless, and the field the
        // user would type into to fix it is the same one they just emptied. Clearing restores the
        // id fallback, which is recoverable.
        var names = PairedDeviceDisplayName.Rename(null, DeviceId, "Old Name");
        Assert.Equal("Old Name", PairedDeviceDisplayName.Resolve(DeviceId, names));

        var cleared = PairedDeviceDisplayName.Rename(names, DeviceId, "   ");

        Assert.False(cleared.ContainsKey(DeviceId));
        Assert.Equal(DeviceId, PairedDeviceDisplayName.Resolve(DeviceId, cleared));
    }

    [Fact]
    public void ANameIsTrimmedAndCapped()
    {
        // One row is one line. Without a cap, a single device pushes every other device's unpair
        // button off screen - which turns a cosmetic choice into a usability failure for the rows
        // the user actually needs.
        Assert.Equal("Pixel", PairedDeviceDisplayName.Normalize("   Pixel   "));

        var long_ = new string('x', PairedDeviceDisplayName.MaxLength + 40);
        var normalized = PairedDeviceDisplayName.Normalize(long_)!;

        Assert.Equal(PairedDeviceDisplayName.MaxLength, normalized.Length);
    }

    [Fact]
    public void RenamingReturnsANewMapRatherThanMutatingTheOneItWasGiven()
    {
        // So a caller cannot publish a half-applied map to the UI - and so this can never be handed
        // something that also happens to be backing another store.
        var original = new Dictionary<string, string> { [DeviceId] = "Original" };

        var renamed = PairedDeviceDisplayName.Rename(original, DeviceId, "Changed");

        Assert.Equal("Original", original[DeviceId]);
        Assert.Equal("Changed", renamed[DeviceId]);
        Assert.NotSame(original, renamed);
    }

    [Fact]
    public void RenamingOneDeviceLeavesTheOthersAlone()
    {
        var names = new Dictionary<string, string> { ["aaa"] = "Desktop phone", ["bbb"] = "Laptop phone" };

        var renamed = PairedDeviceDisplayName.Rename(names, "aaa", "Renamed");

        Assert.Equal("Renamed", renamed["aaa"]);
        Assert.Equal("Laptop phone", renamed["bbb"]);
    }

    [Fact]
    public void ARenameForADeviceWithNoIdIsDroppedRatherThanOrphaned()
    {
        // Inventing a key would create an entry no row ever reads, which then survives every future
        // save and cannot be removed from the UI.
        var renamed = PairedDeviceDisplayName.Rename(null, "", "Somebody");

        Assert.Empty(renamed);
    }

    [Fact]
    public void DeviceIdsAreComparedOrdinally()
    {
        // Ids are opaque tokens, not words. Case-insensitive comparison would let two distinct
        // devices share a name entry - and in a list whose other button is "unpair", showing one
        // device's name against another's row is the wrong kind of mistake.
        var names = PairedDeviceDisplayName.Rename(null, "a1b2", "Mine");

        Assert.Equal("Mine", PairedDeviceDisplayName.Resolve("a1b2", names));
        Assert.Equal("A1B2", PairedDeviceDisplayName.Resolve("A1B2", names));
    }
}

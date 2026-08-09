using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Security;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Renaming a paired device is a user OVERRIDE, kept apart from the name the device reports
/// (RemEx-4gbp2).
/// </summary>
/// <remarks>
/// <para>
/// A first version wrote renames into <see cref="PairedClientNameStore"/> — the store holding what
/// the DEVICE says it is — and review caught it against that class's own remarks. One slot per
/// device means a re-pair refreshes the reported name and silently discards the user's choice, and
/// clearing a rename deletes the reported name too, leaving a raw client id with no way back from
/// the UI. Both behaviours are pinned below.
/// </para>
/// <para>
/// It also must never reach <see cref="PairedClientRegistry"/>, the sole authentication path in
/// production. That is enforced by what the type can see, and asserted so it cannot be widened.
/// </para>
/// </remarks>
public sealed class PairedDeviceRenamerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory();

    private (PairedDeviceRenamer Renamer, PairedDeviceNameOverrideStore Overrides,
             PairedClientNameStore Reported) NewRenamer()
    {
        var overrides = new PairedDeviceNameOverrideStore(
            NullLogger<PairedDeviceNameOverrideStore>.Instance,
            Path.Combine(_root.FullName, "overrides.json"));
        var reported = new PairedClientNameStore(
            NullLogger<PairedClientNameStore>.Instance,
            Path.Combine(_root.FullName, "names.json"));

        return (new PairedDeviceRenamer(overrides), overrides, reported);
    }

    public void Dispose()
    {
        _root.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheRenamerCannotSeeThePairingRegistry()
    {
        // ASSERTED AGAINST THE TYPE rather than trusted to a comment. Widening this to reach the
        // registry means adding a constructor parameter, which is a change a reviewer looks at.
        var parameters = typeof(PairedDeviceRenamer)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType.Name)
            .ToArray();

        Assert.Equal(["PairedDeviceNameOverrideStore"], parameters);
    }

    [Fact]
    public void ARenameDoesNotTouchTheNameTheDeviceReported()
    {
        // THE DEFECT REVIEW FOUND, pinned from the side that matters. If the rename landed in the
        // reported-name store, the next re-pair would overwrite it and the user's choice would
        // vanish with nothing to explain it.
        var (renamer, overrides, reported) = NewRenamer();
        reported.Remember("phone-a", "Pixel 9");

        renamer.Rename("phone-a", "Study Phone");

        Assert.Equal("Study Phone", overrides.Snapshot()["phone-a"]);
        Assert.Equal("Pixel 9", reported.Resolve("phone-a"));
    }

    [Fact]
    public void ClearingARenameRestoresTheDeviceSOwnNameRatherThanDestroyingIt()
    {
        // THE OTHER HALF, and the one that has no undo if it goes wrong: clearing must drop only the
        // override. Deleting the reported name would leave the row showing a raw client id, and the
        // UI offers no way to get "Pixel 9" back — only an unpair and a fresh pairing would.
        var (renamer, overrides, reported) = NewRenamer();
        reported.Remember("phone-a", "Pixel 9");
        renamer.Rename("phone-a", "Study Phone");

        renamer.Rename("phone-a", "   ");

        Assert.False(overrides.Snapshot().ContainsKey("phone-a"));
        Assert.Equal("Pixel 9", reported.Resolve("phone-a"));
    }

    [Fact]
    public void AnOverrideSurvivesARestart()
    {
        var (renamer, _, _) = NewRenamer();
        renamer.Rename("phone-a", "Study Phone");

        var reopened = new PairedDeviceNameOverrideStore(
            NullLogger<PairedDeviceNameOverrideStore>.Instance,
            Path.Combine(_root.FullName, "overrides.json"));

        Assert.Equal("Study Phone", reopened.Snapshot()["phone-a"]);
    }

    [Fact]
    public void ARenameForABlankDeviceIdWritesNothing()
    {
        // REPLACES AN INERT VERSION (review). The first shape asserted Resolve("   ") was null — which
        // Resolve answers itself on its first line, so deleting the renamer's own guard left the test
        // green. Asserting the store is EMPTY is answered by the code under test.
        var (renamer, overrides, _) = NewRenamer();

        renamer.Rename("   ", "Study Phone");

        Assert.Empty(overrides.Snapshot());
    }

    [Fact]
    public void TheOverrideFileSitsBesideThePairingStoreItDescribes()
    {
        // A record that outlived its pairing, or died while the pairing lived, is worse than no
        // record — and that only holds while the files move together.
        Assert.Equal(
            Path.GetDirectoryName(PairedClientRegistry.DefaultStorePathForTests),
            Path.GetDirectoryName(PairedDeviceNameOverrideStore.DefaultStorePathForTests));
    }

    [Fact]
    public void ACorruptOverrideFileCostsNamesAndNothingElse()
    {
        // This constructor is resolved by DI on the /ws path, so an escaping exception would block
        // pairing outright rather than merely losing a name.
        var path = Path.Combine(_root.FullName, "corrupt.json");
        File.WriteAllText(path, "{ this is not json");

        var store = new PairedDeviceNameOverrideStore(
            NullLogger<PairedDeviceNameOverrideStore>.Instance, path);

        Assert.Empty(store.Snapshot());

        store.Set("phone-a", "Study Phone");
        Assert.Equal("Study Phone", store.Snapshot()["phone-a"]);
    }
}

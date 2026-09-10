using Remex.Core.Services;

namespace Remex.Core.Tests;

/// <summary>
/// The one-time legacy-file migration, now that it can be reached (RemEx-9lbg).
/// </summary>
/// <remarks>
/// <para>
/// RemEx-4u29 made the host-state redirect unconditional in every test assembly, so
/// <c>TryMigrateWindowsFile</c> returns before doing anything whenever a test calls it — its copy
/// logic became unreachable. It had no coverage before that either, so nothing was lost; what
/// changed is that it became impossible to add. The seam mirrors
/// <c>PairedClientRegistry.TryMigrateLegacyStore</c>, which already existed and was already covered.
/// </para>
/// <para>
/// THE INVERTED-GUARD CASE IS THE POINT. The migration's guards are all refusals — target exists,
/// same path, legacy missing — and a source-level pin of the shape cannot tell a guard from its
/// negation. Each of these drives one refusal and checks the file on disk rather than the return
/// value alone, because "returned false" and "left the target alone" are different claims.
/// </para>
/// </remarks>
public sealed class RemexDataPathsMigrationTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory();

    public void Dispose()
    {
        _root.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => Path.Combine(_root.FullName, name);

    [Fact]
    public void ALegacyFileIsCopiedForwardWhenNothingIsThereYet()
    {
        var legacy = Path_("legacy.json");
        var target = Path.Combine(_root.FullName, "machine", "store.json");
        File.WriteAllText(legacy, "{\"paired\":true}");

        Assert.True(RemexDataPaths.TryMigrateFile(target, legacy));

        Assert.Equal("{\"paired\":true}", File.ReadAllText(target));
        Assert.True(File.Exists(legacy), "a migration copies forward; it does not move");
    }

    [Fact]
    public void AnExistingTargetIsNEVEROverwritten()
    {
        // THE ONE THAT WOULD COST REAL STATE. A target already present means the migration has run,
        // or something else owns the machine-wide file — copying over it replaces live credentials
        // with a stale per-user copy, and nothing would say so.
        var legacy = Path_("legacy.json");
        var target = Path_("target.json");
        File.WriteAllText(legacy, "stale");
        File.WriteAllText(target, "live");

        Assert.False(RemexDataPaths.TryMigrateFile(target, legacy));

        Assert.Equal("live", File.ReadAllText(target));
    }

    [Fact]
    public void AMissingLegacyFileIsNotAFailure()
    {
        // The ordinary case on a fresh install: there is nothing to migrate, and that is not an
        // error to report — it is the answer.
        var target = Path_("target.json");

        Assert.False(RemexDataPaths.TryMigrateFile(target, Path_("not-there.json")));

        Assert.False(File.Exists(target), "nothing should have been created");
    }

    [Fact]
    public void CopyingAFileOntoItselfIsRefused()
    {
        // Reachable wherever the two locations resolve together — off Windows, or on a machine where
        // the per-user and machine-wide paths coincide. File.Copy with overwrite:false would throw;
        // the guard makes it a refusal instead.
        var path = Path_("same.json");
        File.WriteAllText(path, "one copy");

        Assert.False(RemexDataPaths.TryMigrateFile(path, path));

        Assert.Equal("one copy", File.ReadAllText(path));
    }

    [Fact]
    public void AnUnreadableLegacyFileIsRefusedRatherThanThrown()
    {
        // A directory where the legacy file should be: File.Copy raises, and the caller is a
        // constructor on the /ws path — an escaping exception there would block pairing outright
        // rather than cost a migration.
        var legacy = Path_("legacy-is-a-directory");
        Directory.CreateDirectory(legacy);
        var target = Path_("target.json");

        Assert.False(RemexDataPaths.TryMigrateFile(target, legacy));

        Assert.False(File.Exists(target));
    }
}

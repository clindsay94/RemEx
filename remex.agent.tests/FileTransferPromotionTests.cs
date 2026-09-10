using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.FileTransfer;

namespace Remex.Agent.Tests;

/// <summary>
/// Cover for promoting a verified transfer out of staging and into its destination (RemEx-fq6f).
/// </summary>
/// <remarks>
/// <para>
/// A verified upload used to be re-read from its <c>.remexpart</c> staging file and streamed into the
/// destination, so a 5 GB push cost 10 GB of writes and a 5 GB read to relocate bytes already sitting
/// on the right disk. Promotion is now a rename whenever the two paths share a volume.
/// </para>
/// <para>
/// THE TESTS THAT MATTER MOST ARE THE AUTHORIZATION ONES. Landing a file in a shared root is a write
/// grant, and the checks that gate it — size cap, root writability, and root-escape safety — used to
/// live inside <c>OpenForWriteAsync</c>, the only way to write. Adding a second way in is precisely
/// how such a check gets bypassed: not by removing it, but by a new path that never had it. Both
/// paths now funnel through one <c>ResolveForWrite</c>, and the cases below fail if a future change
/// gives promotion its own resolution logic.
/// </para>
/// <para>
/// The rename itself is asserted through a side effect rather than a stopwatch: a move CONSUMES the
/// staging file, a copy leaves it behind. That distinction is observable and stable, where timing is
/// neither.
/// </para>
/// </remarks>
public sealed class FileTransferPromotionTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private (FileTransferService service, string rootDir, string stagingDir) CreateService(bool isWritable = true)
    {
        var baseTemp = Path.Combine(Path.GetTempPath(), "remex-promote-" + Guid.NewGuid().ToString("N"));
        var rootDir = Path.Combine(baseTemp, "root");
        var stagingDir = Path.Combine(baseTemp, "staging");
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(stagingDir);
        _tempDirs.Add(baseTemp);

        var service = new FileTransferService(
            NullLogger<FileTransferService>.Instance, Path.Combine(baseTemp, "roots.json"));
        service.SeedRootsForTests(
            ("root-1", "Test Root", rootDir, isWritable, true, true, true, false));

        return (service, rootDir, stagingDir);
    }

    private static string StageFile(string stagingDir, string contents)
    {
        var path = Path.Combine(stagingDir, Guid.NewGuid().ToString("N") + ".remexpart");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public async Task PromotionMovesTheStagedFileRatherThanCopyingIt()
    {
        // THE BEAD. A move consumes the source; a copy would leave it sitting there. Asserting the
        // staging file is gone is what distinguishes the two without measuring anything.
        var (service, rootDir, stagingDir) = CreateService();
        var staged = StageFile(stagingDir, "payload");

        await service.PromoteStagedFileAsync("root-1", "landed.txt", 7, staged, CancellationToken.None);

        Assert.False(File.Exists(staged), "promotion must consume the staging file, not copy out of it");
        Assert.Equal("payload", File.ReadAllText(Path.Combine(rootDir, "landed.txt")));
    }

    [Fact]
    public async Task PromotionReplacesAnExistingFileAtTheDestination()
    {
        // Overwrite has to keep working: re-sending a file is ordinary, and the previous
        // FileMode.Create copy replaced silently. The improvement is that a rename replaces
        // atomically, so a failure cannot leave the old file truncated — which the copy could.
        var (service, rootDir, stagingDir) = CreateService();
        File.WriteAllText(Path.Combine(rootDir, "landed.txt"), "the previous version");
        var staged = StageFile(stagingDir, "the new version");

        await service.PromoteStagedFileAsync("root-1", "landed.txt", 15, staged, CancellationToken.None);

        Assert.Equal("the new version", File.ReadAllText(Path.Combine(rootDir, "landed.txt")));
    }

    [Fact]
    public async Task PromotionCreatesMissingParentDirectories()
    {
        // A push into a subfolder the root does not have yet. OpenForWriteAsync did this, so the
        // promotion path must too or nested uploads start failing.
        var (service, rootDir, stagingDir) = CreateService();
        var staged = StageFile(stagingDir, "nested");

        await service.PromoteStagedFileAsync("root-1", "a/b/c.txt", 6, staged, CancellationToken.None);

        Assert.Equal("nested", File.ReadAllText(Path.Combine(rootDir, "a", "b", "c.txt")));
    }

    [Fact]
    public async Task PromotionIntoAReadOnlyRootIsRefused()
    {
        // AUTHORIZATION. The read-only flag is what makes the default Documents/Desktop/Pictures roots
        // safe to expose, and a promotion path that skipped it would let any push write to them.
        var (service, rootDir, stagingDir) = CreateService(isWritable: false);
        var staged = StageFile(stagingDir, "payload");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.PromoteStagedFileAsync("root-1", "landed.txt", 7, staged, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(rootDir, "landed.txt")));
        Assert.True(File.Exists(staged), "a refused promotion must leave staging intact for cleanup");
    }

    [Fact]
    public async Task PromotionCannotEscapeTheRoot()
    {
        // AUTHORIZATION, and the one with the worst consequence: a traversal here writes an
        // attacker-supplied file anywhere the elevated agent can reach. The client controls the
        // relative path, so this is reachable input, not a hypothetical.
        var (service, rootDir, stagingDir) = CreateService();
        var staged = StageFile(stagingDir, "payload");
        var escapeTarget = Path.GetFullPath(Path.Combine(rootDir, "..", "escaped.txt"));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.PromoteStagedFileAsync(
                "root-1", "../escaped.txt", 7, staged, CancellationToken.None));

        Assert.False(File.Exists(escapeTarget), "a traversal must not write outside the shared root");
    }

    [Fact]
    public async Task PromotionRefusesAFileOverTheSizeCap()
    {
        // The cap is declared by the sender, so it is checked before the bytes are trusted. Promotion
        // happens after the transfer, but re-checking costs nothing and keeps the two write paths
        // behaviourally identical — which is the property that stops them drifting.
        var (service, _, stagingDir) = CreateService();
        var staged = StageFile(stagingDir, "payload");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.PromoteStagedFileAsync(
                "root-1", "huge.bin", 6_000_000_000L, staged, CancellationToken.None));
    }

    [Fact]
    public async Task PromotionIntoAnUnknownRootIsRefused()
    {
        var (service, _, stagingDir) = CreateService();
        var staged = StageFile(stagingDir, "payload");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.PromoteStagedFileAsync(
                "no-such-root", "landed.txt", 7, staged, CancellationToken.None));
    }

    [Fact]
    public async Task APromotedFileDoesNotCarryStagingPermissions()
    {
        // WINDOWS-ONLY BY NATURE. NTFS does not recompute inherited ACEs on a cross-directory rename,
        // so a promoted file keeps whatever it inherited from staging — which lives machine-wide under
        // ProgramData. Left unrepaired, a file received into the user's Documents arrives READ-ONLY to
        // its owner and readable by every local account, neither of which was true when the old code
        // created the file in place. Unix rename() preserves owner and mode and the agent already runs
        // as the signed-in user, so there is nothing to repair and nothing to assert there.
        if (!OperatingSystem.IsWindows())
            return;

        var (service, rootDir, stagingDir) = CreateService();

        // Mark staging with an ACE the destination does not have, so the assertion below can only pass
        // if the stale inheritance was actually dropped.
        var everyone = new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.WorldSid, null);
        var stagingDirInfo = new DirectoryInfo(stagingDir);
        var stagingAcl = stagingDirInfo.GetAccessControl();
        stagingAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            everyone,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.InheritanceFlags.ObjectInherit
                | System.Security.AccessControl.InheritanceFlags.ContainerInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        stagingDirInfo.SetAccessControl(stagingAcl);

        var staged = StageFile(stagingDir, "payload");
        await service.PromoteStagedFileAsync("root-1", "landed.txt", 7, staged, CancellationToken.None);

        // Pin that this went through the RENAME branch. Root and staging are siblings under one temp
        // volume today, but if that ever stopped being true the copy branch would also produce a clean
        // ACL and this test would pass without exercising the fix at all.
        Assert.False(File.Exists(staged), "this test is only meaningful on the rename branch");

        var landed = Path.Combine(rootDir, "landed.txt");
        var rules = new FileInfo(landed)
            .GetAccessControl()
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));

        var carriedOver = rules
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .Any(r => r.IdentityReference.Equals(everyone));

        Assert.False(carriedOver, "the promoted file must not inherit staging's permissions");
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { /* best effort: a temp dir left behind fails nothing */ }
            catch (UnauthorizedAccessException) { /* ditto */ }
        }
    }
}


using Xunit.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.FileTransfer;

namespace Remex.Agent.Tests;

/// <summary>
/// RemEx-cojhy. Promoting a staged upload onto a destination that somebody still has open.
/// </summary>
/// <remarks>
/// <para>
/// This is not a contrived situation. Uploading a file that already lives inside the target shared
/// root makes the destination and the client's source the same path, and
/// <c>FileTransferClient.UploadAsync</c> holds the source open <c>FileShare.Read</c> for the whole
/// transfer — through the final round-trip, which is when the host promotes. Nothing upstream
/// rejects it: <c>ResolveForWrite</c> checks the size cap and the root's writability, and has no
/// idea what the client is reading.
/// </para>
/// <para>
/// What must be true is that the destination is never left worse than it was found. A clean failure
/// is an acceptable outcome; a truncated file is not, and that is what the cross-volume branch used
/// to do — it opened the destination <c>FileMode.Create</c>, which truncates before the first byte
/// arrives, while the reader still had it open.
/// </para>
/// </remarks>
public sealed class FileTransferPromoteOverOpenDestinationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _tempDirs = new();

    public FileTransferPromoteOverOpenDestinationTests(ITestOutputHelper output) => _output = output;

    private const string ExistingContents = "the file the user already had";
    private const string StagedContents = "the bytes that were uploaded";

    [Fact]
    public async Task PromotionOntoAFileSomebodyIsReadingNeverLeavesItTruncated()
    {
        var (service, rootDir, stagingDir) = CreateService();
        var destination = Path.Combine(rootDir, "report.pdf");
        File.WriteAllText(destination, ExistingContents);

        var staged = Path.Combine(stagingDir, "upload.remexpart");
        File.WriteAllText(staged, StagedContents);

        // Exactly what the desktop client holds while it uploads (FileTransferClient.cs:673).
        await using var readerHoldingTheDestination = new FileStream(
            destination, FileMode.Open, FileAccess.Read, FileShare.Read);

        var promoting = async () => await service.PromoteStagedFileAsync(
            "root-1", "report.pdf", StagedContents.Length, staged, CancellationToken.None);

        // EITHER outcome is allowed - a clean throw, or a successful atomic replace. What is not
        // allowed is the third one: the destination emptied or half-written. So the assertion is on
        // the file, not on whether it threw.
        try
        {
            await promoting();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A refusal is fine. Windows cannot replace a file held without FileShare.Delete.
        }

        var landed = ReadWithoutLocking(destination);
        Assert.False(
            string.IsNullOrEmpty(landed),
            "the destination was left empty - a truncated file is the one outcome this guards against");
        Assert.True(
            landed == ExistingContents || landed == StagedContents,
            $"the destination holds neither the old contents nor the new ones, so it was written part-way: '{landed}'");
    }

    [Fact]
    public async Task TheFailureSaysWhatIsActuallyWrongRatherThanBlamingPermissions()
    {
        // MoveFileEx reports a blocked replace as UnauthorizedAccessException, "Access to the path is
        // denied", and TransferSessionManager passes that straight to the client as "Verified but
        // could not be saved: Access to the path is denied". That names the wrong cause: the shared
        // folder's permissions are fine, and the handle blocking the replace is usually the client's
        // own, because the file being uploaded is the one already sitting in the target folder.
        var (service, rootDir, stagingDir) = CreateService();
        var destination = Path.Combine(rootDir, "report.pdf");
        File.WriteAllText(destination, ExistingContents);

        var staged = Path.Combine(stagingDir, "upload.remexpart");
        File.WriteAllText(staged, StagedContents);

        await using var readerHoldingTheDestination = new FileStream(
            destination, FileMode.Open, FileAccess.Read, FileShare.Read);

        var failure = await Record.ExceptionAsync(() => service.PromoteStagedFileAsync(
            "root-1", "report.pdf", StagedContents.Length, staged, CancellationToken.None));

        // Linux replaces an open file happily, so there is nothing to report there.
        if (failure is null)
        {
            Assert.True(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
                "the replace succeeded on a platform where an open handle should have blocked it");
            return;
        }

        Assert.Contains("report.pdf", failure.Message);
        Assert.Contains("open in another program", failure.Message);
        Assert.NotNull(failure.InnerException);
    }

    [Fact]
    public async Task ACrossVolumePromotionNeverTruncatesTheDestination()
    {
        // The branch that used to open the destination FileMode.Create. Needs two real volumes: a
        // staging directory and a shared root that do not share a mount point.
        //
        // WHAT THIS CAN AND CANNOT SHOW, MEASURED RATHER THAN ASSUMED. On Windows, opening a file
        // FileMode.Create while another handle holds it FileShare.Read is refused before anything is
        // truncated — checked directly, the open throws and the contents are intact. So on Windows
        // this test exercises the cross-volume branch and its cleanup, but it CANNOT tell the fix
        // from the bug: reverting to the destination-truncating open leaves it green here. That was
        // confirmed by injection, not assumed.
        //
        // The truncation is reachable on Linux, where the open succeeds and destroys the file the
        // client is still reading.
        //
        // AND THIS TEST CANNOT GUARD IT THERE EITHER, TODAY. .github/workflows/dotnet.yml runs only
        // remex.core.tests and remex.desktop.tests on the ubuntu leg; remex.agent.tests is Windows
        // only, and the bead that would change that (RemEx-a83w) is deferred. So the data-loss half
        // of RemEx-cojhy has no test that executes anywhere — it is guarded by this comment and by
        // the code's own, and it stays that way until agent tests run on Linux (RemEx-rpy54).
        //
        // The Windows-observable half is the sibling cleanup asserted at the end, which does fail if
        // the catch is removed.
        var stagingDir = Path.Combine(Path.GetTempPath(), "remex-cojhy-" + Guid.NewGuid().ToString("N"));
        var otherVolumeRoot = SecondVolumeDirectory(Path.GetTempPath());

        if (otherVolumeRoot is null)
        {
            // NOT A SKIP, AND THAT IS A LIMITATION RATHER THAN A CHOICE. xUnit 2.9.3 has no dynamic
            // skip - Assert.Skip is a v3 API - and adding a package for one test is out of
            // proportion. So this reports as a pass with a line in the test output saying it did
            // nothing. What was here first, Assert.True(true, "skipped..."), was worse: a passing
            // assertion's message is discarded entirely, so a machine that never exercised this
            // branch looked identical to one that did.
            _output.WriteLine(
                "DID NOT RUN: no second fixed volume with an existing scratch directory on this "
                + "machine, so the cross-volume promotion branch was not exercised.");
            return;
        }

        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(otherVolumeRoot);
        _tempDirs.Add(stagingDir);
        _tempDirs.Add(otherVolumeRoot);

        var service = new FileTransferService(
            NullLogger<FileTransferService>.Instance, Path.Combine(stagingDir, "roots.json"));
        service.SeedRootsForTests(("root-1", "Other Volume", otherVolumeRoot, true, true, true, true, false));

        var destination = Path.Combine(otherVolumeRoot, "report.pdf");
        File.WriteAllText(destination, ExistingContents);

        var staged = Path.Combine(stagingDir, "upload.remexpart");
        File.WriteAllText(staged, StagedContents);

        await using var readerHoldingTheDestination = new FileStream(
            destination, FileMode.Open, FileAccess.Read, FileShare.Read);

        try
        {
            await service.PromoteStagedFileAsync(
                "root-1", "report.pdf", StagedContents.Length, staged, CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A refusal is fine; a truncated file is not.
        }

        var landed = ReadWithoutLocking(destination);
        Assert.True(
            landed == ExistingContents || landed == StagedContents,
            $"the cross-volume promotion left the destination part-written: '{landed}'");

        // And it must not leave its working file behind in the user's folder.
        Assert.Empty(Directory.GetFiles(otherVolumeRoot, "*.remexnew-*"));
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("a")]
    public void TheTempFileSitsBesideTheDestinationAndKeepsItsName(string name)
    {
        var destination = Path.Combine(Path.GetTempPath(), name);

        var sibling = FileTransferService.SiblingPathFor(destination);

        Assert.Equal(Path.GetDirectoryName(destination), Path.GetDirectoryName(sibling));
        Assert.StartsWith(name, Path.GetFileName(sibling), StringComparison.Ordinal);
        Assert.Contains(".remexnew-", sibling, StringComparison.Ordinal);
    }

    [Fact]
    public void ALegalFilenameNeverProducesAnIllegalTempFilename()
    {
        // 245 characters is a legal name that nothing upstream rejects - FilePathValidation has no
        // length rule at all - and NAME_MAX is 255 on both NTFS and ext4 regardless of long-path
        // settings. Appending the suffix without a budget would produce a 288-character component
        // and fail a transfer that works fine when the shared root happens to sit on the staging
        // volume, so the failure would look random depending on which drive the folder is on.
        var longButLegal = new string('x', 241) + ".pdf";
        Assert.Equal(245, longButLegal.Length);

        var sibling = FileTransferService.SiblingPathFor(Path.Combine(Path.GetTempPath(), longButLegal));

        Assert.True(
            Path.GetFileName(sibling).Length <= 255,
            $"the temp filename is {Path.GetFileName(sibling).Length} characters, past NAME_MAX");
        Assert.Contains(".remexnew-", sibling, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scratch directory on a volume other than <paramref name="notThisOne"/>'s, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FIXED VOLUMES ONLY, AND NEVER THE VOLUME ROOT. An earlier draft accepted
    /// <see cref="DriveType.Network"/> and composed its path directly at the drive root, which on
    /// this project's own machine reaches the network share the repository lives on. Cleanup is in
    /// <c>Dispose</c>, so a run killed part-way — an interrupted verify, a cancelled CI job — would
    /// have left fixture files at the root of a shared drive.
    /// </para>
    /// <para>
    /// So this only uses a conventional scratch directory that ALREADY EXISTS on the other volume,
    /// and creates nothing if there is none. That means the test skips on a machine whose second
    /// volume has no such directory, which is the correct trade: this test cannot distinguish the
    /// fix from the bug on Windows anyway (see the note in the test itself), so buying coverage with
    /// litter on somebody's drive is a bad exchange.
    /// </para>
    /// </remarks>
    private static string? SecondVolumeDirectory(string notThisOne)
    {
        var exclude = Path.GetPathRoot(Path.GetFullPath(notThisOne));

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not DriveType.Fixed) continue;
            if (string.Equals(drive.RootDirectory.FullName, exclude, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var scratch in new[] { "Temp", "tmp", "var/tmp" })
            {
                var existing = Path.Combine(drive.RootDirectory.FullName, scratch);
                if (!Directory.Exists(existing)) continue;

                var candidate = Path.Combine(existing, "remex-cojhy-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(candidate);
                    Directory.Delete(candidate);
                    return candidate;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Present but not writable — try the next candidate.
                }
            }
        }

        return null;
    }

    /// <summary>Reads a file without taking a lock that would perturb what is being measured.</summary>
    private static string ReadWithoutLocking(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private (FileTransferService service, string rootDir, string stagingDir) CreateService()
    {
        var baseTemp = Path.Combine(Path.GetTempPath(), "remex-cojhy-" + Guid.NewGuid().ToString("N"));
        var rootDir = Path.Combine(baseTemp, "root");
        var stagingDir = Path.Combine(baseTemp, "staging");
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(stagingDir);
        _tempDirs.Add(baseTemp);

        var service = new FileTransferService(
            NullLogger<FileTransferService>.Instance, Path.Combine(baseTemp, "roots.json"));
        service.SeedRootsForTests(
            ("root-1", "Test Root", rootDir, true, true, true, true, false));

        return (service, rootDir, stagingDir);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A held handle on a temp file is not worth failing a test run over.
            }
        }
    }
}

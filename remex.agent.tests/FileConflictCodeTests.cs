using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.FileTransfer;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that a real collision produces a machine-readable code, not just prose (RemEx-6vd8).
/// </summary>
/// <remarks>
/// **THE BEAD'S CENTRAL WARNING WAS ABOUT SEQUENCING.** An <c>errorCode</c> field was written during
/// RemEx-12cj and reverted before commit, because it would have shipped unset — the same shape as
/// RemEx-mneb, where a message type is declared and round-trip tested while no code path ever sets
/// it. So these tests run against the REAL <c>FileTransferService</c> on a REAL temp directory: they
/// fail if the field exists but nothing populates it, which a serialization round-trip cannot.
/// </remarks>
public sealed class FileConflictCodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remex-conflict-" + Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly FileTransferService _service;

    public FileConflictCodeTests()
    {
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "roots.json");
        _service = new FileTransferService(NullLogger<FileTransferService>.Instance, _configPath);
        _service.SeedRootsForTests(("r1", "Shared", _root, true, true, true, true, false));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string relativeName, string content = "x")
    {
        var path = Path.Combine(_root, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task CopyingOntoAnExistingFileCarriesTheCollisionCode()
    {
        Write("a.txt", "source");
        Write("b.txt", "victim");

        var ex = await Assert.ThrowsAsync<FileConflictException>(
            () => _service.CopyAsync("r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None));

        Assert.Equal(FileTransferErrorCodes.DestinationExists, ex.ErrorCode);
        Assert.Equal("b.txt", ex.ConflictingName);

        // The prose is KEPT, not replaced - it is still what a person reads when the client is older
        // than this change, or when the UI has nothing better to show.
        Assert.Contains("b.txt", ex.Message);
    }

    [Fact]
    public async Task ItIsStillAnIOException_SoExistingCatchBlocksAreUnaffected()
    {
        Write("a.txt");
        Write("b.txt");

        // The code is additional information for the one handler that looks for it, never a new
        // failure mode for the many that do not.
        await Assert.ThrowsAsync<FileConflictException>(
            () => _service.CopyAsync("r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None));

        var caught = await Record.ExceptionAsync(
            () => _service.CopyAsync("r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None));

        Assert.IsAssignableFrom<IOException>(caught);
    }

    [Fact]
    public async Task AFolderStandingWhereAFileShouldGoIsADIFFERENTCode()
    {
        // "Replace" here would mean deleting a whole directory tree to make room for one file.
        // Nobody intends that from a copy and nothing undoes it, so a client must be able to tell
        // this apart and withhold the button.
        Write("a.txt");
        Directory.CreateDirectory(Path.Combine(_root, "b.txt"));

        var ex = await Assert.ThrowsAsync<FileConflictException>(
            () => _service.CopyAsync("r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None));

        Assert.Equal(FileTransferErrorCodes.DestinationIsDifferentKind, ex.ErrorCode);
    }

    [Fact]
    public async Task KeepBothWritesASiblingAndReportsTheNameItUsed()
    {
        Write("a.txt", "source");
        Write("b.txt", "keep me");

        var resolved = await _service.CopyAsync(
            "r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None,
            FileConflictResolutions.KeepBoth);

        Assert.Equal("b (2).txt", resolved);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_root, "b.txt")));
        Assert.Equal("source", File.ReadAllText(Path.Combine(_root, "b (2).txt")));
    }

    [Fact]
    public async Task ReplaceOverwritesWithoutNeedingTheLegacyFlag()
    {
        Write("a.txt", "source");
        Write("b.txt", "victim");

        var resolved = await _service.CopyAsync(
            "r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None,
            FileConflictResolutions.Replace);

        Assert.Null(resolved);
        Assert.Equal("source", File.ReadAllText(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public async Task MovingHonoursKeepBothToo_AndLeavesTheExistingFileAlone()
    {
        // Move is the destructive one: a wrong answer here loses the source AND the destination.
        Write("a.txt", "source");
        Write("b.txt", "keep me");

        var resolved = await _service.MoveAsync(
            "r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None,
            FileConflictResolutions.KeepBoth);

        Assert.Equal("b (2).txt", resolved);
        Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_root, "b.txt")));
        Assert.Equal("source", File.ReadAllText(Path.Combine(_root, "b (2).txt")));
    }

    [Fact]
    public async Task ACopyThatDoesNotCollideReportsNoResolvedName()
    {
        // Null means "what you asked for is what you got", and the UI shows nothing extra.
        Write("a.txt", "source");

        var resolved = await _service.CopyAsync(
            "r1", "a.txt", "c.txt", overwrite: false, CancellationToken.None,
            FileConflictResolutions.KeepBoth);

        Assert.Null(resolved);
        Assert.True(File.Exists(Path.Combine(_root, "c.txt")));
    }

    [Fact]
    public async Task ARequestWithNoResolutionStillFails_WhichIsTheCompatibilityGuarantee()
    {
        // Every client that predates this field must behave exactly as before. If passing null
        // started overwriting or renaming, this change would silently alter what existing phones do.
        Write("a.txt", "source");
        Write("b.txt", "victim");

        await Assert.ThrowsAsync<FileConflictException>(
            () => _service.CopyAsync("r1", "a.txt", "b.txt", overwrite: false, CancellationToken.None, null));

        Assert.Equal("victim", File.ReadAllText(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public async Task KeepBothThatProducesAnUncreatableNameSaysSo()
    {
        // REPRODUCED BEFORE IT WAS FIXED, and this test was RED against unmodified main with a raw
        // IOException where a coded one belongs. NextAvailableName guarantees the chosen name is
        // ABSENT from the destination; it never guarantees it is CREATABLE. Measured on Windows: a
        // 255-character name creates fine, the same name with " (2)" appended is 259 and throws -
        // and long-path support does not save it, because the limit breached is the COMPONENT limit.
        //
        // Reporting that as a collision would re-open the sheet on a question the user has answered,
        // and the only answer that could work is the one they declined.
        var stem = new string('n', 251);
        Write("src.txt", "source");
        Write(stem + ".txt", "victim");

        var ex = await Assert.ThrowsAsync<FileConflictException>(
            () => _service.CopyAsync("r1", "src.txt", stem + ".txt", overwrite: false,
                CancellationToken.None, FileConflictResolutions.KeepBoth));

        Assert.Equal(FileTransferErrorCodes.ResolvedNameUnusable, ex.ErrorCode);

        // Names the name that FAILED, not the one asked for - it is longer than what the user was
        // shown, and its length is the whole problem.
        Assert.Equal(stem + " (2).txt", ex.ConflictingName);

        // And the file the user asked to keep is untouched.
        Assert.Equal("victim", File.ReadAllText(Path.Combine(_root, stem + ".txt")));
    }

    [Fact]
    public async Task AKeepBothThatSucceedsIsUnaffectedByTheWrapper()
    {
        // The control. A wrapper that reported every renamed create as unusable would satisfy the
        // test above while breaking the feature it protects.
        Write("a.txt", "source");
        Write("b.txt", "keep me");

        var resolved = await _service.CopyAsync("r1", "a.txt", "b.txt", overwrite: false,
            CancellationToken.None, FileConflictResolutions.KeepBoth);

        Assert.Equal("b (2).txt", resolved);
        Assert.Equal("source", File.ReadAllText(Path.Combine(_root, "b (2).txt")));
    }

    [Theory]
    [InlineData("disk full")]
    [InlineData("denied on a nested child")]
    [InlineData("a child path too long")]
    public void AFailureFROMTheOPERATIONIsNotBlamedOnTheChosenName(string cause)
    {
        // THE MIS-ATTRIBUTION REVIEW FOUND. The wrapper used to cover the WHOLE operation, including
        // a recursive copy over an entire tree, so anything that went wrong deep inside it came back
        // as "the name we chose is unusable" - a confident wrong diagnosis the client renders as a
        // sheet about the wrong problem, offering a rename that fixes nothing.
        //
        // THE FAILURE IS INJECTED RATHER THAN PROVOKED, and that is the point. The first version of
        // this test built a 400-character-deep source tree and copied it, expecting the depth to
        // fail - it did not, because long-path support is enabled here, so the copy simply succeeded
        // and the test passed against the UNFIXED code. It could not fail; it proved nothing.
        // Injecting the exception tests the attribution rule itself and holds on any filesystem.
        Exception thrown = cause switch
        {
            "disk full" => new IOException("There is not enough space on the disk."),
            "denied on a nested child" => new UnauthorizedAccessException("Access to a child is denied."),
            _ => new PathTooLongException("A child path is too long."),
        };
        var plan = new ConflictResolutionPlan(
            Overwrite: false,
            DestinationPath: Path.Combine(_root, "dst (2)"),
            ResolvedName: "dst (2)");

        var caught = Record.Exception(
            () => FileTransferService.RunRenamedCreate(plan, () => throw thrown));

        Assert.Same(thrown, caught);
        Assert.IsNotType<FileConflictException>(caught);
    }

    [Fact]
    public void TheProbeDoesNotRunWhenNoNameWasChosen()
    {
        // A PROBE ON THE REPLACE PATH WOULD BE DESTRUCTIVE, not merely wasteful: it opens the
        // destination with CreateNew and then removes it, so on a plan that resolved to no new name
        // it would destroy the very file the operation is about before the operation ran.
        //
        // HONEST ABOUT WHAT THIS PROVES: the early return predates this change, so this test is
        // green on the unfixed code too. It guards the precondition the new probe relies on rather
        // than the fix itself - the regression tests for the fix are the two below.
        var victim = Write("victim.txt", "irreplaceable");
        var plan = new ConflictResolutionPlan(Overwrite: true, DestinationPath: victim, ResolvedName: null);
        var ran = false;

        FileTransferService.RunRenamedCreate(plan, () =>
        {
            ran = true;
            Assert.Equal("irreplaceable", File.ReadAllText(victim));
        });

        Assert.True(ran, "the operation must still run");
        Assert.Equal("irreplaceable", File.ReadAllText(victim));
    }

    [Fact]
    public async Task TheProbeLeavesNothingBehindWhenTheNameIsFine()
    {
        // Rewritten after review pointed out the first version was a byte-identical copy of
        // AKeepBothThatSucceedsIsUnaffectedByTheWrapper - it could not fail unless that one did.
        // Asserting the WHOLE listing of the shared root is what earns the name: it catches a probe
        // that left anything behind anywhere the operation could see, not just at the resolved path.
        //
        // ORDINAL, NOT Order(). The default comparer is culture-aware, and "b (2).txt" vs "b.txt"
        // differ only in a space and a parenthesis - exactly the characters whose collation weight
        // moves between Windows NLS and Linux ICU. This suite is run on both.
        Write("a.txt", "source");
        Write("b.txt", "keep me");

        var resolved = await _service.CopyAsync("r1", "a.txt", "b.txt", overwrite: false,
            CancellationToken.None, FileConflictResolutions.KeepBoth);

        Assert.Equal("b (2).txt", resolved);
        Assert.Equal(
            new[] { "a.txt", "b (2).txt", "b.txt", "roots.json" },
            Directory.GetFileSystemEntries(_root).Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheProbeNEVERDestroysWhateverAlreadyHoldsTheName(bool squatterIsDirectory)
    {
        // THE DATA-LOSS BUG REVIEW CAUGHT IN THE FIRST VERSION OF THIS FIX. Cleanup lived in a
        // finally, and a finally runs on the THROW path - the path taken when CreateNew fails
        // BECAUSE SOMETHING IS ALREADY THERE. So the probe deleted what it had never created and
        // then reported that the name could not be created, having just destroyed what was using it.
        //
        // IT ALSO PINS THE ORDERING BUG, which is why the directory case is here rather than in a
        // test of its own. A directory on the path is refused as UnauthorizedAccessException on
        // Windows - the same type a genuine ACL denial produces, and the denial case is deliberately
        // swallowed so the operation can report it - so classifying by type before checking what is
        // actually there swallowed the OCCUPIED case too. Linux reaches the same place by a different
        // route: O_EXCL returns EEXIST, which .NET surfaces as IOException.
        //
        // A CONSEQUENCE WORTH KNOWING BEFORE TRUSTING THIS TEST ON LINUX: the behaviour asserted
        // holds on both platforms, but the MUTATION kill does not. A type-before-existence mutant
        // dies here on Windows and SURVIVES on Linux, where IOException is attributed either way.
        //
        // RACE-ONLY IN PRODUCTION, and worth saying plainly: with keep-both, Overwrite is always
        // false, so all four pre-checks in CopyAsync fire before RunRenamedCreate is reached. Only a
        // change in the gap gets here - a second paired device, another message on the same socket,
        // or the person at the PC saving a file - which is why this drives the internal seam
        // directly rather than pretending CopyAsync can stage the race.
        var squatter = Path.Combine(_root, "b (2).txt");
        if (squatterIsDirectory)
        {
            Directory.CreateDirectory(squatter);
            File.WriteAllText(Path.Combine(squatter, "inside.txt"), "somebody else's work");
        }
        else
        {
            File.WriteAllText(squatter, "somebody else's work");
        }

        var plan = new ConflictResolutionPlan(
            Overwrite: false, DestinationPath: squatter, ResolvedName: "b (2).txt");

        var ex = Assert.Throws<FileConflictException>(
            () => FileTransferService.RunRenamedCreate(
                plan, () => Assert.Fail("the operation must not run")));

        var survivor = squatterIsDirectory ? Path.Combine(squatter, "inside.txt") : squatter;
        Assert.Equal("somebody else's work", File.ReadAllText(survivor));

        // AND CRUCIALLY NOT destination_exists, which review showed is a data-loss button here. That
        // code unlocks Replace, and Replace re-answers the ORIGINAL request - overwrite b.txt - while
        // the sheet is naming b (2).txt. A user who chose "keep both" precisely to protect b.txt
        // would destroy it by answering a question about a different file. resolved_name_unusable is
        // Skip-only; RemEx-od7s adds a code that can say "taken" and still offer keep-both safely.
        Assert.Equal(FileTransferErrorCodes.ResolvedNameUnusable, ex.ErrorCode);
        Assert.Equal("b (2).txt", ex.ConflictingName);
    }

}

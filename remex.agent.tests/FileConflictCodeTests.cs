using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

        // TAKEN, NOT UNUSABLE - the distinction is whether asking again can work, and here it can:
        // the name is creatable, something simply got there first, so a retry re-lists and picks the
        // next free one. The client can therefore offer keep-both again.
        //
        // AND CRUCIALLY NOT destination_exists, however true that sentence is. That code unlocks
        // Replace, and Replace re-answers the ORIGINAL request - overwrite b.txt - while the sheet
        // is naming b (2).txt. A user who chose "keep both" precisely to protect b.txt would destroy
        // it by answering a question about a different file.
        Assert.Equal(FileTransferErrorCodes.ResolvedNameTaken, ex.ErrorCode);
        Assert.Equal("b (2).txt", ex.ConflictingName);
    }


    [Fact]
    public void TheWireTOKENSArePinnedVerbatim_BecauseTheOtherSideCannotSeeThem()
    {
        // A PRIVATE AGREEMENT BETWEEN TWO CODEBASES THAT CANNOT SEE EACH OTHER, and review found the
        // host's half unpinned. Every other test here compares a constant to a constant, which
        // survives any edit to the literal - so renaming "resolved_name_taken" to a typo would leave
        // this whole suite AND the Kotlin suite green while the phone silently falls back to
        // Skip-only, losing a button that works. Kotlin pins its side; this pins ours.
        Assert.Equal("destination_exists", FileTransferErrorCodes.DestinationExists);
        Assert.Equal("destination_is_different_kind", FileTransferErrorCodes.DestinationIsDifferentKind);
        Assert.Equal("resolved_name_unusable", FileTransferErrorCodes.ResolvedNameUnusable);
        Assert.Equal("resolved_name_taken", FileTransferErrorCodes.ResolvedNameTaken);

        // And the resolutions, which travel the same way and fail just as quietly.
        Assert.Equal("keep_both", FileConflictResolutions.KeepBoth);
        Assert.Equal("replace", FileConflictResolutions.Replace);
    }

    [Fact]
    public async Task MoveKeepBothOntoAnUncreatableNameSaysSo_AndKEEPSTheSource()
    {
        // MOVE HAD THE SAME HOLE COPY CLOSED IN RemEx-cirk, and kept it after copy was fixed - so
        // the identical request produced a coded, answerable refusal one way and the OS's opaque
        // "the filename, directory name, or volume label syntax is incorrect" the other. The client
        // cannot branch on that, so no sheet ever opened for a moved file.
        //
        // AND THE SOURCE ASSERTION IS THE POINT OF DOING THIS FOR MOVE. Copy failing costs a retry;
        // move failing after it has removed the source costs the file. The probe runs before the
        // operation, so a refusal here must leave everything exactly where it was.
        //
        // THE OTHER REFUSAL - resolved_name_taken - CANNOT BE STAGED FROM HERE, and trying taught
        // something worth keeping: seeding "b (2).txt" before the call does not race anything,
        // because ConflictResolver re-lists the directory and simply picks "b (3).txt". The occupied
        // branch needs the name claimed after the File.Exists pre-check and before the create -
        // claimed any earlier and that pre-check reports destination_exists instead - which is a
        // window a few instructions wide that no public call can arrange. Hence the seam tests
        // above. Move reaches the same seam, so it inherits that
        // behaviour; what these two tests prove is the ROUTING, since resolved_name_unusable is a
        // code only RunRenamedCreate can emit.
        var stem = new string('n', 251);
        Write("src.txt", "the only copy");
        Write(stem + ".txt", "victim");

        var ex = await Assert.ThrowsAsync<FileConflictException>(
            () => _service.MoveAsync("r1", "src.txt", stem + ".txt", overwrite: false,
                CancellationToken.None, FileConflictResolutions.KeepBoth));

        Assert.Equal(FileTransferErrorCodes.ResolvedNameUnusable, ex.ErrorCode);
        Assert.Equal(stem + " (2).txt", ex.ConflictingName);

        Assert.Equal("the only copy", File.ReadAllText(Path.Combine(_root, "src.txt")));
        Assert.Equal("victim", File.ReadAllText(Path.Combine(_root, stem + ".txt")));
    }

    [Fact]
    public async Task MovingAFolderIsProbedToo_NotJustAFile()
    {
        // The directory branch composes a name the same way and had the same gap. Asserted through
        // MoveAsync rather than the seam, because the seam cannot show that the DIRECTORY path
        // reaches it - which is the whole claim.
        // 253, NOT 251, AND THE ARITHMETIC IS THE TEST. A directory has no extension to spend, so
        // 251 + " (2)" is exactly 255 - at the limit, and it creates fine. The first version of this
        // test used 251 and passed against UNPROBED code by simply succeeding. 253 + " (2)" is 257,
        // which is the case that actually breaches the component limit.
        var stem = new string('d', 253);
        Directory.CreateDirectory(Path.Combine(_root, "srcdir"));
        Write(Path.Combine("srcdir", "inner.txt"), "the only copy");
        Directory.CreateDirectory(Path.Combine(_root, stem));

        var ex = await Assert.ThrowsAsync<FileConflictException>(
            () => _service.MoveAsync("r1", "srcdir", stem, overwrite: false,
                CancellationToken.None, FileConflictResolutions.KeepBoth));

        Assert.Equal(FileTransferErrorCodes.ResolvedNameUnusable, ex.ErrorCode);
        Assert.Equal(stem + " (2)", ex.ConflictingName);
        Assert.Equal("the only copy", File.ReadAllText(Path.Combine(_root, "srcdir", "inner.txt")));
        Assert.True(Directory.Exists(Path.Combine(_root, stem)));
    }

    [Fact]
    public async Task AFolderMoveThatSUCCEEDSIsUnaffectedByTheProbe()
    {
        // THE CONTROL FOR THE DIRECTORY BRANCH, which review found had none. Its sibling test throws
        // at the probe and never reaches Directory.Move, so a probe that failed to release the name
        // in time would break every keep-both folder move while the suite stayed green. The file
        // branch has had this control since RemEx-cirk; the directory branch did not.
        Directory.CreateDirectory(Path.Combine(_root, "srcdir"));
        Write(Path.Combine("srcdir", "inner.txt"), "moved");
        Directory.CreateDirectory(Path.Combine(_root, "dst"));
        Write(Path.Combine("dst", "keep.txt"), "keep me");

        var resolved = await _service.MoveAsync("r1", "srcdir", "dst", overwrite: false,
            CancellationToken.None, FileConflictResolutions.KeepBoth);

        Assert.Equal("dst (2)", resolved);
        Assert.Equal("moved", File.ReadAllText(Path.Combine(_root, "dst (2)", "inner.txt")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_root, "dst", "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "srcdir")), "the source must be gone");

        // And nothing the probe made survives anywhere the operation could see.
        Assert.Equal(
            new[] { "dst", "dst (2)", "roots.json" },
            Directory.GetFileSystemEntries(_root).Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    // ── Which code an occupied destination gets, and who chose the name (RemEx-nhw2) ───────────

    [Fact]
    public void ANameTHEUSERChoseKeepsTheCodeThatOffersReplace()
    {
        // Unchanged behaviour, and it must stay unchanged: the user named this destination, so
        // "that already exists" is a question they can answer, and Replace answers exactly the
        // request they made. Removing that would break the ordinary collision the whole feature
        // exists for.
        var plan = new ConflictResolutionPlan(
            Overwrite: false, DestinationPath: Path.Combine(_root, "b.txt"), ResolvedName: null);

        var ex = FileTransferService.Occupied(plan, FileConflictException.FileExists);

        Assert.Equal(FileTransferErrorCodes.DestinationExists, ex.ErrorCode);
        Assert.Equal("b.txt", ex.ConflictingName);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("different kind")]
    public void ANameTHEHOSTInventedNEVERGetsTheCodeThatOffersReplace(string whatIsInTheWay)
    {
        // THE DATA-LOSS WINDOW. Under keep-both the resolver picks a sibling and the pre-checks run
        // a few instructions later; anything claiming the name in between reached those pre-checks,
        // which reported destination_exists - naming "b (2).txt", a file the user has never seen,
        // while offering Replace. Replace re-answers the ORIGINAL request, so tapping it overwrites
        // b.txt: the file they chose keep-both to preserve, destroyed by answering a question about
        // a different one.
        //
        // Swept across all three pre-check flavours because the danger is the CODE, not the kind of
        // thing in the way, and one un-migrated site is enough to reopen the window.
        Func<string, FileConflictException> factory = whatIsInTheWay switch
        {
            "file" => FileConflictException.FileExists,
            "directory" => FileConflictException.DirectoryExists,
            _ => FileConflictException.DifferentKindExists,
        };
        var plan = new ConflictResolutionPlan(
            Overwrite: false, DestinationPath: Path.Combine(_root, "b (2).txt"), ResolvedName: "b (2).txt");

        var ex = FileTransferService.Occupied(plan, factory);

        Assert.Equal(FileTransferErrorCodes.ResolvedNameTaken, ex.ErrorCode);
        Assert.NotEqual(FileTransferErrorCodes.DestinationExists, ex.ErrorCode);

        // And it names the invented sibling, which is what is actually taken.
        Assert.Equal("b (2).txt", ex.ConflictingName);
    }

    [Fact]
    public void NoCollisionPreCheckInCopyOrMoveStillThrowsTheRawCodeItself()
    {
        // A GUARD ON THE CALL SITES, because the helper is worthless if a new pre-check bypasses it
        // and nothing this suite can set up reaches that code path. This repo uses source reading as
        // a last resort for exactly that (ConfigureAwaitBanTests, SystemCommandArgumentTests).
        //
        // "NOT A RACE" WAS TOO STRONG, and review measured the counterexample: on a case-INSENSITIVE
        // mount under a Linux host, NextAvailableName compares Ordinal and can pick "b (2).txt" while
        // "B (2).txt" is present, so the pre-check fires deterministically (RemEx-2knx). CI cannot
        // provide such a mount, so the conclusion holds even though the reason changed.
        //
        // Scoped to the two methods that carry a ConflictResolutionPlan. CreateDirectoryAsync takes
        // no conflictResolution, so no invented name can exist there. CopyDirectoryRecursive keeps
        // the direct factories because its destination is a CHILD path and naming the dragged folder
        // would misdirect the user - that settles the NAME, not the CODE, and the code is still
        // wrong there for the same reason it was wrong here (RemEx-f448). Out of scope: the fix
        // needs a child-aware code, and routing it through Occupied would report the parent's
        // invented name instead of the child's, which is worse.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.agent", "Services", "FileTransfer",
            "FileTransferService.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        foreach (var (start, end) in new[]
                 {
                     ("Task<string?> CopyAsync", "Task<string?> MoveAsync"),
                     ("Task<string?> MoveAsync", "internal static FileConflictException Occupied"),
                 })
        {
            var from = source.IndexOf(start, StringComparison.Ordinal);
            Assert.True(from >= 0, $"'{start}' was renamed; re-point this guard.");
            var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);

            // END MARKERS ARE MEMBER SIGNATURES, not punctuation - the lesson recorded in
            // SystemCommandArgumentTests, where a ";" marker would truncate the scan at the first
            // statement, capturing nothing the test was looking for. A non-empty prefix, not an
            // empty region, which is exactly why the count assertion below matters.
            Assert.True(to > from, $"'{end}' moved above '{start}'; re-point this guard.");

            var body = source[from..to];
            // THE CONSTRUCTOR IS PUBLIC, so banning only the factories leaves the window open to
            // `new FileConflictException(FileTransferErrorCodes.DestinationExists, ...)`, which
            // reads as ordinary code and reopens this green. Banned by both spellings.
            foreach (var raw in new[]
                     {
                         "FileConflictException.FileExists(",
                         "FileConflictException.DirectoryExists(",
                         "FileConflictException.DifferentKindExists(",
                         "new FileConflictException(",
                         "FileTransferErrorCodes.DestinationExists",
                         "FileTransferErrorCodes.DestinationIsDifferentKind",
                     })
            {
                Assert.False(
                    body.Contains(raw, StringComparison.Ordinal),
                    $"{start} throws {raw} directly. Under keep-both that names a sibling the HOST "
                        + "invented and unlocks Replace, which answers the original request and "
                        + "deletes the file the user chose keep-both to keep. Use Occupied(plan, ...).");
            }

            // A MEASURED COUNT, NOT MERE PRESENCE. One occurrence satisfies "contains" while three
            // of the four sites have been reverted, and it is also the backstop against the region
            // silently collapsing to a fragment. Four each: copy checks a directory-in-the-way, a
            // file-in-the-way, a folder destination and a file-where-a-folder-goes; move checks the
            // same four. Change this number only alongside a real change in the pre-checks.
            Assert.Equal(4, Regex.Matches(body, @"Occupied\(plan,").Count);
        }
    }

    [Fact]
    public void AChildCollidingInsideACopiedTreeNEVEROffersReplace()
    {
        // THE SECOND HALF OF THE REPLACE PROBLEM. RemEx-nhw2 fixed the eight top-level pre-checks;
        // this one is a file INSIDE a recursive copy, and it still reported destination_exists. The
        // client answers that with Replace, and Replace re-answers the ORIGINAL request - retry the
        // whole copy with overwrite onto the folder the user was copying - so one stray file
        // appearing inside a fresh tree could destroy the tree it collided with.
        //
        // Driven at the seam because production cannot reach it otherwise: this throw is guarded by
        // !overwrite, and both callers refuse an existing destination directory in that case, so the
        // folder is always one CopyDirectoryRecursive just made. Staging the collision means handing
        // it a directory that already has one.
        var source = Path.Combine(_root, "tree");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "child.txt"), "incoming");

        var dest = Path.Combine(_root, "tree (2)");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "child.txt"), "somebody else's work");

        var ex = Assert.Throws<FileConflictException>(
            () => FileTransferService.CopyDirectoryRecursive(source, dest, overwrite: false, CancellationToken.None));

        Assert.Equal(FileTransferErrorCodes.ResolvedNameTaken, ex.ErrorCode);

        // THE NAME STAYS THE CHILD'S. Naming the folder the user dragged would send them looking at
        // the wrong thing, and that part was already right before this change.
        Assert.Equal("child.txt", ex.ConflictingName);

        // And nothing was overwritten on the way to refusing.
        Assert.Equal("somebody else's work", File.ReadAllText(Path.Combine(dest, "child.txt")));
    }

    [Fact]
    public void ACollisionONELEVELDownStillNamesTheCHILD_NotTheSubfolder()
    {
        // THE ONLY TEST THAT EXECUTES THE RECURSIVE CALL. Its sibling above collides at depth 0, so
        // it never enters the subdirectory loop at all - anything that goes wrong only on the way
        // down is invisible to it.
        //
        // AND THE FIRST REASON WRITTEN HERE WAS WRONG, which is worth keeping because the wrong
        // reason was the plausible one. It claimed to kill a parent-naming mutant that depth 0
        // misses; measured, Path.GetFileName(Path.GetDirectoryName(target)) yields "tree (2)" at
        // depth 0, which is no more "child.txt" than "sub" is, so BOTH tests kill it. This is not
        // the test that pins the name.
        //
        // What it does kill, measured, is a mutant depth 0 cannot reach and which loses data:
        // hardcoding overwrite: true on the recursive call leaves the depth-0 test green while this
        // one stops throwing AND overwrites the very file it was refusing to touch.
        var source = Path.Combine(_root, "tree", "sub");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "child.txt"), "incoming");

        var dest = Path.Combine(_root, "tree (2)");
        Directory.CreateDirectory(Path.Combine(dest, "sub"));
        File.WriteAllText(Path.Combine(dest, "sub", "child.txt"), "somebody else's work");

        var ex = Assert.Throws<FileConflictException>(
            () => FileTransferService.CopyDirectoryRecursive(
                Path.Combine(_root, "tree"), dest, overwrite: false, CancellationToken.None));

        Assert.Equal(FileTransferErrorCodes.ResolvedNameTaken, ex.ErrorCode);
        Assert.Equal("child.txt", ex.ConflictingName);
        Assert.Equal("somebody else's work", File.ReadAllText(Path.Combine(dest, "sub", "child.txt")));
    }

    [Fact]
    public void AnOverwritingTreeCopyStillOverwrites_WhichIsWhatWasAskedFor()
    {
        // The control. A refusal that fired regardless of the flag would break every overwriting
        // folder copy, and the test above would still pass.
        var source = Path.Combine(_root, "tree");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "child.txt"), "incoming");

        var dest = Path.Combine(_root, "dest");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "child.txt"), "replace me");

        FileTransferService.CopyDirectoryRecursive(source, dest, overwrite: true, CancellationToken.None);

        Assert.Equal("incoming", File.ReadAllText(Path.Combine(dest, "child.txt")));
    }

    [Fact]
    public void TheTreeCopyDoesNotThrowTheReplaceUnlockingCodeEither()
    {
        // The call-site guard extended to the recursion, for the same reason it exists for the two
        // top-level methods: the window is unreachable from a fixture, so only the source can say
        // whether a future edit reintroduced the dangerous code.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.agent", "Services", "FileTransfer",
            "FileTransferService.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        var from = source.IndexOf("internal static void CopyDirectoryRecursive", StringComparison.Ordinal);
        Assert.True(from >= 0, "CopyDirectoryRecursive was renamed; re-point this guard.");
        var to = source.IndexOf("private static string? InferMimeType", from, StringComparison.Ordinal);
        Assert.True(to > from, "the end marker moved above the start; re-point this guard.");

        var body = source[from..to];
        foreach (var raw in new[]
                 {
                     "FileConflictException.FileExists(",
                     "FileConflictException.DirectoryExists(",
                     "FileConflictException.DifferentKindExists(",
                     "new FileConflictException(",
                     "FileTransferErrorCodes.DestinationExists",

                     // THE WRONG FIX, BANNED BY NAME. Routing this through Occupied(plan, ...) is
                     // what a maintainer will reach for, and it reports the PARENT's invented name
                     // instead of the child's - review flagged it as worse than the bug. It cannot
                     // compile today because no plan is in scope, but adding one as a parameter
                     // would re-open it silently, and "cannot compile" is not what a guard is for.
                     "Occupied(",
                 })
        {
            Assert.False(
                body.Contains(raw, StringComparison.Ordinal),
                $"CopyDirectoryRecursive throws {raw}. That code offers Replace, which retries the "
                    + "whole copy with overwrite onto the tree the user was copying - so answering a "
                    + "question about one child file can destroy the folder it collided with.");
        }

        // Present, not merely not-absent - the same backstop against the region collapsing.
        Assert.Single(Regex.Matches(body, @"FileConflictException\.ResolvedNameTaken\("));
    }

    /// <summary>The repo root, resolved from this file rather than the test working directory.</summary>
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}

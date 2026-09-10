using System.Globalization;
using Remex.Core.Models;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins the "Keep both" name so the phone and the host cannot disagree about it (RemEx-12cj).
/// </summary>
/// <remarks>
/// The failure this prevents is not cosmetic. If the client composes a suffix and the host composes
/// a different one, the user is shown one filename and gets another — or the guess still collides
/// and the upload fails again with the same opaque error the conflict sheet exists to replace.
/// </remarks>
public class FileConflictNamingTests
{
    [Fact]
    public void AFreeNameIsReturnedUnchanged()
    {
        // No suffix when there is no conflict: the sheet only appears on a collision, but the caller
        // should be able to run every name through this without special-casing.
        Assert.Equal("report.pdf",
            FileConflictNaming.NextAvailableName("report.pdf", ["other.pdf"], caseSensitive: false));
    }

    [Fact]
    public void ATakenNameGetsTheSuffixAPersonWouldExpect()
    {
        // Numbering starts at 2, matching Windows Explorer and most file managers - the name is shown
        // to someone who will compare it against what their own machine would have done.
        Assert.Equal("report (2).pdf",
            FileConflictNaming.NextAvailableName("report.pdf", ["report.pdf"], caseSensitive: false));
    }

    [Fact]
    public void ItKeepsCountingPastNamesAlreadySuffixed()
    {
        // The second collision. Returning "report (2).pdf" again would collide immediately, which is
        // the bug that makes a naive implementation useless on the third upload.
        Assert.Equal("report (4).pdf",
            FileConflictNaming.NextAvailableName(
                "report.pdf", ["report.pdf", "report (2).pdf", "report (3).pdf"], caseSensitive: false));
    }

    [Fact]
    public void OnACaseInsensitiveVolumeADifferentlyCasedNameIsAConflict()
    {
        // NTFS: Report.pdf and report.pdf are the same file, so proposing the existing name back
        // would overwrite it - the precise opposite of "keep both".
        Assert.Equal("report (2).pdf",
            FileConflictNaming.NextAvailableName("report.pdf", ["REPORT.PDF"], caseSensitive: false));
    }

    [Fact]
    public void OnACaseSensitiveVolumeADifferentlyCasedNameIsFree()
    {
        // ext4: they are two files, so the name is available and suffixing it would be wrong -
        // the user would get "report (2).pdf" when "report.pdf" was theirs to take.
        //
        // This pair is why the parameter has no default. A single comparison rule is wrong on one
        // volume or the other, and CLAUDE.md's hard rule 4 exists because this repo has already
        // been bitten by assuming Windows case semantics hold on the Linux host.
        Assert.Equal("report.pdf",
            FileConflictNaming.NextAvailableName("report.pdf", ["REPORT.PDF"], caseSensitive: true));
    }

    [Fact]
    public void CaseInsensitivityAppliesToTheSuffixedCandidatesToo()
    {
        // The initial check and the loop are separate comparisons; an implementation could easily
        // get the first right and compare candidates with the wrong comparer, which would hand back
        // a name that still collides.
        Assert.Equal("report (3).pdf",
            FileConflictNaming.NextAvailableName(
                "report.pdf", ["REPORT.PDF", "Report (2).PDF"], caseSensitive: false));
    }

    [Fact]
    public void CaseFoldingIsOrdinalAndNotTheUsersLocale()
    {
        // THE TEST THE FIRST DRAFT WAS MISSING, and the reason the class doc calls case sensitivity
        // the trap. Every other test here passes just as happily with a culture-aware comparer,
        // because under en-US the two agree. Under tr-TR they do not: Turkish folds "I" to dotless
        // "ı", so CurrentCultureIgnoreCase reports FILE.TXT and file.txt as DIFFERENT - the function
        // would call the name free on an NTFS volume that already holds it, and "keep both" would
        // overwrite. NTFS folds case with the volume's invariant $UpCase table, not the locale.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.Equal("file (2).txt",
                FileConflictNaming.NextAvailableName("file.txt", ["FILE.TXT"], caseSensitive: false));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void CaseSensitivityAppliesToTheSuffixedCandidatesToo()
    {
        // THE GAP ROUND-2 REVIEW PROVED BY MUTATION. Every other test here passes against an
        // implementation that honours caseSensitive in the FIRST check but hardcodes
        // OrdinalIgnoreCase inside the loop - all 24 of them, measured. The insensitive direction is
        // covered above; this is the sensitive one, which nothing else reaches, because every other
        // caseSensitive:true case either returns before the loop or uses exact-case fixtures.
        //
        // On ext4 "REPORT (2).PDF" is a different file, so "report (2).pdf" is free. The mutant
        // skips it and hands back "report (3).pdf" - the user loses a name that was theirs to take,
        // which is the same defect as OnACaseSensitiveVolumeADifferentlyCasedNameIsFree, one step
        // further into the loop.
        Assert.Equal("report (2).pdf",
            FileConflictNaming.NextAvailableName(
                "report.pdf", ["report.pdf", "REPORT (2).PDF"], caseSensitive: true));
    }

    [Fact]
    public void ADoubleExtensionSplitsWhereTheHostSplitsIt()
    {
        // Characterization, not an endorsement: Path.GetExtension sees only ".gz", so this yields
        // "report.tar (2).gz". Pinned because the phone and the host must agree, and consistent beats
        // clever here - if it is ever changed it must change on both sides at once.
        Assert.Equal("report.tar (2).gz",
            FileConflictNaming.NextAvailableName("report.tar.gz", ["report.tar.gz"], caseSensitive: false));
    }

    [Fact]
    public void ANameWithNoExtensionStillGetsASuffix()
    {
        Assert.Equal("README (2)",
            FileConflictNaming.NextAvailableName("README", ["README"], caseSensitive: false));
    }

    [Fact]
    public void ADotfileIsSuffixedWholeRatherThanTreatedAsAllExtension()
    {
        // ".gitignore" is all extension and no stem, so the naive split produces " (2).gitignore" -
        // a LEADING SPACE, and a name shown to the user in the conflict sheet before it is created
        // on disk. Android's own file picker hands out dotfiles, so this is reachable.
        Assert.Equal(".gitignore (2)",
            FileConflictNaming.NextAvailableName(".gitignore", [".gitignore"], caseSensitive: true));

        // "..gitignore" leaves a stem of "." rather than "", which is the same family of nonsense
        // one character along - it would otherwise yield ". (2).gitignore". Hence the guard trims
        // dots instead of checking for an empty stem. Android's picker can hand out "..foo".
        Assert.Equal("..gitignore (2)",
            FileConflictNaming.NextAvailableName("..gitignore", ["..gitignore"], caseSensitive: true));
    }

    [Fact]
    public void ANullDestinationSetIsARejectedArgumentRatherThanAnObscureOne()
    {
        // fileName is null-tolerant by design (it returns null), so without this the two arguments
        // would fail in visibly different ways - and the HashSet constructor's own exception names
        // an internal parameter, "collection", which tells a caller nothing.
        var ex = Assert.Throws<ArgumentNullException>(
            () => FileConflictNaming.NextAvailableName("report.pdf", null!, caseSensitive: true));

        Assert.Equal("existingNames", ex.ParamName);
    }

    [Fact]
    public void ATrailingDotIsNotSilentlyDropped()
    {
        // "report." and "report" are two different files on a case-sensitive host. Splitting on the
        // dot and reassembling would lose it, so the result would be a suffixed name for a file the
        // user never named.
        Assert.Equal("report. (2)",
            FileConflictNaming.NextAvailableName("report.", ["report."], caseSensitive: true));
    }

    [Fact]
    public void AnEmptyDestinationNeverSuffixes()
    {
        // The common case, asserted so a future guard cannot start suffixing unconditionally.
        Assert.Equal("report.pdf",
            FileConflictNaming.NextAvailableName("report.pdf", [], caseSensitive: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("sub/report.pdf")]
    [InlineData("sub\\report.pdf")]
    [InlineData("..\\..\\evil.dll")]
    public void ANameThatIsNotAPlainFileNameIsRefusedOutright(string? hostile)
    {
        // Delegated to FilePathValidation.IsValidFileName, which this repo already uses to keep a
        // client from smuggling a traversal through a name. Refusing matters more than it looks:
        // without it the function was ASYMMETRIC - "sub/report.pdf" passed through untouched when
        // free but had its directory amputated to "report (2).pdf" when taken, so the same upload
        // landed in a different directory depending on whether a collision happened. Worse, the
        // amputation was done by Path.*, so a backslash name resolved differently on a Windows host
        // than a Linux one.
        Assert.Null(FileConflictNaming.NextAvailableName(hostile, ["report.pdf"], caseSensitive: true));
    }

    [Fact]
    public void RunningOutOfSuffixesReturnsNullRatherThanTheTakenName()
    {
        // THE ONE THAT PREVENTS DATA LOSS. An earlier draft returned the original fileName here -
        // a name it had just proven was taken, byte-identical to the success return, so no caller
        // could tell the two apart. In a "keep both" path the caller would then write over the file
        // the user explicitly asked to preserve. Null is the only answer that lets RemEx-6vd8 fail
        // closed.
        var full = new List<string> { "report.pdf" };
        for (int n = 2; n <= FileConflictNaming.MaxSuffix; n++) full.Add($"report ({n}).pdf");

        Assert.Null(FileConflictNaming.NextAvailableName("report.pdf", full, caseSensitive: true));
    }

    [Fact]
    public void TheLastAvailableSuffixIsStillOffered()
    {
        // The counterpart: the bound is inclusive, so it must not give up one short and report an
        // exhaustion that has not happened.
        var almostFull = new List<string> { "report.pdf" };
        for (int n = 2; n < FileConflictNaming.MaxSuffix; n++) almostFull.Add($"report ({n}).pdf");

        Assert.Equal($"report ({FileConflictNaming.MaxSuffix}).pdf",
            FileConflictNaming.NextAvailableName("report.pdf", almostFull, caseSensitive: true));
    }

    [Fact]
    public void EveryReturnedNameIsActuallyAbsentFromTheDestination()
    {
        // The contract in one assertion, over a destination seeded with awkward casing and gaps.
        // Whatever comes back must be safe to create; that is the whole promise of "keep both".
        string[] existing = ["report.pdf", "REPORT (2).PDF", "report (3).pdf", "report (5).pdf"];

        var result = FileConflictNaming.NextAvailableName("report.pdf", existing, caseSensitive: false);

        Assert.NotNull(result);
        Assert.DoesNotContain(existing, e => string.Equals(e, result, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("report (4).pdf", result);
    }
}

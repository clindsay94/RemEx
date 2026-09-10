using Remex.Agent.Services.FileTransfer;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Tests for what a client's answer to a filename collision turns into (RemEx-6vd8).
/// </summary>
/// <remarks>
/// The failure that matters is not "the wrong name" but "a file the user asked to keep is gone".
/// Every case below is chosen against that: the rule is that nothing may be destroyed unless the
/// caller unambiguously asked for it.
/// </remarks>
public class ConflictResolverTests
{
    private const string Root = @"C:\shared";
    private static readonly string Dir = Path.Combine(Root, "docs");
    private static readonly string Destination = Path.Combine(Dir, "report.pdf");

    private static ConflictResolutionPlan Resolve(
        string? resolution,
        IReadOnlyList<string>? existing = null,
        bool overwriteRequested = false,
        bool caseSensitive = false) =>
        ConflictResolver.Resolve(
            resolution,
            Destination,
            Root,
            overwriteRequested,
            _ => existing ?? [],
            caseSensitive);

    [Fact]
    public void NoAnswerLeavesTheOperationExactlyAsItWas()
    {
        // THE COMPATIBILITY CASE, AND THE ONE A MISTAKE HERE WOULD BE WORST IN. A client that
        // predates this field, or one that simply has not asked the user yet, must get precisely
        // what it got before. Inventing a resolution would silently overwrite or rename files for
        // every client that never opted in.
        var plan = Resolve(null, ["report.pdf"]);

        Assert.False(plan.Overwrite);
        Assert.Equal(Destination, plan.DestinationPath);
        Assert.Null(plan.ResolvedName);
    }

    [Fact]
    public void NoAnswerPreservesAnExplicitOverwriteFlag()
    {
        // The legacy flag is still the only thing some callers send, so it must survive untouched.
        Assert.True(Resolve(null, ["report.pdf"], overwriteRequested: true).Overwrite);
        Assert.True(Resolve("   ", ["report.pdf"], overwriteRequested: true).Overwrite);
    }

    [Fact]
    public void ReplaceOverwritesTheNameThatWasAskedFor()
    {
        var plan = Resolve(FileConflictResolutions.Replace, ["report.pdf"]);

        Assert.True(plan.Overwrite);
        Assert.Equal(Destination, plan.DestinationPath);
        Assert.Null(plan.ResolvedName);
    }

    [Fact]
    public void KeepBothPicksTheNextFreeNameAndReportsIt()
    {
        var plan = Resolve(FileConflictResolutions.KeepBoth, ["report.pdf"]);

        Assert.Equal("report (2).pdf", plan.ResolvedName);
        Assert.Equal(Path.Combine(Dir, "report (2).pdf"), plan.DestinationPath);
    }

    [Fact]
    public void KeepBothNeverOverwrites_EvenWhenTheLegacyFlagSaysOtherwise()
    {
        // THE TWO FIELDS CAN DISAGREE AND ONLY ONE READING IS SAFE. The user asked to keep the
        // existing file; nothing may be destroyed to satisfy that, whatever else the request says.
        var plan = Resolve(FileConflictResolutions.KeepBoth, ["report.pdf"], overwriteRequested: true);

        Assert.False(plan.Overwrite);
        Assert.Equal("report (2).pdf", plan.ResolvedName);
    }

    [Fact]
    public void KeepBothReportsNoRenameWhenTheNameWasFreeAllAlong()
    {
        // A race, or a user answering a stale sheet. "Resolved to report.pdf" for a request that
        // asked for report.pdf is noise the UI would have to filter out again.
        var plan = Resolve(FileConflictResolutions.KeepBoth, ["other.pdf"]);

        Assert.Null(plan.ResolvedName);
        Assert.Equal(Destination, plan.DestinationPath);
        Assert.False(plan.Overwrite);
    }

    [Fact]
    public void KeepBothSkipsEveryNameThatIsTaken()
    {
        var plan = Resolve(
            FileConflictResolutions.KeepBoth,
            ["report.pdf", "report (2).pdf", "report (3).pdf"]);

        Assert.Equal("report (4).pdf", plan.ResolvedName);
    }

    [Fact]
    public void AFolderOccupyingTheNameCountsAsTaken()
    {
        // Files and folders share one namespace. Offering "report (2).pdf" as free while a FOLDER of
        // that name sits there reproduces the collision one step later, with the user believing it
        // was handled.
        var plan = Resolve(FileConflictResolutions.KeepBoth, ["report.pdf", "report (2).pdf"]);

        Assert.Equal("report (3).pdf", plan.ResolvedName);
    }

    [Fact]
    public void CaseSensitivityChangesTheAnswer_WhichIsWhyItHasNoDefault()
    {
        // THE MATCHED PAIR. On Windows Report.pdf and report.pdf are the same file, so the requested
        // name is taken and must be skipped. On Linux they are two files, so the requested name is
        // free and renaming it would skip a name that was available.
        var existing = new[] { "Report.pdf" };

        Assert.Equal("report (2).pdf", Resolve(FileConflictResolutions.KeepBoth, existing, caseSensitive: false).ResolvedName);
        Assert.Null(Resolve(FileConflictResolutions.KeepBoth, existing, caseSensitive: true).ResolvedName);
    }

    [Fact]
    public void AnUnrecognisedResolutionFallsBackToTheRefusal()
    {
        // A future client sending a resolution this host does not implement must be told no. The two
        // things it could plausibly mean - replace and rename - are the two outcomes a user would
        // most want to have been asked about first.
        var plan = Resolve("obliterate", ["report.pdf"]);

        Assert.False(plan.Overwrite);
        Assert.Equal(Destination, plan.DestinationPath);
        Assert.Null(plan.ResolvedName);
    }

    [Fact]
    public void AnUnrecognisedResolutionIsMatchedOrdinally()
    {
        // The wire value is a protocol token, not prose. "KEEP_BOTH" is not the token, and quietly
        // accepting it would make the host's behaviour depend on a client's casing.
        Assert.Null(Resolve("KEEP_BOTH", ["report.pdf"]).ResolvedName);
        Assert.False(Resolve("Replace", ["report.pdf"]).Overwrite);
    }

    [Fact]
    public void ADirectoryThatCannotBeListedLeavesTheRequestedNameAlone()
    {
        // The lister answers empty for an unreadable directory, so every candidate looks free and
        // the requested name is chosen. That is the safe direction: the operation proceeds and fails
        // on the real filesystem with the ordinary collision error, rather than this deciding an
        // outcome it could not see.
        var plan = ConflictResolver.Resolve(
            FileConflictResolutions.KeepBoth, Destination, Root, false, _ => [], caseSensitive: false);

        Assert.Null(plan.ResolvedName);
        Assert.False(plan.Overwrite);
    }

    [Fact]
    public void TheDirectoryTheListerIsAskedAboutIsTheDestinationsOwn()
    {
        // A lister pointed anywhere else would judge the wrong set of names, and would do it
        // silently - the rename would look correct and collide on disk.
        string? asked = null;

        ConflictResolver.Resolve(
            FileConflictResolutions.KeepBoth, Destination, Root, false,
            d => { asked = d; return []; }, caseSensitive: false);

        Assert.Equal(Dir, asked);
    }

    [Fact]
    public void KeepBothCannotEscapeTheDestinationsDirectory()
    {
        var plan = Resolve(FileConflictResolutions.KeepBoth, ["report.pdf"]);

        Assert.Equal(Dir, Path.GetDirectoryName(plan.DestinationPath));
    }

    [Fact]
    public void KeepBothRefusesWhenTheDestinationIsTheRootItself()
    {
        // THE ESCAPE REVIEW FOUND, AND THE REASON THIS CHECK IS NOT REDUNDANT WITH THE CALLER'S.
        // ResolveWithinRoot deliberately maps "", "/", "." and "x/.." to the root ITSELF - all
        // legitimate ways to name it - and the root's PARENT is outside the share. Renaming a
        // sibling of "shared" therefore produced "shared (2)" NEXT TO the share: a copy wrote a
        // stray file there, and a move relocated the whole tree out of it.
        //
        // The asserted invariant is deliberately NOT "the sibling stays in the destination's
        // directory" - that held while the bug was live, because that directory was already outside
        // the root. It is that the operation is REFUSED.
        var plan = ConflictResolver.Resolve(
            FileConflictResolutions.KeepBoth, Root, Root, false, _ => ["shared"], caseSensitive: false);

        Assert.Null(plan.ResolvedName);
        Assert.Equal(Root, plan.DestinationPath);
        Assert.False(plan.Overwrite);
    }

    [Fact]
    public void ASiblingRootWithASharedPrefixIsNotInsideTheRoot()
    {
        // The classic way this check is written wrong: a plain StartsWith puts C:\sharedOther
        // inside C:\shared. Comparing with the separator appended is what stops that.
        var outside = Path.Combine(@"C:\sharedOther", "report.pdf");

        var plan = ConflictResolver.Resolve(
            FileConflictResolutions.KeepBoth, outside, Root, false, _ => ["report.pdf"], caseSensitive: false);

        Assert.Null(plan.ResolvedName);
        Assert.Equal(outside, plan.DestinationPath);
    }

    [Fact]
    public void ADestinationDeepInsideTheRootIsStillAllowed()
    {
        // The guard must not be so tight that it refuses ordinary nested destinations.
        var deep = Path.Combine(Root, "a", "b", "c", "report.pdf");

        var plan = ConflictResolver.Resolve(
            FileConflictResolutions.KeepBoth, deep, Root, false, _ => ["report.pdf"], caseSensitive: false);

        Assert.Equal("report (2).pdf", plan.ResolvedName);
    }

    [Fact]
    public void NoFreeNameFallsBackToTheRefusalRatherThanForcingOne()
    {
        // NextAvailableName gives up after 10,000 suffixes. The honest answer is the collision error
        // the client already knows how to show. Overwriting would destroy a file the user asked to
        // keep; appending a name anyway would put back the collision this exists to remove.
        var everything = new List<string> { "report.pdf" };
        for (var i = 2; i <= FileConflictNaming.MaxSuffix + 1; i++) everything.Add($"report ({i}).pdf");

        var plan = Resolve(FileConflictResolutions.KeepBoth, everything);

        Assert.Null(plan.ResolvedName);
        Assert.Equal(Destination, plan.DestinationPath);
        Assert.False(plan.Overwrite);
    }

    [Fact]
    public void TheHostDecidesCaseSensitivityFromItsOwnOperatingSystem()
    {
        // The phone cannot answer this about a machine it is not running on, which is the second
        // reason the naming cannot live on the client.
        Assert.Equal(!OperatingSystem.IsWindows(), ConflictResolver.HostFileSystemIsCaseSensitive);
    }
}

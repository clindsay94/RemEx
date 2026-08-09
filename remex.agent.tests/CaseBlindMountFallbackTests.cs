using Remex.Agent.Services.FileTransfer;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Keep-both stops handing back a name the mount already has (RemEx-2knx).
/// </summary>
/// <remarks>
/// <para>
/// **A DETERMINISTIC LIVELOCK, NOT A RACE.** On Linux the name search compares Ordinal, because
/// <c>HostFileSystemIsCaseSensitive</c> is <c>!IsWindows()</c>. On a case-INSENSITIVE mount under a
/// Linux host — SMB, exFAT, ntfs-3g, ciopfs — a directory holding <c>b.txt</c> and <c>B (2).txt</c>
/// resolves keep-both to <c>b (2).txt</c>, which really is absent Ordinal-wise. The mount then says
/// it exists, the caller throws, the user retries, and the same name comes back. Every time. Only
/// "skip" ended it.
/// </para>
/// <para>
/// THESE PASS <c>caseSensitive: true</c> EXPLICITLY rather than reading the host's own value, which
/// is the only reason they can run at all: on this Windows dev box that property is false, so the
/// branch under test is unreachable through the production accessor. The call sites already took
/// case-sensitivity as an argument, so nothing had to be loosened to get here.
/// </para>
/// </remarks>
public sealed class CaseBlindMountFallbackTests
{
    private const string Root = "/share";
    private const string Directory = "/share/docs";

    /// <summary>The listing a case-blind mount would give: b.txt and a differently-cased B (2).txt.</summary>
    /// <remarks>
    /// Every assertion below reads the NAME rather than the whole path. Path.Combine uses the
    /// platform separator, so comparing full paths would fail on Windows for a reason that has
    /// nothing to do with the mount these tests describe — and the name is the subject anyway.
    /// </remarks>
    private static IReadOnlyList<string> Listing(string _) => ["b.txt", "B (2).txt"];

    private static ConflictResolutionPlan Resolve(bool caseSensitive, Func<string, bool> isTaken) =>
        ConflictResolver.ResolveAllowingForACaseBlindMount(
            conflictResolution: "keep_both",
            requestedDestination: $"{Directory}/b.txt",
            rootPath: Root,
            overwriteRequested: false,
            listDirectory: Listing,
            caseSensitive: caseSensitive,
            isTaken: isTaken);

    [Fact]
    public void TheOrdinalAnswerIsKeptWhenTheMountAgreesWithIt()
    {
        // ext4, which is the common case and must not change. "b (2).txt" is genuinely free there
        // because "B (2).txt" is a different file, and the user is entitled to that name.
        var plan = Resolve(caseSensitive: true, isTaken: _ => false);

        Assert.Equal("b (2).txt", Path.GetFileName(plan.DestinationPath));
        Assert.Equal("b (2).txt", plan.ResolvedName);
    }

    [Fact]
    public void AMountThatContradictsTheListingGetsADifferentNameRatherThanTheSameOneAgain()
    {
        // THE LIVELOCK, ENDED. The mount reports the Ordinal-free name as taken - which is the mount
        // telling us our comparison was wrong for it - so the second pass skips the colliding
        // candidate and offers the next one instead of insisting.
        var plan = Resolve(caseSensitive: true, isTaken: path => path.EndsWith("b (2).txt", StringComparison.Ordinal));

        Assert.Equal("b (3).txt", Path.GetFileName(plan.DestinationPath));
        Assert.Equal("b (3).txt", plan.ResolvedName);
    }

    [Fact]
    public void TheRetryHappensAtMostOnceAndItsAnswerIsNotSecondGuessed()
    {
        // CONVERGENCE IS THE PROPERTY, since the bug was non-convergence. A mount that claims
        // EVERYTHING is taken gets exactly one re-resolve, its answer is checked too, and then it
        // stops - two questions, never a third, and never a name already refused.
        var asked = new List<string>();
        var plan = Resolve(caseSensitive: true, isTaken: path => { asked.Add(path); return true; });

        Assert.Equal(2, asked.Count);

        // A REFUSAL, NOT ANOTHER INVENTED NAME, which review corrected. Handing back "b (3).txt"
        // here would offer a name this mount has already rejected once - the livelock with an extra
        // step. A null ResolvedName makes the caller raise the ordinary file-exists conflict on the
        // user's OWN name, which offers Replace and Skip: outcomes that end.
        Assert.Null(plan.ResolvedName);
        Assert.Equal("b.txt", Path.GetFileName(plan.DestinationPath));

        // AND NEVER WITH THE CALLER'S OVERWRITE FLAG. Keep-both's one promise is that nothing is
        // destroyed, and it has to survive the fallback failing to find a name - a client sending
        // keep_both together with the legacy overwrite:true would otherwise have the file it asked
        // to keep replaced.
        Assert.False(plan.Overwrite);
    }

    [Fact]
    public void AMountWhoseFoldWeCannotReproduceGetsARefusalRatherThanTheSameNameTwice()
    {
        // THE HOLE REVIEW FOUND IN THE CONVERGENCE CLAIM. The second pass only skips a candidate if
        // something in the LISTING folds onto it under OrdinalIgnoreCase - and a case-blind mount
        // does not necessarily fold the way .NET does. exFAT maps U+0131 (dotless i) onto 'I' while
        // ToUpperInvariant leaves it alone, so both passes return the same name and the original
        // livelock comes back one step later. U+212A KELVIN and ciopfs under a Turkish locale are
        // the same shape.
        //
        // Modelled here by a mount that rejects the name both passes produce. The fix does not try to
        // guess the mount's fold table; it notices that the retry changed nothing and stops.
        var plan = ConflictResolver.ResolveAllowingForACaseBlindMount(
            conflictResolution: "keep_both",
            requestedDestination: $"{Directory}/i.txt",
            rootPath: Root,
            overwriteRequested: true,
            listDirectory: _ => ["i.txt"],
            caseSensitive: true,
            isTaken: path => path.EndsWith("i (2).txt", StringComparison.Ordinal));

        Assert.Null(plan.ResolvedName);
        Assert.Equal("i.txt", Path.GetFileName(plan.DestinationPath));
        Assert.False(plan.Overwrite);
    }

    [Fact]
    public void AlreadyCaseInsensitiveResolutionIsNotResolvedTwice()
    {
        // Windows and anything else that already compares insensitively: the first answer is the
        // insensitive one, so a second pass could only produce the same thing. The filesystem is not
        // consulted at all.
        var consulted = false;
        var plan = Resolve(caseSensitive: false, isTaken: _ => { consulted = true; return true; });

        Assert.False(consulted);
        Assert.Equal("b (3).txt", Path.GetFileName(plan.DestinationPath));
    }

    [Fact]
    public void TheTransferServiceActuallyUsesTheFallbackAwareOverload()
    {
        // A SOURCE SCAN, AND I WOULD RATHER IT WERE NOT. Every other test here drives the helper
        // directly, which proves the decision and nothing about whether the caller makes it - the
        // same gap that let RemEx-bye7 ship two defects in wiring around a well-tested pure core.
        //
        // It cannot be covered behaviourally from here: HostFileSystemIsCaseSensitive is
        // !IsWindows(), so on this dev box the call sites pass false and the branch is unreachable
        // through them. Driving it would need the service to take case-sensitivity as a parameter,
        // which is a wider change to production shape than this fix earns.
        //
        // So this pins the one thing that would silently undo the fix: a call site reverting to the
        // plain Resolve. It is deliberately narrow - it says nothing about the arguments - and it is
        // honest about being a proxy rather than a proof.
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "remex.agent", "Services", "FileTransfer", "FileTransferService.cs"));

        // The count is expected to move when a legitimate third call site appears - update it
        // deliberately rather than loosening the assertion. Plain substring matching also means a
        // COMMENT mentioning the old call would trip this; that is the accepted cost of a
        // tripwire this cheap.
        Assert.Equal(2, Count(source, "ConflictResolver.ResolveAllowingForACaseBlindMount("));
        Assert.Equal(0, Count(source, "ConflictResolver.Resolve("));
    }

    private static int Count(string haystack, string needle)
    {
        var total = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            total++;
        }

        return total;
    }

    [Fact]
    public void ANameTheUserCHOSEIsNeverQuietlyMoved()
    {
        // THE LIMIT, AND IT IS DELIBERATE. With no keep-both there is no invented name, so
        // ResolvedName is null and the destination is the one the user asked for. Re-resolving that
        // because the filesystem says it is occupied would silently write their file somewhere they
        // did not name - worse than the conflict error they get today.
        var consulted = false;
        var plan = ConflictResolver.ResolveAllowingForACaseBlindMount(
            conflictResolution: null,
            requestedDestination: $"{Directory}/b.txt",
            rootPath: Root,
            overwriteRequested: false,
            listDirectory: Listing,
            caseSensitive: true,
            isTaken: _ => { consulted = true; return true; });

        Assert.False(consulted);
        Assert.Null(plan.ResolvedName);
        Assert.Equal("b.txt", Path.GetFileName(plan.DestinationPath));
    }
}

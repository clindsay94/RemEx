using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// A direct save supersedes a queued debounced one (RemEx-dbkzy, round-2 review finding).
/// </summary>
/// <remarks>
/// <c>SaveInternalAsync</c> wrote the file and left <c>_pendingProfile</c> armed, so a debounced
/// write queued up to two seconds earlier fired afterwards and put the older profile back. The
/// reachable case is the savefile import, which calls <c>SaveAsync</c> directly: the import lands,
/// the stale timer fires, and the restore silently reverts on disk with nothing logged.
/// <para>
/// The shape is older than this bead — a card drag racing an import does the same thing — but the
/// load-time migration write-back arms that timer on every launch of an unmigrated profile, which
/// turns a rare race into a routine one. That is what made it worth fixing here.
/// </para>
/// <para>
/// ASSERTED ON THE ProfileSaved COUNT RATHER THAN ON THE FILE'S CONTENT, deliberately — and the
/// first version of this remark gave the wrong reason for it. It claimed xUnit runs classes in
/// parallel here; it does not. <c>AssemblyInfo.cs</c> carries
/// <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c>, added for the
/// <c>LocalizationService</c> singleton (RemEx-6s34), so this assembly is sequential. This repo has
/// already shipped a defect from a comment asserting a false threading fact (RemEx-rbfq), which is
/// why the correction is worth more than the deletion.
/// <para>
/// The real reason is scope: the event is raised by THIS instance for THIS instance's writes, so it
/// counts exactly the thing under test, while several other classes construct the service against
/// the same redirected <c>dashboard_layout.json</c> and a content assertion would be measuring a
/// file none of us owns.
/// </para>
/// <para>
/// One limit worth knowing: <c>SaveInternalAsync</c> swallows write failures before raising
/// <c>ProfileSaved</c>, so "no event" means either "the fix works" or "the write threw". The poll
/// below would report the second as the first.
/// </para>
/// </para>
/// </remarks>
public class DashboardLayoutSaveOrderingTests
{
    [Fact]
    public async Task ADirectSaveCancelsAQueuedOneInsteadOfBeingOverwrittenByIt()
    {
        using var service = new DashboardLayoutService(new ThemeService());

        var saves = 0;
        service.ProfileSaved += () => Interlocked.Increment(ref saves);

        service.RequestSave(new DashboardProfile { Language = "queued" });
        saves.Should().Be(0, "a debounced save has not fired yet - if it had, this test proves nothing");

        await service.SaveAsync(new DashboardProfile { Language = "direct" });
        saves.Should().Be(1, "the direct save writes immediately");

        // The flush is what the debounce timer would have called. With the queue cancelled it has
        // nothing to do; without the cancel it writes the superseded profile over the direct one.
        await service.FlushAsync();

        saves.Should().Be(1,
            "a direct save supersedes a queued one - a second write here is the older profile "
            + "landing on top of the newer, which is how an import silently reverts");
    }

    /// <summary>Disposing drains a queued save rather than dropping it.</summary>
    /// <remarks>
    /// SINCE THE MIGRATION WRITE-BACK, THE DROPPED EDIT CAN BE THE SCHEMA STAMP ITSELF. Closing the
    /// app inside the debounce window of a migrating launch used to lose it, and the next launch
    /// would re-run the legacy arm — which makes the "once per install" claim in <c>LoadAsync</c>
    /// false. Dispose cannot await, so the drain is a synchronous best-effort write.
    /// <para>
    /// ASSERTED ON FILE CONTENT HERE, unlike the tests above, and the difference is real rather than
    /// inconsistent: the drain writes directly and raises no <c>ProfileSaved</c>, so there is no
    /// event to count. A GUID marker keeps it honest whatever else in this assembly has touched the
    /// shared redirected file.
    /// </para>
    /// </remarks>
    [Fact]
    public void DisposeDrainsAQueuedSaveInsteadOfDroppingIt()
    {
        var service = new DashboardLayoutService(new ThemeService());
        var path = service.FilePathForTests;
        var marker = "drained-" + Guid.NewGuid().ToString("N");

        service.RequestSave(new DashboardProfile { Language = marker });
        service.Dispose();

        File.ReadAllText(path).Should().Contain(marker,
            "a queued edit that Dispose throws away is an edit the user made and lost");
    }

    [Theory]
    [InlineData(null, 7L, false)]   // a direct save is never superseded
    [InlineData(7L, 7L, false)]     // nothing happened while it waited
    [InlineData(7L, 8L, true)]      // something did
    [InlineData(7L, 6L, true)]      // and a mismatch in either direction counts
    public void IsSupersededDecidesWhetherAQueuedWriteStillApplies(long? captured, long current, bool expected) =>
        DashboardLayoutService.IsSuperseded(captured, current).Should().Be(expected);

    /// <summary>
    /// The supersede check sits AFTER the gate is acquired, not before it.
    /// </summary>
    /// <remarks>
    /// THE PLACEMENT IS THE WHOLE FIX AND IT CANNOT BE TESTED BEHAVIOURALLY. The race is a debounced
    /// write preempted between dequeuing its profile and acquiring the gate; reproducing that
    /// through the public API needs a timing dependency, which is a flake rather than a test. So the
    /// decision itself is pinned by the theory above and its position is pinned here — checked
    /// before the wait it is worthless, because the gate is exactly where the ordering is decided
    /// (SemaphoreSlim does not release FIFO).
    /// </remarks>
    [Fact]
    public void TheSupersedeCheckRunsAfterTheGateIsAcquired()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "Services", "DashboardLayoutService.cs"));

        var body = Regex.Match(source, @"private async Task SaveInternalAsync\(.*?\n    \}", RegexOptions.Singleline);
        body.Success.Should().BeTrue("SaveInternalAsync moved or changed shape - this guard cannot see it");

        var wait = body.Value.IndexOf("_gate.WaitAsync()", StringComparison.Ordinal);
        var check = body.Value.IndexOf("IsSuperseded(", StringComparison.Ordinal);
        // WriteProfileAtomicallyAsync, not a raw File.WriteAllTextAsync, since RemEx-8y3qy round 2 -
        // the write itself moved behind an atomic temp-file-then-move so a concurrent reader can
        // never observe a partial file. Still the one place the bytes actually leave this method.
        var write = body.Value.IndexOf("WriteProfileAtomicallyAsync(", StringComparison.Ordinal);

        wait.Should().BeGreaterOrEqualTo(0);
        check.Should().BeGreaterOrEqualTo(0, "the staleness check has to exist to be positioned");
        write.Should().BeGreaterOrEqualTo(0, "anti-vacuity: without the write these offsets mean nothing");

        check.Should().BeGreaterThan(wait, "checked before the wait, it answers a question that is already stale");
        check.Should().BeLessThan(write, "and it has to answer it before the bytes go out");
    }

    /// <summary>
    /// A queued save reaches disk on its own, without anyone calling <c>FlushAsync</c>.
    /// </summary>
    /// <remarks>
    /// ANTI-VACUITY for the test above, AND THE FIRST VERSION OF IT WAS ITSELF VACUOUS. It called
    /// <c>FlushAsync</c> explicitly, which reads <c>_pendingProfile</c> directly — so replacing
    /// <c>ArmDebounce()</c> with <c>null</c>, i.e. deleting the debounce timer outright, left it
    /// green. That mutation kills how every card move reaches disk outside of shutdown, and the
    /// test named for covering it could not see it.
    /// <para>
    /// So this waits for the timer rather than short-circuiting it. POLLED, NOT SLEPT: a fixed
    /// <c>Task.Delay(DebounceMs + margin)</c> is a flake on a loaded machine and a fixed cost on an
    /// idle one. The poll returns as soon as the write lands — about two seconds — and only spends
    /// the full budget when the behaviour is genuinely broken, which is the run where waiting is
    /// worth it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AQueuedSaveReachesDiskOnItsOwn_WithoutAnExplicitFlush()
    {
        using var service = new DashboardLayoutService(new ThemeService());

        var saves = 0;
        service.ProfileSaved += () => Interlocked.Increment(ref saves);

        service.RequestSave(new DashboardProfile { Language = "queued" });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Volatile.Read(ref saves) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        saves.Should().Be(1,
            "RequestSave has to arm the debounce timer - it is how every card move reaches disk, and "
            + "without it a layout edit survives only if something else happens to flush");
    }

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}

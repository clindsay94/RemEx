using System;
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
/// ASSERTED ON THE ProfileSaved COUNT RATHER THAN ON THE FILE'S CONTENT, deliberately. Several test
/// classes construct this service and they share one redirected <c>dashboard_layout.json</c>; xUnit
/// runs classes in parallel, so a content assertion would be a flake waiting for a busy machine.
/// The event is raised by this instance, for this instance's writes, and counts them exactly.
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
}

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

    [Fact]
    public async Task AQueuedSaveStillFlushesWhenNothingSupersededIt()
    {
        // ANTI-VACUITY for the test above: if CancelPendingSave were called unconditionally, or
        // RequestSave stopped arming anything, the assertion above would pass for the wrong reason
        // and the debounced save - which is how every card move reaches disk - would be dead.
        using var service = new DashboardLayoutService(new ThemeService());

        var saves = 0;
        service.ProfileSaved += () => Interlocked.Increment(ref saves);

        service.RequestSave(new DashboardProfile { Language = "queued" });
        await service.FlushAsync();

        saves.Should().Be(1, "a queued save with nothing after it must still reach disk");
    }
}

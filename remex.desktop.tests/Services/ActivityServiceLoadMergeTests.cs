using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Perf audit P3-59: the activity feed now loads on the thread pool, so events can be recorded before
/// the stored history arrives. The merge must keep newest-first order, respect the cap, and not
/// resurrect a feed the user cleared while it loaded.
/// </summary>
public class ActivityServiceLoadMergeTests
{
    private static ActivityEntry Entry(string detail) =>
        new() { Kind = ActivityKind.CommandRun, Detail = detail, TimestampLocal = DateTime.Now };

    [Fact]
    public void StoredHistoryGoesAfterEventsRecordedWhileItLoaded()
    {
        var recent = new List<ActivityEntry> { Entry("new") };

        ActivityService.MergeLoaded(recent, new[] { Entry("old1"), Entry("old2") }, clearedBeforeLoad: false, max: 60);

        recent.Select(e => e.Detail).Should().Equal("new", "old1", "old2");
    }

    [Fact]
    public void TheMergeRespectsTheCap()
    {
        var recent = new List<ActivityEntry> { Entry("new") };

        ActivityService.MergeLoaded(recent, Enumerable.Range(0, 10).Select(i => Entry("old" + i)).ToList(),
            clearedBeforeLoad: false, max: 3);

        recent.Select(e => e.Detail).Should().Equal("new", "old0", "old1");
    }

    [Fact]
    public void AClearWhileLoadingDropsTheStoredHistory()
    {
        var recent = new List<ActivityEntry> { Entry("after-clear") };

        ActivityService.MergeLoaded(recent, new[] { Entry("old") }, clearedBeforeLoad: true, max: 60);

        recent.Select(e => e.Detail).Should().Equal("after-clear");
    }
}

using System;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Perf audit P3-63: the shell keeps one Logs view model for the session, and it used to keep
/// following every log line while the page was not shown. It now suspends on navigate-away and
/// replays from the sink's buffer on return.
/// </summary>
public class DiagnosticLogsSuspendResumeTests
{
    [Fact]
    public void ResumeReplaysWhatWasLoggedWhileSuspended()
    {
        var vm = new DiagnosticLogsViewModel(null!);
        try
        {
            vm.Suspend();
            vm.IsLive.Should().BeFalse();

            var marker = "p3-63-" + Guid.NewGuid().ToString("N");
            InMemoryLogSink.Append(LogLevel.Error, "Test", marker, null);

            vm.Resume();

            vm.IsLive.Should().BeTrue();
            vm.VisibleEntries.Should().Contain(e => e.Message == marker, "the page catches up from the sink on return");
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void AnArrivalAlreadyInTheResumeSnapshotIsNotShownTwice()
    {
        var vm = new DiagnosticLogsViewModel(null!);
        try
        {
            vm.Suspend();
            var marker = "p3-63-overlap-" + Guid.NewGuid().ToString("N");
            InMemoryLogSink.Append(LogLevel.Error, "Test", marker, null);
            vm.Resume();
            var overlapping = vm.VisibleEntries.Single(e => e.Message == marker);

            // The dispatcher delivers the same entry the snapshot already carried.
            vm.ProcessIncomingEntry(overlapping);
            vm.VisibleEntries.Count(e => e.Message == marker).Should().Be(1);

            var fresh = new LogEntry(DateTime.Now, LogLevel.Error, "Test", marker + "-fresh", null);
            vm.ProcessIncomingEntry(fresh);
            vm.VisibleEntries.Should().Contain(fresh);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void SuspendAndResumeAreIdempotent()
    {
        var vm = new DiagnosticLogsViewModel(null!);
        try
        {
            vm.Resume();
            vm.IsLive.Should().BeTrue("a fresh view model already follows the log");
            vm.Suspend();
            vm.Suspend();
            vm.IsLive.Should().BeFalse();
            vm.Resume();
            vm.IsLive.Should().BeTrue();
        }
        finally
        {
            vm.Dispose();
        }
    }
}

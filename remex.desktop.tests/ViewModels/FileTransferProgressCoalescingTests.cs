using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Perf audit P3-53: every transfer progress message (50-170 a second on a fast link) used to hop to
/// the UI thread twice and re-format the rate and ETA text. Reports are now coalesced: at most one UI
/// post per <see cref="FileTransferQueueItem.ProgressApplyIntervalMs"/>, always carrying the LATEST
/// value, and a value held back by the window is applied by the queue's periodic tick.
/// </summary>
public class FileTransferProgressCoalescingTests
{
    private const long Total = 100L * 1024 * 1024;

    private sealed class Clock
    {
        public long Now { get; set; } = 1_000;
    }

    private static FileTransferQueueItem NewItem(Clock clock) =>
        new(FileTransferQueueKind.Download, "big.bin", (_, _) => Task.CompletedTask, () => clock.Now);

    [Fact]
    public void ABurstInsideOneWindowPostsOnceAndAppliesTheLatestValue()
    {
        var clock = new Clock();
        var item = NewItem(clock);
        var posted = new List<Action>();

        for (var i = 1; i <= 50; i++)
            item.ReportProgress(new TransferProgress(i * 1024L * 1024, Total), posted.Add);

        posted.Should().HaveCount(1, "fifty reports inside one window are one UI hop, not fifty");

        posted[0]();
        item.Progress.Should().BeApproximately(50.0, 0.001, "the post applies the newest value, not the first");
    }

    [Fact]
    public void AReportAfterTheWindowPostsAgain()
    {
        var clock = new Clock();
        var item = NewItem(clock);
        var posted = new List<Action>();

        item.ReportProgress(new TransferProgress(1, Total), posted.Add);
        posted[0]();

        clock.Now += FileTransferQueueItem.ProgressApplyIntervalMs;
        item.ReportProgress(new TransferProgress(2 * 1024L * 1024, Total), posted.Add);

        posted.Should().HaveCount(2);
    }

    [Fact]
    public void AValueHeldBackByTheWindowIsAppliedByTheTick()
    {
        var clock = new Clock();
        var item = NewItem(clock);
        var posted = new List<Action>();

        item.ReportProgress(new TransferProgress(1, Total), posted.Add);
        posted[0]();
        clock.Now += 10;
        item.ReportProgress(new TransferProgress(Total / 2, Total), posted.Add);
        posted.Should().HaveCount(1, "still inside the window");

        item.ApplyPendingProgress();

        item.Progress.Should().BeApproximately(50.0, 0.001, "a stall right after a held-back report must not freeze the bar short");
    }
}

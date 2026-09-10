using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// <see cref="FileTransferQueueItem"/>'s <c>RateText</c>/<c>EtaText</c> under a scripted sequence of
/// progress observations, on an injected clock the test controls directly (RemEx-4lcq).
/// </summary>
/// <remarks>
/// These construct <see cref="FileTransferQueueItem"/> directly rather than through
/// <see cref="FileTransferQueue"/>. The item's constructor is public and takes an injectable
/// <c>Func&lt;long&gt;</c> clock precisely so this class does not need the pump, a real transfer, or
/// a wall-clock <c>Task.Delay</c> to prove the estimator wiring — the sequence of "now" values is
/// exactly what the test dictates.
/// </remarks>
public class FileTransferQueueItemProgressTests
{
    private const long BytesPerMebibyte = 1024L * 1024L;

    private static FileTransferQueueItem NewItem(long[] clockValues)
    {
        var index = -1;
        return new FileTransferQueueItem(
            FileTransferQueueKind.Download,
            "test.bin",
            (_, _) => Task.CompletedTask,
            () => clockValues[++index]);
    }

    /// <summary>A constant 1 MiB/s feed settles the estimator's smoothing after the first interval.</summary>
    [Fact]
    public void SteadyRate_ProducesBothRateAndEtaText()
    {
        var item = NewItem([1000, 2000, 3000, 4000, 5000]);
        const long total = 10 * BytesPerMebibyte;

        for (var i = 1; i <= 5; i++)
            item.ApplyProgress(new TransferProgress(BytesPerMebibyte * i, total));

        item.RateText.Should().NotBeNull();
        item.RateText.Should().Contain("MB/s", "1 MiB/s crosses the megabyte threshold");
        item.EtaText.Should().NotBeNull();
        item.RateEtaText.Should().Be($"{item.RateText} · {item.EtaText}");
    }

    /// <summary>
    /// Staleness is evaluated AT READ TIME against the clock it is given, not cached from whenever
    /// the last observation happened to arrive — the whole point of <c>RefreshRateAndEta</c> taking
    /// its own "now" separately from <c>ApplyProgress</c>.
    /// </summary>
    /// <remarks>
    /// THIS IS A UNIT TEST OF THE METHOD, CALLING IT DIRECTLY — it does not prove anything calls
    /// <c>RefreshRateAndEta</c> again once progress genuinely stops arriving. That behavioural proof
    /// (review finding on RemEx-4lcq) is
    /// <c>FileTransferQueueTests.ActiveTransfer_ThatStalls_GoesBlankThroughThePeriodicRefreshTimer</c>,
    /// which drives the same staleness entirely through the queue's own periodic tick and never calls
    /// this method by name.
    /// </remarks>
    [Fact]
    public void AfterFourTimeConstantsOfSilence_TheEstimateGoesBlankRatherThanFreezing()
    {
        var item = NewItem([1000, 2000, 3000, 4000, 5000]);
        const long total = 10 * BytesPerMebibyte;

        for (var i = 1; i <= 5; i++)
            item.ApplyProgress(new TransferProgress(BytesPerMebibyte * i, total));

        item.RateText.Should().NotBeNull("a steady rate must be established before the stall");

        // Default time constant is 5s, so StaleTimeConstants (4) * 5s = 20s of silence goes stale.
        // 25s past the last observation is comfortably beyond that.
        item.RefreshRateAndEta(5000 + 25_000);

        item.RateText.Should().BeNull("a stalled transfer must go blank, not keep showing its last figure");
        item.EtaText.Should().BeNull();
        item.RateEtaText.Should().BeNull();
    }

    /// <summary>Under a second remaining reads "finishing", never "0 seconds left".</summary>
    [Fact]
    public void LessThanASecondRemaining_ShowsFinishingRatherThanAZeroCount()
    {
        var item = NewItem([1000, 2000, 3000, 4000, 5000]);
        const long finalRemainingBytes = 500_000; // well under one second at ~1 MiB/s
        const long total = BytesPerMebibyte * 5 + finalRemainingBytes;

        for (var i = 1; i <= 4; i++)
            item.ApplyProgress(new TransferProgress(BytesPerMebibyte * i, total));
        item.ApplyProgress(new TransferProgress(total - finalRemainingBytes, total));

        item.EtaText.Should().Be(LocalizationService.Instance["FileTransfer_EtaFinishing"]);
    }

    /// <summary>An ETA beyond 23 hours is refused entirely rather than shown as an unbelievable number.</summary>
    [Fact]
    public void AnAbsurdlyLongEta_IsSuppressedButTheRateStillShows()
    {
        var item = NewItem([1000, 2000, 3000, 4000, 5000]);
        const long bytesPerTick = 2000; // just above the estimator's meaningful-rate floor (1024 B/s)
        const long total = bytesPerTick * 90_000; // at ~2000 B/s this is beyond the 23-hour cap

        for (var i = 1; i <= 5; i++)
            item.ApplyProgress(new TransferProgress(bytesPerTick * i, total));

        item.RateText.Should().NotBeNull("the throughput itself is still known and meaningful");
        item.EtaText.Should().BeNull("a multi-day estimate is fiction the user would plan around");
    }

    /// <summary>
    /// Reaching a terminal state clears the displayed text — a finished row must not keep advertising
    /// a rate for a transfer that no longer exists.
    /// </summary>
    [Fact]
    public void ReachingATerminalState_ClearsRateAndEtaText()
    {
        var item = NewItem([1000, 2000, 3000]);
        const long total = 10 * BytesPerMebibyte;

        item.ApplyProgress(new TransferProgress(BytesPerMebibyte, total));
        item.ApplyProgress(new TransferProgress(BytesPerMebibyte * 2, total));
        item.RateText.Should().NotBeNull("a rate must be established before completion for this test to mean anything");

        item.State = TransferState.Done;

        item.RateText.Should().BeNull();
        item.EtaText.Should().BeNull();
    }
}

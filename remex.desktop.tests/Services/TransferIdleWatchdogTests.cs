using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Covers the transfer idle watchdog: that its deadline slides while a transfer is alive, that it
/// fires on genuine silence, and that a message arriving after teardown cannot leak an entry.
/// </summary>
/// <remarks>
/// RemEx-l519 added this watchdog with no test, and its review then found a real leak inside the
/// watchdog itself — the receive path used the <c>ConcurrentDictionary</c> indexer, which INSERTS,
/// so a chunk arriving after a cancelled transfer's teardown resurrected an entry nothing removes.
/// That is the ordinary cancelled-download path, not an exotic one, because the peer keeps
/// streaming until it has processed the cancel. The fix shipped unguarded; this is the guard
/// (RemEx-sf27).
/// <para>
/// These use a short <see cref="TransferIdleWatchdog.IdleLimit"/> rather than the shipping two
/// minutes — which is why the limit is a constructor parameter and no longer a private const.
/// Margins are deliberately wide (the sliding test marks activity at a quarter of the limit and
/// runs for four times it) so a loaded CI machine cannot turn a real pass into a red build.
/// </para>
/// </remarks>
public class TransferIdleWatchdogTests
{
    /// <summary>
    /// The deadline must slide: a transfer that keeps receiving must not be failed, however long it
    /// runs past the idle limit.
    /// </summary>
    /// <remarks>
    /// This doubles as the positive control for <see cref="TransferIdleWatchdog.Mark"/>. Without it,
    /// the leak test below would pass just as well if <c>Mark</c> did nothing at all — a watchdog
    /// that never records activity cannot leak an entry either, and would also fail every transfer
    /// that lasts longer than the limit.
    /// </remarks>
    [Fact]
    public async Task DeadlineSlides_WhileTheTransferKeepsReceiving()
    {
        var idleLimit = TimeSpan.FromMilliseconds(400);
        var watchdog = new TransferIdleWatchdog(idleLimit);
        var lease = watchdog.Begin("transfer-1");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var waiting = watchdog.AwaitCompletionAsync(tcs, lease, CancellationToken.None);

        // Four times the idle limit, with a message arriving at a quarter of it.
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < idleLimit * 4)
        {
            await Task.Delay(idleLimit / 4);
            watchdog.Mark("transfer-1");
            waiting.IsFaulted.Should().BeFalse(
                "a transfer that is still receiving must never be failed, no matter how long it runs");
        }

        tcs.TrySetResult("done");
        (await waiting).Should().Be("done");
    }

    [Fact]
    public async Task Fires_WhenNothingIsReceived()
    {
        var watchdog = new TransferIdleWatchdog(TimeSpan.FromMilliseconds(150));
        var lease = watchdog.Begin("transfer-2");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var act = () => watchdog.AwaitCompletionAsync(tcs, lease, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<TimeoutException>(
            "a peer that has gone silent must not hang the transfer forever");
        thrown.Which.Message.Should().Contain("stopped responding");
    }

    /// <summary>
    /// THE REGRESSION GUARD. A message arriving for a transfer that has already ended must not
    /// re-register it.
    /// </summary>
    /// <remarks>
    /// This is the shape the original defect took: cancel a download, the peer's in-flight chunk
    /// lands a moment later, and <c>Mark</c> inserts a dictionary entry for a transfer that will
    /// never be reaped again — one leaked per cancelled download, for the life of the process.
    /// </remarks>
    [Fact]
    public void LateMessageAfterTeardown_DoesNotResurrectTheEntry()
    {
        var watchdog = new TransferIdleWatchdog(TimeSpan.FromSeconds(30));

        watchdog.Begin("cancelled-transfer");
        watchdog.ActiveTransferCount.Should().Be(1, "the transfer is live at this point");

        watchdog.End("cancelled-transfer");
        watchdog.ActiveTransferCount.Should().Be(0, "teardown must reap it");

        // The chunk the peer had already sent when the cancel was issued.
        watchdog.Mark("cancelled-transfer");

        watchdog.ActiveTransferCount.Should().Be(0,
            "a message for a dead transfer must never re-register it — an entry inserted here is " +
            "never reaped, so every cancelled download would leak one");
    }

    [Fact]
    public void Begin_ThenEnd_LeavesNothingBehind_ForSeveralConcurrentTransfers()
    {
        var watchdog = new TransferIdleWatchdog(TimeSpan.FromSeconds(30));

        foreach (var id in new[] { "a", "b", "c" })
            watchdog.Begin(id);
        watchdog.ActiveTransferCount.Should().Be(3);

        foreach (var id in new[] { "a", "b", "c" })
            watchdog.End(id);

        watchdog.ActiveTransferCount.Should().Be(0, "nothing may outlive its transfer");
    }

    /// <summary>
    /// Cancelling must unwind promptly, through the completion source rather than the idle timer.
    /// </summary>
    /// <remarks>
    /// This is the subtlest path in the class and the only one that was previously covered by
    /// reading the code rather than running it. Cancellation completes the TCS <em>and</em> cancels
    /// the linked timer, so <c>Task.WhenAny</c> may legitimately return either — and if it returns
    /// the timer, the guard at the top of the loop is the only thing stopping the next pass from
    /// building an already-cancelled delay that completes instantly and spinning a core until the
    /// idle deadline expires.
    /// <para>
    /// The idle limit here is deliberately far longer than the test's own patience, so the timer
    /// cannot be what ends the wait: if cancellation did not work, this fails on the outer
    /// <see cref="TaskExtensions"/> wait with a <see cref="TimeoutException"/> rather than hanging
    /// the suite for thirty seconds. The token is wired with exactly the
    /// <c>ct.Register(() =&gt; tcs.TrySetCanceled(ct))</c> contract the real callers in
    /// <c>FileTransferClient</c> use.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Cancelling_UnwindsPromptly_ThroughTheCompletionSource()
    {
        var watchdog = new TransferIdleWatchdog(TimeSpan.FromSeconds(30));
        var lease = watchdog.Begin("transfer-3");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));

        var waiting = watchdog.AwaitCompletionAsync(tcs, lease, cts.Token);
        cts.Cancel();

        var act = async () => await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled transfer must surface cancellation, not sit until the idle limit expires");
    }

    [Theory]
    [MemberData(nameof(NonPositiveLimits))]
    public void RejectsANonPositiveIdleLimit(TimeSpan limit)
    {
        var act = () => new TransferIdleWatchdog(limit);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "a zero or negative limit would fail every transfer on its first pass, before a single " +
            "byte could arrive");
    }

    public static TheoryData<TimeSpan> NonPositiveLimits =>
        new() { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1), TimeSpan.FromSeconds(-30) };
}

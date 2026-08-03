using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins the bookkeeping that lets a caller abandon its own pairing attempt (RemEx-defb).
/// </summary>
/// <remarks>
/// This is the part of that change with real concurrency reasoning in it, and it was extracted from
/// the JNI exports precisely so the reasoning could be asserted rather than argued. Every test here
/// is one sentence from the design doc, made checkable.
/// </remarks>
public sealed class PairingAbortRegistryTests
{
    [Fact]
    public void AnAttemptRunsUncancelledWhenNobodyAbandonsIt()
    {
        var registry = new PairingAbortRegistry();

        var token = registry.Begin(1);

        Assert.False(token.IsCancellationRequested);
        Assert.True(registry.HasActiveScope);
    }

    [Fact]
    public void CancellingTheAttemptInFlightTripsItsToken()
    {
        var registry = new PairingAbortRegistry();
        var token = registry.Begin(1);

        Assert.True(registry.Cancel(1));

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void ACancelThatArrivesBeforeItsAttemptStartsIsHonouredWhenItDoes()
    {
        // THE CASE AN UNKEYED DESIGN CANNOT EXPRESS, and it is the common one rather than the exotic
        // one: the caller registers its cancellation handler before making the call, and an attempt
        // can sit queued on the pairing lock while a previous one runs. Miss this and the attempt
        // starts anyway and runs its full budget with nobody waiting for it — which is the bug the
        // whole mechanism exists to fix, moved one step along.
        var registry = new PairingAbortRegistry();

        Assert.False(registry.Cancel(7));

        var token = registry.Begin(7);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void APendingCancelIsConsumedByTheAttemptItNamedAndNoOther()
    {
        var registry = new PairingAbortRegistry();
        registry.Cancel(7);

        registry.Begin(7);
        Assert.Equal(0, registry.PendingCancelCount);

        registry.End(registry.Begin(8));
        Assert.False(registry.Begin(9).IsCancellationRequested);
    }

    [Fact]
    public void OneAttemptsCancelLeavesAnotherAttemptAlone()
    {
        // THE CROSS-SURFACE CASE. Two independent pairing surfaces call these exports, so an unkeyed
        // cancel from one could abort the other's PIN submission — tearing down a pairing that was
        // midway through completing, with the host having already filed it.
        var registry = new PairingAbortRegistry();
        var mine = registry.Begin(100);

        Assert.False(registry.Cancel(99));

        Assert.False(mine.IsCancellationRequested);
    }

    [Fact]
    public void ACancelForAFinishedAttemptDoesNotTouchTheNextOne()
    {
        // Ids are monotonic, so a late cancel names something that can never come round again.
        var registry = new PairingAbortRegistry();
        registry.End(registry.Begin(1));

        Assert.False(registry.Cancel(1));

        Assert.False(registry.Begin(2).IsCancellationRequested);
    }

    [Fact]
    public void EndingAScopeThatIsNoLongerCurrentLeavesTheCurrentOneCancellable()
    {
        // A newer attempt may already have replaced the scope. Disposing it here would leave that
        // attempt unabandonable — the failure would be invisible until somebody tried to cancel.
        var registry = new PairingAbortRegistry();
        var stale = registry.Begin(1);
        var current = registry.Begin(2);

        registry.End(stale);

        Assert.True(registry.HasActiveScope);
        Assert.True(registry.Cancel(2));
        Assert.True(current.IsCancellationRequested);
    }

    [Fact]
    public void StartingAnAttemptCancelsAScopeItsOwnerNeverClosed()
    {
        var registry = new PairingAbortRegistry();
        var abandoned = registry.Begin(1);

        registry.Begin(2);

        Assert.True(abandoned.IsCancellationRequested);
    }

    [Fact]
    public void PendingCancelsArePrunedOldestFirstAndKeepTheOnesStillWaiting()
    {
        // Late cancels for attempts that already finished have nowhere to go, and would otherwise
        // accumulate for the life of the process. Pruning the OLDEST is what makes that safe: ids
        // are monotonic, so anything still queued carries a newer id than anything finished.
        var registry = new PairingAbortRegistry();

        for (var id = 1; id <= PairingAbortRegistry.PreCancelCapacity + 1; id++) registry.Cancel(id);

        Assert.Equal(PairingAbortRegistry.PreCancelKeep, registry.PendingCancelCount);

        // The newest survived and is still honoured.
        Assert.True(registry.Begin(PairingAbortRegistry.PreCancelCapacity + 1).IsCancellationRequested);
    }

    [Fact]
    public void CancellingTwiceIsHarmless()
    {
        var registry = new PairingAbortRegistry();
        var token = registry.Begin(1);

        Assert.True(registry.Cancel(1));
        Assert.True(registry.Cancel(1));

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void ACancelRacingTheAttemptsOwnCompletionReportsFailureRatherThanThrowing()
    {
        // The window is real: the source is captured under the lock and cancelled outside it, so the
        // attempt can finish and dispose in between. The caller has already given up by then, so a
        // teardown that throws must not become its result.
        var registry = new PairingAbortRegistry();
        var token = registry.Begin(1);
        registry.End(token);

        Assert.False(registry.Cancel(1));
    }

    [Fact]
    public async Task ConcurrentAttemptsAndCancelsDoNotCorruptTheRegistry()
    {
        // Not a proof of thread safety, but it would catch the obvious lock omission — every field is
        // touched from several threads at once and the invariants still hold at the end.
        var registry = new PairingAbortRegistry();

        var work = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var id = (worker * 1000) + i;
                var token = registry.Begin(id);
                registry.Cancel(id);
                registry.End(token);
                registry.Cancel(id + 500_000);
            }
        }));

        await Task.WhenAll(work);

        Assert.True(registry.PendingCancelCount <= PairingAbortRegistry.PreCancelCapacity);
        Assert.False(registry.Begin(9_999_999).IsCancellationRequested);
    }
}

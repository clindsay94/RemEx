using Remex.Agent.Services.Telemetry;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Client streams wait on the sampler instead of running their own clock (RemEx-uj7s).
/// </summary>
/// <remarks>
/// <para>
/// The point of the gate is not the saved timer. It is that the sampler's period is 1000 ms PLUS the
/// sample duration while a stream's was a flat 1000 ms, so a stream was systematically the faster
/// loop and routinely woke to find the sample it had already sent. Skipping is correct; the drift is
/// not — a client's updates became mostly one second apart with a two-second gap whenever the phase
/// caught up, and the phone plots history against an index axis, so those gaps render as uniform and
/// the chart's x-axis quietly stops being linear in time.
/// </para>
/// <para>
/// Every wait here is bounded. An unbounded await in a test for a synchronisation primitive turns the
/// bug it is meant to catch — a waiter that sleeps forever — into a hung suite rather than a failure.
/// </para>
/// </remarks>
public class TelemetrySnapshotGateTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private sealed record Sample(string Name);

    [Fact]
    public async Task AWaiterWithNothingToSeeYetIsWokenByTheFirstPublish()
    {
        // The plain wakeup: park with nothing to see, get woken by the first sample.
        //
        // **THIS DOES NOT TEST THE LOST-WAKEUP RACE, AND AN EARLIER DRAFT OF THIS COMMENT CLAIMED IT
        // DID.** Mutation testing said otherwise: inverting the gate's internal read order left every
        // test in this file green. WaitForNextAsync runs synchronously to its first await, so by the time
        // Publish runs on this same thread the waiter has already parked - the interleaving that loses
        // a publish never occurs. The gate answers that by construction instead (see its remarks); no
        // test here covers it, which is the honest statement and the reason it is written down.
        var gate = new TelemetrySnapshotGate<Sample>();
        var waiting = gate.WaitForNextAsync(null, CancellationToken.None);

        var published = new Sample("first");
        gate.Publish(published);

        Assert.Same(published, await waiting.WaitAsync(Bound));
    }

    [Fact]
    public async Task AStreamThatAlreadySentTheCurrentSampleWaitsForTheNextOne()
    {
        var gate = new TelemetrySnapshotGate<Sample>();
        var first = new Sample("first");
        gate.Publish(first);

        var waiting = gate.WaitForNextAsync(first, CancellationToken.None);
        Assert.False(waiting.IsCompleted, "the caller has already sent this one");

        var second = new Sample("second");
        gate.Publish(second);

        Assert.Same(second, await waiting.WaitAsync(Bound));
    }

    [Fact]
    public async Task AClientConnectingMidCycleGetsTheCurrentSampleWithoutWaiting()
    {
        // The behaviour the old Task.Delay loop got for free by polling on entry. A gate that only
        // ever woke on the NEXT publish would leave a freshly connected phone blank for up to a
        // second, which is exactly when someone is looking at it.
        var gate = new TelemetrySnapshotGate<Sample>();
        var current = new Sample("already there");
        gate.Publish(current);

        var waiting = gate.WaitForNextAsync(null, CancellationToken.None);

        // Asserted as SYNCHRONOUS completion, not merely completion within the bound. A gate that
        // always parked and woke on the next publish would still satisfy `await waiting` here - it
        // would just fail five seconds later, on a timeout that names nothing. This says the property.
        Assert.True(waiting.IsCompletedSuccessfully, "the current reading should need no wait at all");
        Assert.Same(current, await waiting.WaitAsync(Bound));
    }

    [Fact]
    public async Task EveryWaiterIsWokenByOnePublish()
    {
        // THE WHOLE POINT OF ONE CLOCK. Three clients, one sample, and all three receive it - if the
        // pulse woke only the first waiter the others would stall until the next publish and the
        // fan-out would be worse than the per-stream timers it replaced.
        var gate = new TelemetrySnapshotGate<Sample>();
        var waiters = Enumerable.Range(0, 3)
            .Select(_ => gate.WaitForNextAsync(null, CancellationToken.None))
            .ToList();

        var sample = new Sample("fan-out");
        gate.Publish(sample);

        foreach (var w in waiters)
            Assert.Same(sample, await w.WaitAsync(Bound));
    }

    [Fact]
    public async Task AWaiterArrivingAfterSeveralPublishesGetsOnlyTheNewest()
    {
        // A gate that buffered would hand a client a backlog to send after a stall - which is exactly
        // what the stream's own skip logic exists to prevent, since an old telemetry sample has no
        // value once a newer one exists. Latest-wins, deliberately.
        //
        // NAMED FOR WHAT IT ACTUALLY DOES. It was called ASlowWaiter..., which it is not: no waiter is
        // parked across the two publishes, so this is the mid-cycle case above plus an extra publish.
        // The genuine slow-waiter version cannot be asserted, and not for want of trying - if the
        // continuation runs between the two publishes the waiter legitimately returns "stale", because
        // the gate promises "something newer than alreadySeen", never "the newest at the moment you
        // resume". A test asserting otherwise would be asserting a guarantee the gate does not make.
        var gate = new TelemetrySnapshotGate<Sample>();
        gate.Publish(new Sample("stale"));
        var newest = new Sample("newest");
        gate.Publish(newest);

        Assert.Same(newest, await gate.WaitForNextAsync(null, CancellationToken.None).WaitAsync(Bound));
    }

    [Fact]
    public async Task RepublishingTheSAMEInstanceIsNotANewSample()
    {
        // "Already seen" is by reference, and one wakeup is not proof of a new sample - so the wait
        // re-checks after waking rather than returning whatever it finds. The sampler builds a fresh
        // object per tick, so this cannot happen today; the loop is what stops a caller that resends
        // an unchanged instance from being reported to every client as a fresh reading.
        var gate = new TelemetrySnapshotGate<Sample>();
        var sample = new Sample("unchanged");
        gate.Publish(sample);

        var waiting = gate.WaitForNextAsync(sample, CancellationToken.None);
        gate.Publish(sample);

        // Given real time to complete, rather than a single Task.Yield. The waiter resumes on the
        // thread pool, so a bare IsCompleted check can read false simply because the continuation has
        // not been scheduled yet - passing for a reason that has nothing to do with the assertion.
        Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(250)));

        var moved = new Sample("moved");
        gate.Publish(moved);
        Assert.Same(moved, await waiting.WaitAsync(Bound));
    }

    [Fact]
    public async Task PublishDoesNotStallBehindASlowClientsContinuation()
    {
        // **THE PULSE IS CREATED WITH RunContinuationsAsynchronously, AND THIS IS WHY.** Without it
        // TrySetResult runs every waiting stream's continuation INLINE on the sampler's thread, so one
        // client slow to serialise or write to its socket delays the next sample for every other
        // client - the fix reintroducing, through its own plumbing, the coupling it exists to remove.
        //
        // The waiter below holds its thread hostage after waking. Publish therefore returns promptly
        // or not at all, and running it inside a bounded task turns "not at all" into a failure here
        // rather than a hung suite.
        var gate = new TelemetrySnapshotGate<Sample>();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();

        var waiter = Task.Run(async () =>
        {
            var waiting = gate.WaitForNextAsync(null, CancellationToken.None);
            // WaitForNextAsync runs synchronously to its first await, so returning a task means parked.
            parked.SetResult();
            await waiting;
            release.Wait();
        });

        try
        {
            await parked.Task.WaitAsync(Bound);
            var publish = Task.Run(() => gate.Publish(new Sample("tick")));
            await publish.WaitAsync(Bound);
        }
        finally
        {
            release.Set();
        }

        await waiter.WaitAsync(Bound);
    }

    [Fact]
    public async Task CancellingAWaiterDoesNotWedgeTheGateForAnybodyElse()
    {
        // A client disconnecting is the ordinary case, not an edge one, and it must not take the
        // shared pulse with it - every other connected phone is waiting on the same object.
        var gate = new TelemetrySnapshotGate<Sample>();
        using var cts = new CancellationTokenSource();

        var doomed = gate.WaitForNextAsync(null, cts.Token);
        var survivor = gate.WaitForNextAsync(null, CancellationToken.None);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doomed.WaitAsync(Bound));

        var sample = new Sample("after the cancel");
        gate.Publish(sample);

        Assert.Same(sample, await survivor.WaitAsync(Bound));
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenDoesNotReturnASampleAnyway()
    {
        // Anti-vacuity for the cancellation path: a gate that checked the token only after handing
        // back a value would satisfy the test above while ignoring cancellation whenever a snapshot
        // happened to be available, which is most of the time.
        var gate = new TelemetrySnapshotGate<Sample>();
        gate.Publish(new Sample("available"));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.WaitForNextAsync(null, cts.Token).WaitAsync(Bound));
    }
}

using Remex.Core.Models;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins <see cref="FrameDropKeyframeGate"/> (perf audit P0-13): when a shed video frame earns a
/// keyframe request, how that request is throttled to the host's cooldown, and why nothing from one
/// stream session may leak into the next.
/// </summary>
public sealed class FrameDropKeyframeGateTests
{
    private static FrameDropKeyframeGate PrimedGate(long minIntervalMs = FrameDropKeyframeGate.DefaultMinIntervalMs)
    {
        var gate = new FrameDropKeyframeGate(minIntervalMs);
        Assert.False(gate.TryClaimRequest(0, deliveredKeyframe: true));
        Assert.True(gate.IsPrimed);
        return gate;
    }

    [Fact]
    public void TheRequestIsClaimedOnTheNextSuccessfulEnqueueNotAtTheDrop()
    {
        var gate = PrimedGate();

        gate.RecordDrop();

        // The drop only records the debt; nothing has been claimed yet.
        Assert.True(gate.RecoveryOwed);

        Assert.True(gate.TryClaimRequest(100, deliveredKeyframe: false));
        Assert.False(gate.RecoveryOwed);
        Assert.False(gate.TryClaimRequest(200, deliveredKeyframe: false));
    }

    [Fact]
    public void AThrottledDebtIsKeptAndClaimedOnceTheWindowReopens()
    {
        var gate = PrimedGate(minIntervalMs: 5000);

        gate.RecordDrop();
        Assert.True(gate.TryClaimRequest(1000, deliveredKeyframe: false));

        gate.RecordDrop();
        Assert.False(gate.TryClaimRequest(2000, deliveredKeyframe: false));
        Assert.True(gate.RecoveryOwed);
        Assert.False(gate.TryClaimRequest(5999, deliveredKeyframe: false));
        Assert.True(gate.RecoveryOwed);

        Assert.True(gate.TryClaimRequest(6000, deliveredKeyframe: false));
        Assert.False(gate.RecoveryOwed);
    }

    [Fact]
    public async Task ConcurrentClaimsProduceExactlyOneRequest()
    {
        const int threads = 8;
        for (int iteration = 0; iteration < 200; iteration++)
        {
            var gate = PrimedGate();
            gate.RecordDrop();

            int claimed = 0;
            using var barrier = new Barrier(threads);
            // Dedicated threads, not pool tasks: the barrier blocks every worker, and a pool with fewer
            // than `threads` ready workers would stall on thread injection.
            var workers = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                if (gate.TryClaimRequest(10_000, deliveredKeyframe: false))
                {
                    Interlocked.Increment(ref claimed);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, claimed);
        }
    }

    [Fact]
    public void ADropIsHeldUntilTheSessionDeliversItsFirstIdrWhichSettlesIt()
    {
        var gate = new FrameDropKeyframeGate();
        Assert.False(gate.IsPrimed);

        gate.RecordDrop();
        Assert.False(gate.TryClaimRequest(100, deliveredKeyframe: false));
        Assert.True(gate.RecoveryOwed);

        Assert.False(gate.TryClaimRequest(200, deliveredKeyframe: true));
        Assert.True(gate.IsPrimed);
        Assert.False(gate.RecoveryOwed);
        Assert.False(gate.TryClaimRequest(300, deliveredKeyframe: false));
    }

    [Fact]
    public void ADeliveredIdrSettlesAnOwedDebtWithoutARequest()
    {
        var gate = PrimedGate();
        gate.RecordDrop();

        Assert.False(gate.TryClaimRequest(100, deliveredKeyframe: true));
        Assert.False(gate.RecoveryOwed);
        Assert.False(gate.TryClaimRequest(200, deliveredKeyframe: false));
    }

    /// <summary>
    /// The reviewer's repro: session A sheds a frame inside the throttle window and the user stops
    /// before the debt is claimed. Without the reset, session B's first frame claimed A's debt and
    /// spent B's one host keyframe allowance on it.
    /// </summary>
    [Fact]
    public void ADebtFromAStoppedSessionIsNotClaimedByTheNextSession()
    {
        var gate = PrimedGate(minIntervalMs: 5000);

        // Session A: one request spent, then a drop that is throttled and left owed.
        gate.RecordDrop();
        Assert.True(gate.TryClaimRequest(0, deliveredKeyframe: false));
        gate.RecordDrop();
        Assert.False(gate.TryClaimRequest(1000, deliveredKeyframe: false));
        Assert.True(gate.RecoveryOwed);

        // User stops; then session B starts.
        gate.Reset();
        Assert.False(gate.RecoveryOwed);
        Assert.False(gate.IsPrimed);

        // Session B's first frame (well past A's window) claims nothing.
        Assert.False(gate.TryClaimRequest(7000, deliveredKeyframe: false));

        // Session B primes on its own IDR; its throttle window starts fresh, not from A's request.
        Assert.False(gate.TryClaimRequest(7100, deliveredKeyframe: true));
        gate.RecordDrop();
        Assert.True(gate.TryClaimRequest(7200, deliveredKeyframe: false));
    }

    [Fact]
    public void ResetClearsSessionAThrottleWindow()
    {
        var gate = PrimedGate(minIntervalMs: 5000);
        gate.RecordDrop();
        Assert.True(gate.TryClaimRequest(1000, deliveredKeyframe: false));

        gate.Reset();
        Assert.False(gate.TryClaimRequest(1100, deliveredKeyframe: true));
        gate.RecordDrop();

        // Inside A's window, but A's window belongs to A.
        Assert.True(gate.TryClaimRequest(1200, deliveredKeyframe: false));
    }

    [Fact]
    public void KeyframeCheckIsWantedOnlyBeforePrimingOrWhileADebtIsOwed()
    {
        var gate = new FrameDropKeyframeGate();
        Assert.True(gate.WantsKeyframeCheck);

        gate.TryClaimRequest(0, deliveredKeyframe: true);
        Assert.False(gate.WantsKeyframeCheck);

        gate.RecordDrop();
        Assert.True(gate.WantsKeyframeCheck);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 1, 0x65, 0x88, 0x84 }, true)] // 4-byte start code, IDR
    [InlineData(new byte[] { 0, 0, 1, 0x67, 0x42, 0, 0, 1, 0x68, 0xCE, 0, 0, 1, 0x65, 0x88 }, true)] // SPS, PPS, IDR
    [InlineData(new byte[] { 0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x41, 0x9A }, false)] // AUD + P slice
    [InlineData(new byte[] { 0, 0, 0, 1 }, false)] // start code with no NAL header
    [InlineData(new byte[] { }, false)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0, 0, 1, 0x65, 0xFF, 0xD9 }, false)] // bare JPEG mimicking a start code
    public void ContainsIdrScansBareAnnexB(byte[] frame, bool expected)
        => Assert.Equal(expected, FrameDropKeyframeGate.ContainsIdr(frame));

    [Fact]
    public void ContainsIdrUnwrapsTheFrameEnvelopeAndIgnoresItsKeyFrameFlag()
    {
        byte[] idr = [0, 0, 0, 1, 0x65, 0x88];
        byte[] pSlice = [0, 0, 0, 1, 0x41, 0x9A];

        Assert.True(FrameDropKeyframeGate.ContainsIdr(DesktopFrameEnvelope.Wrap(idr, 1, 1, DesktopCodecKind.H264)));

        // The host's flag rides on the frame it ASKED to key; a pipelined encoder can put a P-frame there.
        Assert.False(FrameDropKeyframeGate.ContainsIdr(
            DesktopFrameEnvelope.Wrap(pSlice, 1, 2, DesktopCodecKind.H264, DesktopFrameFlags.KeyFrame)));

        // An MJPEG payload is never an H.264 IDR, whatever its bytes look like.
        Assert.False(FrameDropKeyframeGate.ContainsIdr(DesktopFrameEnvelope.Wrap(idr, 1, 3, DesktopCodecKind.Mjpeg)));
    }
}

/// <summary>
/// Pins <see cref="LatestWinsSlots{TKey, TValue}"/> (perf audit P0-13): one pending value per key,
/// a drain queued only when none is pending, and no value stranded when an offer races a take.
/// </summary>
public sealed class LatestWinsSlotsTests
{
    [Fact]
    public void AnOfferOnAnEmptySlotAsksForADrain()
    {
        var slots = new LatestWinsSlots<string, int>();

        Assert.True(slots.Offer("telemetry", 1));
        Assert.True(slots.TryTake("telemetry", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void AnOfferWhileAValueIsPendingFoldsIntoThatDrainAndReplacesTheValue()
    {
        var slots = new LatestWinsSlots<string, int>();

        Assert.True(slots.Offer("telemetry", 1));
        Assert.False(slots.Offer("telemetry", 2));
        Assert.False(slots.Offer("telemetry", 3));

        Assert.True(slots.TryTake("telemetry", out var value));
        Assert.Equal(3, value);
        Assert.False(slots.TryTake("telemetry", out _));

        // Taken, so the next offer needs a fresh drain.
        Assert.True(slots.Offer("telemetry", 4));
    }

    [Fact]
    public void KeysAreIndependent()
    {
        var slots = new LatestWinsSlots<string, int>();

        Assert.True(slots.Offer("processes", 1));
        Assert.True(slots.Offer("launchers", 2));

        Assert.True(slots.TryTake("processes", out var processes));
        Assert.True(slots.TryTake("launchers", out var launchers));
        Assert.Equal(1, processes);
        Assert.Equal(2, launchers);
    }

    /// <summary>
    /// The race the class exists for: a take removes the pending value between an offer's read and its
    /// compare-and-swap. The offer must then re-add and ask for a new drain; answering "folded" would
    /// strand the newest value in a slot no queued drain will ever read. Driven as a stress run with a
    /// real drain loop, asserting the newest value always arrives and nothing is left behind.
    /// </summary>
    [Fact]
    public async Task AnOfferRacingATakeNeverStrandsTheNewestValue()
    {
        const int values = 20_000;
        for (int round = 0; round < 20; round++)
        {
            var slots = new LatestWinsSlots<int, int>();
            using var drainsQueued = new SemaphoreSlim(0);
            int lastTaken = 0;
            bool orderViolated = false;
            var producerDone = new TaskCompletionSource();

            var consumer = Task.Run(async () =>
            {
                while (true)
                {
                    if (!await drainsQueued.WaitAsync(TimeSpan.FromMilliseconds(50)))
                    {
                        if (producerDone.Task.IsCompleted && drainsQueued.CurrentCount == 0) return;
                        continue;
                    }

                    if (slots.TryTake(0, out var taken))
                    {
                        if (taken <= lastTaken) orderViolated = true;
                        lastTaken = taken;
                    }
                }
            });

            var producer = Task.Run(() =>
            {
                for (int i = 1; i <= values; i++)
                {
                    if (slots.Offer(0, i)) drainsQueued.Release();
                }
                producerDone.SetResult();
            });

            await Task.WhenAll(producer, consumer).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.False(orderViolated, "an older value was delivered after a newer one");
            Assert.Equal(values, lastTaken);
            Assert.False(slots.TryTake(0, out _), "a value was left in the slot with no drain queued for it");
        }
    }
}

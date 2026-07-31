using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Covers the ordering guarantee the Remote Desktop send path depends on (RemEx-krvz).
/// </summary>
/// <remarks>
/// <para>
/// Twelve call sites in <c>AndroidNativeExports</c> each did <c>_ = Task.Run(async () =&gt; …)</c>.
/// That is fire-and-forget onto the thread pool, so two operations handed over microseconds apart
/// started on different workers and reached the socket in whichever order they got there.
/// </para>
/// <para>
/// The consequence is not subtle. A key press is sent as a SEPARATE keyDown and keyUp, so an
/// inversion tells the host to release a key it was never told to press — leaving the key physically
/// held down on the user's actual desktop, recoverable only by walking over to the PC. And because
/// the send took no lock, an overlap threw into a catch that only wrote to logcat, losing the message
/// outright. Both failures are silent.
/// </para>
/// <para>
/// These tests exist because the guarantee is one <c>await</c>. Remove it and everything compiles,
/// every call site still reads correctly, and the bug is back.
/// </para>
/// </remarks>
public class OrderedAsyncWorkQueueTests
{
    /// <summary>Waits for a condition without pinning the test to a fixed sleep.</summary>
    private static async Task WaitFor(Func<bool> condition, string because)
    {
        for (int i = 0; i < 2000 && !condition(); i++)
        {
            await Task.Delay(5);
        }

        Assert.True(condition(), because);
    }

    [Fact]
    public async Task RunsWorkInTheOrderItWasQueued()
    {
        // THE POINT. Each item sleeps LONGER than the one after it, so anything that starts them
        // concurrently finishes them in reverse. Fire-and-forget scores 20, 19, 18…; a queue scores
        // 1, 2, 3….
        var queue = new OrderedAsyncWorkQueue();
        var completed = new ConcurrentQueue<int>();

        for (int i = 1; i <= 20; i++)
        {
            int index = i;
            queue.Enqueue($"item-{index}", async () =>
            {
                await Task.Delay(21 - index);
                completed.Enqueue(index);
            });
        }

        await WaitFor(() => completed.Count == 20, "all queued work should run");

        Assert.Equal(Enumerable.Range(1, 20).ToList(), completed.ToList());
    }

    [Fact]
    public async Task NeverRunsTwoItemsAtOnce()
    {
        // The overlap half. A ClientWebSocket allows only one outstanding SendAsync, so concurrency
        // here does not merely reorder — it throws, and the message is dropped.
        var queue = new OrderedAsyncWorkQueue();
        int running = 0;
        int maxObserved = 0;
        int finished = 0;

        for (int i = 0; i < 30; i++)
        {
            queue.Enqueue("overlap", async () =>
            {
                int now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxObserved, now);
                await Task.Delay(2);
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref finished);
            });
        }

        await WaitFor(() => Volatile.Read(ref finished) == 30, "all queued work should run");

        Assert.True(Volatile.Read(ref maxObserved) == 1,
            $"the consumer must await each item before starting the next; saw {Volatile.Read(ref maxObserved)} at once");
    }

    [Fact]
    public async Task AFailingItemDoesNotStopTheQueue()
    {
        // A consumer that dies on one exception takes every later operation with it — the user's
        // keyboard and mouse simply stop working, which is far worse than the item that threw.
        var queue = new OrderedAsyncWorkQueue();
        var completed = new ConcurrentQueue<string>();

        queue.Enqueue("first", () => { completed.Enqueue("first"); return Task.CompletedTask; });
        queue.Enqueue("boom", () => throw new InvalidOperationException("host went away"));
        queue.Enqueue("third", () => { completed.Enqueue("third"); return Task.CompletedTask; });

        await WaitFor(() => completed.Count == 2, "work after a failure should still run");

        Assert.Equal(new[] { "first", "third" }, completed.ToArray());
    }

    [Fact]
    public async Task AFailingItemIsReportedWithItsLabel()
    {
        // Swallowing is deliberate, but silent swallowing is how the original bug hid. The label is
        // what makes a logcat line identify WHICH operation was lost.
        var failures = new ConcurrentQueue<(string Label, Exception Error)>();
        var queue = new OrderedAsyncWorkQueue((label, ex) => failures.Enqueue((label, ex)));

        queue.Enqueue("DesktopInput", () => throw new InvalidOperationException("socket closed"));

        await WaitFor(() => !failures.IsEmpty, "the failure should be reported");

        Assert.True(failures.TryDequeue(out var failure));
        Assert.Equal("DesktopInput", failure.Label);
        Assert.Equal("socket closed", failure.Error.Message);
    }

    [Fact]
    public async Task WorkQueuedFromManyThreadsStillRunsOneAtATime()
    {
        // The JNI exports carry no promise of arriving on one thread, which is why the consumer is
        // started with Interlocked rather than a null check. Two consumers would defeat the queue
        // entirely while still passing the single-threaded tests above.
        var queue = new OrderedAsyncWorkQueue();
        int running = 0;
        int maxObserved = 0;
        int finished = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 10; i++)
            {
                queue.Enqueue("concurrent", async () =>
                {
                    InterlockedMax(ref maxObserved, Interlocked.Increment(ref running));
                    await Task.Delay(1);
                    Interlocked.Decrement(ref running);
                    Interlocked.Increment(ref finished);
                });
            }
        })));

        await WaitFor(() => Volatile.Read(ref finished) == 80, "all queued work should run");

        Assert.True(Volatile.Read(ref maxObserved) == 1,
            $"one consumer, however many producers; saw {Volatile.Read(ref maxObserved)} at once");
    }

    [Fact]
    public async Task AnItemThatNeverFinishesDoesNotStrandTheWorkBehindIt()
    {
        // THE FAILURE THIS CLASS COULD OTHERWISE CAUSE, and it is worse than the one it prevents.
        // Serialising means a single item that never completes blocks everything behind it forever —
        // there is no second consumer and the real queue lives for the process. A keyDown already on
        // the wire with its keyUp stuck behind such an item leaves the key held down on the user's
        // PC with no way to release it, and the DesktopStop that would tear the stream down is
        // queued behind it too.
        var failures = new ConcurrentQueue<string>();
        var queue = new OrderedAsyncWorkQueue(
            (label, _) => failures.Enqueue(label),
            itemTimeout: TimeSpan.FromMilliseconds(120));
        var completed = new ConcurrentQueue<string>();
        var neverCompletes = new TaskCompletionSource();

        queue.Enqueue("hangs", () => neverCompletes.Task);
        queue.Enqueue("after", () => { completed.Enqueue("after"); return Task.CompletedTask; });

        await WaitFor(() => completed.Count == 1, "work behind a hung item must still run");

        Assert.Equal(new[] { "after" }, completed.ToArray());
        Assert.True(failures.TryDequeue(out var label));
        Assert.Equal("hangs", label);

        neverCompletes.SetResult(); // let the orphaned task finish so the test leaves nothing running
    }

    [Fact]
    public async Task EachQueueIsIndependent()
    {
        // Instance state, not statics: two queues must not share a consumer, or a test (or a second
        // stream) would serialise against unrelated work.
        var first = new OrderedAsyncWorkQueue();
        var second = new OrderedAsyncWorkQueue();
        var order = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource();

        first.Enqueue("blocked", async () => { await gate.Task; order.Enqueue("first"); });
        second.Enqueue("free", () => { order.Enqueue("second"); return Task.CompletedTask; });

        await WaitFor(() => order.Count == 1, "the unblocked queue should not wait on the blocked one");
        Assert.Equal(new[] { "second" }, order.ToArray());

        gate.SetResult();
        await WaitFor(() => order.Count == 2, "the blocked queue should finish once released");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen = Volatile.Read(ref target);
        while (value > seen)
        {
            int previous = Interlocked.CompareExchange(ref target, value, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }
}

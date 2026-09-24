using System.Threading.Channels;

namespace Remex.Core.Native;

/// <summary>
/// Runs queued asynchronous work one item at a time, in the order it was queued.
/// </summary>
/// <remarks>
/// <para>
/// Written for the Remote Desktop send path (RemEx-krvz), where a dozen call sites each did
/// <c>_ = Task.Run(async () =&gt; …)</c>. That is fire-and-forget onto the thread pool: two operations
/// handed over microseconds apart start on different workers and reach the socket in whichever order
/// they happen to get there. It is invisible until it bites, and when it bites it does so silently —
/// a key press is sent as a separate keyDown and keyUp, so an inversion leaves a key physically held
/// down on the user's PC.
/// </para>
/// <para>
/// A LOCK AROUND THE SEND WOULD NOT HAVE FIXED THAT. Serialising execution prevents overlap; it does
/// nothing about order, because whichever thread wakes first takes the lock first. Order can only be
/// preserved by not re-parallelising in the first place, which is what this is: one writer-visible
/// entry point, one consumer, and an <c>await</c> between items.
/// </para>
/// <para>
/// Failures are reported and swallowed rather than allowed to kill the consumer. A queue that stops
/// draining because one item threw would take every later operation with it, which is a far worse
/// failure than the one that threw.
/// </para>
/// <para>
/// COALESCING IS ADJACENCY-ONLY, AND THAT IS WHAT KEEPS IT FROM BEING A LATEST-WINS SLOT (perf audit
/// P0-14). An item queued with a coalesce key is skipped only when the item IMMEDIATELY behind it in
/// the queue carries the same key — the newer one supersedes it and runs in its place. Nothing ever
/// moves: a skipped item's successor was already next in line, so no item runs earlier or later
/// relative to any item it was not merged with. An unkeyed item (a button, a key, a stream command)
/// between two keyed ones breaks the run, so both keyed items run, one on each side of it, exactly as
/// queued. Callers key only work that is genuinely superseded by its successor — an ABSOLUTE pointer
/// move, never a relative one, whose deltas add up rather than replace each other.
/// </para>
/// </remarks>
internal sealed class OrderedAsyncWorkQueue
{
    private readonly record struct QueuedWork(string Label, Func<Task> Work, string? CoalesceKey);

    private readonly Channel<QueuedWork> _queue =
        Channel.CreateUnbounded<QueuedWork>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Action<string, Exception>? _onError;
    private readonly TimeSpan _itemTimeout;
    private int _loopStarted;

    /// <summary>
    /// Longest any single item may hold the consumer.
    /// </summary>
    /// <remarks>
    /// Serialising work means one item that never finishes strands every item behind it — forever,
    /// since there is no second consumer and the queue is process-lifetime. That is a WORSE failure
    /// than the reordering this class exists to prevent: a keyDown already sent with its keyUp stuck
    /// in the queue leaves the key held down on the user's PC with no way to release it.
    ///
    /// So the queue refuses to trust its work. Callers should still bound their own waits — the
    /// desktop client does — but this guarantees the queue keeps draining even when one does not.
    /// Generous enough for a real connect to a sleeping PC; note that timing out stops the WAITING,
    /// it does not cancel the work, which continues on its own.
    /// </remarks>
    internal static readonly TimeSpan DefaultItemTimeout = TimeSpan.FromSeconds(30);

    /// <param name="onError">
    /// Called with the item's label when it throws, or when the queue refuses it. Optional so tests
    /// can observe failures without a platform logger.
    /// </param>
    internal OrderedAsyncWorkQueue(Action<string, Exception>? onError = null, TimeSpan? itemTimeout = null)
    {
        _onError = onError;
        _itemTimeout = itemTimeout ?? DefaultItemTimeout;
    }

    /// <summary>
    /// Queues <paramref name="work"/> to run after everything already queued.
    /// </summary>
    /// <remarks>
    /// Returns immediately — callers are JNI entry points that must not block the calling thread.
    /// Ordering is by call order: the channel is FIFO and an unbounded channel
    /// accepts synchronously, so two calls from one thread are queued in the order they were made.
    /// </remarks>
    /// <param name="coalesceKey">
    /// Optional. When set, this item is skipped if — at the moment the consumer reaches it — the
    /// very next queued item has the same key. Leave null for anything whose effect is not fully
    /// replaced by its successor's; see the class remarks.
    /// </param>
    internal void Enqueue(string label, Func<Task> work, string? coalesceKey = null)
    {
        EnsureLoopStarted();

        if (!_queue.Writer.TryWrite(new QueuedWork(label, work, coalesceKey)))
        {
            _onError?.Invoke(label, new InvalidOperationException("work queue rejected the item"));
        }
    }

    /// <summary>How many items have been skipped as superseded by an identical-key successor.</summary>
    internal long CoalescedCount => Interlocked.Read(ref _coalescedCount);

    private long _coalescedCount;

    private void EnsureLoopStarted()
    {
        // Interlocked, not a null check: Enqueue is reachable from several JNI exports and there is
        // no guarantee they arrive on one thread. Two consumers would defeat the entire purpose.
        if (Interlocked.Exchange(ref _loopStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var item))
            {
                if (IsSupersededByNext(reader, item))
                {
                    Interlocked.Increment(ref _coalescedCount);
                    continue;
                }

                try
                {
                    // THE AWAIT IS THE GUARANTEE. Change this to `_ = work()` and everything still
                    // compiles, every call site still looks right, and the reordering bug is back with
                    // nothing to indicate it. The timeout is what stops that guarantee turning into a
                    // permanent stall when an item never completes.
                    await item.Work().WaitAsync(_itemTimeout);
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(item.Label, ex);
                }
            }
        }
    }

    /// <summary>
    /// True when <paramref name="item"/> is keyed and the item directly behind it has the same key.
    /// </summary>
    /// <remarks>
    /// Peek, never scan. Looking past the head for a later match would merge across whatever sits in
    /// between — a mouseDown between two moves would then run AFTER the position it was meant to
    /// precede, which is the reordering this class exists to prevent. Safe as a peek-then-read because
    /// the reader is single: nothing else can take the head between this check and the next TryRead.
    /// </remarks>
    private static bool IsSupersededByNext(ChannelReader<QueuedWork> reader, QueuedWork item) =>
        item.CoalesceKey is not null
        && reader.TryPeek(out var next)
        && string.Equals(next.CoalesceKey, item.CoalesceKey, StringComparison.Ordinal);
}

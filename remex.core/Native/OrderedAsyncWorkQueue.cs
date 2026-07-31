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
/// </remarks>
internal sealed class OrderedAsyncWorkQueue
{
    private readonly Channel<(string Label, Func<Task> Work)> _queue =
        Channel.CreateUnbounded<(string, Func<Task>)>(new UnboundedChannelOptions { SingleReader = true });

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
    internal void Enqueue(string label, Func<Task> work)
    {
        EnsureLoopStarted();

        if (!_queue.Writer.TryWrite((label, work)))
        {
            _onError?.Invoke(label, new InvalidOperationException("work queue rejected the item"));
        }
    }

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
        await foreach (var (label, work) in _queue.Reader.ReadAllAsync())
        {
            try
            {
                // THE AWAIT IS THE GUARANTEE. Change this to `_ = work()` and everything still
                // compiles, every call site still looks right, and the reordering bug is back with
                // nothing to indicate it. The timeout is what stops that guarantee turning into a
                // permanent stall when an item never completes.
                await work().WaitAsync(_itemTimeout);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(label, ex);
            }
        }
    }
}

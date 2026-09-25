using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Remex.Core.Native;

/// <summary>
/// An unbounded single-reader FIFO that knows how many items it holds (perf audit P4-4).
/// </summary>
/// <remarks>
/// <para>
/// EXISTS BECAUSE <c>ChannelReader.Count</c> DOES NOT WORK ON THE CHANNEL IT WRAPS. An unbounded
/// channel created with <c>SingleReader = true</c> reports <c>CanCount == false</c>, and its
/// <c>Count</c> throws <see cref="NotSupportedException"/>. The first cut of P4-4 read that property
/// behind a catch that answered 0, so the legacy v2 transfer loops were told the queue was always
/// empty and never paced at all — and the Kotlin test injected its own depth, so nothing noticed.
/// </para>
/// <para>
/// The depth is therefore kept by hand: incremented BEFORE the write (and given back if the write
/// fails) and decremented as the reader takes each item. Incrementing first means a reader racing the
/// writer can never observe the count below zero, and <see cref="Depth"/> clamps anyway. Every writer
/// goes through <see cref="TryEnqueue"/> and the one reader through <see cref="ReadAllAsync"/>, so the
/// count cannot be forgotten at a new call site the way a bare <c>Interlocked</c> call could.
/// </para>
/// <para>
/// It stays UNBOUNDED on purpose (perf audit P0-12, see the field in <c>AndroidNativeExports</c>):
/// the depth is advisory, for callers that want to pace themselves, and never refuses a message.
/// </para>
/// <para>
/// Extracted from <c>AndroidNativeExports</c> so it can be tested against the real channel: nothing
/// about a JNI export can be exercised in a unit test.
/// </para>
/// </remarks>
internal sealed class CountedOutboundQueue<T>
{
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private int _depth;

    /// <summary>Items written and not yet taken by the reader.</summary>
    public int Depth => Math.Max(0, Volatile.Read(ref _depth));

    /// <summary>Queues <paramref name="item"/>; false only if the queue has been completed.</summary>
    public bool TryEnqueue(T item)
    {
        Interlocked.Increment(ref _depth);
        if (_channel.Writer.TryWrite(item))
        {
            return true;
        }

        Interlocked.Decrement(ref _depth);
        return false;
    }

    /// <summary>
    /// Yields every item in order, counting each one out as it is taken. Single reader only.
    /// </summary>
    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            Interlocked.Decrement(ref _depth);
            yield return item;
        }
    }

    /// <summary>Marks the queue complete; later writes fail and the reader ends once drained.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}

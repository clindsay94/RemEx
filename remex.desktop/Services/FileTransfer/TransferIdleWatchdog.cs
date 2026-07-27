using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// Fails a file transfer when the peer goes silent, without bounding how long the transfer may
/// legitimately take.
/// </summary>
/// <remarks>
/// A total-duration timeout is the wrong instrument for a wait whose length is proportional to file
/// size: any value large enough for a 4 GB file over Wi-Fi is far too large to notice a dead peer.
/// This measures SILENCE instead. The deadline is recomputed from the last observed activity on
/// every pass, so it slides forward for as long as the transfer is making progress and only bites
/// when nothing has arrived for <see cref="IdleLimit"/> (RemEx-l519).
/// <para>
/// Extracted from <see cref="FileTransferClient"/> so the rule can be tested without a socket, a
/// <c>ConnectionViewModel</c> or a message envelope — and so the limit is a constructor parameter
/// rather than a private const that only tests would want to change (RemEx-sf27).
/// </para>
/// </remarks>
public sealed class TransferIdleWatchdog
{
    /// <summary>
    /// Live transfers and the timestamp of the last thing received for each.
    /// </summary>
    /// <remarks>
    /// <see cref="StrongBox{T}"/> rather than a plain <c>long</c> value so <see cref="Mark"/> can
    /// update a transfer's timestamp through a reference without touching the dictionary at all.
    /// That is what makes the "never insert" rule in <see cref="Mark"/> enforceable.
    /// </remarks>
    private readonly ConcurrentDictionary<string, StrongBox<long>> _lastActivity = new();

    /// <param name="idleLimit">
    /// How long the peer may be silent before the transfer fails. Defaults to
    /// <see cref="DefaultIdleLimitSeconds"/>.
    /// </param>
    public TransferIdleWatchdog(TimeSpan? idleLimit = null)
    {
        var limit = idleLimit ?? TimeSpan.FromSeconds(DefaultIdleLimitSeconds);
        if (limit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleLimit), limit, "The idle limit must be positive.");

        IdleLimit = limit;
    }

    /// <summary>
    /// Two minutes: long enough to ride out a phone's screen-off Wi-Fi power saving and a slow
    /// disk flush on the receiving end, short enough that a dead peer does not hang the UI
    /// indefinitely.
    /// </summary>
    public const int DefaultIdleLimitSeconds = 120;

    /// <summary>How long the peer may be silent before <see cref="AwaitCompletionAsync"/> gives up.</summary>
    public TimeSpan IdleLimit { get; }

    /// <summary>
    /// How many transfers are currently being watched.
    /// </summary>
    /// <remarks>
    /// Exposed because it is the observable for the leak this class was extracted to guard: an
    /// entry that outlives its transfer is invisible from the outside in every other way, and grows
    /// without bound over a session. A test asserts this returns to zero after a cancelled transfer
    /// receives a late message.
    /// </remarks>
    public int ActiveTransferCount => _lastActivity.Count;

    /// <summary>
    /// Starts watching <paramref name="transferId"/>, returning the handle to pass to
    /// <see cref="AwaitCompletionAsync"/>.
    /// </summary>
    public StrongBox<long> Begin(string transferId)
    {
        var lease = new StrongBox<long>(Stopwatch.GetTimestamp());
        _lastActivity[transferId] = lease;
        return lease;
    }

    /// <summary>Stops watching <paramref name="transferId"/>. Safe to call more than once.</summary>
    public void End(string transferId) => _lastActivity.TryRemove(transferId, out _);

    /// <summary>
    /// Records that something arrived for <paramref name="transferId"/> — but only if it is still
    /// live.
    /// </summary>
    /// <remarks>
    /// <strong>TryGetValue, never the indexer.</strong> This is called from the receive path, which
    /// races teardown by construction: a chunk or progress message already in flight when a
    /// transfer is cancelled arrives after <see cref="End"/> has run. The indexer would INSERT an
    /// entry for that dead transfer, and nothing would ever remove it — every cancelled download
    /// would leak one, on the ordinary path rather than an exotic one. That was a real defect found
    /// in review of RemEx-l519, and it is what <see cref="ActiveTransferCount"/> exists to let a
    /// test pin (RemEx-sf27).
    /// </remarks>
    public void Mark(string transferId)
    {
        if (_lastActivity.TryGetValue(transferId, out var lease))
            Volatile.Write(ref lease.Value, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Awaits <paramref name="tcs"/>, failing with <see cref="TimeoutException"/> only if nothing
    /// is received for <see cref="IdleLimit"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <c>tcs.Task.WaitAsync(timeout)</c>, which is what the control requests in
    /// <see cref="FileTransferClient"/> use — see the type remarks for why a total-duration bound
    /// is wrong here.
    /// <para>
    /// Cancellation is expected to arrive by completing <paramref name="tcs"/> (the callers
    /// register <c>ct.Register(() =&gt; tcs.TrySetCanceled(ct))</c>), so the cancelled path returns
    /// through the normal completion branch rather than through the timer.
    /// </para>
    /// </remarks>
    public async Task<T> AwaitCompletionAsync<T>(
        TaskCompletionSource<T> tcs,
        StrongBox<long> lease,
        CancellationToken ct)
    {
        while (true)
        {
            // Load-bearing, not defensive. Cancellation completes the TCS *and* cancels the linked
            // timer, so WhenAny may legitimately return either. If it returns the timer, without
            // this guard the next pass would build an already-cancelled delay that completes
            // instantly, and the loop would spin a core until the idle deadline expired.
            ct.ThrowIfCancellationRequested();

            var remaining = IdleLimit - Stopwatch.GetElapsedTime(Volatile.Read(ref lease.Value));

            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    "The transfer stopped responding: nothing was received for " +
                    $"{IdleLimit.TotalSeconds:0.###} seconds.");
            }

            // Linked source so the pending timer is cancelled the moment the wait resolves,
            // rather than being left to run out on its own once per idle window.
            using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(remaining, timerCts.Token));
            timerCts.Cancel();

            if (ReferenceEquals(completed, tcs.Task))
                return await tcs.Task;

            // The timer won, but activity may have landed while it was running: loop and
            // re-measure rather than failing on the strength of a stale deadline.
        }
    }
}

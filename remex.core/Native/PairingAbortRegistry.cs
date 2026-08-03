namespace Remex.Core.Native;

/// <summary>
/// Lets a caller abandon the pairing attempt it started, and only that one (RemEx-defb).
/// </summary>
/// <remarks>
/// <para>
/// The pairing exports block their JNI thread for their own budgets — 10s + 20s + 60s for a
/// handshake — and no amount of caller-side `withTimeout` can interrupt a blocking JNI frame. A
/// caller that gives up must therefore be able to TELL the native side to stop, or the abandoned
/// work runs on holding the pairing lock and the user's next attempt queues behind the one they just
/// gave up on.
/// </para>
/// <para>
/// **KEYED BY ATTEMPT, WHICH IS THE WHOLE DESIGN.** "Cancel whatever is in flight" is wrong twice
/// over. A cancel routinely arrives BEFORE its own attempt has begun, because the caller registers
/// its cancellation handler before making the call and an attempt can sit queued on the pairing lock
/// while a previous one runs — so an unkeyed cancel would miss, and the attempt would then run its
/// full budget with nobody waiting. And there are two independent pairing surfaces in the app, so an
/// unkeyed cancel from one could abort the other's PIN submission, which tears down a pairing that
/// was midway through completing.
/// </para>
/// <para>
/// Extracted from <c>AndroidNativeExports</c> so this can be tested: nothing about a JNI export can
/// be exercised in a unit test, but every claim above is about bookkeeping.
/// </para>
/// </remarks>
internal sealed class PairingAbortRegistry
{
    /// <summary>
    /// How many pre-cancelled ids to remember before pruning, and how many to keep when pruning.
    /// </summary>
    /// <remarks>
    /// A cancel that arrives after its attempt already finished has nowhere to go and is recorded
    /// here forever otherwise. Pruning the OLDEST is safe because ids are monotonic: anything still
    /// queued carries a newer id than anything that has finished.
    /// </remarks>
    internal const int PreCancelCapacity = 64;
    internal const int PreCancelKeep = 32;

    private readonly object _gate = new();
    private readonly HashSet<long> _preCancelled = [];
    private CancellationTokenSource? _current;
    private long _currentAttemptId;

    /// <summary>Opens a scope for <paramref name="attemptId"/>, returning the token its budgets link to.</summary>
    /// <remarks>
    /// Returns an ALREADY-CANCELLED token when this attempt was abandoned while it was queued. That
    /// is the case an unkeyed design cannot express, and the reason the pre-cancel set exists.
    /// </remarks>
    internal CancellationToken Begin(long attemptId)
    {
        var fresh = new CancellationTokenSource();
        bool alreadyCancelled;
        CancellationToken token;

        lock (_gate)
        {
            var previous = _current;
            _current = fresh;
            _currentAttemptId = attemptId;
            alreadyCancelled = _preCancelled.Remove(attemptId);

            // READ THE TOKEN HERE, NOT AFTER THE LOCK. A later Begin disposes whatever it displaces,
            // and CancellationTokenSource.Token throws once disposed — so returning `fresh.Token`
            // from outside the lock races a concurrent Begin and throws ObjectDisposedException at
            // the caller. Found by the concurrency test rather than by reading, which is the whole
            // reason this bookkeeping was extracted from the JNI export.
            token = fresh.Token;

            // A previous scope still present means its owner never closed it. Cancel and retire it so
            // it cannot outlive the attempt it belonged to.
            if (previous is not null)
            {
                try { previous.Cancel(); } catch (ObjectDisposedException) { /* already retired */ }
                previous.Dispose();
            }
        }

        // Outside the lock: Cancel() runs its registrations synchronously.
        if (alreadyCancelled)
        {
            try { fresh.Cancel(); } catch (ObjectDisposedException) { /* not reachable; belt and braces */ }
        }

        return token;
    }

    /// <summary>Closes the scope identified by <paramref name="token"/>, if it is still the current one.</summary>
    /// <remarks>
    /// The "still ours" test matters: a newer attempt may already have replaced this scope, and
    /// disposing that one would leave the new attempt unabandonable.
    /// </remarks>
    internal void End(CancellationToken token)
    {
        lock (_gate)
        {
            if (_current is { } current && current.Token == token)
            {
                _current = null;
                _currentAttemptId = 0;
                current.Dispose();
            }
        }
    }

    /// <summary>Abandons <paramref name="attemptId"/>, whether it has started yet or not.</summary>
    /// <returns>True when an in-flight attempt was cancelled; false when the request was recorded for later.</returns>
    internal bool Cancel(long attemptId)
    {
        CancellationTokenSource? target;

        lock (_gate)
        {
            if (_current is not null && _currentAttemptId == attemptId)
            {
                target = _current;
            }
            else
            {
                target = null;
                _preCancelled.Add(attemptId);

                if (_preCancelled.Count > PreCancelCapacity)
                {
                    // Oldest first — see PreCancelCapacity.
                    var stale = _preCancelled.Order().Take(_preCancelled.Count - PreCancelKeep).ToArray();
                    foreach (var id in stale) _preCancelled.Remove(id);
                }
            }
        }

        if (target is null) return false;

        // OUTSIDE THE LOCK. Cancel() runs every registration synchronously, including a
        // ClientWebSocket teardown, and this is reached from the caller's cancellation handler — the
        // main thread, for a UI-scoped timeout. Holding the gate across that would also stall the
        // next attempt's Begin.
        try
        {
            target.Cancel();
        }
        catch (Exception)
        {
            // Two ways this throws, and THE RACE IS THE LIKELY ONE: the attempt can finish naturally
            // between the capture above and this line, so End disposes the source and Cancel gets an
            // ObjectDisposedException. Cancel also aggregates any callback failure. Either way the
            // caller has already given up, so a teardown that throws must not become its result.
            return false;
        }

        return true;
    }

    /// <summary>Whether an attempt is currently in flight. Test seam.</summary>
    internal bool HasActiveScope
    {
        get { lock (_gate) return _current is not null; }
    }

    /// <summary>How many cancels are waiting for an attempt that has not started. Test seam.</summary>
    internal int PendingCancelCount
    {
        get { lock (_gate) return _preCancelled.Count; }
    }
}

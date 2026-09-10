namespace Remex.Agent.Handlers;

/// <summary>
/// Remembers which keys the remote client has pressed and not yet released, so they can be released
/// when its session ends (RemEx-e2p4).
/// </summary>
/// <remarks>
/// <para>
/// A key press from the phone is two independent messages, and a chord is more: Ctrl+Shift+C is six.
/// Nothing guarantees the client gets to send the closing half of that. It can be force-stopped, lose
/// Wi-Fi, or simply tear the stream down before its own keyUps have drained — and the host has
/// already told Windows the key is down. The result is a modifier physically held on the user's
/// desktop, turning every subsequent keystroke into a chord, with no way to clear it from the phone
/// because the phone is what is gone.
/// </para>
/// <para>
/// The client-side ordering fixes (RemEx-3uhp, RemEx-7rq3, RemEx-krvz) make the messages arrive in
/// order when they arrive at all. This covers the case where they never arrive, which no amount of
/// client-side ordering can: the host holds the state, so the host cleans it up.
/// </para>
/// <para>
/// ONLY KEYS THIS HOST INJECTED are tracked — a key the user presses on the PC's own keyboard never
/// enters this set. That is not the same as promising it cannot affect them: Windows keeps ONE
/// keyboard state, so if somebody is sitting at the PC physically holding Ctrl while the client has
/// also pressed Ctrl, releasing ours lifts theirs too. Unavoidable with SendInput, and rare enough
/// (it needs a person at the machine being remoted) to be worth stating rather than solving.
/// </para>
/// </remarks>
internal sealed class HeldKeyTracker
{
    private readonly HashSet<int> _held = [];
    private readonly Lock _gate = new();

    /// <summary>Records a key as held. Idempotent — a repeat is auto-repeat, not a second press.</summary>
    public void Pressed(int keyCode)
    {
        lock (_gate)
        {
            _held.Add(keyCode);
        }
    }

    /// <summary>Records a key as released.</summary>
    public void Released(int keyCode)
    {
        lock (_gate)
        {
            _held.Remove(keyCode);
        }
    }

    /// <summary>
    /// Returns everything still held and forgets it, so the caller can release each one.
    /// </summary>
    /// <remarks>
    /// Take-and-clear rather than read-then-clear: teardown races the input thread, and reading
    /// without clearing under the same lock would let a key be released twice — or, worse, let a
    /// second teardown find the set still populated and send a duplicate release.
    /// </remarks>
    public IReadOnlyList<int> TakeAll()
    {
        lock (_gate)
        {
            if (_held.Count == 0)
            {
                return [];
            }

            var held = _held.ToArray();
            _held.Clear();
            return held;
        }
    }

    /// <summary>Keys currently held. For assertions and diagnostics.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _held.Count;
            }
        }
    }
}

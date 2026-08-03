using System.Collections.Concurrent;
using System.Net.WebSockets;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Agent.Services;

/// <summary>
/// The live <c>/ws</c> sessions, so the host can answer "which clients are attached" and "reach THIS
/// one" (RemEx-xuyu).
/// </summary>
/// <remarks>
/// <para>
/// **THERE WAS NO SUCH THING BEFORE, AND THAT ABSENCE BLOCKED THREE BEADS.** The only record of a
/// connected client was <c>connectionClientId</c>, a local inside <c>PingPongHandler.HandleAsync</c>
/// scoped to one connection — so nothing could count attached phones (RemEx-0z7w), ask whether a
/// given client was still there (<c>ConsentRoutePolicy.Route</c>'s first argument), or send that
/// client a message it had not asked for (RemEx-220r).
/// </para>
/// <para>
/// Keyed by CONNECTION, not by client id, because a connection exists before it identifies and a
/// client id is not unique in time — a phone that drops and redials briefly has two. Lookups by
/// client id therefore resolve to the most recent still-open connection carrying it, falling back
/// to the most recent of any state.
/// </para>
/// <para>
/// **ONLY AUTHENTICATED SESSIONS ARE VISIBLE.** Registration happens before the pairing gate so the
/// handle covers every exit path, but an unauthenticated connection is inert here: it cannot be
/// counted, found, or sent to. See <see cref="MarkAuthenticated"/> for why that is a security
/// property and not tidiness.
/// </para>
/// </remarks>
public sealed class ClientSessionRegistry
{
    /// <summary>Hands out the arrival order that <see cref="Find"/> resolves duplicates by.</summary>
    private static long _sequence;

    /// <summary>One live connection. Identity arrives after the socket does.</summary>
    private sealed class Entry(string? remoteAddress, WebSocket socket)
    {
        /// <summary>
        /// Arrival order, so "most recent" is a fact rather than a guess.
        /// </summary>
        /// <remarks>
        /// <c>ConcurrentDictionary</c> enumerates in hash-bucket order, which has nothing to do with
        /// insertion order — so without this, picking between two connections carrying the same
        /// client id is a coin flip. See <see cref="Find"/> for why that matters.
        /// </remarks>
        public long Sequence { get; } = Interlocked.Increment(ref _sequence);

        public string? RemoteAddress { get; } = remoteAddress;
        public WebSocket Socket { get; } = socket;

        public volatile string? ClientId;
        public volatile string? DeviceName;

        /// <summary>Whether this client says it can render a consent prompt (RemEx-220r).</summary>
        public volatile bool SupportsPhonePrompt;

        /// <summary>Whether the connection cleared the pairing gate. See <see cref="MarkAuthenticated"/>.</summary>
        public volatile bool Authenticated;

        /// <summary>
        /// Whether this connection PROVED which client it is, as opposed to merely clearing the gate.
        /// </summary>
        /// <remarks>
        /// The two come apart for exactly one connection: loopback, which is authenticated by
        /// construction because it is the PC talking to itself, and never proves an identity because
        /// it has no pairing to prove. It can therefore claim any client id it likes — so it must not
        /// be reachable BY one. See <see cref="Find"/>.
        /// </remarks>
        public volatile bool IdentityProven;
    }

    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();

    /// <summary>Records a connection for as long as the returned handle is undisposed.</summary>
    /// <remarks>
    /// A handle rather than a matching Unregister call, so a connection cannot be left in the
    /// registry by an exit path somebody forgot — <c>HandleAsync</c> has several, which is exactly
    /// how a stale "1 phone connected" would appear with nothing attached.
    /// </remarks>
    public IDisposable Register(string? remoteAddress, WebSocket socket)
    {
        var id = Guid.NewGuid();
        _sessions[id] = new Entry(remoteAddress, socket);
        return new Registration(this, id);
    }

    /// <summary>Attaches the identity a connection presented, once it has presented one.</summary>
    /// <remarks>
    /// <para>
    /// Callable repeatedly: the client id arrives on the first message carrying one, and the device
    /// name (if it comes at all) only on the pairing request. A blank value never clears a name that
    /// was already learned.
    /// </para>
    /// <para>
    /// **AN AUTHENTICATED SESSION'S CLIENT ID IS FROZEN, AND THAT IS A SECURITY PROPERTY.** Every
    /// message carrying a client id calls this. Without the freeze a connection could authenticate as
    /// the one id it can actually prove, then simply claim to be somebody else on its next message —
    /// leaving an AUTHENTICATED session keyed to an id it never proved, which is exactly what
    /// <see cref="MarkAuthenticated"/> exists to make impossible. Identity is settled at the moment a
    /// connection authenticates: callers that need to correct it (the reconnect path re-keying to the
    /// id whose secret produced the HMAC) must do so BEFORE marking it, which they do.
    /// </para>
    /// </remarks>
    public void Identify(IDisposable registration, string? clientId, string? deviceName)
    {
        if (registration is not Registration handle || !_sessions.TryGetValue(handle.Id, out var entry))
            return;

        if (!string.IsNullOrWhiteSpace(clientId)
            && (!entry.Authenticated || entry.ClientId is null
                || string.Equals(entry.ClientId, clientId, StringComparison.Ordinal)))
        {
            entry.ClientId = clientId;
        }

        if (!string.IsNullOrWhiteSpace(deviceName)) entry.DeviceName = deviceName;
    }

    /// <summary>Promotes a connection to visible, once it has cleared the pairing gate.</summary>
    /// <remarks>
    /// **THIS IS A SECURITY BOUNDARY, NOT BOOKKEEPING.** Identifying is not authenticating: a client
    /// id rides on <c>ping</c>, which <c>RequiresPairing</c> exempts, so any host on the LAN can send
    /// one message and name itself anything. Without this gate three things follow, all bad. It would
    /// be counted as an attached phone, reproducing the confident "1 phone connected" with nothing
    /// attached that this whole area exists to prevent. It could claim a device name of its choosing,
    /// straight into the PC's UI, with no PIN. And worst, by presenting a client id it has merely
    /// overheard it would win <see cref="Find"/> on recency over the genuine phone — so RemEx-220r's
    /// <c>file_consent_request</c> would be delivered to it, and a stranger would be answering
    /// consent prompts for somebody else's file transfers.
    /// </remarks>
    /// <param name="identityProven">
    /// True when the connection proved WHICH client it is — pairing verified, or a reconnect proof
    /// accepted. False for loopback, which is trusted to be here but has no identity to prove.
    /// </param>
    public void MarkAuthenticated(IDisposable registration, bool identityProven)
    {
        if (registration is Registration handle && _sessions.TryGetValue(handle.Id, out var entry))
        {
            entry.IdentityProven = identityProven;
            entry.Authenticated = true;
        }
    }

    /// <summary>Records whether a client can be asked for consent on its own screen (RemEx-220r).</summary>
    /// <remarks>
    /// Set from <c>clientCapabilities.supportsConsentPrompt</c> on any message that carries it
    /// (RemEx-220r). ABSENT MEANS FALSE, which is the correct compatibility answer: a client that has
    /// not said it can render the prompt cannot. No shipping client sends the field yet — the phone
    /// half is RemEx-vyhm — so in practice this is still false everywhere, and every consent question
    /// still goes to the PC dialog.
    /// </remarks>
    public void SetSupportsPhonePrompt(IDisposable registration, bool supported)
    {
        if (registration is Registration handle && _sessions.TryGetValue(handle.Id, out var entry))
            entry.SupportsPhonePrompt = supported;
    }

    /// <summary>
    /// Every AUTHENTICATED live session, in the shape <see cref="PhonePresence"/> already consumes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **UNAUTHENTICATED CONNECTIONS ARE EXCLUDED, AND THAT IS THE POINT.** Registration happens on
    /// the first line of <c>HandleAsync</c>, ahead of the pairing gate, so the handle covers every
    /// exit path — but it means any LAN host that opens <c>/ws</c> is momentarily a session.
    /// Reporting those would reproduce the exact bug <see cref="PhonePresence"/> exists to prevent:
    /// a confident "1 phone connected" with nothing attached. Note that IDENTIFIED is not the test —
    /// a client id rides on <c>ping</c>, which needs no pairing, so naming yourself is free. See
    /// <see cref="MarkAuthenticated"/>.
    /// </para>
    /// <para>
    /// Loopback is deliberately NOT filtered here. That rule — a session is a phone only if it is not
    /// loopback — is stated once in <see cref="PhonePresence"/> with its own tests (RemEx-porg), and
    /// re-deriving it here is how three binding sites got it wrong in the first place.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ClientSession> Snapshot() =>
        [.. _sessions
            .Select(kvp => kvp.Value)
            .Where(e => e.Authenticated)
            .Select(e => new ClientSession(e.RemoteAddress, e.DeviceName))];

    /// <summary>Whether a client with this id has a live, reachable session right now.</summary>
    /// <remarks>
    /// OPEN, not merely present. A connection whose socket has closed can linger in the registry
    /// while <c>HandleAsync</c> unwinds — the close handshake, the held-key release, the telemetry
    /// task await. Reporting that as connected while <see cref="TrySendAsync"/> refuses to send to it
    /// is the fail-OPEN direction: <c>ConsentRoutePolicy</c> would route a consent question at a
    /// phone that provably cannot receive it, and the transfer would hang to its timeout-deny.
    /// </remarks>
    public bool IsConnected(string? clientId) => Find(clientId)?.Socket.State == WebSocketState.Open;

    /// <summary>Whether that client advertises the phone-side consent prompt.</summary>
    /// <remarks>Absent means false, which routes consent to the PC dialog — the compatibility path.</remarks>
    public bool SupportsPhonePrompt(string? clientId) => Find(clientId)?.SupportsPhonePrompt ?? false;

    /// <summary>
    /// Sends a message to one named client. False when it is not connected or the send failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failure is reported rather than thrown: every caller of this is deciding what to do about a
    /// peer that may simply have walked away, and an exception would make "the phone is gone" look
    /// like a fault. Cancelling <paramref name="ct"/> still throws — that is the caller giving up,
    /// not the peer disappearing, and the two deserve different handling.
    /// </para>
    /// <para>
    /// No lock is taken here. A WebSocket permits only one outstanding send, and
    /// <see cref="MessageSerializer"/> already serialises every send on a socket through a gate keyed
    /// weakly on that socket — the same gate <c>PingPongHandler</c>'s reader loop goes through. A
    /// second gate at this layer would not add exclusion, only two windows in which
    /// <c>Registration.Dispose</c> could dispose a semaphore out from under an in-flight send.
    /// </para>
    /// </remarks>
    public async Task<bool> TrySendAsync(string? clientId, RemexMessage message, CancellationToken ct)
    {
        var entry = Find(clientId);
        if (entry is null || entry.Socket.State != WebSocketState.Open) return false;

        try
        {
            await MessageSerializer.SendAsync(entry.Socket, message, ct);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A send on an ABORTED socket does not surface as WebSocketException: ManagedWebSocket
            // raises OperationCanceledException(nameof(WebSocketState.Aborted)) instead. That is the
            // peer vanishing — the case this method promises never to dress up as a fault — and it
            // is distinguishable from the caller giving up only by ct, which is still unsignalled
            // here. TransferSessionManager.cs:617 draws the same line for the same reason.
            return false;
        }
    }

    /// <summary>
    /// The newest live session for a client id, preferring one whose socket is still open.
    /// </summary>
    /// <remarks>
    /// NEWEST MATTERS, AND OPEN IS NOT ENOUGH ON ITS OWN. A phone that drops and redials can briefly
    /// have two entries, and the dead one is usually still <see cref="WebSocketState.Open"/> — that is
    /// what "the disconnect has not been noticed yet" means, since a socket only leaves Open after a
    /// failed I/O or a close handshake. So both candidates look alive, and without the arrival order
    /// the winner is a hash-bucket coin flip. Picking the stale one would let a send report success
    /// into a half-open socket's buffer and be silently never seen.
    /// </remarks>
    private Entry? Find(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;

        Entry? open = null;
        Entry? any = null;
        foreach (var (_, entry) in _sessions)
        {
            // PROVEN, not merely authenticated. Loopback clears the gate without ever proving who it
            // is, so any local process — including an unelevated one — could open /ws, claim a paired
            // phone's id, and become the newest session for it. It would then be handed that phone's
            // consent prompts and could answer them, which is precisely the outcome MarkAuthenticated
            // exists to prevent. Being findable by a client id requires having proved that id.
            if (!entry.IdentityProven) continue;
            if (!string.Equals(entry.ClientId, clientId, StringComparison.Ordinal)) continue;

            if (any is null || entry.Sequence > any.Sequence) any = entry;
            if (entry.Socket.State == WebSocketState.Open && (open is null || entry.Sequence > open.Sequence))
                open = entry;
        }

        return open ?? any;
    }

    private sealed class Registration(ClientSessionRegistry owner, Guid id) : IDisposable
    {
        public Guid Id { get; } = id;

        public void Dispose() => owner._sessions.TryRemove(Id, out _);
    }
}

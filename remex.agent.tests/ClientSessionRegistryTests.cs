using System.Net.WebSockets;
using Remex.Agent.Services;
using Remex.Core.Messages;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the live-session registry that three beads were blocked behind (RemEx-xuyu).
/// </summary>
/// <remarks>
/// Nothing on the host could previously answer "which phones are attached", "is THIS client still
/// there", or "send that client something it did not ask for" — the only record of a connection was
/// a local variable inside <c>PingPongHandler.HandleAsync</c>.
/// </remarks>
public sealed class ClientSessionRegistryTests
{
    /// <summary>Records what was written, and can pretend to be closed.</summary>
    private class FakeSocket(WebSocketState state = WebSocketState.Open) : WebSocket
    {
        public List<RemexMessage> Sent { get; } = [];

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
        {
            var message = MessageSerializer.Deserialize(buffer.AsSpan());
            if (message is not null) Sent.Add(message);
            return Task.CompletedTask;
        }

        public override WebSocketState State => state;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
            => throw new NotSupportedException();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
    }

    private static RemexMessage Ping() => new() { Type = MessageTypes.Ping };

    [Fact]
    public void DisposingTheHandleRemovesTheSession()
    {
        // THE FAILURE THIS PREVENTS is a phantom "1 phone connected" with nothing attached, which is
        // the original bug the presence work exists to fix. HandleAsync has several exit paths, so a
        // handle is used rather than a matching unregister call somebody could miss on one of them.
        var registry = new ClientSessionRegistry();

        var handle = registry.Register("192.168.1.50", new FakeSocket());
        registry.Identify(handle, "phone-1", deviceName: null);
        registry.MarkAuthenticated(handle, identityProven: true);
        Assert.Single(registry.Snapshot());

        handle.Dispose();
        Assert.Empty(registry.Snapshot());
        Assert.False(registry.IsConnected("phone-1"));
    }

    [Fact]
    public void ASessionIsFoundByTheClientIdItIdentifiedWith()
    {
        var registry = new ClientSessionRegistry();
        using var handle = registry.Register("192.168.1.50", new FakeSocket());

        // A connection exists BEFORE it identifies, which is why the registry is keyed by connection
        // and not by client id.
        Assert.False(registry.IsConnected("phone-1"));

        registry.Identify(handle, "phone-1", deviceName: null);
        registry.MarkAuthenticated(handle, identityProven: true);
        Assert.True(registry.IsConnected("phone-1"));
        Assert.False(registry.IsConnected("phone-2"));
    }

    [Fact]
    public void AnUnknownOrBlankClientIsNotConnected()
    {
        // Fail-closed: ConsentRoutePolicy treats "not connected" as deny, so guessing yes here would
        // route a consent question at a device that is not there.
        var registry = new ClientSessionRegistry();

        Assert.False(registry.IsConnected(null));
        Assert.False(registry.IsConnected(""));
        Assert.False(registry.IsConnected("never-seen"));
    }

    [Fact]
    public void TheSnapshotKeepsLoopbackSessions()
    {
        // "A session is a phone only if it is not loopback" is stated once in PhonePresence with its
        // own tests (RemEx-porg); re-deriving it here is how three binding sites got it wrong in the
        // first place. So loopback must survive the snapshot and be rejected downstream.
        var registry = new ClientSessionRegistry();
        using var phone = registry.Register("192.168.1.50", new FakeSocket());
        using var loopback = registry.Register("127.0.0.1", new FakeSocket());
        registry.Identify(phone, "phone-1", deviceName: null);
        registry.Identify(loopback, "the-pc-ui", deviceName: null);
        registry.MarkAuthenticated(phone, identityProven: true);
        registry.MarkAuthenticated(loopback, identityProven: true);

        var snapshot = registry.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, s => s.RemoteAddress == "127.0.0.1");
    }

    [Fact]
    public void NamingYourselfIsNotAuthenticatingAndBuysNoVisibility()
    {
        // NAMING YOURSELF IS FREE. Registration happens on HandleAsync's first line, ahead of the
        // pairing gate, so the handle covers every exit path — and a client id rides on `ping`, which
        // RequiresPairing exempts. So any LAN host can connect, send one unauthenticated message and
        // call itself whatever it likes. Counting that is the confident "1 phone connected" with
        // nothing attached all over again, and letting it be FOUND is worse: see the next test.
        var registry = new ClientSessionRegistry();
        using var stranger = registry.Register("192.168.1.99", new FakeSocket());

        Assert.Empty(registry.Snapshot());

        registry.Identify(stranger, "phone-1", "Definitely A Real Phone");
        Assert.Empty(registry.Snapshot());
        Assert.False(registry.IsConnected("phone-1"));

        registry.MarkAuthenticated(stranger, identityProven: true);
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public async Task AnUnauthenticatedPeerCannotHijackAPairedClientsIdAndStealItsMessages()
    {
        // THE ATTACK THIS CLOSES. Find prefers the newest session, so a peer that has merely
        // OVERHEARD a client id can connect afterwards, claim it, and outrank the genuine phone —
        // with no PIN, because the message carrying a client id needs no pairing. RemEx-220r's
        // file_consent_request would then be delivered to the stranger, who would be answering
        // consent prompts for somebody else's file transfers. Requiring authentication in Find is
        // what makes the newest-wins rule safe.
        var registry = new ClientSessionRegistry();
        var phoneSocket = new FakeSocket();
        using var phone = registry.Register("192.168.1.50", phoneSocket);
        registry.Identify(phone, "phone-1", deviceName: null);
        registry.MarkAuthenticated(phone, identityProven: true);

        var attackerSocket = new FakeSocket();
        using var attacker = registry.Register("192.168.1.99", attackerSocket);
        registry.Identify(attacker, "phone-1", deviceName: null);

        Assert.True(await registry.TrySendAsync("phone-1", Ping(), CancellationToken.None));

        Assert.Single(phoneSocket.Sent);
        Assert.Empty(attackerSocket.Sent);
    }

    [Fact]
    public async Task AnAuthenticatedSessionCannotBeRekeyedToAnIdItNeverProved()
    {
        // THE HIJACK, THROUGH A SECOND DOOR. Requiring authentication in Find stops a stranger
        // claiming a paired id on its own connection — but every message carrying a client id calls
        // Identify, so without this a connection could authenticate as the id it can prove and then
        // simply say it is somebody else on the next message. It would be an AUTHENTICATED session
        // keyed to an id it never proved, which is precisely what the authentication gate exists to
        // make impossible. Identity is settled when the connection authenticates, not after.
        var registry = new ClientSessionRegistry();
        var attackerSocket = new FakeSocket();
        using var attacker = registry.Register("192.168.1.99", attackerSocket);

        registry.Identify(attacker, "attacker-own-id", deviceName: null);
        registry.MarkAuthenticated(attacker, identityProven: true);

        registry.Identify(attacker, "the-real-phone", deviceName: "Not Really Connor's Pixel");

        Assert.False(registry.IsConnected("the-real-phone"));
        Assert.False(await registry.TrySendAsync("the-real-phone", Ping(), CancellationToken.None));
        Assert.Empty(attackerSocket.Sent);

        // Its own identity is untouched — this refuses a CHANGE, it does not deauthenticate.
        Assert.True(registry.IsConnected("attacker-own-id"));
    }

    [Fact]
    public void ADeviceNameIsCarriedThroughAndSurvivesLaterAnonymousMessages()
    {
        // Only pairing_request carries a name, and it arrives once — every later message identifies
        // with a client id alone. If those overwrote the name with null, the name would survive
        // exactly one message.
        var registry = new ClientSessionRegistry();
        using var handle = registry.Register("192.168.1.50", new FakeSocket());

        registry.Identify(handle, "phone-1", "Connor's Pixel");
        registry.Identify(handle, "phone-1", deviceName: null);
        registry.Identify(handle, "phone-1", "   ");
        registry.MarkAuthenticated(handle, identityProven: true);

        Assert.Equal("Connor's Pixel", Assert.Single(registry.Snapshot()).DeviceName);
    }

    [Fact]
    public async Task SendingReachesTheIdentifiedClient()
    {
        var registry = new ClientSessionRegistry();
        var socket = new FakeSocket();
        using var handle = registry.Register("192.168.1.50", socket);
        registry.Identify(handle, "phone-1", deviceName: null);
        registry.MarkAuthenticated(handle, identityProven: true);

        Assert.True(await registry.TrySendAsync("phone-1", Ping(), CancellationToken.None));

        Assert.Equal(MessageTypes.Ping, Assert.Single(socket.Sent).Type);
    }

    [Fact]
    public async Task SendingToSomebodyWhoIsNotThereFailsRatherThanThrows()
    {
        // Every caller of this is deciding what to do about a peer that may simply have walked away.
        // An exception would make "the phone is gone" look like a fault.
        var registry = new ClientSessionRegistry();

        Assert.False(await registry.TrySendAsync("never-seen", Ping(), CancellationToken.None));
        Assert.False(await registry.TrySendAsync(null, Ping(), CancellationToken.None));
    }

    [Fact]
    public async Task AClosedSocketIsNotSentTo()
    {
        var registry = new ClientSessionRegistry();
        using var handle = registry.Register("192.168.1.50", new FakeSocket(WebSocketState.Closed));
        registry.Identify(handle, "phone-1", deviceName: null);
        registry.MarkAuthenticated(handle, identityProven: true);

        Assert.False(await registry.TrySendAsync("phone-1", Ping(), CancellationToken.None));

        // And it must SAY so. A session can linger while HandleAsync unwinds — the close handshake,
        // the held-key release, the telemetry await — and reporting that as connected while the send
        // refuses is the fail-open direction: ConsentRoutePolicy would route a consent question at a
        // phone that provably cannot receive it, and the transfer would hang to its timeout-deny.
        Assert.False(registry.IsConnected("phone-1"));
    }

    [Fact]
    public async Task AReconnectBeatsAnAbortedSocketUnderTheSameId()
    {
        var registry = new ClientSessionRegistry();
        using var dead = registry.Register("192.168.1.50", new FakeSocket(WebSocketState.Aborted));
        registry.Identify(dead, "phone-1", deviceName: null);
        registry.MarkAuthenticated(dead, identityProven: true);

        var freshSocket = new FakeSocket();
        using var fresh = registry.Register("192.168.1.50", freshSocket);
        registry.Identify(fresh, "phone-1", deviceName: null);
        registry.MarkAuthenticated(fresh, identityProven: true);

        Assert.True(await registry.TrySendAsync("phone-1", Ping(), CancellationToken.None));
        Assert.Single(freshSocket.Sent);
    }

    [Fact]
    public async Task AReconnectBeatsAStillOpenSocketTheHostHasNotNoticedIsDead()
    {
        // THE HARD CASE, AND THE REALISTIC ONE. A phone that drops and redials leaves the old socket
        // reporting Open — that is what "the disconnect has not been noticed yet" means, since a
        // socket only leaves Open after a failed I/O or a close handshake. Both candidates look
        // alive, so preferring an open socket decides nothing and ConcurrentDictionary enumerates in
        // hash-bucket order. Without an arrival order the winner is a coin flip, and picking the
        // stale one makes TrySendAsync report success into a buffer nobody is reading: RemEx-220r's
        // consent prompt is never seen and the transfer hangs to its timeout-deny.
        var registry = new ClientSessionRegistry();
        var abandoned = new FakeSocket();
        using var old = registry.Register("192.168.1.50", abandoned);
        registry.Identify(old, "phone-1", deviceName: null);
        registry.MarkAuthenticated(old, identityProven: true);

        var currentSocket = new FakeSocket();
        using var current = registry.Register("192.168.1.50", currentSocket);
        registry.Identify(current, "phone-1", deviceName: null);
        registry.MarkAuthenticated(current, identityProven: true);

        Assert.True(await registry.TrySendAsync("phone-1", Ping(), CancellationToken.None));

        Assert.Single(currentSocket.Sent);
        Assert.Empty(abandoned.Sent);
    }

    [Fact]
    public async Task SendingToAConnectionThatWentAwayMidCallReportsFailureInsteadOfThrowing()
    {
        // The race this pins: RemEx-220r sends file_consent_request unprompted to a phone that may
        // have just walked out of Wi-Fi range, so the disconnect landing DURING the send is the
        // expected case, not the exotic one. An earlier draft held a per-connection SemaphoreSlim
        // that Dispose disposed, which turned this into an uncaught ObjectDisposedException from the
        // finally block — a peer walking away dressed up as a fault. MessageSerializer already
        // serialises sends per socket, so the registry holds no lock of its own.
        var registry = new ClientSessionRegistry();
        var socket = new ThrowingSocket();
        using var handle = registry.Register("192.168.1.50", socket);
        registry.Identify(handle, "phone-1", deviceName: null);
        registry.MarkAuthenticated(handle, identityProven: true);

        Assert.False(await registry.TrySendAsync("phone-1", Ping(), CancellationToken.None));
    }

    [Fact]
    public async Task DisposingTheHandleWithASendInFlightDoesNotThrow()
    {
        // GENUINELY OVERLAPPED, and it has to be. An earlier version of this test used a socket whose
        // SendAsync completed synchronously, so TrySendAsync had already returned a finished task
        // before Dispose was reached — it duplicated the disposal test and proved nothing about the
        // race in its own name. The blocking socket is what holds the send open across the Dispose.
        var registry = new ClientSessionRegistry();
        var socket = new BlockingSocket();
        var handle = registry.Register("192.168.1.50", socket);
        registry.Identify(handle, "phone-1", deviceName: null);
        registry.MarkAuthenticated(handle, identityProven: true);

        var send = registry.TrySendAsync("phone-1", Ping(), CancellationToken.None);
        Assert.True(await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(send.IsCompleted);

        handle.Dispose();
        socket.Release();

        Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await registry.TrySendAsync("phone-1", Ping(), CancellationToken.None));
    }

    [Fact]
    public async Task AnAbortedSocketReportsFailureEvenThoughItThrowsCancellation()
    {
        // NOT A WebSocketException. A send on an aborted socket surfaces as
        // OperationCanceledException(nameof(WebSocketState.Aborted)) from ManagedWebSocket, which is
        // what Kestrel hands the host — so the obvious catch list misses precisely the case where the
        // peer vanished. RemEx-220r will push with a host-side token rather than the client's
        // RequestAborted, so ct is unsignalled and the exception would escape a method documented to
        // return false. TransferSessionManager.cs:617 draws the same line.
        var registry = new ClientSessionRegistry();
        using var handle = registry.Register("192.168.1.50", new AbortingSocket());
        registry.Identify(handle, "phone-1", deviceName: null);
        registry.MarkAuthenticated(handle, identityProven: true);

        Assert.False(await registry.TrySendAsync("phone-1", Ping(), CancellationToken.None));
    }

    [Fact]
    public async Task TheCallerGivingUpStillThrowsBecauseThatIsNotThePeerLeaving()
    {
        // The other side of the same coin: a cancelled ct is the HOST abandoning the send, and
        // swallowing it would hide a shutdown or a timeout as an ordinary "phone is gone".
        var registry = new ClientSessionRegistry();
        using var handle = registry.Register("192.168.1.50", new AbortingSocket());
        registry.Identify(handle, "phone-1", deviceName: null);
        registry.MarkAuthenticated(handle, identityProven: true);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registry.TrySendAsync("phone-1", Ping(), cancelled.Token));
    }

    /// <summary>Reports Open, then fails the way a socket that died mid-send does.</summary>
    private sealed class ThrowingSocket : FakeSocket
    {
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
            => throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
    }

    /// <summary>Fails the way ManagedWebSocket does when the socket has been aborted.</summary>
    private sealed class AbortingSocket : FakeSocket
    {
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
            => throw new OperationCanceledException(nameof(WebSocketState.Aborted));
    }

    /// <summary>Parks inside SendAsync until released, so a send can be observed mid-flight.</summary>
    private sealed class BlockingSocket : FakeSocket
    {
        public TaskCompletionSource<bool> SendStarted { get; } = new();
        private readonly TaskCompletionSource _gate = new();

        public override async Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
        {
            SendStarted.TrySetResult(true);
            await _gate.Task;
        }

        public void Release() => _gate.TrySetResult();
    }

    [Fact]
    public void PhonePromptSupportIsFalseUntilAClientSaysOtherwise()
    {
        // Absent means false, which routes consent to the PC dialog — the compatibility path. An
        // older phone cannot render the prompt, so assuming it can would break a working setup on an
        // app the user has no reason to suspect.
        var registry = new ClientSessionRegistry();
        using var handle = registry.Register("192.168.1.50", new FakeSocket());
        registry.Identify(handle, "phone-1", deviceName: null);
        registry.MarkAuthenticated(handle, identityProven: true);

        Assert.False(registry.SupportsPhonePrompt("phone-1"));
        Assert.False(registry.SupportsPhonePrompt("never-seen"));

        registry.SetSupportsPhonePrompt(handle, true);
        Assert.True(registry.SupportsPhonePrompt("phone-1"));
    }
}

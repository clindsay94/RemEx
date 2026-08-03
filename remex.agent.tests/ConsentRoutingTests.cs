using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins where a file-consent question is actually asked, now that it has somewhere to go (RemEx-220r).
/// </summary>
/// <remarks>
/// <para>
/// <c>file_consent_request</c> was declared and round-trip tested and had NO SENDER — the canonical
/// declared-with-no-sender wire type. Every consent question went to a dialog on the PC, including
/// the ones a phone in another room had just asked, where it waited in front of nobody until it
/// timed out into a deny.
/// </para>
/// <para>
/// <c>ConsentRoutePolicy</c> already decided WHERE to ask and has its own tests. These cover the
/// wiring the policy had nothing to drive: that the phone actually receives a prompt, that the PC
/// dialog still fires for a client that cannot render one, and that nobody is asked about a request
/// whose asker has gone.
/// </para>
/// </remarks>
public sealed class ConsentRoutingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"remex-consent-{Guid.NewGuid():N}");

    public ConsentRoutingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    /// <summary>Records what the host sent it, so a prompt can be seen rather than assumed.</summary>
    private sealed class RecordingSocket(WebSocketState state = WebSocketState.Open) : WebSocket
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

    private FileTrustService NewService(ClientSessionRegistry sessions, TimeSpan? timeout = null)
    {
        var paired = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance, Path.Combine(_dir, "paired_clients.json"));
        paired.RegisterClient("phone-1");

        return new FileTrustService(
            NullLogger<FileTrustService>.Instance,
            paired,
            sessions,
            Path.Combine(_dir, "file_transfer_trust.json"),
            timeout ?? TimeSpan.FromMilliseconds(200));
    }

    private static FileConsentRequest Request() =>
        new() { ConsentId = Guid.NewGuid().ToString("N"), Kind = FileConsentKinds.FullBrowse };

    [Fact]
    public async Task APhoneThatCanRenderThePromptIsSentOne()
    {
        // THE BEAD, IN ONE ASSERTION. The type existed and nothing ever set it.
        var socket = new RecordingSocket();
        var sessions = new ClientSessionRegistry();
        var handle = sessions.Register("192.168.1.50", socket);
        sessions.Identify(handle, "phone-1", deviceName: null);
        sessions.MarkAuthenticated(handle, identityProven: true);
        sessions.SetSupportsPhonePrompt(handle, true);

        var service = NewService(sessions);
        FileConsentPrompt? raised = null;
        service.ConsentRequested += p => raised = p;
        var request = Request();

        var decision = await service.RequestConsentAsync("phone-1", request, CancellationToken.None);

        var prompt = Assert.Single(socket.Sent);
        Assert.Equal(MessageTypes.FileConsentRequest, prompt.Type);
        Assert.Equal(request.ConsentId, prompt.FileConsentRequest?.ConsentId);

        // AND THE PC STAYS QUIET. Asking both surfaces would let two people answer one question, and
        // is the natural shape of a botched merge — every other test here passes with it.
        Assert.Null(raised);

        // Nobody answered, so it still times out into a clean deny — the prompt being delivered does
        // not change what silence means.
        Assert.False(decision.Granted);
    }

    [Fact]
    public async Task APhoneThatCannotRenderThePromptStillGetsThePcDialog()
    {
        // THE COMPATIBILITY PATH, AND NOT DEAD CODE. A phone built before the phone-side sheet cannot
        // show this, so routing to a surface it does not have would break a working setup on an app
        // the user has no reason to suspect — caused entirely by updating the PC.
        var socket = new RecordingSocket();
        var sessions = new ClientSessionRegistry();
        var handle = sessions.Register("192.168.1.50", socket);
        sessions.Identify(handle, "phone-1", deviceName: null);
        sessions.MarkAuthenticated(handle, identityProven: true);
        // No SetSupportsPhonePrompt: absent means false, which is the whole compatibility story.

        var service = NewService(sessions);
        FileConsentPrompt? raised = null;
        service.ConsentRequested += p => raised = p;

        await service.RequestConsentAsync("phone-1", Request(), CancellationToken.None);

        Assert.NotNull(raised);
        Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task ARequestWhoseAskerHasGoneIsRefusedWithoutAskingAnyone()
    {
        // Not a PC dialog: it would ask about a transfer that can no longer happen, and an "allow"
        // would grant durable trust to a device that is not there, on a request whose origin the
        // person answering cannot see.
        var sessions = new ClientSessionRegistry();
        var service = NewService(sessions, TimeSpan.FromSeconds(30));

        var raised = false;
        service.ConsentRequested += _ => raised = true;

        var decision = await service.RequestConsentAsync("phone-1", Request(), CancellationToken.None);

        Assert.False(decision.Granted);
        Assert.False(decision.Remember);
        Assert.False(raised);
    }

    [Fact]
    public async Task ARefusalForAnAbsentAskerIsImmediateRatherThanATimeout()
    {
        // The 30-second timeout above is the assertion: a deny that waited for it would be
        // indistinguishable from a deny that was decided, and the user would sit through a minute of
        // nothing before being told no.
        var sessions = new ClientSessionRegistry();
        var service = NewService(sessions, TimeSpan.FromSeconds(30));

        var started = DateTime.UtcNow;
        await service.RequestConsentAsync("phone-1", Request(), CancellationToken.None);

        Assert.True(
            DateTime.UtcNow - started < TimeSpan.FromSeconds(5),
            "an absent asker must be refused immediately, not after the consent timeout");
    }

    [Fact]
    public async Task APhonePromptThatCannotBeDeliveredIsARefusalRatherThanAPcDialog()
    {
        // The client went away between the routing decision and the send. That is the same state
        // Route already calls a deny, so falling back to the PC would put a question on screen about
        // an asker who is not there — the exact outcome the deny branch exists to prevent.
        var sessions = new ClientSessionRegistry();
        var handle = sessions.Register("192.168.1.50", new RecordingSocket(WebSocketState.Aborted));
        sessions.Identify(handle, "phone-1", deviceName: null);
        sessions.MarkAuthenticated(handle, identityProven: true);
        sessions.SetSupportsPhonePrompt(handle, true);

        var service = NewService(sessions, TimeSpan.FromSeconds(30));
        var raised = false;
        service.ConsentRequested += _ => raised = true;

        var decision = await service.RequestConsentAsync("phone-1", Request(), CancellationToken.None);

        Assert.False(decision.Granted);
        Assert.False(raised);
    }

    [Fact]
    public async Task ThePhonesAnswerResolvesItsOwnPrompt()
    {
        var socket = new RecordingSocket();
        var sessions = new ClientSessionRegistry();
        var handle = sessions.Register("192.168.1.50", socket);
        sessions.Identify(handle, "phone-1", deviceName: null);
        sessions.MarkAuthenticated(handle, identityProven: true);
        sessions.SetSupportsPhonePrompt(handle, true);

        var service = NewService(sessions, TimeSpan.FromSeconds(30));
        var request = Request();

        var pending = service.RequestConsentAsync("phone-1", request, CancellationToken.None);
        await WaitForPromptAsync(socket);

        Assert.True(service.TryResolveRemoteConsent("phone-1", request.ConsentId, granted: true, remember: true));

        var decision = await pending;
        Assert.True(decision.Granted);
        Assert.True(decision.Remember);
    }

    [Fact]
    public async Task ANOTHERClientCannotAnswerSomebodyElsesPrompt()
    {
        // THE REASON THE RESPONSE IS BOUND TO A CLIENT. Resolution used to key on the consent id
        // alone, which was harmless only while nothing sent these prompts. With a sender, any paired
        // device that learned an id could answer a question it was never asked — and the answer that
        // matters is "allow", which hands it the access the other user was being asked about.
        var socket = new RecordingSocket();
        var sessions = new ClientSessionRegistry();
        var handle = sessions.Register("192.168.1.50", socket);
        sessions.Identify(handle, "phone-1", deviceName: null);
        sessions.MarkAuthenticated(handle, identityProven: true);
        sessions.SetSupportsPhonePrompt(handle, true);

        var service = NewService(sessions, TimeSpan.FromSeconds(30));
        var request = Request();

        var pending = service.RequestConsentAsync("phone-1", request, CancellationToken.None);
        await WaitForPromptAsync(socket);

        Assert.False(
            service.TryResolveRemoteConsent("phone-2", request.ConsentId, granted: true, remember: true));

        // And the real owner can still answer, so the rejection blocked the impostor rather than the
        // prompt.
        Assert.True(service.TryResolveRemoteConsent("phone-1", request.ConsentId, granted: false, remember: false));
        Assert.False((await pending).Granted);
    }

    [Fact]
    public void AnUnknownOrResolvedPromptIsNotRevivedByALateAnswer()
    {
        var sessions = new ClientSessionRegistry();
        var service = NewService(sessions);

        Assert.False(service.TryResolveRemoteConsent("phone-1", "never-asked", granted: true, remember: true));
        Assert.False(service.TryResolveRemoteConsent("phone-1", null, granted: true, remember: true));
        Assert.False(service.TryResolveRemoteConsent(null, "some-id", granted: true, remember: true));
    }

    /// <summary>Waits for the prompt to actually reach the socket before answering it.</summary>
    private static async Task WaitForPromptAsync(RecordingSocket socket)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (socket.Sent.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.NotEmpty(socket.Sent);
    }

    [Fact]
    public async Task ThePromptGoesToTheNAMEDClientAndNotSomeOtherConnectedOne()
    {
        // With one session registered, "sent to the right client" and "sent at all" are the same
        // assertion. Two make them different.
        var mine = new RecordingSocket();
        var theirs = new RecordingSocket();
        var sessions = new ClientSessionRegistry();

        var a = sessions.Register("192.168.1.50", mine);
        sessions.Identify(a, "phone-1", deviceName: null);
        sessions.MarkAuthenticated(a, identityProven: true);
        sessions.SetSupportsPhonePrompt(a, true);

        var b = sessions.Register("192.168.1.51", theirs);
        sessions.Identify(b, "phone-2", deviceName: null);
        sessions.MarkAuthenticated(b, identityProven: true);
        sessions.SetSupportsPhonePrompt(b, true);

        await NewService(sessions).RequestConsentAsync("phone-1", Request(), CancellationToken.None);

        Assert.Single(mine.Sent);
        Assert.Empty(theirs.Sent);
    }

    [Fact]
    public async Task ALocalProcessOnLoopbackCannotClaimAPhonesIdAndBeSentItsPrompt()
    {
        // LOOPBACK IS AUTHENTICATED WITHOUT PROVING ANYTHING — it is the PC talking to itself, and it
        // has no pairing to prove. So any local process, including an unelevated one, could open /ws,
        // claim a paired phone's id, and become the newest session for it. It would then be handed
        // that phone's consent prompts and could answer them: a durable full-browse grant with no
        // human involved. Being findable BY a client id has to require having proved that id.
        var phoneSocket = new RecordingSocket();
        var impostorSocket = new RecordingSocket();
        var sessions = new ClientSessionRegistry();

        var phone = sessions.Register("192.168.1.50", phoneSocket);
        sessions.Identify(phone, "phone-1", deviceName: null);
        sessions.MarkAuthenticated(phone, identityProven: true);
        sessions.SetSupportsPhonePrompt(phone, true);

        // Registered later, so it would win the newest-wins rule if it were eligible at all.
        var impostor = sessions.Register("127.0.0.1", impostorSocket);
        sessions.Identify(impostor, "phone-1", deviceName: null);
        sessions.MarkAuthenticated(impostor, identityProven: false);
        sessions.SetSupportsPhonePrompt(impostor, true);

        await NewService(sessions).RequestConsentAsync("phone-1", Request(), CancellationToken.None);

        Assert.Single(phoneSocket.Sent);
        Assert.Empty(impostorSocket.Sent);
    }

    [Fact]
    public void ALoopbackSessionIsStillPresentEvenThoughItCannotBeAddressed()
    {
        // The exclusion is from FIND, not from existence. Presence counting is a separate question
        // with its own rule in PhonePresence, and breaking it here would trade one bug for another.
        var sessions = new ClientSessionRegistry();
        var loopback = sessions.Register("127.0.0.1", new RecordingSocket());
        sessions.Identify(loopback, "the-pc-ui", deviceName: null);
        sessions.MarkAuthenticated(loopback, identityProven: false);

        Assert.Single(sessions.Snapshot());
        Assert.False(sessions.IsConnected("the-pc-ui"));
    }
}

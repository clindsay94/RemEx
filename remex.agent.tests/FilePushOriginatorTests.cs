using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.FileTransfer;
using Remex.Core.Messages;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The sending half of <c>file_push_offer</c>: what goes on the wire, and which answer resolves it
/// (RemEx-9c160, an item of RemEx-hn23).
/// </summary>
/// <remarks>
/// <para>
/// RemEx-y7my shipped seventeen tests over <c>FilePushNegotiation</c> — the pure interpretation —
/// and none over the component that actually offers a file and waits. The gap was not an oversight
/// about which case mattered; it was cost. The interesting case is a reply that does NOT match, and
/// observing a non-resolution means waiting out the deadline, which was an inline seventy seconds.
/// RemEx-gfx3n made that an injectable seam, so the whole class now runs in well under a second.
/// </para>
/// <para>
/// **THE MISMATCH CASE IS THE ONE THAT CAN FAIL SILENTLY IN THE USEFUL DIRECTION.** A freshness or
/// happy-path break is loud — no file moves. A <c>Complete</c> that resolved any waiting offer
/// regardless of id would still work perfectly for one push at a time, and would go wrong only when
/// two overlap or when a reply arrives after its own deadline and finds the next offer in the map:
/// a decline for one file silently accepting another. The class documents dropping unmatched ids as
/// deliberate; until now nothing held it to that.
/// </para>
/// </remarks>
public sealed class FilePushOriginatorTests
{
    /// <summary>Long enough that nothing races it, short enough that a refusal is not a wait.</summary>
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(250);

    private static FilePushOriginator NewOriginator(TimeSpan? offerTimeout = null) =>
        new(NullLogger<FilePushOriginator>.Instance)
        {
            OfferTimeout = offerTimeout ?? ShortDeadline,
        };

    [Fact]
    public async Task EachOfferCarriesItsOwnPushId()
    {
        // MINTED PER OFFER, NOT PER CLASS. Every id here is also a key in the pending map, so a
        // shared or reused one would make two concurrent pushes indistinguishable to Complete —
        // which is the same failure the mismatch test below guards from the other side.
        var originator = NewOriginator();

        var first = new CapturingSocket();
        var second = new CapturingSocket();

        // Both refuse by deadline; the outcome is not what this asserts, the wire frames are.
        await originator.OfferFileAsync(first, "a.png", 10, CancellationToken.None);
        await originator.OfferFileAsync(second, "b.png", 10, CancellationToken.None);

        var firstId = first.OfferedPushId();
        var secondId = second.OfferedPushId();

        Assert.False(string.IsNullOrWhiteSpace(firstId));
        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public async Task TheOfferOnTheWireNamesTheFileItIsOffering()
    {
        // The frame is the only observable output of the send half, and a test that read only the
        // pushId would pass against an originator that offered the wrong file.
        var originator = NewOriginator();
        var socket = new CapturingSocket();

        await originator.OfferFileAsync(socket, "screenshot.png", 4096, CancellationToken.None);

        var offer = socket.Offer();
        Assert.Equal(MessageTypes.FilePushOffer, socket.SentType());
        var file = Assert.Single(offer.Files);
        Assert.Equal("screenshot.png", file.Name);
        Assert.Equal(4096, file.Size);
    }

    [Fact]
    public async Task AMatchingAnswerResolvesTheOffer()
    {
        // The happy path, and it is deliberately given a LONG deadline: if this passed because the
        // originator gave up and refused, it would look identical to a refusal test. The only way it
        // can report Accepted is by the answer arriving.
        var originator = NewOriginator(TimeSpan.FromSeconds(30));
        var socket = new CapturingSocket();

        var offering = originator.OfferFileAsync(socket, "a.png", 10, CancellationToken.None);
        await socket.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        originator.Complete(new FilePushResponse
        {
            PushId = socket.OfferedPushId(),
            Accepted = true,
            TransferIds = ["t-1"],
        });

        var outcome = await offering.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(outcome.Accepted);
        Assert.Equal("t-1", Assert.Single(outcome.TransferIds));
    }

    [Fact]
    public async Task AnAnswerForADIFFERENTPushDoesNotResolveThisOne()
    {
        // A stray id must not be accepted. NOTE WHAT THIS DOES AND DOES NOT PROVE, because the first
        // version of this comment overclaimed and mutation caught it: with only ONE offer pending,
        // breaking Complete's routing so it resolves any waiter still leaves this green.
        // FilePushNegotiation.Interpret re-checks the id and refuses the mismatch, so the outcome is
        // identical whether the routing worked or a second layer rescued it.
        //
        // So this covers the outer behaviour — a foreign id gets nothing — and the test below is the
        // one that actually pins the routing.
        // A LONG DEADLINE, AND THE DISCRIMINATOR IS TIMING RATHER THAN OUTCOME. That is the whole
        // trick, and it took two failed attempts to find. Both the correct code and a Complete that
        // ignores ids end in a refusal, so no assertion about the OUTCOME can tell them apart. What
        // differs is WHEN: correct routing leaves this offer untouched and waiting on its deadline,
        // while a mis-routed reply resolves it on the spot. Checking that it is still pending a
        // moment later separates the two with no reliance on ordering.
        //
        // The two-offer version tried first is not reliable here: a broken Complete picks an
        // arbitrary entry from a ConcurrentDictionary, so it resolves the right one often enough to
        // pass by luck.
        var originator = NewOriginator(TimeSpan.FromSeconds(30));
        var socket = new CapturingSocket();

        var offering = originator.OfferFileAsync(socket, "a.png", 10, CancellationToken.None);
        await socket.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        originator.Complete(new FilePushResponse
        {
            PushId = "an-id-nobody-here-is-waiting-on",
            Accepted = true,
            TransferIds = ["t-1"],
        });

        // Far short of the 30s deadline, so a still-pending offer here means the foreign answer was
        // dropped rather than consumed.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(offering.IsCompleted);

        // And when its own answer does arrive, it is still able to take it — the offer was left
        // usable, not merely unresolved.
        originator.Complete(new FilePushResponse
        {
            PushId = socket.OfferedPushId(),
            Accepted = true,
            TransferIds = ["t-mine"],
        });

        var outcome = await offering.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(outcome.Accepted);
        Assert.Equal("t-mine", Assert.Single(outcome.TransferIds));
    }

    [Fact]
    public async Task AnAnswerGOESTOTheOfferItNamesWhenTwoAreWaiting()
    {
        // THE ASSERTION THIS FILE EXISTS FOR, and it took two concurrent offers to write honestly.
        // With one offer pending, Complete's id lookup is unfalsifiable: Interpret's own mismatch
        // check produces the same refusal whether the reply was routed correctly or handed to the
        // wrong waiter. Only with two in the map does routing become observable.
        //
        // Answering the SECOND is what discriminates. If Complete resolved whichever entry it found
        // first, the second offer would never be resolved at all and would refuse at its deadline —
        // so asserting the second is ACCEPTED fails against exactly that break. The first must still
        // be waiting, which its own refusal-by-deadline shows.
        //
        // The failure this prevents is silent and needs no exotic timing: two overlapping pushes, or
        // a reply arriving after its own deadline and finding whichever offer has since taken its
        // place in the map. A phone declining one file would accept a different one.
        var originator = NewOriginator(TimeSpan.FromSeconds(30));

        var firstSocket = new CapturingSocket();
        var secondSocket = new CapturingSocket();

        var first = originator.OfferFileAsync(firstSocket, "first.png", 10, CancellationToken.None);
        await firstSocket.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = originator.OfferFileAsync(secondSocket, "second.png", 10, CancellationToken.None);
        await secondSocket.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(firstSocket.OfferedPushId(), secondSocket.OfferedPushId());

        originator.Complete(new FilePushResponse
        {
            PushId = secondSocket.OfferedPushId(),
            Accepted = true,
            TransferIds = ["t-second"],
        });

        var secondOutcome = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(secondOutcome.Accepted);
        Assert.Equal("t-second", Assert.Single(secondOutcome.TransferIds));

        // And the offer nobody answered is still waiting rather than having taken the other's reply.
        Assert.False(first.IsCompleted);
    }

    [Fact]
    public async Task ANullOrIdlessAnswerIsDroppedRatherThanThrowing()
    {
        // Both are reachable from the wire: a malformed frame deserialises to a null payload, and a
        // peer can send an empty pushId. Neither should reach the map, and neither should surface as
        // an exception on the receive loop that called this.
        var originator = NewOriginator();

        originator.Complete(null);
        originator.Complete(new FilePushResponse { PushId = "   ", Accepted = true });

        var socket = new CapturingSocket();
        var outcome = await originator.OfferFileAsync(socket, "a.png", 10, CancellationToken.None);

        Assert.False(outcome.Accepted);
    }

    /// <summary>A WebSocket that keeps what was written to it, so the offer can be read back.</summary>
    /// <remarks>
    /// <c>PendingPushOwnershipTests</c> has a sibling fake that only SIGNALS a send. This one keeps
    /// the bytes, because these assertions are about the frame's contents — the minted id and the
    /// file it names — which a signal cannot show.
    /// </remarks>
    private sealed class CapturingSocket : WebSocket
    {
        private readonly List<byte> _written = [];

        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RemexMessage Message() =>
            JsonSerializer.Deserialize<RemexMessage>(Encoding.UTF8.GetString([.. _written]))
                ?? throw new InvalidOperationException("nothing was written to the socket");

        public string? SentType() => Message().Type;

        public FilePushOffer Offer() =>
            Message().FilePushOffer ?? throw new InvalidOperationException("the frame carried no offer");

        public string OfferedPushId() => Offer().PushId;

        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
            => throw new NotSupportedException("nothing here reads");

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        {
            _written.AddRange(buffer.AsSpan().ToArray());
            if (endOfMessage) Sent.TrySetResult();
            return Task.CompletedTask;
        }
    }
}

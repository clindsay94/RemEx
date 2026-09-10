using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Messages;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Cover for <see cref="MessageSerializer.ReceiveAsync"/> after it stopped allocating a fresh 4 KB
/// buffer, a MemoryStream and a <c>ToArray</c> copy for every inbound message (RemEx-tfi6).
/// </summary>
/// <remarks>
/// <para>
/// WHAT IS ACTUALLY AT RISK. The optimisation added a fast path that parses a single-frame message
/// straight out of the pooled receive buffer, bypassing the accumulator entirely. Everything that is
/// NOT single-frame therefore travels a path that is now reached less often — and a WebSocket splits
/// messages at frame boundaries the caller does not control, so a chunked file transfer or a large
/// telemetry payload is exactly the traffic that would break while every small message kept working.
/// A bug here would look like "file transfer randomly corrupts" rather than anything obvious.
/// </para>
/// <para>
/// This method is on the receive path of every <c>/ws</c> consumer — pairing, telemetry, remote
/// desktop input and file transfer all funnel through it — so the tests below deliberately assert
/// round-trip equality of a real <see cref="RemexMessage"/> rather than byte counts. The contract
/// that matters to those callers is "the message I sent is the message you parsed", and it must not
/// depend on how the transport happened to split it.
/// </para>
/// <para>
/// The buffer is returned to <c>ArrayPool.Shared</c> with <c>clearArray: true</c> and the fast path
/// hands a span over it to the deserializer. Nothing here can observe a use-after-return directly —
/// the deserializer materializes its result — so these pin the observable contract and the review
/// reasoning covers the lifetime.
/// </para>
/// </remarks>
public class MessageSerializerReceiveTests
{
    /// <summary>
    /// A <see cref="WebSocket"/> that replays a scripted sequence of frames, so a test can dictate
    /// exactly where the message boundaries fall.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than mocked: <see cref="WebSocket"/> is an abstract class with nine
    /// members, and the two that matter here need real buffer-copying behaviour — a mock returning
    /// canned results would not exercise the partial-fill logic that the fast path keys off.
    /// </remarks>
    private sealed class ScriptedWebSocket : WebSocket
    {
        // A LinkedList, not a Queue: when a frame is larger than the caller's buffer the remainder
        // must go back at the FRONT to preserve wire order. Enqueuing it at the back silently
        // reorders the stream, which makes this fake lie about what a real socket does.
        private readonly LinkedList<(byte[] Payload, bool EndOfMessage, WebSocketMessageType Type)> _frames = new();

        /// <summary>Splits <paramref name="payload"/> into frames of at most <paramref name="frameSize"/> bytes.</summary>
        public static ScriptedWebSocket Framing(byte[] payload, int frameSize)
        {
            var socket = new ScriptedWebSocket();
            for (int offset = 0; offset < payload.Length; offset += frameSize)
            {
                int count = Math.Min(frameSize, payload.Length - offset);
                bool last = offset + count >= payload.Length;
                socket._frames.AddLast((payload[offset..(offset + count)], last, WebSocketMessageType.Text));
            }
            return socket;
        }

        /// <summary>Frames not yet read. Zero means the message was drained off the wire.</summary>
        public int RemainingFrames => _frames.Count;

        public void EnqueueClose() =>
            _frames.AddLast((Array.Empty<byte>(), true, WebSocketMessageType.Close));

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var (payload, endOfMessage, type) = _frames.First!.Value;
            _frames.RemoveFirst();

            // A real socket fills only as much as the caller's buffer allows and reports the rest as a
            // continuation. Reproducing that is the point: it is how the fast path gets bypassed.
            int count = Math.Min(payload.Length, buffer.Count);
            payload.AsSpan(0, count).CopyTo(buffer.AsSpan());

            if (count < payload.Length)
            {
                _frames.AddFirst((payload[count..], endOfMessage, type));
                endOfMessage = false;
            }

            return Task.FromResult(new WebSocketReceiveResult(count, type, endOfMessage));
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken c)
            => Task.CompletedTask;
    }

    private static RemexMessage MessageWithPayloadOfSize(int payloadBytes) => new()
    {
        Type = "file_chunk",
        ClientId = "client-7f3a",
        CommandMessage = new string('A', payloadBytes),
    };

    private static void AssertRoundTrips(RemexMessage original, RemexMessage? received)
    {
        Assert.NotNull(received);
        Assert.Equal(original.Type, received!.Type);
        Assert.Equal(original.ClientId, received.ClientId);
        Assert.Equal(original.CommandMessage, received.CommandMessage);
    }

    [Fact]
    public async Task ASingleFrameMessageRoundTrips()
    {
        // The fast path — the overwhelming majority of traffic, including every input event.
        var original = MessageWithPayloadOfSize(64);
        var bytes = MessageSerializer.Serialize(original);

        var received = await MessageSerializer.ReceiveAsync(
            ScriptedWebSocket.Framing(bytes, frameSize: bytes.Length));

        AssertRoundTrips(original, received);
    }

    [Fact]
    public async Task AMessageSplitAcrossManyFramesIsReassembledIntact()
    {
        // THE REGRESSION THIS EXISTS FOR. The fast path must not swallow a continuation: if it ever
        // treated the first frame as the whole message, this would parse a truncated JSON document
        // and return null — or worse, parse a prefix that happened to be valid.
        var original = MessageWithPayloadOfSize(200_000);
        var bytes = MessageSerializer.Serialize(original);

        var received = await MessageSerializer.ReceiveAsync(
            ScriptedWebSocket.Framing(bytes, frameSize: 1024));

        AssertRoundTrips(original, received);
    }

    [Fact]
    public async Task AMessageLargerThanTheReceiveBufferRoundTrips()
    {
        // Delivered as ONE frame by the sender, but larger than the 32 KB scratch buffer, so the
        // socket reports partial reads and the fast path must decline. This is the ~87 KB file chunk
        // that motivated the bead, and it is the case a naive "EndOfMessage means done" check breaks.
        var original = MessageWithPayloadOfSize(87 * 1024);
        var bytes = MessageSerializer.Serialize(original);

        var received = await MessageSerializer.ReceiveAsync(
            ScriptedWebSocket.Framing(bytes, frameSize: bytes.Length));

        AssertRoundTrips(original, received);
    }

    [Fact]
    public async Task AMessageExactlyFillingTheReceiveBufferRoundTrips()
    {
        // The off-by-one seam: the payload is sized so the serialized message lands within a byte or
        // two of the 32 KB buffer, where "did the frame end, or did the buffer?" is easiest to confuse.
        var probe = MessageSerializer.Serialize(MessageWithPayloadOfSize(0));
        var original = MessageWithPayloadOfSize((32 * 1024) - probe.Length);
        var bytes = MessageSerializer.Serialize(original);

        var received = await MessageSerializer.ReceiveAsync(
            ScriptedWebSocket.Framing(bytes, frameSize: bytes.Length));

        AssertRoundTrips(original, received);
    }

    [Fact]
    public async Task AClosedSocketReturnsNullRatherThanThrowing()
    {
        // The receive loops treat null as "stop"; a throw here would surface as an unhandled fault on
        // a normal disconnect.
        var socket = new ScriptedWebSocket();
        socket.EnqueueClose();

        Assert.Null(await MessageSerializer.ReceiveAsync(socket));
    }

    [Fact]
    public async Task AnOversizeMessageReturnsNullWithoutThrowing()
    {
        // Over the 4 MB cap. Returning null is only half the contract: the remaining frames MUST be
        // read off the wire, because bailing early leaves the socket mid-message and desyncs every
        // subsequent read on that connection — a silent, permanent break of one client's session.
        // Asserting RemainingFrames is what makes that half testable; without it this test passes
        // even if the drain is deleted outright.
        var bytes = MessageSerializer.Serialize(MessageWithPayloadOfSize(5 * 1024 * 1024));
        var socket = ScriptedWebSocket.Framing(bytes, frameSize: 64 * 1024);

        Assert.Null(await MessageSerializer.ReceiveAsync(socket));
        Assert.Equal(0, socket.RemainingFrames);
    }

    [Fact]
    public async Task AnEmptyMessageReturnsNullRatherThanThrowing()
    {
        // A zero-length text frame takes the fast path with a zero-length span. The old code reached
        // the same place via ToArray() on an empty stream; both must yield null, not an exception
        // escaping into a receive loop.
        var socket = ScriptedWebSocket.Framing(Array.Empty<byte>(), frameSize: 1);
        socket.EnqueueClose();

        Assert.Null(await MessageSerializer.ReceiveAsync(socket));
    }

    [Fact]
    public async Task MalformedJsonReturnsNullRatherThanThrowing()
    {
        // Deserialize swallows JsonException by design; the fast path must not have routed around it.
        var garbage = Encoding.UTF8.GetBytes("{ this is not json ");

        Assert.Null(await MessageSerializer.ReceiveAsync(
            ScriptedWebSocket.Framing(garbage, frameSize: garbage.Length)));
    }

    [Fact]
    public async Task ConsecutiveReceivesOnOneSocketDoNotBleedIntoEachOther()
    {
        // The scratch buffer is pooled, so the second receive is very likely handed the same array
        // that just held the first, longer message. This pins that a shorter message does not pick up
        // the tail. NOTE the honest limit: the parse is bounded by result.Count, so this cannot fail
        // for that reason alone — it is a guard against a future change that derives the length from
        // the buffer instead, not a demonstration that the current code needed it.
        var first = MessageWithPayloadOfSize(4096);
        var second = MessageWithPayloadOfSize(8);

        var socket = ScriptedWebSocket.Framing(MessageSerializer.Serialize(first), 64 * 1024);
        AssertRoundTrips(first, await MessageSerializer.ReceiveAsync(socket));

        var socket2 = ScriptedWebSocket.Framing(MessageSerializer.Serialize(second), 64 * 1024);
        AssertRoundTrips(second, await MessageSerializer.ReceiveAsync(socket2));
    }
}

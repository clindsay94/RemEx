using System.Linq;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.Session;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Cover for sending the desktop frame envelope as two WebSocket fragments instead of one
/// concatenated buffer (RemEx-41xu).
/// </summary>
/// <remarks>
/// <para>
/// Every sent frame used to be copied into a fresh <c>28 + payload</c> array purely to put a 28-byte
/// header in front of it — at full frame rate, the entire stream's bytes again. Header and payload now
/// go out as two fragments of ONE message. Fragmentation is a transport detail: the bytes the client
/// parses are identical, and its receive loop already reassembles on <c>EndOfMessage</c>.
/// </para>
/// <para>
/// THE FAILURE MODE IS SILENT, WHICH IS WHY THE INTERLEAVING TEST EXISTS. A WebSocket carries one
/// message at a time per socket. If the send lock were released between the two fragments, a
/// concurrent sender could splice its own fragments into the middle of this message; the client would
/// reassemble one corrupt frame, and because the envelope validates by exact length that frame does
/// not raise an error — it fails to parse and falls through to the legacy untagged path, feeding
/// header bytes to the decoder as though they were video.
/// </para>
/// </remarks>
public class DesktopFrameFragmentSendTests
{
    /// <summary>Records each fragment in send order, yielding mid-send so interleaving can occur.</summary>
    private sealed class FragmentRecordingWebSocket : WebSocket
    {
        public List<(byte[] Bytes, bool EndOfMessage)> Fragments { get; } = [];

        public override async Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        {
            // A real suspension, so a second sender gets the chance to run between fragments. If the
            // lock did not span both, this is exactly where the interleave would appear.
            await Task.Yield();
            lock (Fragments) Fragments.Add((buffer.ToArray(), endOfMessage));
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
            => throw new NotSupportedException();
    }

    private static RemoteDesktopHandler NewHandler() =>
        new(
            NullLogger<RemoteDesktopHandler>.Instance,
            Mock.Of<IScreenCaptureService>(),
            Mock.Of<IInputSimulationService>(),
            Mock.Of<IDesktopWindowControlService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInteractiveSessionGuard>());

    private static byte[] Header(int payloadLength, long serial = 7, long sequence = 42)
    {
        var header = new byte[DesktopFrameEnvelope.HeaderSize];
        DesktopFrameEnvelope.WriteHeader(
            header, payloadLength, serial, sequence, DesktopCodecKind.H264, DesktopFrameFlags.KeyFrame);
        return header;
    }

    [Fact]
    public async Task TheHeaderAndPayloadAreSentAsTwoFragmentsOfOneMessage()
    {
        // The first fragment must NOT end the message, or the client parses a 28-byte frame with no
        // payload and the real payload arrives as a second, headerless message.
        var handler = NewHandler();
        var socket = new FragmentRecordingWebSocket();
        byte[] payload = [0x00, 0x00, 0x00, 0x01, 0x65, 0xAA, 0xBB];

        using var gate = new SemaphoreSlim(1, 1);
        await handler.SendFramedBinaryAsync(socket, Header(payload.Length), payload, gate, CancellationToken.None);

        Assert.Equal(2, socket.Fragments.Count);
        Assert.False(socket.Fragments[0].EndOfMessage);
        Assert.True(socket.Fragments[1].EndOfMessage);
        Assert.Equal(DesktopFrameEnvelope.HeaderSize, socket.Fragments[0].Bytes.Length);
        Assert.Equal(payload, socket.Fragments[1].Bytes);
    }

    [Fact]
    public async Task TheReassembledMessageIsByteIdenticalToTheConcatenatedForm()
    {
        // THE COMPATIBILITY CLAIM, as an assertion rather than an argument: what arrives is exactly
        // what the old single-buffer path produced.
        var handler = NewHandler();
        var socket = new FragmentRecordingWebSocket();
        byte[] payload = [.. Enumerable.Range(0, 300).Select(i => (byte)i)];

        using var gate = new SemaphoreSlim(1, 1);
        await handler.SendFramedBinaryAsync(socket, Header(payload.Length), payload, gate, CancellationToken.None);

        var reassembled = socket.Fragments.SelectMany(f => f.Bytes).ToArray();
        var wrapped = DesktopFrameEnvelope.Wrap(payload, 7, 42, DesktopCodecKind.H264, DesktopFrameFlags.KeyFrame);

        Assert.Equal(wrapped, reassembled);
    }

    [Fact]
    public async Task TheReassembledMessageStillParsesAsAnEnvelope()
    {
        var handler = NewHandler();
        var socket = new FragmentRecordingWebSocket();
        byte[] payload = [0x11, 0x22, 0x33];

        using var gate = new SemaphoreSlim(1, 1);
        await handler.SendFramedBinaryAsync(socket, Header(payload.Length), payload, gate, CancellationToken.None);

        var reassembled = socket.Fragments.SelectMany(f => f.Bytes).ToArray();

        Assert.True(DesktopFrameEnvelope.TryRead(reassembled, out var header, out var parsed));
        Assert.Equal(7, header.StreamSerial);
        Assert.Equal(42, header.Sequence);
        Assert.Equal(DesktopCodecKind.H264, header.Codec);
        Assert.Equal(DesktopFrameFlags.KeyFrame, header.Flags);
        Assert.Equal(payload, parsed.ToArray());
    }

    [Fact]
    public async Task ConcurrentSendsDoNotInterleaveTheirFragments()
    {
        // THE SAFETY PROPERTY. Two senders on one socket must produce H P H P, never H H P P. The fake
        // yields inside every send specifically to give the scheduler a chance to interleave.
        var handler = NewHandler();
        var socket = new FragmentRecordingWebSocket();
        byte[] first = [0xAA];
        byte[] second = [0xBB];

        using var gate = new SemaphoreSlim(1, 1);
        await Task.WhenAll(
            handler.SendFramedBinaryAsync(socket, Header(first.Length), first, gate, CancellationToken.None),
            handler.SendFramedBinaryAsync(socket, Header(second.Length), second, gate, CancellationToken.None));

        Assert.Equal(4, socket.Fragments.Count);
        Assert.Collection(
            socket.Fragments,
            f => Assert.False(f.EndOfMessage),
            f => Assert.True(f.EndOfMessage),
            f => Assert.False(f.EndOfMessage),
            f => Assert.True(f.EndOfMessage));

        // The flag order alone would still admit a mispairing — sender A's header terminated by
        // sender B's payload. Assert each message carries ONE sender's bytes end to end.
        var firstMessage = socket.Fragments[1].Bytes;
        var secondMessage = socket.Fragments[3].Bytes;
        Assert.Single(firstMessage);
        Assert.Single(secondMessage);
        Assert.Equal(new byte[] { 0xAA, 0xBB }.Order(), new[] { firstMessage[0], secondMessage[0] }.Order());
        Assert.NotEqual(firstMessage[0], secondMessage[0]);
    }

    [Fact]
    public void WriteHeaderRejectsABufferTooSmallToHoldAHeader()
    {
        // Silently writing a short header would put a truncated envelope on the wire.
        var tooSmall = new byte[DesktopFrameEnvelope.HeaderSize - 1];

        Assert.Throws<ArgumentException>(() =>
            DesktopFrameEnvelope.WriteHeader(tooSmall, 0, 1, 1, DesktopCodecKind.H264));
    }

    [Fact]
    public void WriteHeaderAndWrapAgreeOnTheHeaderBytes()
    {
        // The two must not drift: Wrap is what TryRead is tested against, so a header written by the
        // streaming path that differed would parse differently from the one those tests bless.
        byte[] payload = [1, 2, 3, 4, 5];
        var wrapped = DesktopFrameEnvelope.Wrap(payload, -3, long.MaxValue, DesktopCodecKind.Mjpeg);

        var standalone = new byte[DesktopFrameEnvelope.HeaderSize];
        DesktopFrameEnvelope.WriteHeader(standalone, payload.Length, -3, long.MaxValue, DesktopCodecKind.Mjpeg);

        Assert.Equal(wrapped[..DesktopFrameEnvelope.HeaderSize], standalone);
    }
}

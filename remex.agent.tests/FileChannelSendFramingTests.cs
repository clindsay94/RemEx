using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Remex.Agent.Services.FileTransfer;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Asserts on the exact bytes <c>TransferSessionManager.FileChannel.SendFrameAsync</c> hands to the
/// socket (RemEx-npdm).
///
/// WHY THIS EXISTS SEPARATELY FROM THE CODEC TESTS. The frame buffer is now RENTED rather than
/// allocated, because at the 256 KB payload cap a fresh <c>byte[]</c> per frame is a Large Object
/// Heap allocation and a transfer at 100 MB/s makes roughly 400 a second. <c>ArrayPool</c> hands back
/// an array AT LEAST the requested size — usually a power-of-two bucket larger — so the send has to
/// be bounded by what was WRITTEN, not by the buffer.
///
/// <c>FileFrameWriteIntoPooledBufferTests</c> pins that trap at the codec level, but nothing there
/// touches the sender: swapping the bounded segment for <c>new ArraySegment&lt;byte&gt;(buffer)</c>
/// compiles, runs, and ships the tail as well. And the pool is NOT cleared on return, so that tail
/// is whatever the previous renter left — realistically bytes of an earlier frame, possibly from a
/// different file or transfer. The bound is therefore load-bearing for confidentiality, not only for
/// a well-formed frame, and neither failure announces itself. This file is what makes that swap fail.
/// </summary>
public sealed class FileChannelSendFramingTests
{
    /// <summary>
    /// Captures what is written to the socket. Only <c>SendAsync</c> is reachable from the code under
    /// test; every other member throws so an unnoticed dependency on one shows up loudly rather than
    /// silently returning a default.
    /// </summary>
    private sealed class RecordingWebSocket : WebSocket
    {
        public List<byte[]> Sent { get; } = new();
        public List<bool> EndOfMessageFlags { get; } = new();

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            // Copy: the sender returns the array to the pool the moment this completes, so keeping the
            // reference would be reading a recycled buffer — the exact bug class this test guards.
            var copy = new byte[buffer.Count];
            Array.Copy(buffer.Array!, buffer.Offset, copy, 0, buffer.Count);
            Sent.Add(copy);
            EndOfMessageFlags.Add(endOfMessage);
            return Task.CompletedTask;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => throw new NotSupportedException();
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) =>
            throw new NotSupportedException();
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) =>
            throw new NotSupportedException();
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static FileFrameEnvelope DataEnvelope(int payloadLength) => new()
    {
        Kind = FileFrameKinds.Data,
        TransferId = "tx-framing",
        Offset = 65536,
        Length = payloadLength,
        Final = false,
    };

    [Theory]
    [InlineData(1)]        // a single byte — the frame is far smaller than any pool bucket
    [InlineData(4096)]
    [InlineData(262144)]   // the 256 KB payload cap, the size this change exists for
    public async Task TheSocketReceivesExactlyTheFrameAndNotTheRentedTail(int payloadLength)
    {
        var payload = new byte[payloadLength];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 253);
        var envelope = DataEnvelope(payloadLength);

        var socket = new RecordingWebSocket();
        var channel = new TransferSessionManager.FileChannel(socket);

        await channel.SendFrameAsync(envelope, payload, CancellationToken.None);

        var sent = Assert.Single(socket.Sent);
        Assert.True(Assert.Single(socket.EndOfMessageFlags), "a frame is one complete WebSocket message");

        // The length is the assertion that matters. A rented 256 KB buffer comes back as 262144 or
        // larger, so an unbounded send would make this longer than the frame.
        var header = FileFrameCodec.SerializeHeader(envelope);
        Assert.Equal(FileFrameCodec.GetFrameLength(header.Length, payloadLength), sent.Length);

        Assert.True(FileFrameCodec.TryRead(sent, out var back, out var readPayload));
        Assert.Equal("data", back!.Kind);
        Assert.Equal("tx-framing", back.TransferId);
        Assert.True(readPayload.SequenceEqual(payload), "the payload must survive the pooled write intact");
    }

    [Fact]
    public async Task ConsecutiveFramesOnOneChannelDoNotBleedIntoEachOther()
    {
        // The buffer is returned to the pool after each send, so the NEXT frame very likely rents the
        // same array and writes over it. This proves a frame written into a RECYCLED buffer still
        // round-trips intact — 0xB2 laid over an array that held 0xA1.
        //
        // What it does NOT prove, deliberately stated so nobody assumes otherwise: it cannot detect a
        // premature Return, because RecordingWebSocket completes synchronously and copies immediately.
        // That the send is awaited before the buffer goes back is guaranteed by the finally nesting,
        // not by this test.
        var socket = new RecordingWebSocket();
        var channel = new TransferSessionManager.FileChannel(socket);

        var first = new byte[8192];
        Array.Fill(first, (byte)0xA1);
        var second = new byte[8192];
        Array.Fill(second, (byte)0xB2);

        await channel.SendFrameAsync(DataEnvelope(first.Length), first, CancellationToken.None);
        await channel.SendFrameAsync(DataEnvelope(second.Length), second, CancellationToken.None);

        Assert.Equal(2, socket.Sent.Count);
        Assert.True(FileFrameCodec.TryRead(socket.Sent[0], out _, out var firstPayload));
        Assert.True(FileFrameCodec.TryRead(socket.Sent[1], out _, out var secondPayload));

        Assert.True(firstPayload.SequenceEqual(first), "the first frame must not be disturbed by the second");
        Assert.True(secondPayload.SequenceEqual(second));
    }

    [Fact]
    public async Task ASupersededChannelSendsNothing()
    {
        // Pre-existing behaviour, pinned here because the pooled path wrapped a rent/return around it
        // and the early return has to stay ahead of the Rent. This asserts only the observable half —
        // that nothing reaches the wire. That no buffer is leaked follows from the return preceding
        // the Rent, which a test has no way to see.
        var socket = new RecordingWebSocket();
        var channel = new TransferSessionManager.FileChannel(socket);
        channel.MarkSuperseded();

        await channel.SendFrameAsync(DataEnvelope(16), new byte[16], CancellationToken.None);

        Assert.Empty(socket.Sent);
    }
}

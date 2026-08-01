using System;
using System.Buffers;
using System.Buffers.Binary;
using Remex.Core.Models;

namespace Remex.Core.Tests;

/// <summary>
/// Covers writing a <c>/ws/files</c> frame into a buffer the caller owns — the shape a POOLED sender
/// needs, where the destination is longer than the frame (RemEx-npdm).
///
/// WHY THIS IS ITS OWN FILE. At the 256 KB payload cap, allocating a fresh <c>byte[]</c> per frame is
/// a Large Object Heap allocation, and a transfer at 100 MB/s makes roughly 400 a second. Renting
/// instead is the obvious fix and brings one specific trap with it: <c>ArrayPool</c> returns an array
/// AT LEAST the requested size, usually a power-of-two bucket larger. Anything that infers the frame
/// length from the BUFFER rather than from what was written ships the tail too — and since the pool
/// is not cleared on return, that tail is whatever the PREVIOUS renter left, not zeros. It compiles,
/// it runs, and the far end reads a frame whose payload does not match its header — the same class of
/// defect that made <c>ScreenCaptureResult</c> carry a <c>ReadOnlyMemory</c> instead of a
/// <c>byte[]</c> (RemEx-hgox).
/// </summary>
public class FileFrameWriteIntoPooledBufferTests
{
    private static FileFrameEnvelope DataEnvelope(int payloadLength) => new()
    {
        Kind = FileFrameKinds.Data,
        TransferId = "tx-pooled",
        Offset = 1024,
        Length = payloadLength,
        Final = false,
    };

    [Fact]
    public void TheFrameLayoutIsLittleEndianPrefixThenHeaderThenPayload()
    {
        // Pinned against CONSTANTS, not against another function. Wrap now delegates to WriteFrame,
        // so comparing the two could never fail — it would be WriteFrame measured against itself.
        // The wire contract is what needs a fixed point, because the Kotlin client carries its own
        // independent mirror of this layout (FileTransferFrame.kt) and the two must agree.
        var payload = new byte[] { 9, 8, 7, 6, 5 };
        var envelope = DataEnvelope(payload.Length);
        var header = FileFrameCodec.SerializeHeader(envelope);

        var destination = new byte[FileFrameCodec.GetFrameLength(header.Length, payload.Length)];
        var written = FileFrameCodec.WriteFrame(header, payload, destination);

        Assert.Equal(destination.Length, written);
        Assert.Equal(header.Length, BinaryPrimitives.ReadInt32LittleEndian(destination.AsSpan(0, 4)));
        Assert.True(destination.AsSpan(4, header.Length).SequenceEqual(header));
        Assert.True(destination.AsSpan(4 + header.Length).SequenceEqual(payload));
    }

    [Fact]
    public void WrapStillProducesExactlyWhatTheWriteIntoPathDoes()
    {
        // Weaker than the layout test above and kept only as a net for a future re-inlining of Wrap:
        // today it cannot fail, because Wrap calls WriteFrame. If someone gives Wrap its own copy of
        // the three writes again, this is what notices.
        var payload = new byte[] { 1, 2, 3, 4 };
        var envelope = DataEnvelope(payload.Length);

        var expected = FileFrameCodec.Wrap(envelope, payload);

        var header = FileFrameCodec.SerializeHeader(envelope);
        var destination = new byte[FileFrameCodec.GetFrameLength(header.Length, payload.Length)];
        FileFrameCodec.WriteFrame(header, payload, destination);

        Assert.True(destination.AsSpan().SequenceEqual(expected));
    }

    [Fact]
    public void AFrameWrittenIntoAnOversizedRentedBufferRoundTripsWhenBoundedByItsWrittenLength()
    {
        // The real usage. Rent, write, then read back only what was written.
        var payload = new byte[4096];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        var envelope = DataEnvelope(payload.Length);

        var header = FileFrameCodec.SerializeHeader(envelope);
        var frameLength = FileFrameCodec.GetFrameLength(header.Length, payload.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(frameLength);
        try
        {
            var written = FileFrameCodec.WriteFrame(header, payload, buffer);
            Assert.Equal(frameLength, written);

            Assert.True(FileFrameCodec.TryRead(buffer.AsSpan(0, written), out var back, out var readPayload));
            Assert.Equal("data", back!.Kind);
            Assert.Equal("tx-pooled", back.TransferId);
            Assert.True(readPayload.SequenceEqual(payload));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [Fact]
    public void ReadingTheWholeRentedBufferInsteadOfTheWrittenLengthCorruptsThePayload()
    {
        // THE TRAP, pinned as a fact so the reason the length travels separately cannot be optimised
        // away by someone who sees a byte[] and reaches for `new ArraySegment<byte>(buffer)`.
        //
        // Note what does NOT save you: the frame still parses. The header length prefix is intact and
        // the JSON is intact, so TryRead SUCCEEDS — it just yields a payload with a tail glued on.
        // There is no exception and nothing logs.
        //
        // The tail is zeros HERE only because this destination is a fresh array. From ArrayPool,
        // which is not cleared on return, it is whatever the previous renter left — so in production
        // this is a disclosure of other frames' bytes, not merely a length error.
        var payload = new byte[] { 1, 2, 3 };
        var envelope = DataEnvelope(payload.Length);

        var header = FileFrameCodec.SerializeHeader(envelope);
        var frameLength = FileFrameCodec.GetFrameLength(header.Length, payload.Length);

        // A deliberately over-sized destination, standing in for a rented array.
        var oversized = new byte[frameLength + 64];
        FileFrameCodec.WriteFrame(header, payload, oversized);

        Assert.True(FileFrameCodec.TryRead(oversized, out var back, out var readPayload));
        Assert.Equal("data", back!.Kind);
        Assert.Equal(
            payload.Length + 64, readPayload.Length);
        Assert.False(
            readPayload.SequenceEqual(payload),
            "reading the whole buffer must visibly differ from reading the written length — if these " +
            "ever agree, the trap this test documents has stopped existing and the test should go");
    }

    [Fact]
    public void ADestinationTooSmallForTheFrameIsRejectedRatherThanTruncated()
    {
        var payload = new byte[32];
        var envelope = DataEnvelope(payload.Length);
        var header = FileFrameCodec.SerializeHeader(envelope);
        var tooSmall = new byte[FileFrameCodec.GetFrameLength(header.Length, payload.Length) - 1];

        // Silently writing a short frame would put a header on the wire that promises more bytes
        // than follow, which the far end cannot detect as truncation.
        Assert.Throws<ArgumentException>(() =>
        {
            FileFrameCodec.WriteFrame(header, payload, tooSmall);
        });
    }

    [Fact]
    public void AnEmptyPayloadStillWritesAWellFormedFrame()
    {
        // Ack and control frames carry no payload; they go through the identical pooled path.
        var envelope = new FileFrameEnvelope
        {
            Kind = FileFrameKinds.Ack,
            TransferId = "tx-ack",
            Final = true,
            CommittedOffset = 8192,
        };

        var header = FileFrameCodec.SerializeHeader(envelope);
        var frameLength = FileFrameCodec.GetFrameLength(header.Length, payloadLength: 0);
        var buffer = ArrayPool<byte>.Shared.Rent(frameLength);
        try
        {
            var written = FileFrameCodec.WriteFrame(header, ReadOnlySpan<byte>.Empty, buffer);

            Assert.Equal(frameLength, written);
            Assert.True(FileFrameCodec.TryRead(buffer.AsSpan(0, written), out var back, out var readPayload));
            Assert.Equal("ack", back!.Kind);
            Assert.Equal(8192, back.CommittedOffset);
            Assert.True(readPayload.IsEmpty);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

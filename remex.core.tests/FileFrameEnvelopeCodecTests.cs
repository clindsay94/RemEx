using System.Buffers.Binary;
using System.Text;
using Remex.Core.Models;

namespace Remex.Core.Tests;

/// <summary>
/// Round-trips the <c>/ws/files</c> binary frame layout <c>[header-length][JSON envelope][payload]</c>
/// and asserts malformed frames are rejected cleanly (never throw out of <see cref="FileFrameCodec.TryRead"/>).
/// </summary>
public class FileFrameEnvelopeCodecTests
{
    [Fact]
    public void WrapThenTryRead_DataFrame_RoundTripsHeaderAndPayload()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var envelope = new FileFrameEnvelope
        {
            Kind = FileFrameKinds.Data,
            TransferId = "tx-1",
            Offset = 262144,
            Length = payload.Length,
            Final = false,
        };

        var frame = FileFrameCodec.Wrap(envelope, payload);
        Assert.True(FileFrameCodec.TryRead(frame, out var readEnvelope, out var readPayload));

        Assert.NotNull(readEnvelope);
        Assert.Equal("data", readEnvelope!.Kind);
        Assert.Equal("tx-1", readEnvelope.TransferId);
        Assert.Equal(262144, readEnvelope.Offset);
        Assert.Equal(payload.Length, readEnvelope.Length);
        Assert.False(readEnvelope.Final);
        Assert.Null(readEnvelope.CommittedOffset);
        Assert.True(readPayload.SequenceEqual(payload));
    }

    [Fact]
    public void WrapThenTryRead_AckFrame_HasNoPayloadAndCarriesCommittedOffset()
    {
        var envelope = new FileFrameEnvelope
        {
            Kind = FileFrameKinds.Ack,
            TransferId = "tx-2",
            Final = true,
            CommittedOffset = 4_194_304,
        };

        var frame = FileFrameCodec.Wrap(envelope, ReadOnlySpan<byte>.Empty);
        Assert.True(FileFrameCodec.TryRead(frame, out var back, out var payload));

        Assert.Equal("ack", back!.Kind);
        Assert.True(back.Final);
        Assert.Equal(4_194_304, back.CommittedOffset);
        Assert.Equal(0, payload.Length);
    }

    [Fact]
    public void WrapThenTryRead_ErrorFrame_CarriesErrorText()
    {
        var envelope = new FileFrameEnvelope { Kind = FileFrameKinds.Error, TransferId = "tx-3", Error = "disk full" };
        var frame = FileFrameCodec.Wrap(envelope, ReadOnlySpan<byte>.Empty);

        Assert.True(FileFrameCodec.TryRead(frame, out var back, out _));
        Assert.Equal("error", back!.Kind);
        Assert.Equal("disk full", back.Error);
    }

    [Fact]
    public void TryRead_TruncatedPrefix_ReturnsFalse()
    {
        Assert.False(FileFrameCodec.TryRead(new byte[] { 0x01, 0x02 }, out var env, out _));
        Assert.Null(env);
    }

    [Fact]
    public void TryRead_HeaderLengthLongerThanFrame_ReturnsFalse()
    {
        var frame = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), 9999); // claims a huge header
        Assert.False(FileFrameCodec.TryRead(frame, out _, out _));
    }

    [Fact]
    public void TryRead_NegativeHeaderLength_ReturnsFalse()
    {
        var frame = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), -1);
        Assert.False(FileFrameCodec.TryRead(frame, out _, out _));
    }

    [Fact]
    public void TryRead_InvalidHeaderJson_ReturnsFalse()
    {
        var garbage = Encoding.UTF8.GetBytes("not-json{{");
        var frame = new byte[4 + garbage.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), garbage.Length);
        garbage.CopyTo(frame.AsSpan(4));

        Assert.False(FileFrameCodec.TryRead(frame, out var env, out _));
        Assert.Null(env);
    }

    [Fact]
    public void Wrap_NullEnvelope_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FileFrameCodec.Wrap(null!, ReadOnlySpan<byte>.Empty));
    }
}

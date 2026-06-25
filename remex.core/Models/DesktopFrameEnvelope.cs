using System;
using System.Buffers.Binary;

namespace Remex.Core.Models;

[Flags]
public enum DesktopFrameFlags : byte
{
    None = 0,
    KeyFrame = 1 << 0,
}

public readonly record struct DesktopFrameEnvelopeHeader(
    long StreamSerial,
    long Sequence,
    DesktopCodecKind Codec,
    DesktopFrameFlags Flags);

public static class DesktopFrameEnvelope
{
    private static ReadOnlySpan<byte> Magic => "RDXF"u8;
    public const byte Version = 1;
    public const int HeaderSize = 28;

    public static byte[] Wrap(
        ReadOnlySpan<byte> payload,
        long streamSerial,
        long sequence,
        DesktopCodecKind codec,
        DesktopFrameFlags flags = DesktopFrameFlags.None)
    {
        var buffer = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(buffer);
        buffer[4] = Version;
        buffer[5] = (byte)codec;
        buffer[6] = (byte)flags;
        buffer[7] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8, 8), streamSerial);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16, 8), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24, 4), payload.Length);
        payload.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static bool TryRead(
        ReadOnlySpan<byte> frame,
        out DesktopFrameEnvelopeHeader header,
        out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;

        if (frame.Length < HeaderSize ||
            !frame[..4].SequenceEqual(Magic) ||
            frame[4] != Version)
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(24, 4));
        if (payloadLength < 0 || frame.Length != HeaderSize + payloadLength)
        {
            return false;
        }

        header = new DesktopFrameEnvelopeHeader(
            BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(8, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(16, 8)),
            (DesktopCodecKind)frame[5],
            (DesktopFrameFlags)frame[6]);
        payload = frame[HeaderSize..];
        return true;
    }
}

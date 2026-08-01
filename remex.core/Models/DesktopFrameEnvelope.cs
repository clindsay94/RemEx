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

    /// <summary>
    /// Writes the <see cref="HeaderSize"/>-byte header for a payload of
    /// <paramref name="payloadLength"/> bytes into <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="Wrap"/> so the sender can put the header on the wire WITHOUT first
    /// copying the whole access unit behind it. At full frame rate that copy was the payload size
    /// again for every frame sent, to prepend 28 bytes (RemEx-41xu).
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="HeaderSize"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payloadLength"/> is negative.</exception>
    public static void WriteHeader(
        Span<byte> destination,
        int payloadLength,
        long streamSerial,
        long sequence,
        DesktopCodecKind codec,
        DesktopFrameFlags flags = DesktopFrameFlags.None)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException(
                $"A frame header needs {HeaderSize} bytes; got {destination.Length}.", nameof(destination));
        }

        Magic.CopyTo(destination);
        destination[4] = Version;
        destination[5] = (byte)codec;
        destination[6] = (byte)flags;
        destination[7] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(8, 8), streamSerial);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(16, 8), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(24, 4), payloadLength);
    }

    /// <summary>
    /// Builds one contiguous header + payload buffer.
    /// </summary>
    /// <remarks>
    /// The streaming sender uses <see cref="WriteHeader"/> and two WebSocket fragments instead, to
    /// avoid copying the payload. This remains for callers that genuinely need one array — and it is
    /// what <see cref="TryRead"/> is tested against, so the two halves stay provably consistent.
    /// </remarks>
    public static byte[] Wrap(
        ReadOnlySpan<byte> payload,
        long streamSerial,
        long sequence,
        DesktopCodecKind codec,
        DesktopFrameFlags flags = DesktopFrameFlags.None)
    {
        var buffer = new byte[HeaderSize + payload.Length];
        WriteHeader(buffer, payload.Length, streamSerial, sequence, codec, flags);
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

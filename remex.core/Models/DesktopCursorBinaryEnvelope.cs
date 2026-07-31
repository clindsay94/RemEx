using System.Buffers.Binary;

namespace Remex.Core.Models;

/// <summary>
/// Compact binary envelope for high-frequency cursor POSITION updates, sent over the same
/// <c>/ws/desktop</c> binary channel as H.264 frames and demultiplexed by its 4-byte magic ("RDXC").
///
/// RD-E (Gemini #3): cursor position streams 60–90×/s. Sending it as a JSON <c>RemexMessage</c> (and, on
/// the client, re-serializing it to a JSON string across JNI before <c>JSONObject</c>-parsing it) churned
/// the GC on both the .NET host and the Android JVM, causing micro-stutter. This fixed 32-byte packet is
/// written/parsed with <see cref="BinaryPrimitives"/> — zero JSON, zero per-message string allocation.
/// The cursor SHAPE (rare, large) stays JSON; only the hot position/visibility/shape-serial travels here.
///
/// Magic "RDXC" is distinct from the H.264 frame envelope ("RDXF") AND from a raw Annex-B NAL start code
/// (<c>00 00 00 01</c>), so the receiver can demux purely on the leading bytes regardless of whether the
/// frame envelope is in use. NativeAOT-safe (BinaryPrimitives + spans, no reflection).
///
/// Layout (little-endian): <c>magic[4] version[1] flags[1] reserved[2] X[int32] Y[int32]
/// shapeSerial[int64] streamSerial[int64]</c> = 32 bytes. X/Y are SIGNED so monitors with a negative
/// virtual-desktop origin (left of / above primary) are representable.
/// </summary>
public static class DesktopCursorBinaryEnvelope
{
    private static ReadOnlySpan<byte> Magic => "RDXC"u8;
    public const byte Version = 1;
    public const int Size = 32;

    private const byte FlagVisible = 1 << 0;

    /// <summary>True when <paramref name="data"/> begins with the cursor-packet magic ("RDXC").</summary>
    public static bool HasMagic(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data[..4].SequenceEqual(Magic);

    /// <summary>Packs a 32-byte cursor position packet.</summary>
    public static byte[] Write(int x, int y, bool visible, long shapeSerial, long streamSerial)
    {
        var buffer = new byte[Size];
        Magic.CopyTo(buffer);
        buffer[4] = Version;
        buffer[5] = visible ? FlagVisible : (byte)0;
        buffer[6] = 0;
        buffer[7] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), x);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12, 4), y);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16, 8), shapeSerial);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24, 8), streamSerial);
        return buffer;
    }

    /// <summary>
    /// Reads a cursor packet. Returns <c>false</c> (and leaves outputs at defaults) when the buffer is
    /// not a valid current-version cursor packet — the caller should then treat it as a video frame.
    /// </summary>
    public static bool TryRead(
        ReadOnlySpan<byte> data,
        out int x, out int y, out bool visible, out long shapeSerial, out long streamSerial)
    {
        x = 0;
        y = 0;
        visible = false;
        shapeSerial = 0;
        streamSerial = 0;

        if (data.Length < Size || !data[..4].SequenceEqual(Magic) || data[4] != Version)
        {
            return false;
        }

        visible = (data[5] & FlagVisible) != 0;
        x = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8, 4));
        y = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(12, 4));
        shapeSerial = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(16, 8));
        streamSerial = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(24, 8));
        return true;
    }
}

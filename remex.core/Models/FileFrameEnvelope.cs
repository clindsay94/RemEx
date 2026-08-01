using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Remex.Core.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// JSON header of a binary frame on the dedicated <c>/ws/files</c> channel (plan §1.1). Bulk chunk
/// data and acks flow over this channel; the control plane (browse/manage/consent/negotiation) stays
/// JSON on <c>/ws</c>. Modeled on the proven <see cref="DesktopFrameEnvelope"/> pattern, but the header
/// is length-prefixed JSON (rather than a fixed binary struct) so it can grow additively.
/// </summary>
/// <remarks>
/// Wire layout of one binary WebSocket message:
/// <c>[header-length: int32 little-endian][UTF-8 JSON FileFrameEnvelope][raw payload]</c>.
/// <para>
/// This type is serialized via the source-generated <see cref="RemexJsonSerializerContext"/> so it stays
/// NativeAOT-safe. It MUST have a <c>[JsonSerializable]</c> entry in that context.
/// </para>
/// </remarks>
public sealed record FileFrameEnvelope
{
    /// <summary>Frame kind: "data" | "ack" | "error" (see <see cref="FileFrameKinds"/>).</summary>
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    /// <summary>Byte offset of this data frame's payload within the file. Ignored for ack/error frames.</summary>
    [JsonPropertyName("offset")] public long Offset { get; init; }
    /// <summary>Length of the raw payload following the header. Ignored for ack/error frames.</summary>
    [JsonPropertyName("length")] public int Length { get; init; }
    /// <summary>True on the final data frame of a stream.</summary>
    [JsonPropertyName("final")] public bool Final { get; init; }
    /// <summary>On an ack frame, the highest contiguous byte offset the receiver has durably committed.</summary>
    [JsonPropertyName("committedOffset")] public long? CommittedOffset { get; init; }
    /// <summary>On an error frame, a human-readable reason.</summary>
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary><see cref="FileFrameEnvelope.Kind"/> string values.</summary>
public static class FileFrameKinds
{
    public const string Data = "data";
    public const string Ack = "ack";
    public const string Error = "error";
}

/// <summary>
/// Encodes/decodes binary frames for the <c>/ws/files</c> channel. Pure buffer math plus source-gen JSON —
/// no reflection, so it is safe on the NativeAOT <c>Remex.Core</c> surface.
/// </summary>
public static class FileFrameCodec
{
    /// <summary>Size of the leading header-length prefix, in bytes.</summary>
    public const int HeaderLengthPrefixSize = sizeof(int);

    /// <summary>
    /// Serializes the envelope's UTF-8 JSON header on its own, so a caller can size a destination
    /// buffer before writing the frame into it.
    /// </summary>
    /// <remarks>
    /// Split out because the header's length is not knowable until it is serialized, and a sender
    /// that wants to write into a POOLED buffer has to know the total frame size first. Serializing
    /// twice to find out would defeat the point (RemEx-npdm).
    /// </remarks>
    public static byte[] SerializeHeader(FileFrameEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return RemexJson.SerializeToUtf8Bytes(envelope, RemexJsonSerializerContext.Default.FileFrameEnvelope);
    }

    /// <summary>
    /// Total frame size for a header and payload of the given lengths.
    /// </summary>
    public static int GetFrameLength(int headerLength, int payloadLength) =>
        HeaderLengthPrefixSize + headerLength + payloadLength;

    /// <summary>
    /// Writes <c>[header-length][UTF-8 JSON envelope][payload]</c> into <paramref name="destination"/>
    /// and returns the number of bytes written.
    /// </summary>
    /// <remarks>
    /// THE SINGLE DEFINITION OF THE FRAME LAYOUT. <see cref="Wrap"/> calls this rather than repeating
    /// the three writes — it is now test-only (the host's sender writes into a pooled buffer through
    /// here), and it is kept as the reference implementation the pooled path is measured against.
    /// <paramref name="destination"/> may be LONGER than the frame (a pooled array is), which is why
    /// the written length is RETURNED rather than inferred from the buffer: a caller that bounds on
    /// the buffer instead ships whatever the previous renter of that array left behind.
    /// </remarks>
    public static int WriteFrame(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        var frameLength = GetFrameLength(header.Length, payload.Length);
        if (destination.Length < frameLength)
            throw new ArgumentException(
                $"Destination is {destination.Length} bytes; the frame needs {frameLength}.", nameof(destination));

        BinaryPrimitives.WriteInt32LittleEndian(destination[..HeaderLengthPrefixSize], header.Length);
        header.CopyTo(destination[HeaderLengthPrefixSize..]);
        payload.CopyTo(destination[(HeaderLengthPrefixSize + header.Length)..]);
        return frameLength;
    }

    /// <summary>
    /// Wraps a <see cref="FileFrameEnvelope"/> and its (optional) raw payload into a single binary frame:
    /// <c>[header-length][UTF-8 JSON envelope][payload]</c>.
    /// </summary>
    public static byte[] Wrap(FileFrameEnvelope envelope, ReadOnlySpan<byte> payload)
    {
        var headerBytes = SerializeHeader(envelope);
        var frame = new byte[GetFrameLength(headerBytes.Length, payload.Length)];
        WriteFrame(headerBytes, payload, frame);
        return frame;
    }

    /// <summary>
    /// Reads a binary frame produced by <see cref="Wrap"/>, yielding the payload as
    /// <see cref="ReadOnlyMemory{T}"/> so it can be passed ACROSS AN AWAIT.
    /// </summary>
    /// <remarks>
    /// The span overload cannot serve an async consumer at all: <c>ReadOnlySpan</c> is a
    /// <c>ref struct</c> and may not live across an <c>await</c>, which is the ONLY reason the
    /// <c>/ws/files</c> receive loop used to copy every payload with <c>ToArray()</c> before handing
    /// it on. That copy was never defending anything — it was working around the language — and at
    /// the 256 KB frame cap it was a Large Object Heap allocation per frame (RemEx-8su9).
    ///
    /// The payload is a VIEW into <paramref name="frame"/>, so the caller owns the lifetime: it must
    /// not reuse the frame buffer until every consumer of the payload has completed. The receive
    /// loop satisfies that by awaiting its handler before receiving again.
    ///
    /// Parsing is delegated to the span overload rather than repeated, so the two cannot disagree
    /// about what a valid frame is.
    /// </remarks>
    public static bool TryRead(
        ReadOnlyMemory<byte> frame,
        out FileFrameEnvelope? envelope,
        out ReadOnlyMemory<byte> payload)
    {
        payload = default;
        if (!TryRead(frame.Span, out envelope, out var payloadSpan))
            return false;

        // The payload always sits at the TAIL — the span overload slices to the end of the frame —
        // so its offset is derivable from the lengths without pointer arithmetic on the span.
        //
        // Nothing enforces that invariant structurally: if the format ever grew a trailer, this would
        // point at the wrong offset while both overloads still reported success. The parity tests in
        // FileFrameWriteIntoPooledBufferTests cover it at three payload lengths including zero; a
        // trailer would mean giving the parse a private core that also yields the payload offset.
        payload = frame[(frame.Length - payloadSpan.Length)..];
        return true;
    }

    /// <summary>
    /// Reads a binary frame produced by <see cref="Wrap"/>. Returns false (without throwing) for any
    /// malformed frame — a truncated prefix, an out-of-range header length, or invalid header JSON.
    /// On success, <paramref name="payload"/> is a view into <paramref name="frame"/> (no copy).
    /// </summary>
    public static bool TryRead(
        ReadOnlySpan<byte> frame,
        out FileFrameEnvelope? envelope,
        out ReadOnlySpan<byte> payload)
    {
        envelope = null;
        payload = default;

        if (frame.Length < HeaderLengthPrefixSize)
            return false;

        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(0, HeaderLengthPrefixSize));
        if (headerLength < 0 || HeaderLengthPrefixSize + (long)headerLength > frame.Length)
            return false;

        var headerSpan = frame.Slice(HeaderLengthPrefixSize, headerLength);
        try
        {
            envelope = RemexJson.Deserialize(headerSpan, RemexJsonSerializerContext.Default.FileFrameEnvelope);
        }
        catch (JsonException)
        {
            envelope = null;
            return false;
        }

        if (envelope is null)
            return false;

        payload = frame[(HeaderLengthPrefixSize + headerLength)..];
        return true;
    }
}

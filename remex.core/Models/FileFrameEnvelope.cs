using System;
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
    /// Wraps a <see cref="FileFrameEnvelope"/> and its (optional) raw payload into a single binary frame:
    /// <c>[header-length][UTF-8 JSON envelope][payload]</c>.
    /// </summary>
    public static byte[] Wrap(FileFrameEnvelope envelope, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var headerBytes = RemexJson.SerializeToUtf8Bytes(envelope, RemexJsonSerializerContext.Default.FileFrameEnvelope);
        var frame = new byte[HeaderLengthPrefixSize + headerBytes.Length + payload.Length];

        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, HeaderLengthPrefixSize), headerBytes.Length);
        headerBytes.CopyTo(frame.AsSpan(HeaderLengthPrefixSize));
        payload.CopyTo(frame.AsSpan(HeaderLengthPrefixSize + headerBytes.Length));
        return frame;
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

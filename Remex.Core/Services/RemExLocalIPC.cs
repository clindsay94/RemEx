using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;

namespace Remex.Core.Services;

/// <summary>
/// A shared interface for IPC communication between the UI Client and the Host Service.
/// This handles local UI-to-Service delegation over a Named Pipe.
///
/// Wire format is a length-prefixed frame: a 4-byte big-endian unsigned payload length followed by
/// exactly that many UTF-8 JSON bytes. The length is validated against <see cref="MaxMessageBytes"/>
/// before any allocation, so a hostile or corrupt peer cannot trigger an unbounded allocation (DoS).
/// Both the server (<c>LocalIpcServerService</c>) and every client use <see cref="WriteFrameAsync"/> /
/// <see cref="ReadFrameAsync"/> so the framing stays in one place.
/// </summary>
public static class RemExLocalIPC
{
    /// <summary>
    /// The single canonical local IPC pipe name. All UI-to-service local IPC (power commands,
    /// Wake-on-LAN, pairing-PIN queries, app launching) flows over this one pipe.
    /// </summary>
    public const string PipeName = "RemExLocalIPC";

    /// <summary>
    /// Upper bound on a single framed message body. IPC payloads are small JSON command envelopes;
    /// 1 MiB is far more than any legitimate request or response needs and caps the allocation an
    /// untrusted length prefix can request.
    /// </summary>
    public const int MaxMessageBytes = 1024 * 1024;

    private const int LengthPrefixBytes = 4;

    /// <summary>
    /// Writes a single length-prefixed frame: a 4-byte big-endian length followed by the UTF-8 payload.
    /// </summary>
    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException($"IPC message of {payload.Length} bytes exceeds the {MaxMessageBytes}-byte limit.");
        }

        var header = new byte[LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Reads a single length-prefixed frame. Returns <c>null</c> when the peer closes the stream before
    /// sending a full frame (clean disconnect). Throws <see cref="InvalidDataException"/> when the
    /// length prefix is zero or exceeds <see cref="MaxMessageBytes"/>, rejecting oversize frames before
    /// allocating the payload buffer.
    /// </summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[LengthPrefixBytes];
        if (!await TryReadExactlyAsync(stream, header, cancellationToken))
        {
            return null;
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException($"IPC frame length {length} is invalid (must be 1..{MaxMessageBytes}).");
        }

        var payload = new byte[length];
        if (!await TryReadExactlyAsync(stream, payload, cancellationToken))
        {
            // Peer disconnected mid-frame; treat as a truncated/invalid message rather than a clean close.
            throw new InvalidDataException("IPC peer closed the connection before the full frame was received.");
        }

        return payload;
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> from <paramref name="stream"/>. Returns <c>false</c> only when
    /// the very first read returns 0 (clean disconnect before any byte of this read); a partial read
    /// followed by a close throws via <see cref="ReadFrameAsync"/>'s caller path.
    /// </summary>
    private static async Task<bool> TryReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
            {
                return total != 0
                    ? throw new InvalidDataException("IPC stream ended mid-frame.")
                    : false;
            }

            total += read;
        }

        return true;
    }

    /// <summary>
    /// Sends a command to the IPC Named Pipe server and awaits the framed response.
    /// </summary>
    public static async Task<CommandResponse> SendCommandAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, cancellationToken);

            var requestBytes = RemexJson.SerializeToUtf8Bytes(request, RemexJsonSerializerContext.Default.CommandRequest);
            await WriteFrameAsync(client, requestBytes, cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var responseBytes = await ReadFrameAsync(client, cts.Token);

            if (responseBytes == null)
            {
                return new CommandResponse(false, "No response from service.", null);
            }

            return RemexJson.Deserialize(responseBytes, RemexJsonSerializerContext.Default.CommandResponse)
                ?? new CommandResponse(false, "Failed to deserialize response.", null);
        }
        catch (Exception ex)
        {
            return new CommandResponse(false, $"IPC Error: {ex.Message}", ex.ToString());
        }
    }
}

using System.Net.WebSockets;
using Remex.Core.Serialization;

namespace Remex.Core.Messages;

/// <summary>
/// Helpers for serializing/deserializing <see cref="RemexMessage"/> over WebSockets.
/// </summary>
public static class MessageSerializer
{
    /// <summary>
    /// Serialize a message to a UTF-8 JSON byte array.
    /// </summary>
    public static byte[] Serialize(RemexMessage message)
        => RemexJson.SerializeToUtf8Bytes(message, RemexJsonSerializerContext.Default.RemexMessage);

    /// <summary>
    /// Deserialize a UTF-8 JSON byte span into a <see cref="RemexMessage"/>.
    /// Returns null if deserialization fails.
    /// </summary>
    public static RemexMessage? Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return RemexJson.Deserialize(utf8Json, RemexJsonSerializerContext.Default.RemexMessage);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Send a <see cref="RemexMessage"/> over a WebSocket connection.
    /// </summary>
    // A WebSocket permits only ONE outstanding SendAsync at a time. The host sends to a single socket
    // from multiple concurrent producers (the telemetry stream, the control reader loop, and — since the
    // consent-gated handlers run off the reader loop — deferred file responses). Without serialization,
    // overlapping sends throw InvalidOperationException and desync/drop the connection. The gate is keyed
    // weakly on the socket so it is collected together with the socket; no per-connection plumbing needed.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, SemaphoreSlim> SendGates = new();

    public static async Task SendAsync(
        WebSocket webSocket,
        RemexMessage message,
        CancellationToken ct = default)
    {
        var bytes = Serialize(message);
        var gate = SendGates.GetValue(webSocket, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Sends an already-serialized <see cref="RemexMessage"/>.
    /// </summary>
    /// <remarks>
    /// For a message that goes to several sockets unchanged — telemetry is the one that matters, at
    /// 60-100 KB per tick — so the JSON is produced once by the producer instead of once per
    /// recipient (RemEx-0zbj). Shares <see cref="SendAsync"/>'s per-socket gate, because the
    /// one-outstanding-send rule is a property of the socket and does not care where the bytes came
    /// from. Callers must not mutate <paramref name="frame"/> after handing it over; it is shared.
    /// </remarks>
    public static async Task SendRawAsync(
        WebSocket webSocket,
        ReadOnlyMemory<byte> frame,
        CancellationToken ct = default)
    {
        var gate = SendGates.GetValue(webSocket, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await webSocket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private const int MaxMessageSize = 4 * 1024 * 1024; // 4 MB safety limit

    /// <summary>
    /// Size of the scratch buffer each receive rents. Sized so the messages that actually dominate
    /// this path arrive in ONE frame and take the no-copy path below: input events are a few hundred
    /// bytes, and the largest routine message is a ~87 KB v2 file chunk, which at the previous 4 KB
    /// took 22 reads and around six doubling reallocations of the accumulator.
    /// </summary>
    private const int ReceiveChunkSize = 32 * 1024;

    /// <summary>
    /// Receive a single <see cref="RemexMessage"/> from a WebSocket connection.
    /// Returns null if the socket closed or the message was invalid.
    /// </summary>
    /// <remarks>
    /// Allocation-conscious on purpose: this runs per inbound message, and remote-desktop input rides
    /// full messages, so it is effectively per mouse event. The scratch buffer is pooled, and a
    /// message that arrives in a single frame — which is nearly all of them — is deserialized straight
    /// out of that buffer with no accumulator and no copy at all. Only a genuinely multi-frame message
    /// allocates the <see cref="System.IO.MemoryStream"/>, and even then the final copy that
    /// <c>ToArray</c> used to make is gone (RemEx-tfi6).
    /// </remarks>
    public static async Task<RemexMessage?> ReceiveAsync(
        WebSocket webSocket,
        CancellationToken ct = default)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ReceiveChunkSize);

        // Created only if the message spans more than one frame; see the fast path below.
        System.IO.MemoryStream? accumulator = null;
        var oversize = false;

        // Every receive writes from offset 0, so this is the only part of the rented buffer that ever
        // held message bytes — and therefore the only part worth clearing on the way out.
        var bytesTouched = 0;

        try
        {
            System.Net.WebSockets.WebSocketReceiveResult result;

            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                if (result.Count > bytesTouched)
                    bytesTouched = result.Count;

                if (oversize)
                {
                    // Already over the cap: keep reading and discarding the remaining frames so the
                    // socket stays in a clean, framed state for the next message instead of leaving a
                    // half-consumed oversize message that would desync the protocol. We deliberately do
                    // NOT throw — a single oversize message must not crash the /ws receive loop.
                    continue;
                }

                // Only grow the buffer while we're still under the cap. Once we cross it we stop
                // accumulating (to bound memory) but continue draining the frames above.
                if ((accumulator?.Length ?? 0) + result.Count > MaxMessageSize)
                {
                    oversize = true;
                    continue;
                }

                // Single-frame message: the whole payload is already contiguous in the rented buffer,
                // so parse it in place. Deserialize copies out everything it keeps, so nothing
                // survives the buffer going back to the pool in the finally.
                if (result.EndOfMessage && accumulator is null)
                    return Deserialize(buffer.AsSpan(0, result.Count));

                accumulator ??= new System.IO.MemoryStream(ReceiveChunkSize * 2);
                accumulator.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            // Oversize message: the frames have been drained, so the socket is ready for the next
            // message. Treat it as an invalid message (null) rather than throwing.
            if (oversize || accumulator is null)
                return null;

            // GetBuffer over ToArray: the accumulator's backing array is already exactly the bytes we
            // want, and ToArray duplicated it — for an 87 KB chunk that was a second Large Object Heap
            // allocation per message. The span is only valid until the stream is disposed, which is
            // fine because Deserialize is synchronous and materializes everything it returns.
            return Deserialize(new ReadOnlySpan<byte>(accumulator.GetBuffer(), 0, (int)accumulator.Length));
        }
        finally
        {
            accumulator?.Dispose();

            // Wiped before release, not just released: pairing PINs, ECDH key material and file bytes
            // all transit this buffer, and ArrayPool.Shared is process-wide, so an uncleared buffer
            // hands those bytes to whatever component rents next. The GC heap needs no equivalent
            // treatment — the CLR zeroes memory before handing it to a new allocation, so the
            // accumulator below is not an exposure the way a pooled array is.
            //
            // Only the touched prefix is cleared rather than passing clearArray: true, which would wipe
            // the full 32 KB. On the input path a message is a few hundred bytes, so this is ~130x less
            // work per mouse event for identical secrecy.
            if (bytesTouched > 0)
                Array.Clear(buffer, 0, bytesTouched);
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

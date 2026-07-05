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
    public static async Task SendAsync(
        WebSocket webSocket,
        RemexMessage message,
        CancellationToken ct = default)
    {
        var bytes = Serialize(message);
        await webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct);
    }

    private const int MaxMessageSize = 4 * 1024 * 1024; // 4 MB safety limit

    /// <summary>
    /// Receive a single <see cref="RemexMessage"/> from a WebSocket connection.
    /// Returns null if the socket closed or the message was invalid.
    /// </summary>
    public static async Task<RemexMessage?> ReceiveAsync(
        WebSocket webSocket,
        CancellationToken ct = default)
    {
        using var ms = new System.IO.MemoryStream();
        var buffer = new byte[4096];
        System.Net.WebSockets.WebSocketReceiveResult result;
        var oversize = false;

        do
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

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
            if (ms.Length + result.Count > MaxMessageSize)
            {
                oversize = true;
                continue;
            }

            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        // Oversize message: the frames have been drained, so the socket is ready for the next
        // message. Treat it as an invalid message (null) rather than throwing.
        if (oversize)
            return null;

        return Deserialize(ms.ToArray());
    }
}

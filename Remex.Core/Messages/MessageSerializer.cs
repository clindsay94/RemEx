using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Remex.Core.Messages;

/// <summary>
/// Helpers for serializing/deserializing <see cref="RemexMessage"/> over WebSockets.
/// </summary>
public static class MessageSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Serialize a message to a UTF-8 JSON byte array.
    /// </summary>
    public static byte[] Serialize(RemexMessage message)
        => JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

    /// <summary>
    /// Deserialize a UTF-8 JSON byte span into a <see cref="RemexMessage"/>.
    /// Returns null if deserialization fails.
    /// </summary>
    public static RemexMessage? Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<RemexMessage>(utf8Json, JsonOptions);
        }
        catch (JsonException)
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

        do
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            ms.Write(buffer, 0, result.Count);
        } 
        while (!result.EndOfMessage);

        return Deserialize(ms.ToArray());
    }
}

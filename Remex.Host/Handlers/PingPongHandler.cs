using System.Net.WebSockets;
using Remex.Core.Messages;

namespace Remex.Host.Handlers;

/// <summary>
/// Handles a single WebSocket client session.
/// Responds to "ping" with "pong", echoing the client's timestamp for latency measurement.
/// </summary>
public sealed class PingPongHandler(ILogger<PingPongHandler> logger)
{
    public async Task HandleAsync(WebSocket webSocket, CancellationToken ct)
    {
        logger.LogInformation("Client connected.");

        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = await MessageSerializer.ReceiveAsync(webSocket, ct);

                if (message is null)
                {
                    // Client disconnected or sent invalid data.
                    break;
                }

                logger.LogDebug("Received: {Type}", message.Type);

                switch (message.Type)
                {
                    case MessageTypes.Ping:
                        var pong = new RemexMessage
                        {
                            Type = MessageTypes.Pong,
                            Timestamp = message.Timestamp  // Echo back sender's timestamp.
                        };
                        await MessageSerializer.SendAsync(webSocket, pong, ct);
                        logger.LogDebug("Sent pong.");
                        break;

                    default:
                        logger.LogWarning("Unknown message type: {Type}", message.Type);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "WebSocket error.");
        }

        if (webSocket.State == WebSocketState.Open)
        {
            await webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Server shutting down",
                CancellationToken.None);
        }

        logger.LogInformation("Client disconnected.");
    }
}

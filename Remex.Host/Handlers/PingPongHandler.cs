using System.Net.WebSockets;
using Remex.Core.Messages;
using Remex.Core.Services;

namespace Remex.Host.Handlers;

/// <summary>
/// Handles a single WebSocket client session.
/// Responds to "ping" with "pong", echoing the client's timestamp for latency measurement.
/// Background streams telemetry data while the connection is established.
/// </summary>
public sealed class PingPongHandler(ILogger<PingPongHandler> logger, ITelemetryService telemetryService)
{
    public async Task HandleAsync(WebSocket webSocket, CancellationToken ct)
    {
        logger.LogInformation("Client connected.");

        // Start background telemetry stream
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamTask = StreamTelemetryAsync(webSocket, streamCts.Token);

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

        // Cancel background stream
        streamCts.Cancel();
        try { await streamTask; } catch { /* Ignore stream cancellation errors */ }

        if (webSocket.State == WebSocketState.Open)
        {
            await webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Server shutting down",
                CancellationToken.None);
        }

        logger.LogInformation("Client disconnected.");
    }

    private async Task StreamTelemetryAsync(WebSocket webSocket, CancellationToken ct)
    {
        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var payload = await telemetryService.GetTelemetryAsync(ct);
                var message = new RemexMessage
                {
                    Type = MessageTypes.Telemetry,
                    Telemetry = payload,
                    Timestamp = System.Diagnostics.Stopwatch.GetTimestamp()
                };

                await MessageSerializer.SendAsync(webSocket, message, ct);

                // Assuming 1-second ticks as defined in instructions/impl generally
                await Task.Delay(1000, ct); 
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "Telemetry stream halted.");
        }
    }
}

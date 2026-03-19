using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Client.Services.Network;

public class RemoteDesktopService : IDisposable
{
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public event Action<byte[]>? FrameReceived;
    public event Action<DesktopMeta>? MetaReceived;
    public event Action? Disconnected;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public async Task ConnectAsync(string hostAddress, string accessKey, CancellationToken ct = default)
    {
        Disconnect();

        // Derive the desktop WS URL from the main host address
        // e.g. "ws://localhost:5005/ws" → "ws://localhost:5005/ws/desktop"
        var desktopUrl = hostAddress.TrimEnd('/');
        if (desktopUrl.EndsWith("/ws"))
            desktopUrl += "/desktop";
        else
            desktopUrl += "/ws/desktop";

        // Append auth key as query parameter for maximum platform compatibility
        if (!string.IsNullOrEmpty(accessKey))
        {
            desktopUrl += $"?auth={Uri.EscapeDataString(accessKey)}";
        }

        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(new Uri(desktopUrl), ct);

        _receiveCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    public async Task StartStreamAsync(DesktopConfig config, CancellationToken ct = default)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        var msg = new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = config,
        };
        await SendJsonAsync(msg, ct);
    }

    public async Task StopStreamAsync(CancellationToken ct = default)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        var msg = new RemexMessage { Type = MessageTypes.DesktopStop };
        await SendJsonAsync(msg, ct);
    }

    public async Task SendInputAsync(InputEvent input, CancellationToken ct = default)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        var msg = new RemexMessage
        {
            Type = MessageTypes.DesktopInput,
            InputEvent = input,
        };
        await SendJsonAsync(msg, ct);
    }

    public async Task SendConfigAsync(DesktopConfig config, CancellationToken ct = default)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        var msg = new RemexMessage
        {
            Type = MessageTypes.DesktopConfig,
            DesktopConfig = config,
        };
        await SendJsonAsync(msg, ct);
    }

    public void Disconnect()
    {
        _receiveCts?.Cancel();

        var socket = _webSocket;
        _webSocket = null;

        var receiveCts = _receiveCts;
        _receiveCts = null;

        if (socket is not null)
        {
            Task.Run(async () =>
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        try
                        {
                            await socket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "Client disconnect",
                                CancellationToken.None);
                        }
                        catch
                        {
                            // best effort
                        }
                    }
                }
                finally
                {
                    socket.Dispose();
                }
            });
        }

        receiveCts?.Dispose();
    }

    private async Task SendJsonAsync(RemexMessage message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await _webSocket!.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 256]; // 256KB buffer for frames

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Binary = JPEG frame
                    FrameReceived?.Invoke(ms.ToArray());
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    // Text = JSON control message
                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    var msg = JsonSerializer.Deserialize<RemexMessage>(json, JsonOptions);

                    if (msg?.Type == MessageTypes.DesktopMeta && msg.DesktopMeta is not null)
                    {
                        MetaReceived?.Invoke(msg.DesktopMeta);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (WebSocketException) { /* connection lost */ }
        catch (ObjectDisposedException) { /* socket disposed during shutdown */ }
        finally
        {
            Disconnected?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}

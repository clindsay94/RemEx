using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;

namespace Remex.Core.Native;

public sealed class RemexDesktopClient : IDisposable
{
    private static readonly Lazy<RemexDesktopClient> Instance = new(() => new RemexDesktopClient());
    public static RemexDesktopClient Current => Instance.Value;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoopTask;

    public event Action<byte[]>? FrameReceived;
    public event Action<DesktopMeta>? MetaReceived;
    public event Action<string>? ErrorReceived;

    private RemexDesktopClient() { }

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        await DisconnectAsync();

        var wsUri = new Uri($"ws://{host}:{port}{RemexConstants.WebSocketPath}/desktop");
        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(wsUri, ct);

        _receiveCts = new CancellationTokenSource();
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    public async Task DisconnectAsync()
    {
        _receiveCts?.Cancel();
        if (_receiveLoopTask != null)
        {
            try { await _receiveLoopTask; } catch { }
        }

        if (_webSocket != null)
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                try { await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None); } catch { }
            }
            _webSocket.Dispose();
            _webSocket = null;
        }

        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveLoopTask = null;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 256);
        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                using var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    FrameReceived?.Invoke(ms.ToArray());
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    var msg = RemexJson.Deserialize(ms.ToArray(), RemexJsonSerializerContext.Default.RemexMessage);
                    if (msg?.Type == MessageTypes.DesktopMeta && msg.DesktopMeta != null)
                    {
                        MetaReceived?.Invoke(msg.DesktopMeta);
                    }
                    else if (msg?.Type == MessageTypes.DesktopError)
                    {
                        ErrorReceived?.Invoke(msg.ErrorText ?? "Unknown desktop error");
                    }
                }
            }
        }
        catch { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }
}

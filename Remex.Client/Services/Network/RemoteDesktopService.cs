using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Client.Services.Network;

public class RemoteDesktopService : IDisposable
{
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DesktopWindowResult>> _pendingWindowRequests = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public event Action<byte[]>? FrameReceived;
    public event Action<DesktopMeta>? MetaReceived;
    public event Action<string>? ErrorReceived;
    public event Action<DesktopWindowResult>? WindowResultReceived;
    public event Action? Disconnected;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public async Task ConnectAsync(string hostAddress, CancellationToken ct = default)
    {
        Disconnect();

        // Derive the desktop WS URL from the main host address
        // e.g. "wss://localhost:5005/ws" → "wss://localhost:5005/ws/desktop"
        var desktopUrl = hostAddress.TrimEnd('/');
        if (desktopUrl.EndsWith("/ws"))
            desktopUrl += "/desktop";
        else
            desktopUrl += "/ws/desktop";

        _webSocket = new ClientWebSocket();
        // 2.0: Accept self-signed certificates
        _webSocket.Options.RemoteCertificateValidationCallback = AcceptSelfSignedCertificate;
        await _webSocket.ConnectAsync(new Uri(desktopUrl), ct);

        _receiveCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    public async Task StartStreamAsync(DesktopConfig config, CancellationToken ct = default)
    {
        var msg = new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = config,
        };
        await SendJsonAsync(msg, ct);
    }

    public async Task StopStreamAsync(CancellationToken ct = default)
    {
        var msg = new RemexMessage { Type = MessageTypes.DesktopStop };
        await SendJsonAsync(msg, ct);
    }

    public async Task SendInputAsync(InputEvent input, CancellationToken ct = default)
    {
        var msg = new RemexMessage
        {
            Type = MessageTypes.DesktopInput,
            InputEvent = input,
        };
        await SendJsonAsync(msg, ct);
    }

    public async Task SendConfigAsync(DesktopConfig config, CancellationToken ct = default)
    {
        var msg = new RemexMessage
        {
            Type = MessageTypes.DesktopConfig,
            DesktopConfig = config,
        };
        await SendJsonAsync(msg, ct);
    }

    public async Task<DesktopWindowResult> QueryWindowsAsync(DesktopWindowQuery query, CancellationToken ct = default)
    {
        var requestId = string.IsNullOrWhiteSpace(query.RequestId) ? Guid.NewGuid().ToString("N") : query.RequestId;
        var tcs = new TaskCompletionSource<DesktopWindowResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingWindowRequests[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        try
        {
            await SendJsonAsync(new RemexMessage
            {
                Type = MessageTypes.DesktopWindowQuery,
                CorrelationId = requestId,
                DesktopWindowQuery = query with { RequestId = requestId },
            }, ct);

            return await tcs.Task;
        }
        finally
        {
            _pendingWindowRequests.TryRemove(requestId, out _);
        }
    }

    public async Task<DesktopWindowResult> ExecuteWindowActionAsync(DesktopWindowAction action, CancellationToken ct = default)
    {
        var requestId = string.IsNullOrWhiteSpace(action.RequestId) ? Guid.NewGuid().ToString("N") : action.RequestId;
        var tcs = new TaskCompletionSource<DesktopWindowResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingWindowRequests[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        try
        {
            await SendJsonAsync(new RemexMessage
            {
                Type = MessageTypes.DesktopWindowAction,
                CorrelationId = requestId,
                DesktopWindowAction = action with { RequestId = requestId },
            }, ct);

            return await tcs.Task;
        }
        finally
        {
            _pendingWindowRequests.TryRemove(requestId, out _);
        }
    }

    public void Disconnect()
    {
        _receiveCts?.Cancel();
        foreach (var (_, waiter) in _pendingWindowRequests)
        {
            waiter.TrySetCanceled();
        }
        _pendingWindowRequests.Clear();

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
        var socket = _webSocket;
        if (socket?.State != WebSocketState.Open)
            return;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

        await _sendLock.WaitAsync(ct);
        try
        {
            // Re-check state after acquiring the lock to avoid races with Disconnect().
            if (!ReferenceEquals(socket, _webSocket) || socket.State != WebSocketState.Open)
                return;

            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);
        }
        catch (ObjectDisposedException)
        {
            // Best effort during disconnect/race conditions.
        }
        catch (WebSocketException)
        {
            // Best effort during disconnect/race conditions.
        }
        finally
        {
            _sendLock.Release();
        }
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
                    else if (msg?.Type == MessageTypes.DesktopError && msg.ErrorText is not null)
                    {
                        ErrorReceived?.Invoke(msg.ErrorText);
                    }
                    else if (msg?.Type == MessageTypes.DesktopWindowResult && msg.DesktopWindowResult is not null)
                    {
                        var requestId = msg.CorrelationId ?? msg.DesktopWindowResult.RequestId;
                        if (!string.IsNullOrWhiteSpace(requestId) &&
                            _pendingWindowRequests.TryGetValue(requestId, out var waiter))
                        {
                            waiter.TrySetResult(msg.DesktopWindowResult);
                        }

                        WindowResultReceived?.Invoke(msg.DesktopWindowResult);
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

    private bool AcceptSelfSignedCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate == null) return false;

        using var cert2 = new X509Certificate2(certificate);
        var spki = cert2.PublicKey.ExportSubjectPublicKeyInfo();
        var hashBytes = System.Security.Cryptography.SHA256.HashData(spki);
        var hashBase64 = Convert.ToBase64String(hashBytes);

        var store = App.Services?.GetService(typeof(Remex.Client.Services.Security.PinnedCertStore)) as Remex.Client.Services.Security.PinnedCertStore;
        if (store != null)
        {
            var pins = store.GetAllPinsAsync().GetAwaiter().GetResult();
            if (pins.Values.Contains(hashBase64))
            {
                return true;
            }

            // Fallback for RemoteDesktop connecting.
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _sendLock.Dispose();
    }
}

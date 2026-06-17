using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
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
    private static readonly DesktopConfig DefaultConfig = new();

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoopTask;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DesktopWindowResult>> _pendingWindowRequests = new();
    private bool _isStreaming;

    public event Action<byte[]>? FrameReceived;
    public event Action<DesktopMeta>? MetaReceived;
    public event Action<string>? ErrorReceived;
    public event Action<DesktopWindowResult>? WindowResultReceived;
    /// <summary>Raised when the host sends a stream surface descriptor (Stage 3).</summary>
    public event Action<DesktopStreamDescriptor>? StreamDescriptorReceived;
    /// <summary>Raised when the host sends the available-display catalog in response to a display query.</summary>
    public event Action<DesktopDisplayCatalog>? DisplayCatalogReceived;
    /// <summary>Raised when the host sends a cursor position/visibility update (native-cursor overlay).</summary>
    public event Action<DesktopCursorState>? CursorStateReceived;
    /// <summary>Raised when the host sends a new cursor shape bitmap (native-cursor overlay).</summary>
    public event Action<DesktopCursorShape>? CursorShapeReceived;
    public event Action? Disconnected;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public bool IsStreaming => _isStreaming;

    private RemexDesktopClient() { }

    public async Task ConnectAsync(string host, int port, string? clientId = null, string? spkiHash = null, CancellationToken ct = default)
    {
        await DisconnectAsync();

        var wsUri = BuildDesktopUri(host, port, clientId);
        _webSocket = new ClientWebSocket();

        if (!string.IsNullOrEmpty(spkiHash))
        {
            _webSocket.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                if (cert == null) return false;
                using var cert2 = new X509Certificate2(cert);
                var actualSpki = cert2.PublicKey.ExportSubjectPublicKeyInfo();
                var actualHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(actualSpki));
                return actualHash == spkiHash;
            };
        }

        await _webSocket.ConnectAsync(wsUri, ct);

        _receiveCts = new CancellationTokenSource();
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    public async Task EnsureConnectedAsync(string host, int port, string? clientId = null, string? spkiHash = null, CancellationToken ct = default)
    {
        if (IsConnected)
        {
            return;
        }

        await ConnectAsync(host, port, clientId, spkiHash, ct);
    }

    public async Task StartStreamAsync(string host, int port, DesktopConfig? config, string? clientId = null, string? spkiHash = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(host, port, clientId, spkiHash, ct);

        var resolvedConfig = config ?? DefaultConfig;

        if (_isStreaming)
        {
            await SendConfigAsync(resolvedConfig, ct);
            return;
        }

        await SendMessageAsync(new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = resolvedConfig,
        }, ct);

        _isStreaming = true;
    }

    public async Task StopStreamAsync(CancellationToken ct = default)
    {
        if (!IsConnected || !_isStreaming)
        {
            return;
        }

        await SendMessageAsync(new RemexMessage
        {
            Type = MessageTypes.DesktopStop,
        }, ct);

        _isStreaming = false;
    }

    public async Task SendInputAsync(string host, int port, InputEvent input, string? clientId = null, string? spkiHash = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(host, port, clientId, spkiHash, ct);

        if (!_isStreaming)
        {
            await StartStreamAsync(host, port, DefaultConfig, clientId, spkiHash, ct);
        }

        await SendMessageAsync(new RemexMessage
        {
            Type = MessageTypes.DesktopInput,
            InputEvent = input,
        }, ct);
    }

    public async Task SendConfigAsync(DesktopConfig config, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            return;
        }

        await SendMessageAsync(new RemexMessage
        {
            Type = MessageTypes.DesktopConfig,
            DesktopConfig = config,
        }, ct);
    }

    /// <summary>
    /// Asks the host for the catalog of available displays/capture modes. The reply arrives
    /// asynchronously via <see cref="DisplayCatalogReceived"/>. Connects first if needed.
    /// </summary>
    public async Task RequestDisplayCatalogAsync(string host, int port, string? clientId = null, string? spkiHash = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(host, port, clientId, spkiHash, ct);

        await SendMessageAsync(new RemexMessage
        {
            Type = MessageTypes.DesktopDisplayQuery,
        }, ct);
    }

    /// <summary>
    /// Requests a live capture-target switch (display / capture mode) on an active stream.
    /// Only honored by the host when the client advertised frame-envelope + target-switch support.
    /// </summary>
    public async Task SwitchTargetAsync(string host, int port, DesktopTargetSwitchRequest request, string? clientId = null, string? spkiHash = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(host, port, clientId, spkiHash, ct);

        await SendMessageAsync(new RemexMessage
        {
            Type = MessageTypes.DesktopTargetSwitch,
            DesktopTargetSwitch = request,
        }, ct);
    }

    /// <summary>
    /// Sends a batch of high-resolution pointer/stylus samples to the host (Stage 3).
    /// Falls back silently if not connected or not streaming.
    /// </summary>
    public async Task SendPointerBatchAsync(string host, int port, DesktopPointerBatch batch, string? clientId = null, string? spkiHash = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(host, port, clientId, spkiHash, ct);

        if (!_isStreaming)
        {
            await StartStreamAsync(host, port, DefaultConfig, clientId, spkiHash, ct);
        }

        await SendMessageAsync(new RemexMessage
        {
            Type = MessageTypes.DesktopPointerBatch,
            DesktopPointerBatch = batch,
        }, ct);
    }

    public async Task<DesktopWindowResult> QueryWindowsAsync(string host, int port, DesktopWindowQuery query, string? clientId = null, string? spkiHash = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(host, port, clientId, spkiHash, ct);

        var requestId = string.IsNullOrWhiteSpace(query.RequestId) ? Guid.NewGuid().ToString("N") : query.RequestId;
        var tcs = new TaskCompletionSource<DesktopWindowResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingWindowRequests[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        try
        {
            await SendMessageAsync(new RemexMessage
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

    public async Task<DesktopWindowResult> ExecuteWindowActionAsync(string host, int port, DesktopWindowAction action, string? clientId = null, string? spkiHash = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(host, port, clientId, spkiHash, ct);

        var requestId = string.IsNullOrWhiteSpace(action.RequestId) ? Guid.NewGuid().ToString("N") : action.RequestId;
        var tcs = new TaskCompletionSource<DesktopWindowResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingWindowRequests[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        try
        {
            await SendMessageAsync(new RemexMessage
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

    private async Task SendMessageAsync(RemexMessage message, CancellationToken ct)
    {
        if (!IsConnected)
        {
            return;
        }

        var bytes = RemexJson.SerializeToUtf8Bytes(message, RemexJsonSerializerContext.Default.RemexMessage);
        await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static Uri BuildDesktopUri(string host, int port, string? clientId)
    {
        var uri = $"wss://{host}:{port}{RemexConstants.WebSocketPath}/desktop";
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            uri += $"?clientId={Uri.EscapeDataString(clientId)}";
        }

        return new Uri(uri);
    }

    public async Task DisconnectAsync()
    {
        _receiveCts?.Cancel();
        foreach (var (_, waiter) in _pendingWindowRequests)
        {
            waiter.TrySetCanceled();
        }
        _pendingWindowRequests.Clear();
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

        _isStreaming = false;
        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveLoopTask = null;
        Disconnected?.Invoke();
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
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _isStreaming = false;
                        return;
                    }
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
                        _isStreaming = false;
                        ErrorReceived?.Invoke(msg.ErrorText ?? "Unknown desktop error");
                    }
                    else if (msg?.Type == MessageTypes.DesktopWindowResult && msg.DesktopWindowResult != null)
                    {
                        var requestId = msg.CorrelationId ?? msg.DesktopWindowResult.RequestId;
                        if (!string.IsNullOrWhiteSpace(requestId) &&
                            _pendingWindowRequests.TryGetValue(requestId, out var waiter))
                        {
                            waiter.TrySetResult(msg.DesktopWindowResult);
                        }

                        WindowResultReceived?.Invoke(msg.DesktopWindowResult);
                    }
                    else if (msg?.Type == MessageTypes.DesktopStreamDescriptor && msg.DesktopStreamDescriptor != null)
                    {
                        StreamDescriptorReceived?.Invoke(msg.DesktopStreamDescriptor);
                    }
                    else if (msg?.Type == MessageTypes.DesktopDisplayList && msg.DesktopDisplayCatalog != null)
                    {
                        DisplayCatalogReceived?.Invoke(msg.DesktopDisplayCatalog);
                    }
                    else if (msg?.Type == MessageTypes.DesktopCursorState && msg.DesktopCursorState != null)
                    {
                        CursorStateReceived?.Invoke(msg.DesktopCursorState);
                    }
                    else if (msg?.Type == MessageTypes.DesktopCursorShape && msg.DesktopCursorShape != null)
                    {
                        CursorShapeReceived?.Invoke(msg.DesktopCursorShape);
                    }
                }
            }
        }
        catch
        {
            _isStreaming = false;
        }
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

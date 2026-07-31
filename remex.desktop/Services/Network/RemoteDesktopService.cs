using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Desktop.Services.Network;

public class RemoteDesktopService : IDisposable
{
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DesktopWindowResult>> _pendingWindowRequests = new();
    private bool _disposed;

    /// <summary>
    /// Pinned SPKI hashes captured immediately before each connect, so the TLS callback can be
    /// fully synchronous. The previous version called <c>GetAllPinsAsync().GetAwaiter()
    /// .GetResult()</c> inside the callback — blocking on async I/O from a TLS handshake thread.
    /// </summary>
    private IReadOnlyDictionary<string, string>? _pinSnapshot;

    /// <summary>
    /// Whether an EMPTY pin store may be trusted for this connect. True only for loopback, where
    /// the peer is this machine's own embedded host and pinning adds nothing — the same policy
    /// <c>ConnectionViewModel.PrepareTlsValidationForConnectAsync</c> applies to the control
    /// channel, and the reason making this check effective does not break the PC's own session.
    /// </summary>
    private bool _allowFirstTimeTrust;
    private long _latestStreamSerial;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public event Action<byte[]>? FrameReceived;
    public event Action<DesktopMeta>? MetaReceived;
    public event Action<string>? ErrorReceived;
    public event Action<DesktopWindowResult>? WindowResultReceived;
    /// <summary>Raised when the host sends a stream surface descriptor (Stage 3).</summary>
    public event Action<DesktopStreamDescriptor>? StreamDescriptorReceived;
    public event Action<DesktopCursorState>? CursorStateReceived;
    public event Action<DesktopCursorShape>? CursorShapeReceived;
    public event Action? Disconnected;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public async Task ConnectAsync(string hostAddress, CancellationToken ct = default)
    {
        Disconnect();

        await PrepareTlsValidationAsync(hostAddress);
        _webSocket = CreateClientWebSocket();
        await _webSocket.ConnectAsync(BuildDesktopUri(hostAddress), ct);

        _receiveCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    public async Task<DesktopDisplayCatalog> QueryDisplaysAsync(string hostAddress, CancellationToken ct = default)
    {
        await PrepareTlsValidationAsync(hostAddress);
        using var socket = CreateClientWebSocket();
        await socket.ConnectAsync(BuildDesktopUri(hostAddress), ct);

        try
        {
            var query = new RemexMessage { Type = MessageTypes.DesktopDisplayQuery };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(query, JsonOptions);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);

            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var (messageType, payload) = await ReceiveMessageAsync(socket, ct);
                if (messageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (messageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var message = JsonSerializer.Deserialize<RemexMessage>(payload, JsonOptions);
                if (message?.Type == MessageTypes.DesktopDisplayList && message.DesktopDisplayCatalog is not null)
                {
                    return message.DesktopDisplayCatalog;
                }

                if (message?.Type == MessageTypes.DesktopError && !string.IsNullOrWhiteSpace(message.ErrorText))
                {
                    throw new InvalidOperationException(message.ErrorText);
                }
            }
        }
        finally
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Display query complete", CancellationToken.None);
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        }

        throw new InvalidOperationException("The host did not return any desktop display targets.");
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

    public async Task SwitchTargetAsync(DesktopTargetSwitchRequest request, CancellationToken ct = default)
    {
        await SendJsonAsync(new RemexMessage
        {
            Type = MessageTypes.DesktopTargetSwitch,
            DesktopTargetSwitch = request,
        }, ct);
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
        Interlocked.Exchange(ref _latestStreamSerial, 0);

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
                    var binaryPayload = ms.ToArray();
                    if (DesktopFrameEnvelope.TryRead(binaryPayload, out var header, out var payload))
                    {
                        var latestSerial = Interlocked.Read(ref _latestStreamSerial);
                        if (header.StreamSerial < latestSerial)
                        {
                            continue;
                        }

                        if (header.StreamSerial > latestSerial)
                        {
                            Interlocked.Exchange(ref _latestStreamSerial, header.StreamSerial);
                        }

                        FrameReceived?.Invoke(payload.ToArray());
                    }
                    else
                    {
                        FrameReceived?.Invoke(binaryPayload);
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    // Text = JSON control message
                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    var msg = JsonSerializer.Deserialize<RemexMessage>(json, JsonOptions);

                    if (msg?.Type == MessageTypes.DesktopMeta && msg.DesktopMeta is not null)
                    {
                        UpdateStreamSerial(msg.DesktopMeta.StreamSerial);
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
                    else if (msg?.Type == MessageTypes.DesktopStreamDescriptor && msg.DesktopStreamDescriptor is not null)
                    {
                        UpdateStreamSerial(msg.DesktopStreamDescriptor.StreamSerial);
                        StreamDescriptorReceived?.Invoke(msg.DesktopStreamDescriptor);
                    }
                    else if (msg?.Type == MessageTypes.DesktopCursorState && msg.DesktopCursorState is not null)
                    {
                        UpdateStreamSerial(msg.DesktopCursorState.StreamSerial);
                        CursorStateReceived?.Invoke(msg.DesktopCursorState);
                    }
                    else if (msg?.Type == MessageTypes.DesktopCursorShape && msg.DesktopCursorShape is not null)
                    {
                        CursorShapeReceived?.Invoke(msg.DesktopCursorShape);
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

        var accepted = IsCertificateAcceptable(hashBase64, _pinSnapshot, _allowFirstTimeTrust);
        if (!accepted)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[RemoteDesktopService] Rejecting host certificate SPKI {hashBase64}. If the host " +
                "certificate legitimately rotated, the operator must re-pair.");
        }
        return accepted;
    }

    /// <summary>
    /// The pinning decision, as a pure function of the presented hash and the captured policy.
    /// </summary>
    /// <remarks>
    /// Separated from the callback so it can be tested exhaustively without a TLS handshake, a DI
    /// container or a socket. That matters more here than usual: the defect being fixed was not a
    /// wrong rule but an UNUSED one — the old code computed the hash, looked it up, ignored the
    /// answer and returned <c>true</c> (RemEx-mlce). A rule nobody consults reads exactly like a
    /// rule that works.
    /// </remarks>
    internal static bool IsCertificateAcceptable(
        string spkiHashBase64,
        IReadOnlyDictionary<string, string>? pins,
        bool allowFirstTimeTrust)
    {
        // No snapshot means PrepareTlsValidationAsync did not run. A missing store is NOT an empty
        // store, and the safe answer to "I do not know" is no.
        if (pins is null) return false;

        // Pinned hosts exist: the presented cert must be one of them. Mirrors the control channel
        // in ConnectionViewModel.AcceptSelfSignedCertificate, deliberately — the two channels talk
        // to the same host and disagreeing about its certificate is its own kind of defect.
        if (pins.Count > 0) return pins.Values.Contains(spkiHashBase64);

        // Empty store: trusted only for loopback, where the peer is this machine's own host.
        return allowFirstTimeTrust;
    }

    /// <summary>
    /// Captures the pin snapshot and the empty-store policy for the connect that follows.
    /// </summary>
    /// <remarks>
    /// Separate from the callback on purpose. A TLS validation callback runs on the handshake
    /// thread and must not block on async I/O, which is what the previous
    /// <c>.GetAwaiter().GetResult()</c> did — and it was moot anyway, because the result was
    /// discarded and the method returned <c>true</c> regardless (RemEx-mlce).
    /// </remarks>
    private async Task PrepareTlsValidationAsync(string hostAddress)
    {
        var uri = BuildDesktopUri(hostAddress);
        _allowFirstTimeTrust = uri.Host is "localhost" or "127.0.0.1" or "::1";

        var store = App.Services?.GetService(typeof(Remex.Desktop.Services.Security.PinnedCertStore))
            as Remex.Desktop.Services.Security.PinnedCertStore;

        // A missing store is not an empty store: leaving the snapshot null makes the callback fail
        // closed rather than fall through to the loopback allowance.
        _pinSnapshot = store is null ? null : await store.GetAllPinsAsync();
    }

    private ClientWebSocket CreateClientWebSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = AcceptSelfSignedCertificate;
        return socket;
    }

    private static Uri BuildDesktopUri(string hostAddress)
    {
        var desktopUrl = hostAddress.TrimEnd('/');
        if (desktopUrl.EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
            desktopUrl += "/desktop";
        else
            desktopUrl += "/ws/desktop";

        return new Uri(desktopUrl);
    }

    private static async Task<(WebSocketMessageType MessageType, string Payload)> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024 * 64];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (result.MessageType, string.Empty);
            }

            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (result.MessageType, Encoding.UTF8.GetString(ms.ToArray()));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _sendLock.Dispose();
    }

    private void UpdateStreamSerial(long streamSerial)
    {
        if (streamSerial > 0)
        {
            Interlocked.Exchange(ref _latestStreamSerial, streamSerial);
        }
    }
}

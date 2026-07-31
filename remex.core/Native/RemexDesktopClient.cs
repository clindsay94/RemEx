using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
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

    /// <summary>
    /// How long a window query or action waits for its reply before giving up.
    /// </summary>
    /// <remarks>
    /// These are the only request/RESPONSE operations on this client, and they used to wait forever:
    /// no timeout, and the caller passes no cancellation token. A host that never answers — an older
    /// build, a correlation-id mismatch, a handler that faulted — hung the caller permanently. That
    /// was survivable when each call had its own thread; it is not now that desktop work is
    /// serialised, because the hung call blocks every later operation. Generous rather than tight: a
    /// real query answers in milliseconds, so anything near this is already a failure. (RemEx-krvz)
    /// </remarks>
    private static readonly TimeSpan WindowRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Cancels every in-flight window request; used when the socket can no longer answer.</summary>
    private void FailPendingWindowRequests()
    {
        foreach (var (_, waiter) in _pendingWindowRequests)
        {
            waiter.TrySetCanceled();
        }

        _pendingWindowRequests.Clear();
    }
    private bool _isStreaming;

    // VULN-2 (RemEx-s032.2): the reconnect secret established at pairing, base64-encoded. Remembered on
    // the singleton so reconnects after a drop can re-answer the host's proof-of-possession challenge on
    // the /ws/desktop channel without the caller re-supplying it. Null until the first paired connect.
    private string? _clientId;
    private string? _reconnectSecretBase64;

    public event Action<byte[]>? FrameReceived;

    // RD-E: raised with the raw 32-byte "RDXC" cursor-position packet (demuxed from the binary channel).
    // Carries the packet bytes unparsed so the Android JNI layer forwards them as a byte[] and Kotlin parses
    // with ByteBuffer — no JSON string, no JSONObject on the 60–90 Hz hot path. See DesktopCursorBinaryEnvelope.
    public event Action<byte[]>? CursorBinaryReceived;
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

    /// <summary>
    /// Fail-closed guard (RemEx-s032.5 / VULN-5). The JNI trust-manager overrides exported by
    /// <c>AndroidNativeExports</c> unconditionally force the Android OS trust manager to accept ANY
    /// certificate — by design, because <see cref="ClientWebSocket.Options"/>'s
    /// <c>RemoteCertificateValidationCallback</c> is meant to be the sole authority for TLS trust on this
    /// connection. If that callback were ever installed conditionally (only when a pin is present) and a
    /// caller connected with no pin, <see cref="SslStream"/> would fall through to the OS trust manager —
    /// which the JNI overrides force to accept anything — and TLS would silently accept ANY certificate
    /// (full MITM). Calling this before <see cref="ConnectAsync"/> touches the socket makes an empty pin
    /// impossible to reach in production: no pin, no connection, full stop.
    /// </summary>
    internal static void EnsurePinnedOrThrow(string? spkiHash)
    {
        if (string.IsNullOrWhiteSpace(spkiHash))
        {
            throw new InvalidOperationException(
                "Refusing to connect without a pinned certificate SPKI hash (fail-closed).");
        }
    }

    public async Task ConnectAsync(string host, int port, string? clientId = null, string? spkiHash = null, CancellationToken ct = default, string? reconnectSecretBase64 = null)
    {
        EnsurePinnedOrThrow(spkiHash);

        await DisconnectAsync();

        _clientId = clientId;
        // Remember the secret across reconnects; a null on a later call keeps the previously supplied one.
        if (!string.IsNullOrWhiteSpace(reconnectSecretBase64))
        {
            _reconnectSecretBase64 = reconnectSecretBase64;
        }

        var wsUri = BuildDesktopUri(host, port, clientId);
        _webSocket = new ClientWebSocket();

        // Defense in depth: ALWAYS install the validation callback (never leave the socket to fall
        // through to the OS trust manager), and reject outright if somehow reached with no pin
        // configured. EnsurePinnedOrThrow above already guarantees spkiHash is non-empty here, but this
        // callback stands on its own so a future refactor of the early guard can't silently reopen VULN-5.
        _webSocket.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
        {
            if (string.IsNullOrWhiteSpace(spkiHash)) return false;
            if (cert == null) return false;
            using var cert2 = new X509Certificate2(cert);
            var actualSpki = cert2.PublicKey.ExportSubjectPublicKeyInfo();
            var actualHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(actualSpki));
            return actualHash == spkiHash;
        };

        await _webSocket.ConnectAsync(wsUri, ct);

        // VULN-2 (RemEx-s032.2): the host challenges the /ws/desktop channel for proof-of-possession as
        // the FIRST frame after accept (non-loopback only; the Android client is always remote). Answer it
        // synchronously here — before the receive loop starts and before StartStreamAsync sends any
        // DesktopStart — so the proof is guaranteed to be the first frame the host reads back, not racing a
        // stream-control message. Skipped only when no reconnect secret is known (unpaired/legacy path,
        // which the host will then reject — the correct fail-closed outcome).
        await CompleteReconnectProofAsync(ct);

        _receiveCts = new CancellationTokenSource();
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    public async Task EnsureConnectedAsync(string host, int port, string? clientId = null, string? spkiHash = null, CancellationToken ct = default, string? reconnectSecretBase64 = null)
    {
        if (IsConnected)
        {
            return;
        }

        await ConnectAsync(host, port, clientId, spkiHash, ct, reconnectSecretBase64);
    }

    public async Task StartStreamAsync(string host, int port, DesktopConfig? config, string? clientId = null, string? spkiHash = null, CancellationToken ct = default, string? reconnectSecretBase64 = null)
    {
        await EnsureConnectedAsync(host, port, clientId, spkiHash, ct, reconnectSecretBase64);

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
    /// Asks the host to emit an on-demand keyframe (IDR) on the active stream after a decoder desync.
    /// MUST be sent on the desktop stream socket so it reaches <c>RemoteDesktopHandler</c>; the control
    /// <c>/ws</c> channel does not handle desktop stream messages and would log it as "Unknown message
    /// type". No-op when not connected/streaming (nothing to resync). Additive + backward-compatible:
    /// legacy hosts ignore the type. (RemEx-bqc / #2a)
    /// </summary>
    public async Task RequestKeyframeAsync(string host, int port, string? clientId = null, string? spkiHash = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(host, port, clientId, spkiHash, ct);

        if (!_isStreaming)
        {
            return;
        }

        await SendMessageAsync(new RemexMessage
        {
            Type = MessageTypes.DesktopKeyframeRequest,
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

            return await tcs.Task.WaitAsync(WindowRequestTimeout, ct);
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

            return await tcs.Task.WaitAsync(WindowRequestTimeout, ct);
        }
        finally
        {
            _pendingWindowRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Serialises sends onto this socket.
    /// </summary>
    /// <remarks>
    /// A <see cref="System.Net.WebSockets.ClientWebSocket"/> permits only ONE outstanding
    /// <c>SendAsync</c>; a second concurrent one throws <see cref="InvalidOperationException"/>. This
    /// send had no lock at all, and every caller arrived on a fire-and-forget <c>Task.Run</c>, so an
    /// overlap threw into a catch that only wrote to logcat — the message was simply gone, with
    /// nothing surfaced to the user (RemEx-krvz). Matches <c>RemexNativeClient._sendGate</c>.
    ///
    /// This is DEFENCE, not the fix. It stops overlap; it cannot restore order, because whichever
    /// caller wakes first takes the gate first. Ordering comes from the single-consumer queue in
    /// <c>AndroidNativeExports</c>, and this exists for any caller that does not come through it.
    /// </remarks>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private async Task SendMessageAsync(RemexMessage message, CancellationToken ct)
    {
        if (!IsConnected)
        {
            return;
        }

        var bytes = RemexJson.SerializeToUtf8Bytes(message, RemexJsonSerializerContext.Default.RemexMessage);

        await _sendGate.WaitAsync(ct);
        try
        {
            // Re-checked inside the gate: a teardown can complete while this call waits its turn,
            // and sending on a disposed socket throws where doing nothing is correct.
            if (!IsConnected)
            {
                return;
            }

            await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
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
        FailPendingWindowRequests();
        if (_receiveLoopTask != null)
        {
            try { await _receiveLoopTask; } catch { /* draining the receive loop after cancelling it - see RemexNativeClient */ }
        }

        if (_webSocket != null)
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                try { await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None); } catch { /* a polite close on a socket that may already be gone */ }
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

    /// <summary>
    /// VULN-2 (RemEx-s032.2): answers the host's PAIR-1 proof-of-possession challenge on the
    /// <c>/ws/desktop</c> channel. Reads the first frame (a <c>reconnect_challenge</c> carrying a host
    /// nonce), replies with <c>HMAC-SHA256(reconnectSecret, nonce)</c>, and returns — all before the
    /// receive loop or any stream-control message, so the proof is the first frame the host reads back.
    /// No-op when no reconnect secret is configured (the host then rejects the unproven socket).
    /// </summary>
    private async Task CompleteReconnectProofAsync(CancellationToken ct)
    {
        // Prefer an explicitly-supplied secret (tests / explicit callers); otherwise borrow the one the
        // sibling /ws control client authenticated with, so the Android path needs no extra JNI plumbing.
        var secretBase64 = _reconnectSecretBase64 ?? RemexNativeClient.Current.ReconnectSecretBase64;
        if (string.IsNullOrWhiteSpace(secretBase64) || _webSocket is null)
        {
            return;
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var ms = new System.IO.MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var msg = RemexJson.Deserialize(ms.ToArray(), RemexJsonSerializerContext.Default.RemexMessage);
            if (msg?.Type != MessageTypes.ReconnectChallenge || msg.ReconnectChallenge is null)
            {
                // The host did not challenge (only happens on loopback, which the Android client never is).
                // Nothing to prove; the already-consumed frame is not a stream message, so just return.
                return;
            }

            var secret = Convert.FromBase64String(secretBase64);
            try
            {
                var nonce = Convert.FromBase64String(msg.ReconnectChallenge.NonceBase64);
                var proof = System.Security.Cryptography.HMACSHA256.HashData(secret, nonce);
                var reply = new RemexMessage
                {
                    Type = MessageTypes.ReconnectProof,
                    ProtocolVersion = 2,
                    ClientId = _clientId,
                    ReconnectProof = new ReconnectProof
                    {
                        ClientId = _clientId,
                        ProofHmacBase64 = Convert.ToBase64String(proof),
                    },
                };
                var bytes = RemexJson.SerializeToUtf8Bytes(reply, RemexJsonSerializerContext.Default.RemexMessage);
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, linked.Token);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
                    var bytes = ms.ToArray();
                    // RD-E: cursor-position packets ("RDXC") share the binary channel with H.264 frames.
                    // Demux by magic — H.264 (raw NAL 00 00 00 01 or the "RDXF" frame envelope) never starts
                    // with "RDXC", so this is unambiguous regardless of frame-envelope use.
                    if (DesktopCursorBinaryEnvelope.HasMagic(bytes))
                    {
                        CursorBinaryReceived?.Invoke(bytes);
                    }
                    else
                    {
                        FrameReceived?.Invoke(bytes);
                    }
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
            // A waiter's ONLY completers are a matching reply on this loop and DisconnectAsync. So
            // when this loop ends — socket dropped, PC asleep, host faulted — anything still waiting
            // would wait forever. That used to strand one caller; now that desktop work runs on a
            // single-consumer queue it would strand EVERY later operation behind it, including the
            // DesktopStop that is the only route to DisconnectAsync. (RemEx-krvz)
            FailPendingWindowRequests();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }
}

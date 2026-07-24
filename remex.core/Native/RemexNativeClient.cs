using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;

namespace Remex.Core.Native;

/// <summary>
/// A native-optimized WebSocket client for RemEx communication.
/// Handles persistent connection, telemetry streaming, and command dispatching.
/// </summary>
public sealed class RemexNativeClient : IDisposable, IAsyncDisposable
{
    private static readonly Lazy<RemexNativeClient> Instance = new(() => new RemexNativeClient());
    public static RemexNativeClient Current => Instance.Value;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveLoopTask;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResponse>> _pendingCommands = new();
    private long _commandIdCounter;
    // ClientWebSocket.SendAsync is not safe for concurrent calls. The outbound queue
    // serializes file-transfer / launcher / etc. messages, but SendCommandAsync also
    // invokes SendMessageAsync directly — a command racing with a chunk flood would
    // throw "There is already one outstanding 'SendAsync'". Guard every send.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public event Action<TelemetryPayload>? TelemetryReceived;
    public event Action<List<Remex.Core.Models.AppEntry>>? LauncherEntriesReceived;
    public event Action<List<Remex.Core.Models.ProcessInfo>>? ProcessListReceived;
    public event Action<bool>? ConnectionStateChanged;
    public event Action<RemexMessage>? MessageReceived;
    public event Action<string>? ConnectionFailed;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    private RemexNativeClient() { }

    private string? _clientId;

    public string? ClientId
    {
        get => _clientId;
        set => _clientId = value;
    }

    // PAIR-1: the reconnect secret (the pairing session key) the host uses to challenge us on
    // reconnect. Base64-encoded; null when this client has never paired (a fresh pairing is then
    // required). Decoded lazily when a challenge arrives.
    private string? _reconnectSecretBase64;

    /// <summary>
    /// VULN-2 (RemEx-s032.2): the base64 reconnect secret this control client authenticated with,
    /// shared with the sibling <see cref="RemexDesktopClient"/> so the <c>/ws/desktop</c> channel can
    /// answer the host's proof-of-possession challenge without re-plumbing the secret up from Kotlin.
    /// Null before the first paired connect.
    /// </summary>
    internal string? ReconnectSecretBase64 => _reconnectSecretBase64;

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

    public async Task ConnectAsync(
        string host,
        int port,
        string? spkiHash = null,
        string? clientId = null,
        string? reconnectSecretBase64 = null,
        CancellationToken ct = default)
    {
        EnsurePinnedOrThrow(spkiHash);

        _clientId = clientId;
        _reconnectSecretBase64 = reconnectSecretBase64;
        await DisconnectAsync();

        _connectionCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts.Token, ct);

        // Force wss:// for 2.0
        var wsUri = new Uri($"wss://{host}:{port}{RemexConstants.WebSocketPath}");
        _webSocket = new ClientWebSocket();

        JniHelper.AndroidLogE("RemexNative", $"Attempting connection to {wsUri}");

        // Defense in depth: ALWAYS install the validation callback (never leave the socket to fall
        // through to the OS trust manager), and reject outright if somehow reached with no pin
        // configured. EnsurePinnedOrThrow above already guarantees spkiHash is non-empty here, but this
        // callback stands on its own so a future refactor of the early guard can't silently reopen VULN-5.
        _webSocket.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(spkiHash))
                {
                    JniHelper.AndroidLogE("RemexNative", "Certificate validation rejected: no SPKI pin configured (fail-closed).");
                    return false;
                }

                JniHelper.AndroidLogE("RemexNative", $"SSL Validation Callback triggered. Errors: {errors}");

                if (cert == null)
                {
                    JniHelper.AndroidLogE("RemexNative", "Certificate validation failed: Remote certificate is null");
                    return false;
                }

                // Log cert info
                JniHelper.AndroidLogE("RemexNative", $"Cert Subject: {cert.Subject}");
                JniHelper.AndroidLogE("RemexNative", $"Cert Issuer: {cert.Issuer}");

                // Use the raw data to avoid potential PAL object mapping issues
                byte[] rawData = cert.Export(X509ContentType.Cert);
                if (rawData == null || rawData.Length == 0)
                {
                    JniHelper.AndroidLogE("RemexNative", "Failed to export raw certificate data");
                    return false;
                }

                using var cert2 = X509CertificateLoader.LoadCertificate(rawData);
                var spkiInfo = cert2.PublicKey.ExportSubjectPublicKeyInfo();
                var actualHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(spkiInfo));

                JniHelper.AndroidLogE("RemexNative", $"Actual SPKI Hash: {actualHash}");
                JniHelper.AndroidLogE("RemexNative", $"Expected SPKI Hash: {spkiHash}");

                if (actualHash == spkiHash)
                {
                    JniHelper.AndroidLogE("RemexNative", "Certificate hash matches! Validation successful.");
                    return true;
                }

                JniHelper.AndroidLogE("RemexNative", "Certificate mismatch! Rejecting connection.");
                return false;
            }
            catch (Exception ex)
            {
                JniHelper.AndroidLogE("RemexNative", $"CRITICAL ERROR in validation callback: {ex.Message}");
                JniHelper.AndroidLogE("RemexNative", ex.ToString());
                return false;
            }
        };

        try
        {
            await _webSocket.ConnectAsync(wsUri, linkedCts.Token);
            JniHelper.AndroidLogE("RemexNative", "Successfully connected to remote server");
            ConnectionStateChanged?.Invoke(true);
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_connectionCts.Token));

            // PAIR-1 kickoff: the host issues its reconnect challenge lazily, on the first inbound
            // message that carries our clientId (PingPongHandler). Nothing else is guaranteed to
            // send until the user opens a screen that talks (historically only the Task Manager
            // page did), so nudge the handshake NOW with a gate-exempt ping — SendMessageAsync
            // stamps ClientId/ProtocolVersion, the host answers with reconnect_challenge, and
            // RespondToReconnectChallenge completes authentication with no user action. Skipped
            // when this client has never paired (no clientId → nothing to authenticate with).
            // Failure is non-fatal: any later send re-triggers the challenge. (RemEx-moqo)
            if (!string.IsNullOrEmpty(_clientId))
            {
                try
                {
                    await SendMessageAsync(
                        new RemexMessage { Type = MessageTypes.Ping, Timestamp = DateTime.UtcNow.Ticks },
                        linkedCts.Token);
                    JniHelper.AndroidLogE("RemexNative", "Sent post-connect kickoff ping (pairing handshake nudge).");
                }
                catch (Exception pingEx)
                {
                    JniHelper.AndroidLogE("RemexNative", $"Kickoff ping failed (non-fatal): {pingEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            JniHelper.AndroidLogE("RemexNative", $"ConnectAsync failed: {ex.Message}");
            
            var currentEx = ex.InnerException;
            int depth = 0;
            while (currentEx != null && depth < 5)
            {
                JniHelper.AndroidLogE("RemexNative", $"Inner exception [{depth}]: {currentEx.GetType().Name} - {currentEx.Message}");
                currentEx = currentEx.InnerException;
                depth++;
            }

            ConnectionStateChanged?.Invoke(false);
            ConnectionFailed?.Invoke(ex.Message);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        _connectionCts?.Cancel();
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

        _connectionCts?.Dispose();
        _connectionCts = null;
        _receiveLoopTask = null;
        ConnectionStateChanged?.Invoke(false);
    }

    public async Task<CommandResponse> SendCommandAsync(CommandRequest request, CancellationToken ct = default)
    {
        if (!IsConnected) return new CommandResponse(false, "Client is not connected.", null);

        var id = Interlocked.Increment(ref _commandIdCounter).ToString();
        var tcs = new TaskCompletionSource<CommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[id] = tcs;

        var message = new RemexMessage
        {
            Type = MessageTypes.Command,
            ProtocolVersion = 2,
            ClientId = _clientId,
            CommandAction = request.Action,
            CommandParameters = request.Parameters,
            Timestamp = DateTime.UtcNow.Ticks,
            // Set CorrelationId so the host can echo it back; the receive-side
            // HandleMessage lookup resolves the correct pending TCS by this ID.
            CorrelationId = id,
        };

        try
        {
            await SendMessageAsync(message, ct);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            return await tcs.Task.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            return new CommandResponse(false, "Command timed out.", null);
        }
        catch (Exception ex)
        {
            return new CommandResponse(false, $"Error sending command: {ex.Message}", ex.ToString());
        }
        finally
        {
            _pendingCommands.TryRemove(id, out _);
        }
    }

    public async Task SendMessageAsync(RemexMessage message, CancellationToken ct = default)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;

        // Ensure we satisfy Host protocol version and identity checks.
        // RemexMessage defaults to version 2, but we set it explicitly here 
        // to be safe under NativeAOT serialization rules.
        var outgoing = message with 
        { 
            ProtocolVersion = 2,
            ClientId = _clientId 
        };

        var bytes = RemexJson.SerializeToUtf8Bytes(outgoing, RemexJsonSerializerContext.Default.RemexMessage);

        await _sendGate.WaitAsync(ct);
        try
        {
            // Re-check inside the gate — we may have been closed while waiting.
            if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Sends on a SPECIFIC socket instead of the current <see cref="_webSocket"/> field. Used for the
    /// reconnect proof so it can only ever go out on the exact connection whose challenge it answers.
    /// If that socket was replaced out from under us — e.g. the phone is dual-homed on LAN + Tailscale
    /// and the client manager re-pointed to a different address — the state check drops the send rather
    /// than leaking a proof (computed for this challenge's nonce) onto the new connection, whose host
    /// loop issued a DIFFERENT nonce and would reject it, leaving the connection stuck unpaired. (RemEx-060g)
    /// </summary>
    private async Task SendMessageOnSocketAsync(RemexMessage message, ClientWebSocket? targetSocket, CancellationToken ct = default)
    {
        if (targetSocket is null || targetSocket.State != WebSocketState.Open) return;

        var outgoing = message with
        {
            ProtocolVersion = 2,
            ClientId = _clientId
        };

        var bytes = RemexJson.SerializeToUtf8Bytes(outgoing, RemexJsonSerializerContext.Default.RemexMessage);

        await _sendGate.WaitAsync(ct);
        try
        {
            // Re-check the SAME captured socket inside the gate — never fall back to _webSocket.
            if (targetSocket.State != WebSocketState.Open) return;
            await targetSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 32);
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
                        await DisconnectAsync();
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var bytes = ms.ToArray();
                    var msg = RemexJson.Deserialize(bytes, RemexJsonSerializerContext.Default.RemexMessage);
                    if (msg != null) HandleMessage(msg);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            ConnectionStateChanged?.Invoke(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HandleMessage(RemexMessage msg)
    {
        switch (msg.Type)
        {
            case MessageTypes.Telemetry when msg.Telemetry != null:
                TelemetryReceived?.Invoke(msg.Telemetry);
                break;

            case MessageTypes.LauncherSync when msg.LauncherEntries != null:
                LauncherEntriesReceived?.Invoke(msg.LauncherEntries);
                break;

            case MessageTypes.ProcessListSync when msg.ProcessList != null:
                ProcessListReceived?.Invoke(msg.ProcessList);
                break;

            case MessageTypes.ReconnectChallenge when msg.ReconnectChallenge is not null:
                // PAIR-1: the host issued a proof-of-possession challenge. Answer with
                // HMAC-SHA256(reconnectSecret, nonce). Done off the receive loop so we never
                // block message processing on the outbound send gate.
                RespondToReconnectChallenge(msg.ReconnectChallenge, _webSocket);
                break;

            case MessageTypes.CommandResponse:
                if (msg.CorrelationId is not null
                    && _pendingCommands.TryRemove(msg.CorrelationId, out var matchedTcs))
                {
                    // Happy path: host echoed our CorrelationId — resolve the correct awaiter.
                    matchedTcs.TrySetResult(
                        new CommandResponse(msg.CommandSuccess ?? false, msg.CommandMessage ?? "", msg.ErrorText));
                }
                else if (msg.CorrelationId is null && !_pendingCommands.IsEmpty)
                {
                    // Fallback for hosts that do not echo CorrelationId (pre-2.0 or buggy).
                    // Complete the first pending entry — this is best-effort and incorrect
                    // under concurrency; upgrade the host to fix it properly.
                    foreach (var tcs in _pendingCommands.Values)
                    {
                        if (tcs.TrySetResult(
                            new CommandResponse(msg.CommandSuccess ?? false, msg.CommandMessage ?? "", msg.ErrorText)))
                            break;
                    }
                }
                break;
        }

        MessageReceived?.Invoke(msg);
    }

    /// <summary>
    /// PAIR-1: answers a host reconnect challenge by sending HMAC-SHA256(reconnectSecret, nonce).
    /// If we hold no reconnect secret (never paired, or paired before this client version) we cannot
    /// answer; the host will then require a fresh pairing. Runs off the receive loop.
    /// </summary>
    private void RespondToReconnectChallenge(ReconnectChallenge challenge, ClientWebSocket? challengeSocket)
    {
        var secretBase64 = _reconnectSecretBase64;
        if (string.IsNullOrWhiteSpace(secretBase64))
        {
            JniHelper.AndroidLogE("RemexNative", "Reconnect challenge received but no reconnect secret is stored — re-pairing required.");
            return;
        }

        _ = Task.Run(async () =>
        {
            byte[]? secret = null;
            byte[]? nonce = null;
            try
            {
                secret = Convert.FromBase64String(secretBase64);
                nonce = Convert.FromBase64String(challenge.NonceBase64);

                var proof = HMACSHA256.HashData(secret, nonce);
                var message = new RemexMessage
                {
                    Type = MessageTypes.ReconnectProof,
                    ProtocolVersion = 2,
                    ClientId = _clientId,
                    ReconnectProof = new ReconnectProof
                    {
                        ProofHmacBase64 = Convert.ToBase64String(proof),
                        ClientId = _clientId,
                    },
                };

                await SendMessageOnSocketAsync(message, challengeSocket);
                JniHelper.AndroidLogE("RemexNative", "Sent reconnect proof in response to host challenge.");
            }
            catch (Exception ex)
            {
                JniHelper.AndroidLogE("RemexNative", $"Failed to answer reconnect challenge: {ex.Message}");
            }
            finally
            {
                if (secret != null) CryptographicOperations.ZeroMemory(secret);
            }
        });
    }

    /// <summary>
    /// Async disposal — preferred call site.  Awaits a clean WebSocket close before
    /// releasing resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    /// <summary>
    /// Synchronous disposal forwarded to <see cref="DisposeAsync"/>.  Callers that
    /// hold a reference inside an async context should prefer <c>await using</c> to
    /// avoid blocking the calling thread.
    /// </summary>
    public void Dispose()
    {
        // Fire-and-forget on the thread-pool so we don't block the caller's thread.
        // The underlying DisconnectAsync gracefully closes the WebSocket;
        // resources are cleaned up regardless of whether the await completes.
        _ = Task.Run(async () =>
        {
            try { await DisconnectAsync(); }
            catch { /* best-effort — already disconnecting */ }
        });
    }
}

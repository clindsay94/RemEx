using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Messages;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;

namespace Remex.Core.Native;

/// <summary>
/// A native-optimized WebSocket client for RemEx communication.
/// Handles persistent connection, telemetry streaming, and command dispatching.
/// </summary>
public sealed class RemexNativeClient : IDisposable
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

    public async Task ConnectAsync(string host, int port, string? spkiHash = null, CancellationToken ct = default)
    {
        await DisconnectAsync();

        _connectionCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts.Token, ct);

        // Force wss:// for 2.0
        var wsUri = new Uri($"wss://{host}:{port}{RemexConstants.WebSocketPath}");
        _webSocket = new ClientWebSocket();

        JniHelper.AndroidLogE("RemexNative", $"Attempting connection to {wsUri}");

        if (!string.IsNullOrEmpty(spkiHash))
        {
            _webSocket.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                try 
                {
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
        }

        try
        {
            await _webSocket.ConnectAsync(wsUri, linkedCts.Token);
            JniHelper.AndroidLogE("RemexNative", "Successfully connected to remote server");
            ConnectionStateChanged?.Invoke(true);
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_connectionCts.Token));
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
            CommandAction = request.Action,
            CommandParameters = request.Parameters,
            Timestamp = DateTime.UtcNow.Ticks // We use this as correlation ID for now
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
        var bytes = RemexJson.SerializeToUtf8Bytes(message, RemexJsonSerializerContext.Default.RemexMessage);

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

            case MessageTypes.CommandResponse:
                // Correlation via timestamp/parameters if needed, but for now we just find any pending
                // In a real app we should have a proper correlation ID.
                // We'll just complete the first pending one for simplicity in this MVP.
                foreach (var tcs in _pendingCommands.Values)
                {
                    if (tcs.TrySetResult(new CommandResponse(msg.CommandSuccess ?? false, msg.CommandMessage ?? "", msg.ErrorText)))
                        break;
                }
                break;
        }

        MessageReceived?.Invoke(msg);
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }
}

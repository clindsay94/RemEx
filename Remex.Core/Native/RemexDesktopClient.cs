using System.Buffers;
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
    private bool _isStreaming;

    public event Action<byte[]>? FrameReceived;
    public event Action<DesktopMeta>? MetaReceived;
    public event Action<string>? ErrorReceived;
    public event Action? Disconnected;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public bool IsStreaming => _isStreaming;

    private RemexDesktopClient() { }

    public async Task ConnectAsync(string host, int port, string? spkiHash = null, CancellationToken ct = default)
    {
        await DisconnectAsync();

        var wsUri = new Uri($"wss://{host}:{port}{RemexConstants.WebSocketPath}/desktop");
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

    public async Task EnsureConnectedAsync(string host, int port, string? spkiHash = null, CancellationToken ct = default)
    {
        if (IsConnected)
        {
            return;
        }

        await ConnectAsync(host, port, spkiHash, ct);
    }

    public async Task StartStreamAsync(string host, int port, DesktopConfig? config, string? spkiHash = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(host, port, spkiHash, ct);

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

    public async Task SendInputAsync(string host, int port, InputEvent input, string? spkiHash = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(host, port, spkiHash, ct);

        if (!_isStreaming)
        {
            await StartStreamAsync(host, port, DefaultConfig, spkiHash, ct);
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

    private async Task SendMessageAsync(RemexMessage message, CancellationToken ct)
    {
        if (!IsConnected)
        {
            return;
        }

        var bytes = RemexJson.SerializeToUtf8Bytes(message, RemexJsonSerializerContext.Default.RemexMessage);
        await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
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

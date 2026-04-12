using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Remex.Core.Exceptions;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services.Command;

namespace Remex.Core.Services.Network;

public class RemexNetworkListener : INetworkListener, IDisposable
{
    private readonly byte[] _accessKeyBytes;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RemexNetworkListener> _logger;
    private readonly ISystemCommandService _commandService;
    private readonly IWakeOnLanService _wakeOnLanService;
    private const int MaxPayloadSize = 10 * 1024 * 1024; // 10MB limit for JSON commands
    private TcpListener? _tcpListener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public RemexNetworkListener(
        IConfiguration configuration,
        ILogger<RemexNetworkListener> logger,
        ISystemCommandService commandService,
        IWakeOnLanService wakeOnLanService)
    {
        _configuration = configuration;
        _logger = logger;
        _commandService = commandService;
        _wakeOnLanService = wakeOnLanService;
        var accessKey = _configuration["Remex:AccessKey"] ?? "";
        _accessKeyBytes = Encoding.UTF8.GetBytes(accessKey);
    }

public async Task StartListeningAsync(CancellationToken cancellationToken)
{
    var portStr = _configuration["Remex:CommandPort"];
    if (!int.TryParse(portStr, out int port) || port < 1 || port > 65535)
    {
        port = 8338;
    }

    const int maxPortAttempts = 5;
    int actualPort = port;
    TcpListener? listener = null;

    for (int attempt = 0; attempt < maxPortAttempts; attempt++)
    {
        actualPort = port + attempt;
        if (actualPort > 65535) break;
        _logger.LogInformation("Starting external network listener on port {Port}", actualPort);
        try
        {
            listener = new TcpListener(IPAddress.Any, actualPort);
            listener.Start();
            if (attempt > 0)
            {
                _logger.LogWarning("Primary port {Primary} unavailable; fell back to port {Fallback}", port, actualPort);
            }
            break;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            _logger.LogWarning("Port {Port} is already in use. Trying next port...", actualPort);
            listener = null;
        }
    }

    if (listener is null)
    {
        _logger.LogError("Could not bind to any port in range {Start}-{End}. Network listener disabled.", port, port + maxPortAttempts - 1);
        return;
    }

    _tcpListener = listener;

    try
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listenTask = AcceptClientsAsync(_cts.Token);
        await _listenTask;
    }
    catch (OperationCanceledException)
    {
        // Normal shutdown
    }
    catch (SocketException ex)
    {
        _logger.LogError(ex, "Socket error in external network listener");
        throw;
    }
    catch (IOException ex)
    {
        _logger.LogError(ex, "I/O error in external network listener");
        throw;
    }
    // Let unexpected exceptions (OutOfMemoryException, etc.) propagate naturally
}

public void StopListening()
{
    _logger.LogInformation("Stopping external network listener");
    _cts?.Cancel();
    _tcpListener?.Stop();
    if (_listenTask != null && !_listenTask.IsCompleted)
    {
        try
        {
            _listenTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            _logger.LogWarning(ex, "Error while waiting for listen task to stop");
        }
        catch (ObjectDisposedException)
        {
            // Task was already disposed
        }
    }
    _tcpListener = null;
}

private async Task AcceptClientsAsync(CancellationToken token)
{
    try
    {
        while (!token.IsCancellationRequested)
        {
            if (_tcpListener == null) break;

            var client = await _tcpListener.AcceptTcpClientAsync(token);
            _ = HandleClientSafeAsync(client, token);
        }
    }
    catch (OperationCanceledException)
    {
        // Normal cancellation
    }
    catch (SocketException ex)
    {
        _logger.LogWarning(ex, "Socket error while accepting client connection");
    }
    catch (ObjectDisposedException)
    {
        _logger.LogDebug("TcpListener was disposed, stopping client acceptance");
    }
    // Let unexpected exceptions propagate
}

private async Task HandleClientSafeAsync(TcpClient client, CancellationToken token)
{
    try
    {
        await HandleClientAsync(client, token);
    }
    catch (SocketException ex)
    {
        _logger.LogWarning(ex, "Network error while handling client");
    }
    catch (IOException ex)
    {
        _logger.LogWarning(ex, "I/O error while handling client");
    }
    catch (OperationCanceledException)
    {
        _logger.LogDebug("Client handling cancelled during shutdown");
    }
    catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
    {
        // Top-level safety net for per-client processing
        // Only catch exceptions we can handle; let fatal exceptions propagate
        _logger.LogError(ex, "Unexpected exception in client handler");
    }
}

private async Task HandleClientAsync(TcpClient client, CancellationToken token)
{
    try
    {
        using (client)
        using (var stream = client.GetStream())
        {
            // 1. Read 4-byte length prefix
            var lengthBuffer = new byte[4];
            await stream.ReadExactlyAsync(lengthBuffer, 0, 4, token);
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);

            // 2. Validate length
            if (length <= 0 || length > MaxPayloadSize)
            {
                _logger.LogWarning($"Received invalid payload length: {length}. Closing connection.");
                return;
            }

            // 3. Read payload
            var buffer = new byte[length];
            await stream.ReadExactlyAsync(buffer, 0, length, token);

            var json = Encoding.UTF8.GetString(buffer);
            CommandRequest? request = null;
            try
            {
                request = RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.CommandRequest);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize command request - invalid JSON format");
            }
            catch (NotSupportedException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize command request - unsupported JSON structure");
            }

            CommandResponse response;
            if (request == null)
            {
                response = new CommandResponse(false, "Invalid Request", "Payload could not be parsed as CommandRequest.");
            }
            else if (!ValidateAccessKey(request))
            {
                _logger.LogWarning("Rejected command '{Action}' from client: invalid or missing access key.", request.Action);
                response = new CommandResponse(false, "Unauthorized", "Invalid or missing access key.");
            }
            else
            {
                response = await ExecuteCommandAsync(request);
            }

            // 4. Send length-prefixed response
            var responseJson = RemexJson.Serialize(response, RemexJsonSerializerContext.Default.CommandResponse);
            var responseBytes = Encoding.UTF8.GetBytes(responseJson);
            var responseLengthBuffer = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(responseLengthBuffer, responseBytes.Length);

            await stream.WriteAsync(responseLengthBuffer, token);
            await stream.WriteAsync(responseBytes, token);
        }
    }
    catch (EndOfStreamException)
    {
        _logger.LogDebug("Client closed connection unexpectedly");
    }
    catch (OperationCanceledException)
    {
        _logger.LogDebug("Client handling cancelled during shutdown");
    }
    catch (SocketException ex)
    {
        _logger.LogWarning(ex, "Network error during client communication");
    }
    catch (IOException ex)
    {
        _logger.LogWarning(ex, "I/O error during client communication");
    }
    catch (ObjectDisposedException)
    {
        _logger.LogDebug("Stream or client was disposed during communication");
    }
    // Let unexpected exceptions propagate to HandleClientSafeAsync
}

private async Task<CommandResponse> ExecuteCommandAsync(CommandRequest request)
{
    try
    {
        switch (request.Action.ToUpperInvariant())
        {
            case "SHUTDOWN":
                _commandService.Shutdown(ParseDelaySeconds(request.Parameters));
                return new CommandResponse(true, "Shutdown command executed successfully.", null);
            case "FORCESHUTDOWN":
                _commandService.ForceShutdown(ParseDelaySeconds(request.Parameters));
                return new CommandResponse(true, "Force Shutdown command executed successfully.", null);
            case "RESTART":
                _commandService.Restart(ParseDelaySeconds(request.Parameters));
                return new CommandResponse(true, "Restart command executed successfully.", null);
            case "FORCERESTART":
                _commandService.ForceRestart(ParseDelaySeconds(request.Parameters));
                return new CommandResponse(true, "Force Restart command executed successfully.", null);
            case "RESTARTTOUEFI":
                _commandService.RestartToUefi(ParseDelaySeconds(request.Parameters));
                return new CommandResponse(true, "Restart to UEFI command executed successfully.", null);
            case "SLEEP":
                _commandService.Sleep();
                return new CommandResponse(true, "Sleep command executed successfully.", null);
            case "HIBERNATE":
                _commandService.Hibernate();
                return new CommandResponse(true, "Hibernate command executed successfully.", null);
            case "SIGNOUT":
                _commandService.SignOut();
                return new CommandResponse(true, "SignOut command executed successfully.", null);
            case "LOCK":
                _commandService.Lock();
                return new CommandResponse(true, "Lock command executed successfully.", null);
            case "MONITOROFF":
                _commandService.MonitorOff();
                return new CommandResponse(true, "Monitor off command executed successfully.", null);
            case "WAKEONLAN":
                if (request.Parameters != null && request.Parameters.TryGetValue("MacAddress", out var mac))
                {
                    var broadcastIp = request.Parameters.TryGetValue("BroadcastIp", out var bip) ? bip : "255.255.255.255";
                    var port = request.Parameters.TryGetValue("Port", out var pStr) && int.TryParse(pStr, out var p) ? p : 9;
                    await _wakeOnLanService.WakeAsync(mac, broadcastIp, port);
                    return new CommandResponse(true, $"Wake-on-LAN packet sent to {mac}.", null);
                }
                else
                {
                    return new CommandResponse(false, "Missing MacAddress", "Wake-on-LAN requires a MacAddress parameter.");
                }
            default:
                return new CommandResponse(false, "Unknown Command", $"Command action '{request.Action}' is not supported.");
        }
    }
    catch (SocketException ex)
    {
        _logger.LogError(ex, "Network error executing command {Action}", request.Action);
        return new CommandResponse(false, "Network Error", $"Command '{request.Action}' failed due to network issue");
    }
    catch (InvalidOperationException ex)
    {
        _logger.LogWarning(ex, "Invalid operation executing command {Action}", request.Action);
        return new CommandResponse(false, "Invalid Operation", ex.Message);
    }
    catch (ArgumentException ex)
    {
        _logger.LogWarning(ex, "Invalid argument for command {Action}", request.Action);
        return new CommandResponse(false, "Invalid Argument", ex.Message);
    }
    // Let unexpected exceptions propagate to caller
}

private bool ValidateAccessKey(CommandRequest request)
{
    // If no access key is configured, allow all requests (feature disabled).
    if (_accessKeyBytes.Length == 0)
        return true;

    // The client must supply the key in request.Parameters["AccessKey"].
    if (request.Parameters == null ||
        !request.Parameters.TryGetValue("AccessKey", out var suppliedKey) ||
        string.IsNullOrEmpty(suppliedKey))
        return false;

    return CryptographicOperations.FixedTimeEquals(
        _accessKeyBytes,
        Encoding.UTF8.GetBytes(suppliedKey));
}

public void Dispose()
{
    StopListening();
    _cts?.Dispose();
}

private static int ParseDelaySeconds(Dictionary<string, string>? parameters)
{
    if (parameters == null)
    {
        return 0;
    }

    foreach (var key in new[] { "DelaySeconds", "Seconds", "TimerSeconds" })
    {
        if (parameters.TryGetValue(key, out var raw)
            && int.TryParse(raw, out var parsed)
            && parsed > 0)
        {
            return Math.Clamp(parsed, 0, 315360000);
        }
    }

    return 0;
}
}

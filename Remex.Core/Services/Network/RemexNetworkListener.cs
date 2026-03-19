using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Remex.Core.Models.IPC;
using Remex.Core.Services.Command;

namespace Remex.Core.Services.Network;

public class RemexNetworkListener : INetworkListener, IDisposable
{
    private const int MaxPayloadSize = 1 * 1024 * 1024; // 1MB Limit
    private readonly IConfiguration _configuration;
    private readonly ILogger<RemexNetworkListener> _logger;
    private readonly ISystemCommandService _commandService;
    private readonly IWakeOnLanService _wakeOnLanService;
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
    }

    public async Task StartListeningAsync(CancellationToken cancellationToken)
    {
        var portStr = _configuration["Remex:CommandPort"];
        if (!int.TryParse(portStr, out int port))
        {
            port = 8338; // Default to 8338
        }

        _logger.LogInformation($"Starting external network listener on port {port}");

        try
        {
            _tcpListener = new TcpListener(IPAddress.Any, port);
            _tcpListener.Start();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _listenTask = AcceptClientsAsync(_cts.Token);
            await _listenTask;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            _logger.LogWarning($"Port {port} is already in use. Failed to start network listener.");
            // Do not throw, allow the application to continue running without the external network listener if the port is busy.
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting external network listener.");
            throw;
        }
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
                _listenTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while waiting for listen task to stop");
            }
        }
    }

    private async Task AcceptClientsAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_tcpListener == null) break;

                var client = await _tcpListener.AcceptTcpClientAsync(token);
                _ = HandleClientAsync(client, token); // fire and forget
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accepting client connection");
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
                    request = JsonSerializer.Deserialize<CommandRequest>(json);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize command request");
                }

                CommandResponse response;
                if (request == null)
                {
                    response = new CommandResponse(false, "Invalid Request", "Payload could not be parsed as CommandRequest.");
                }
                else
                {
                    var expectedKey = _configuration["Remex:AccessKey"];
                    if (!string.IsNullOrEmpty(expectedKey) && request.AuthKey != expectedKey)
                    {
                        _logger.LogWarning("Unauthorized TCP access attempt. Invalid AuthKey.");
                        response = new CommandResponse(false, "Unauthorized", "Access denied: Invalid AuthKey.");
                    }
                    else
                    {
                        response = await ExecuteCommandAsync(request);
                    }
                }

                // 4. Send length-prefixed response
                var responseJson = JsonSerializer.Serialize(response);
                var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                var responseLengthBuffer = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(responseLengthBuffer, responseBytes.Length);

                await stream.WriteAsync(responseLengthBuffer, token);
                await stream.WriteAsync(responseBytes, token);
            }
        }
        catch (EndOfStreamException)
        {
            _logger.LogDebug("Client closed connection unexpectedly.");
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client");
        }
    }

    private async Task<CommandResponse> ExecuteCommandAsync(CommandRequest request)
    {
        try
        {
            switch (request.Action.ToUpperInvariant())
            {
                case "SHUTDOWN":
                    _commandService.Shutdown();
                    return new CommandResponse(true, "Shutdown command executed successfully.", null);
                case "RESTART":
                    _commandService.Restart();
                    return new CommandResponse(true, "Restart command executed successfully.", null);
                case "FORCERESTART":
                    _commandService.ForceRestart();
                    return new CommandResponse(true, "Force Restart command executed successfully.", null);
                case "RESTARTTOUEFI":
                    _commandService.RestartToUefi();
                    return new CommandResponse(true, "Restart to UEFI command executed successfully.", null);
                case "LOCK":
                    _commandService.Lock();
                    return new CommandResponse(true, "Lock command executed successfully.", null);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error executing command {request.Action}");
            return new CommandResponse(false, "Command Failed", ex.Message);
        }
    }

    public void Dispose()
    {
        StopListening();
        _cts?.Dispose();
    }
}

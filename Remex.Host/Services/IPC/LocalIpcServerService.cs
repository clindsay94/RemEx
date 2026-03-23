using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services.Command;
using Remex.Core.Services.Network;

namespace Remex.Host.Services.IPC;

public class LocalIpcServerService : BackgroundService
{
    private readonly ILogger<LocalIpcServerService> _logger;
    private readonly ISystemCommandService _commandService;
    private readonly IWakeOnLanService _wakeOnLanService;
    private const string PipeName = "RemExLocalIPC";
    private const string MutexName = @"Global\RemExServiceMutex";
    private Mutex? _mutex;

    public LocalIpcServerService(
        ILogger<LocalIpcServerService> logger,
        ISystemCommandService commandService,
        IWakeOnLanService wakeOnLanService)
    {
        _logger = logger;
        _commandService = commandService;
        _wakeOnLanService = wakeOnLanService;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var mutexSecurity = new MutexSecurity();
                var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                mutexSecurity.AddAccessRule(new MutexAccessRule(worldSid, MutexRights.FullControl, AccessControlType.Allow));

                _mutex = MutexAcl.Create(true, MutexName, out bool createdNew, mutexSecurity);
                if (!createdNew)
                {
                    _logger.LogWarning($"Mutex {MutexName} already exists. IPC Server might be running.");
                }
            }
            else
            {
                _mutex = new Mutex(true, MutexName, out bool createdNew);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Global Mutex for IPC.");
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                NamedPipeServerStream pipeServer;

                if (OperatingSystem.IsWindows())
                {
                    var pipeSecurity = new PipeSecurity();
                    var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                    pipeSecurity.AddAccessRule(new PipeAccessRule(worldSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));

                    pipeServer = NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        0,
                        0,
                        pipeSecurity);
                }
                else
                {
                    pipeServer = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                }

                using (pipeServer)
                {
                    await pipeServer.WaitForConnectionAsync(stoppingToken);

                    // Handle the connection
                    await HandleClientAsync(pipeServer, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in IPC Server execution loop.");
                await Task.Delay(1000, stoppingToken); // Backoff
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipeServer, CancellationToken token)
    {
        try
        {
            var buffer = new byte[8192];
            var bytesRead = await pipeServer.ReadAsync(buffer, token);
            if (bytesRead == 0) return;

            var json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            CommandRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<CommandRequest>(json, RemexJson.Compact);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize command request over IPC.");
            }

            CommandResponse response;

            if (request == null)
            {
                response = new CommandResponse(false, "Invalid Request", "Payload could not be parsed as CommandRequest.");
            }
            else
            {
                response = await ExecuteCommandAsync(request);
            }

            var responseJson = JsonSerializer.Serialize(response, RemexJson.Compact);
            var responseBytes = Encoding.UTF8.GetBytes(responseJson);
            await pipeServer.WriteAsync(responseBytes, token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling IPC client connection.");
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

    public override void Dispose()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Ignore if we don't own the mutex
        }

        _mutex?.Dispose();
        base.Dispose();
    }
}

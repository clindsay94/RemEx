using System;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Host.Services;

/// <summary>
/// A background service that listens for local IPC commands from the Client UI.
/// </summary>
public class IpcHostServer : BackgroundService
{
    private readonly ILogger<IpcHostServer> _logger;
    private readonly ISystemCommandService _systemCommandService;

    public IpcHostServer(ILogger<IpcHostServer> logger, ISystemCommandService systemCommandService)
    {
        _logger = logger;
        _systemCommandService = systemCommandService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting RemExLocalIPC Named Pipe Server...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(
                    RemExLocalIPC.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(stoppingToken);

                // Handle in background so we can listen for the next client immediately
                // Pass ownership of the server stream to HandleClientAsync
                _ = HandleClientAsync(server, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting IPC Server.");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new System.IO.StreamReader(server, leaveOpen: true);
            using var writer = new System.IO.StreamWriter(server, leaveOpen: true) { AutoFlush = true };

            var requestJson = await reader.ReadLineAsync();
            if (requestJson == null) return;

            var request = JsonSerializer.Deserialize<CommandRequest>(requestJson);
            if (request == null) return;

            CommandResponse response;

            switch (request.Action)
            {
                case "LaunchApp":
                    if (string.IsNullOrEmpty(request.TargetPath))
                    {
                        response = new CommandResponse(false, "No target path provided.");
                        break;
                    }

                    try
                    {
                        await _systemCommandService.LaunchAppAsync(request.TargetPath);
                        response = new CommandResponse(true, "App launched successfully.");
                    }
                    catch (Exception ex)
                    {
                        response = new CommandResponse(false, $"Error launching app: {ex.Message}");
                    }
                    break;

                default:
                    response = new CommandResponse(false, $"Unknown IPC Action: {request.Action}");
                    break;
            }

            var responseJson = JsonSerializer.Serialize(response);
            await writer.WriteLineAsync(responseJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling IPC client connection.");
        }
        finally
        {
            if (server.IsConnected)
                server.Disconnect();
            server.Dispose();
        }
    }
}

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
    private readonly IAppLauncherService _appLauncherService;

    public IpcHostServer(ILogger<IpcHostServer> logger, IAppLauncherService appLauncherService)
    {
        _logger = logger;
        _appLauncherService = appLauncherService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting RemExLocalIPC Named Pipe Server...");

        while (!stoppingToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                RemExLocalIPC.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await server.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                break;
            }
            catch (Exception ex)
            {
                await server.DisposeAsync();
                _logger.LogError(ex, "Error waiting for IPC connection.");
                await Task.Delay(1000, stoppingToken);
            }

            // Handle in background so we can listen for the next client immediately.
            // Ownership of the server stream is transferred to HandleClientAsync.
            _ = HandleClientAsync(server, stoppingToken);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new System.IO.StreamReader(server, leaveOpen: true);
            using var writer = new System.IO.StreamWriter(server, leaveOpen: true) { AutoFlush = true };

            var requestJson = await reader.ReadLineAsync(cancellationToken);
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
                        await _appLauncherService.LaunchAppAsync(request.TargetPath);
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

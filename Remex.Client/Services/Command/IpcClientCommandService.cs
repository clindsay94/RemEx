using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Tasks;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services;
using Remex.Core.Services.Command;

namespace Remex.Client.Services.Command;

public class IpcClientCommandService : ISystemCommandService
{
    private const string PipeName = RemExLocalIPC.PipeName;

    public Task Shutdown(int delaySeconds = 0) => SendCommandAsync(new CommandRequest("Shutdown", CreateDelayParameters(delaySeconds)));
    public Task ForceShutdown(int delaySeconds = 0) => SendCommandAsync(new CommandRequest("ForceShutdown", CreateDelayParameters(delaySeconds)));
    public Task Restart(int delaySeconds = 0) => SendCommandAsync(new CommandRequest("Restart", CreateDelayParameters(delaySeconds)));
    public Task ForceRestart(int delaySeconds = 0) => SendCommandAsync(new CommandRequest("ForceRestart", CreateDelayParameters(delaySeconds)));
    public Task RestartToUefi(int delaySeconds = 0) => SendCommandAsync(new CommandRequest("RestartToUefi", CreateDelayParameters(delaySeconds)));
    public Task Sleep() => SendCommandAsync(new CommandRequest("Sleep", null));
    public Task Hibernate() => SendCommandAsync(new CommandRequest("Hibernate", null));
    public Task SignOut() => SendCommandAsync(new CommandRequest("SignOut", null));
    public Task Lock() => SendCommandAsync(new CommandRequest("Lock", null));
    public Task MonitorOff() => SendCommandAsync(new CommandRequest("MonitorOff", null));

    private static Dictionary<string, string>? CreateDelayParameters(int delaySeconds)
    {
        if (delaySeconds <= 0)
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            ["DelaySeconds"] = delaySeconds.ToString()
        };
    }

    private async Task SendCommandAsync(CommandRequest request)
    {
        try
        {
            using var pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipeClient.ConnectAsync(5000); // 5 seconds timeout

            var requestBytes = RemexJson.SerializeToUtf8Bytes(request, RemexJsonSerializerContext.Default.CommandRequest);
            await RemExLocalIPC.WriteFrameAsync(pipeClient, requestBytes);

            var responseBytes = await RemExLocalIPC.ReadFrameAsync(pipeClient);
            if (responseBytes != null)
            {
                var response = RemexJson.Deserialize(responseBytes, RemexJsonSerializerContext.Default.CommandResponse);
                if (response != null && !response.Success)
                {
                    throw new Exception($"Command Failed: {response.Message}. Details: {response.ErrorDetails}");
                }
            }
        }
        catch (TimeoutException ex)
        {
            throw new Exception($"Failed to send command over IPC (connection timeout): {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new Exception($"Failed to send command over IPC (I/O error): {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new Exception($"Failed to send command over IPC (JSON error): {ex.Message}", ex);
        }
    }
}

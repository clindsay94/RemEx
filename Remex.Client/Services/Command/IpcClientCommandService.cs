using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services.Command;

namespace Remex.Client.Services.Command;

public class IpcClientCommandService : ISystemCommandService
{
    private const string PipeName = "RemExLocalIPC";

    public void Shutdown(int delaySeconds = 0) => SendCommandAsync(new CommandRequest("Shutdown", CreateDelayParameters(delaySeconds))).Wait();
    public void ForceShutdown(int delaySeconds = 0) => SendCommandAsync(new CommandRequest("ForceShutdown", CreateDelayParameters(delaySeconds))).Wait();
    public void Restart(int delaySeconds = 0) => SendCommandAsync(new CommandRequest("Restart", CreateDelayParameters(delaySeconds))).Wait();
    public void ForceRestart(int delaySeconds = 0) => SendCommandAsync(new CommandRequest("ForceRestart", CreateDelayParameters(delaySeconds))).Wait();
    public void RestartToUefi(int delaySeconds = 0) => SendCommandAsync(new CommandRequest("RestartToUefi", CreateDelayParameters(delaySeconds))).Wait();
    public void Sleep() => SendCommandAsync(new CommandRequest("Sleep", null)).Wait();
    public void Hibernate() => SendCommandAsync(new CommandRequest("Hibernate", null)).Wait();
    public void SignOut() => SendCommandAsync(new CommandRequest("SignOut", null)).Wait();
    public void Lock() => SendCommandAsync(new CommandRequest("Lock", null)).Wait();
    public void MonitorOff() => SendCommandAsync(new CommandRequest("MonitorOff", null)).Wait();

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

            var json = JsonSerializer.Serialize(request, RemexJson.Compact);
            var bytes = Encoding.UTF8.GetBytes(json);
            await pipeClient.WriteAsync(bytes, 0, bytes.Length);

            var buffer = new byte[8192];
            var bytesRead = await pipeClient.ReadAsync(buffer, 0, buffer.Length);
            if (bytesRead > 0)
            {
                var responseJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var response = JsonSerializer.Deserialize<CommandResponse>(responseJson, RemexJson.Compact);
                if (response != null && !response.Success)
                {
                    throw new Exception($"Command Failed: {response.Message}. Details: {response.ErrorDetails}");
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to send command over IPC: {ex.Message}", ex);
        }
    }
}

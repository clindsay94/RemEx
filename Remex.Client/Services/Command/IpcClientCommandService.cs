using System;
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

    public void Shutdown() => SendCommandAsync(new CommandRequest("Shutdown", null)).Wait();
    public void Restart() => SendCommandAsync(new CommandRequest("Restart", null)).Wait();
    public void ForceRestart() => SendCommandAsync(new CommandRequest("ForceRestart", null)).Wait();
    public void RestartToUefi() => SendCommandAsync(new CommandRequest("RestartToUefi", null)).Wait();
    public void Lock() => SendCommandAsync(new CommandRequest("Lock", null)).Wait();

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

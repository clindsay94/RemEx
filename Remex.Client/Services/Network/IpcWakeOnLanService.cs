using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Remex.Core.Models.IPC;
using Remex.Core.Services.Network;

namespace Remex.Client.Services.Network;

public class IpcWakeOnLanService : IWakeOnLanService
{
    private const string PipeName = "RemExLocalIPC";

    public async Task WakeAsync(string macAddress, string broadcastIp = "255.255.255.255", int port = 9)
    {
        var parameters = new Dictionary<string, string>
        {
            { "MacAddress", macAddress },
            { "BroadcastIp", broadcastIp },
            { "Port", port.ToString() }
        };

        var request = new CommandRequest("WakeOnLan", parameters);

        try
        {
            using var pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipeClient.ConnectAsync(5000);

            var json = JsonSerializer.Serialize(request);
            var bytes = Encoding.UTF8.GetBytes(json);
            await pipeClient.WriteAsync(bytes, 0, bytes.Length);

            var buffer = new byte[8192];
            var bytesRead = await pipeClient.ReadAsync(buffer, 0, buffer.Length);
            if (bytesRead > 0)
            {
                var responseJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var response = JsonSerializer.Deserialize<CommandResponse>(responseJson);
                if (response != null && !response.Success)
                {
                    throw new Exception($"WakeOnLan Failed: {response.Message}. Details: {response.ErrorDetails}");
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to send WakeOnLan command over IPC: {ex.Message}", ex);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Tasks;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services;
using Remex.Core.Services.Network;

namespace Remex.Client.Services.Network;

public class IpcWakeOnLanService : IWakeOnLanService
{
    private const string PipeName = RemExLocalIPC.PipeName;

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

            var requestBytes = RemexJson.SerializeToUtf8Bytes(request, RemexJsonSerializerContext.Default.CommandRequest);
            await RemExLocalIPC.WriteFrameAsync(pipeClient, requestBytes);

            var responseBytes = await RemExLocalIPC.ReadFrameAsync(pipeClient);
            if (responseBytes != null)
            {
                var response = RemexJson.Deserialize(responseBytes, RemexJsonSerializerContext.Default.CommandResponse);
                if (response != null && !response.Success)
                {
                    throw new Exception($"WakeOnLan Failed: {response.Message}. Details: {response.ErrorDetails}");
                }
            }
        }
        catch (TimeoutException ex)
        {
            throw new Exception($"Failed to send WakeOnLan command over IPC (connection timeout): {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new Exception($"Failed to send WakeOnLan command over IPC (I/O error): {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new Exception($"Failed to send WakeOnLan command over IPC (JSON error): {ex.Message}", ex);
        }
    }
}

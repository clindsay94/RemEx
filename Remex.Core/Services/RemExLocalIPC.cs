using System;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Remex.Core.Services;

/// <summary>
/// A shared interface for IPC communication between the UI Client and the Host Service.
/// This handles local UI-to-Service delegation over a Named Pipe.
/// </summary>
public static class RemExLocalIPC
{
    public const string PipeName = "RemexIPC";

    /// <summary>
    /// Sends a command to the IPC Named Pipe server.
    /// </summary>
    public static async Task<Models.CommandResponse> SendCommandAsync(Models.CommandRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, cancellationToken);

            var json = JsonSerializer.Serialize(request);
            var writer = new System.IO.StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync(json);

            var reader = new System.IO.StreamReader(client);
            var responseJson = await reader.ReadLineAsync();

            if (responseJson == null)
            {
                return new Models.CommandResponse(false, "No response from service.");
            }

            return JsonSerializer.Deserialize<Models.CommandResponse>(responseJson)
                   ?? new Models.CommandResponse(false, "Failed to deserialize response.");
        }
        catch (Exception ex)
        {
            return new Models.CommandResponse(false, $"IPC Error: {ex.Message}");
        }
    }
}

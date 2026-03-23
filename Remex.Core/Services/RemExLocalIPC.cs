using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;

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
    public static async Task<CommandResponse> SendCommandAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, cancellationToken);

            var json = RemexJson.Serialize(request, RemexJsonSerializerContext.Default.CommandRequest);
            await using var writer = new System.IO.StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(json);

            using var reader = new System.IO.StreamReader(client, leaveOpen: true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var responseJson = await reader.ReadLineAsync(cts.Token);

            if (responseJson == null)
            {
                return new CommandResponse(false, "No response from service.", null);
            }

                 return RemexJson.Deserialize(responseJson, RemexJsonSerializerContext.Default.CommandResponse)
                   ?? new CommandResponse(false, "Failed to deserialize response.", null);
        }
        catch (Exception ex)
        {
            return new CommandResponse(false, $"IPC Error: {ex.Message}", ex.ToString());
        }
    }
}

using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services;

namespace Remex.Desktop.Services.Security;

public sealed class IpcPairingPinQueryService : IPairingPinQueryService
{
    private readonly string _pipeName;

    public IpcPairingPinQueryService(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? RemExLocalIPC.PipeName : pipeName;
    }

    public Task<PairingPinInfo?> GetActivePairingPinAsync(CancellationToken cancellationToken = default)
        => QueryPinAsync("GETPAIRINGPIN", cancellationToken);

    public Task<PairingPinInfo?> GeneratePairingPinAsync(CancellationToken cancellationToken = default)
        => QueryPinAsync("GENERATEPAIRINGPIN", cancellationToken);

    private async Task<PairingPinInfo?> QueryPinAsync(string action, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, cancellationToken);

            var request = new CommandRequest(action, null);
            var requestBytes = RemexJson.SerializeToUtf8Bytes(request, RemexJsonSerializerContext.Default.CommandRequest);
            await RemExLocalIPC.WriteFrameAsync(client, requestBytes, cancellationToken);

            var responseBytes = await RemExLocalIPC.ReadFrameAsync(client, cancellationToken);
            if (responseBytes == null)
                return null;

            var response = RemexJson.Deserialize(responseBytes, RemexJsonSerializerContext.Default.CommandResponse);
            return response?.Success == true ? response.PairingPinInfo : null;
        }
        catch (UnauthorizedAccessException ex)
        {
            // An ACL denial is distinct from "no server running" / clean disconnect (IPC-8 / RemEx-b3m).
            // This legacy interface only returns PairingPinInfo?, so a typed permission result can't be
            // surfaced here (the Core path RemExLocalIPC.SendCommandAsync now does that); at minimum emit
            // a diagnostic naming the exception type instead of silently collapsing to null.
            System.Diagnostics.Trace.TraceWarning(
                $"Pairing PIN query '{action}' denied by pipe ACL: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        catch
        {
            return null;
        }
    }
}

using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services;

namespace Remex.Client.Services.Security;

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
        catch
        {
            return null;
        }
    }
}

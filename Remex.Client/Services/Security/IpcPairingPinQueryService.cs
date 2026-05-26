using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;

namespace Remex.Client.Services.Security;

public sealed class IpcPairingPinQueryService : IPairingPinQueryService
{
    private readonly string _pipeName;

    public IpcPairingPinQueryService(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? "RemExLocalIPC" : pipeName;
    }

    public async Task<PairingPinInfo?> GetActivePairingPinAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, cancellationToken);

            var request = new CommandRequest("GETPAIRINGPIN", null);
            var json = JsonSerializer.Serialize(request, RemexJson.Compact);
            var bytes = Encoding.UTF8.GetBytes(json);

            await client.WriteAsync(bytes, cancellationToken);

            var buffer = new byte[8192];
            var bytesRead = await client.ReadAsync(buffer, cancellationToken);
            if (bytesRead <= 0)
                return null;

            var responseJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var response = JsonSerializer.Deserialize<CommandResponse>(responseJson, RemexJson.Compact);
            return response?.Success == true ? response.PairingPinInfo : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<PairingPinInfo?> GeneratePairingPinAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, cancellationToken);

            var request = new CommandRequest("GENERATEPAIRINGPIN", null);
            var json = JsonSerializer.Serialize(request, RemexJson.Compact);
            var bytes = Encoding.UTF8.GetBytes(json);

            await client.WriteAsync(bytes, cancellationToken);

            var buffer = new byte[8192];
            var bytesRead = await client.ReadAsync(buffer, cancellationToken);
            if (bytesRead <= 0)
                return null;

            var responseJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var response = JsonSerializer.Deserialize<CommandResponse>(responseJson, RemexJson.Compact);
            return response?.Success == true ? response.PairingPinInfo : null;
        }
        catch
        {
            return null;
        }
    }
}

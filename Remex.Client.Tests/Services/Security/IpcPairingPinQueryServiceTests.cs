using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Remex.Client.Services.Security;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;

namespace Remex.Client.Tests.Services.Security;

public sealed class IpcPairingPinQueryServiceTests
{
    [Fact]
    public async Task GetActivePairingPinAsync_UsesStandalonePipeAndReturnsPin()
    {
        var pipeName = $"RemExLocalIPC.Tests.{Guid.NewGuid():N}";
        var service = new IpcPairingPinQueryService(pipeName);
        var pinInfo = new PairingPinInfo("246810", DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());

        var serverTask = RunServerAsync(pipeName, pinInfo);

        var result = await service.GetActivePairingPinAsync();

        Assert.NotNull(result);
        Assert.Equal(pinInfo.Pin, result!.Pin);
        Assert.Equal(pinInfo.ExpiresAtUnixMs, result.ExpiresAtUnixMs);

        await serverTask;
    }

    private static async Task RunServerAsync(string pipeName, PairingPinInfo pinInfo)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await server.WaitForConnectionAsync();

        var buffer = new byte[8192];
        var bytesRead = await server.ReadAsync(buffer);
        var requestJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        var request = JsonSerializer.Deserialize<CommandRequest>(requestJson, RemexJson.Compact);
        Assert.NotNull(request);
        Assert.Equal("GETPAIRINGPIN", request!.Action);

        var response = new CommandResponse(true, "ok", null) { PairingPinInfo = pinInfo };
        var responseJson = JsonSerializer.Serialize(response, RemexJson.Compact);
        var responseBytes = Encoding.UTF8.GetBytes(responseJson);
        await server.WriteAsync(responseBytes);
        await server.FlushAsync();
        server.Disconnect();
    }
}

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Remex.Client.Services.Security;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services;

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

    [Fact]
    public async Task GeneratePairingPinAsync_UsesStandalonePipeAndGeneratesPin()
    {
        var pipeName = $"RemExLocalIPC.Tests.{Guid.NewGuid():N}";
        var service = new IpcPairingPinQueryService(pipeName);
        var pinInfo = new PairingPinInfo("135790", DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());

        var serverTask = RunServerAsync(pipeName, pinInfo, "GENERATEPAIRINGPIN");

        var result = await service.GeneratePairingPinAsync();

        Assert.NotNull(result);
        Assert.Equal(pinInfo.Pin, result!.Pin);
        Assert.Equal(pinInfo.ExpiresAtUnixMs, result.ExpiresAtUnixMs);

        await serverTask;
    }

    private static Task RunServerAsync(string pipeName, PairingPinInfo pinInfo)
    {
        return RunServerAsync(pipeName, pinInfo, "GETPAIRINGPIN");
    }

    private static async Task RunServerAsync(string pipeName, PairingPinInfo pinInfo, string expectedAction)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await server.WaitForConnectionAsync();

        var requestBytes = await RemExLocalIPC.ReadFrameAsync(server);
        Assert.NotNull(requestBytes);
        var requestJson = Encoding.UTF8.GetString(requestBytes!);
        var request = JsonSerializer.Deserialize<CommandRequest>(requestJson, RemexJson.Compact);
        Assert.NotNull(request);
        Assert.Equal(expectedAction, request!.Action);

        var response = new CommandResponse(true, "ok", null) { PairingPinInfo = pinInfo };
        var responseJson = JsonSerializer.Serialize(response, RemexJson.Compact);
        var responseBytes = Encoding.UTF8.GetBytes(responseJson);
        await RemExLocalIPC.WriteFrameAsync(server, responseBytes);
        await server.FlushAsync();
        server.Disconnect();
    }
}

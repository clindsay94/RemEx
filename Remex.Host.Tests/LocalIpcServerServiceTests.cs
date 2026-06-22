using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Core.Models.IPC;
using Remex.Core.Services;
using Remex.Core.Services.Command;
using Remex.Core.Services.Network;
using Remex.Core.Services.Security;
using Remex.Host.Services.IPC;
using Remex.Host.Services.Security;

namespace Remex.Host.Tests;

public class LocalIpcServerServiceTests
{
    private static readonly MethodInfo ExecuteCommandAsyncMethod =
        typeof(LocalIpcServerService).GetMethod(
            "ExecuteCommandAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public async Task StartPairingCommands_ReusesExistingSession()
    {
        var pairingService = new PairingService(
            NullLogger<PairingService>.Instance,
            new FakeCertificateService());
        var ipcService = new LocalIpcServerService(
            NullLogger<LocalIpcServerService>.Instance,
            new Mock<ISystemCommandService>().Object,
            new Mock<IWakeOnLanService>().Object,
            pairingService,
            new Mock<IAppLauncherService>().Object);

        var first = await InvokeExecuteCommandAsync(ipcService, new CommandRequest("STARTPAIRING", null));
        var second = await InvokeExecuteCommandAsync(ipcService, new CommandRequest("GENERATEPAIRINGPIN", null));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.NotNull(first.PairingPinInfo);
        Assert.NotNull(second.PairingPinInfo);
        Assert.Equal(first.PairingPinInfo, second.PairingPinInfo);
    }

    private static async Task<CommandResponse> InvokeExecuteCommandAsync(LocalIpcServerService service, CommandRequest request)
    {
        var task = (Task<CommandResponse>)ExecuteCommandAsyncMethod.Invoke(service, new object[] { request })!;
        return await task;
    }

    private sealed class FakeCertificateService : ICertificateService
    {
        public Task<X509Certificate2> GetOrCreateCertificateAsync(CancellationToken ct) =>
            Task.FromException<X509Certificate2>(new NotSupportedException());

        public string GetSpkiSha256Base64() => "fake-spki-hash==";

        public Task RegenerateAsync(CancellationToken ct) => Task.CompletedTask;
    }
}

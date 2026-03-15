using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Host.Services;

namespace Remex.Host.Tests;

public class IpcHostServerTests : IAsyncLifetime
{
    private readonly FakeSystemCommandService _commandService = new();
    private readonly IpcHostServer _server;
    private readonly CancellationTokenSource _cts = new();

    public IpcHostServerTests()
    {
        _server = new IpcHostServer(NullLogger<IpcHostServer>.Instance, _commandService);
    }

    public async Task InitializeAsync()
    {
        await _server.StartAsync(_cts.Token);
        // The per-command ConnectAsync(5000) call in SendIpcCommand handles
        // waiting until the pipe is ready, so no arbitrary delay is needed here.
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        await _server.StopAsync(CancellationToken.None);
        _cts.Dispose();
    }

    [Fact]
    public async Task SendCommand_UnknownAction_ReturnsFailure()
    {
        var response = await SendIpcCommand(new CommandRequest("UnknownAction"));

        Assert.False(response.Success);
        Assert.Contains("Unknown IPC Action", response.Message);
    }

    [Fact]
    public async Task SendCommand_LaunchApp_MissingPath_ReturnsFailure()
    {
        var response = await SendIpcCommand(new CommandRequest("LaunchApp", null));

        Assert.False(response.Success);
        Assert.Contains("No target path provided", response.Message);
    }

    [Fact]
    public async Task SendCommand_LaunchApp_ValidPath_ReturnsSuccess()
    {
        var response = await SendIpcCommand(new CommandRequest("LaunchApp", "/usr/bin/true"));

        Assert.True(response.Success);
        Assert.Contains("launched successfully", response.Message);
        Assert.Contains("/usr/bin/true", _commandService.LaunchedPaths);
    }

    private static async Task<CommandResponse> SendIpcCommand(CommandRequest request)
    {
        using var client = new NamedPipeClientStream(".", RemExLocalIPC.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);

        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, leaveOpen: true);

        var json = JsonSerializer.Serialize(request);
        await writer.WriteLineAsync(json);

        var responseJson = await reader.ReadLineAsync();
        Assert.NotNull(responseJson);

        return JsonSerializer.Deserialize<CommandResponse>(responseJson!)
               ?? throw new InvalidOperationException("Failed to deserialize IPC response.");
    }

    /// <summary>
    /// A simple test double for ISystemCommandService that records launched paths.
    /// </summary>
    private sealed class FakeSystemCommandService : ISystemCommandService
    {
        public List<string> LaunchedPaths { get; } = new();

        public Task LaunchAppAsync(string targetPath)
        {
            LaunchedPaths.Add(targetPath);
            return Task.CompletedTask;
        }
    }
}

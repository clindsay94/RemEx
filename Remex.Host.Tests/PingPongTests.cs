using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc.Testing;
using Remex.Core;
using Remex.Core.Messages;

namespace Remex.Host.Tests;

public class PingPongTests : IClassFixture<WebApplicationFactory<Program>>
{
    private class MockCommandService : Remex.Core.Services.Command.ISystemCommandService
    {
        public void Lock() { }
        public void Shutdown() { }
        public void Restart() { }
        public void ForceRestart() { }
        public void RestartToUefi() { }
    }

    private class MockLauncherStorageService : Remex.Core.Services.ILauncherStorageService
    {
        public Task<List<Remex.Core.Models.AppEntry>> LoadEntriesAsync() => Task.FromResult(new List<Remex.Core.Models.AppEntry>());
        public Task SaveEntriesAsync(System.Collections.Generic.IEnumerable<Remex.Core.Models.AppEntry> entries) => Task.CompletedTask;
    }

    private class MockProcessMonitorService : Remex.Core.Services.IProcessMonitorService
    {
        public List<Remex.Core.Models.ProcessInfo> Processes { get; set; } = new();
        public bool KillResult { get; set; } = true;

        public Task<List<Remex.Core.Models.ProcessInfo>> GetProcessesAsync()
            => Task.FromResult(Processes);

        public bool KillProcess(int processId) => KillResult;
    }

    [Fact]
    public async Task Command_Lock_ReturnsSuccess()
    {
        var factory = GetFactory();

        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        var cmd = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = "Lock",
        };
        await MessageSerializer.SendAsync(ws, cmd, CancellationToken.None);

        RemexMessage? response = null;
        while (true)
        {
            var msg = await MessageSerializer.ReceiveAsync(ws, CancellationToken.None);
            if (msg?.Type == MessageTypes.CommandResponse) { response = msg; break; }
            if (msg == null) break;
        }

        Assert.NotNull(response);
        Assert.True(response!.CommandSuccess);
    }

    private readonly WebApplicationFactory<Program> _factory;

    private WebApplicationFactory<Program> GetFactory()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<Remex.Core.Services.Command.ISystemCommandService, MockCommandService>();
                services.AddSingleton<Remex.Core.Services.ILauncherStorageService, MockLauncherStorageService>();
            });
        });
    }

    private WebApplicationFactory<Program> GetFactoryWithProcessMonitor(MockProcessMonitorService processMonitor)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<Remex.Core.Services.Command.ISystemCommandService, MockCommandService>();
                services.AddSingleton<Remex.Core.Services.ILauncherStorageService, MockLauncherStorageService>();
                services.AddSingleton<Remex.Core.Services.IProcessMonitorService>(processMonitor);
            });
        });
    }

    public PingPongTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PingPong_SendPing_ReceivesPong()
    {
        var wsClient = GetFactory().Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        // Send ping
        var ping = new RemexMessage { Type = MessageTypes.Ping, Timestamp = 12345 };
        await MessageSerializer.SendAsync(ws, ping);

        // Receive pong
        var pong = await ReceivePongAsync(ws);

        Assert.NotNull(pong);
        Assert.Equal(MessageTypes.Pong, pong!.Type);
    }

    [Fact]
    public async Task PingPong_EchoesTimestamp()
    {
        var wsClient = GetFactory().Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        long sentTimestamp = 987654321;
        var ping = new RemexMessage { Type = MessageTypes.Ping, Timestamp = sentTimestamp };
        await MessageSerializer.SendAsync(ws, ping);

        var pong = await ReceivePongAsync(ws);

        Assert.NotNull(pong);
        Assert.Equal(sentTimestamp, pong!.Timestamp);
    }

    [Fact]
    public async Task PingPong_MultiplePings_AllGetPongs()
    {
        var wsClient = GetFactory().Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        for (int i = 0; i < 5; i++)
        {
            var ping = new RemexMessage { Type = MessageTypes.Ping, Timestamp = i };
            await MessageSerializer.SendAsync(ws, ping);

            var pong = await ReceivePongAsync(ws);

            Assert.NotNull(pong);
            Assert.Equal(MessageTypes.Pong, pong!.Type);
            Assert.Equal(i, pong.Timestamp);
        }

        // Note: Don't call CloseAsync — TestServer's TestWebSocket disposes
        // the connection when the test fixture tears down. Calling CloseAsync
        // on an already-disposed TestWebSocket throws ObjectDisposedException.
    }

    [Fact]
    public async Task ProcessListRequest_Returns_ProcessListSync()
    {
        var monitor = new MockProcessMonitorService
        {
            Processes = new List<Remex.Core.Models.ProcessInfo>
            {
                new() { Id = 42, Name = "TestProc" },
                new() { Id = 99, Name = "AnotherProc" },
            }
        };
        var wsClient = GetFactoryWithProcessMonitor(monitor).Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        var request = new RemexMessage { Type = MessageTypes.ProcessListRequest };
        await MessageSerializer.SendAsync(ws, request, CancellationToken.None);

        var response = await ReceiveMessageOfTypeAsync(ws, MessageTypes.ProcessListSync);

        Assert.NotNull(response);
        Assert.Equal(MessageTypes.ProcessListSync, response!.Type);
        Assert.NotNull(response.ProcessList);
        Assert.Equal(2, response.ProcessList!.Count);
        Assert.Contains(response.ProcessList, p => p.Id == 42 && p.Name == "TestProc");
        Assert.Contains(response.ProcessList, p => p.Id == 99 && p.Name == "AnotherProc");
    }

    [Fact]
    public async Task KillProcess_ValidPid_KillSucceeds_ReturnsSuccess()
    {
        var monitor = new MockProcessMonitorService { KillResult = true };
        var wsClient = GetFactoryWithProcessMonitor(monitor).Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        var cmd = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = "KillProcess",
            CommandParameters = new Dictionary<string, string> { ["ProcessId"] = "1234" },
        };
        await MessageSerializer.SendAsync(ws, cmd, CancellationToken.None);

        var response = await ReceiveMessageOfTypeAsync(ws, MessageTypes.CommandResponse);

        Assert.NotNull(response);
        Assert.True(response!.CommandSuccess);
        Assert.Contains("killed", response.CommandMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KillProcess_ValidPid_KillFails_ReturnsFailure()
    {
        var monitor = new MockProcessMonitorService { KillResult = false };
        var wsClient = GetFactoryWithProcessMonitor(monitor).Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        var cmd = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = "KillProcess",
            CommandParameters = new Dictionary<string, string> { ["ProcessId"] = "9999" },
        };
        await MessageSerializer.SendAsync(ws, cmd, CancellationToken.None);

        var response = await ReceiveMessageOfTypeAsync(ws, MessageTypes.CommandResponse);

        Assert.NotNull(response);
        Assert.False(response!.CommandSuccess);
        Assert.Contains("Failed to kill", response.CommandMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KillProcess_MissingProcessId_ReturnsFailure()
    {
        var monitor = new MockProcessMonitorService();
        var wsClient = GetFactoryWithProcessMonitor(monitor).Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        var cmd = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = "KillProcess",
            // No CommandParameters
        };
        await MessageSerializer.SendAsync(ws, cmd, CancellationToken.None);

        var response = await ReceiveMessageOfTypeAsync(ws, MessageTypes.CommandResponse);

        Assert.NotNull(response);
        Assert.False(response!.CommandSuccess);
        Assert.Contains("Missing or invalid ProcessId", response.CommandMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KillProcess_InvalidProcessId_ReturnsFailure()
    {
        var monitor = new MockProcessMonitorService();
        var wsClient = GetFactoryWithProcessMonitor(monitor).Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        var cmd = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = "KillProcess",
            CommandParameters = new Dictionary<string, string> { ["ProcessId"] = "not-a-number" },
        };
        await MessageSerializer.SendAsync(ws, cmd, CancellationToken.None);

        var response = await ReceiveMessageOfTypeAsync(ws, MessageTypes.CommandResponse);

        Assert.NotNull(response);
        Assert.False(response!.CommandSuccess);
        Assert.Contains("Missing or invalid ProcessId", response.CommandMessage, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<RemexMessage?> ReceivePongAsync(WebSocket ws)
    {
        while (true)
        {
            var message = await MessageSerializer.ReceiveAsync(ws);
            if (message == null || message.Type == MessageTypes.Pong)
            {
                return message;
            }
            // Ignore telemetry spam in these specific tests
        }
    }

    private async Task<RemexMessage?> ReceiveMessageOfTypeAsync(WebSocket ws, string expectedType)
    {
        while (true)
        {
            var message = await MessageSerializer.ReceiveAsync(ws, CancellationToken.None);
            if (message == null || message.Type == expectedType)
            {
                return message;
            }
            // Skip unrelated messages (e.g. telemetry, sync messages sent on connect)
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
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
        private readonly List<Remex.Core.Models.ProcessInfo> _processes;
        private readonly bool _killResult;

        public MockProcessMonitorService(
            List<Remex.Core.Models.ProcessInfo>? processes = null,
            bool killResult = true)
        {
            _processes = processes ?? new List<Remex.Core.Models.ProcessInfo>();
            _killResult = killResult;
        }

        public Task<List<Remex.Core.Models.ProcessInfo>> GetProcessesAsync()
            => Task.FromResult(_processes);

        public bool KillProcess(int processId) => _killResult;
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

    private WebApplicationFactory<Program> GetFactory(
        Remex.Core.Services.IProcessMonitorService? processMonitor = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<Remex.Core.Services.Command.ISystemCommandService, MockCommandService>();
                services.AddSingleton<Remex.Core.Services.ILauncherStorageService, MockLauncherStorageService>();
                if (processMonitor is not null)
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

    private static async Task<RemexMessage?> ReceiveMessageOfTypeAsync(
        WebSocket ws, string targetType, int timeoutSeconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (true)
        {
            try
            {
                var msg = await MessageSerializer.ReceiveAsync(ws, cts.Token);
                if (msg == null) return null;
                if (msg.Type == targetType) return msg;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Timeout elapsed before a matching message was received.
                return null;
            }
        }
    }

    [Fact]
    public async Task ProcessListRequest_ReturnsProcessListSyncType()
    {
        var processes = new List<Remex.Core.Models.ProcessInfo>
        {
            new() { Id = 1, Name = "testproc" }
        };
        var mock = new MockProcessMonitorService(processes);
        var wsClient = GetFactory(mock).Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        await MessageSerializer.SendAsync(ws,
            new RemexMessage { Type = MessageTypes.ProcessListRequest }, CancellationToken.None);

        var response = await ReceiveMessageOfTypeAsync(ws, MessageTypes.ProcessListSync);

        Assert.NotNull(response);
        Assert.Equal(MessageTypes.ProcessListSync, response!.Type);
    }

    [Fact]
    public async Task ProcessListRequest_ReturnsExpectedProcesses()
    {
        var processes = new List<Remex.Core.Models.ProcessInfo>
        {
            new() { Id = 42, Name = "chrome" },
            new() { Id = 99, Name = "notepad" }
        };
        var mock = new MockProcessMonitorService(processes);
        var wsClient = GetFactory(mock).Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        await MessageSerializer.SendAsync(ws,
            new RemexMessage { Type = MessageTypes.ProcessListRequest }, CancellationToken.None);

        var response = await ReceiveMessageOfTypeAsync(ws, MessageTypes.ProcessListSync);

        Assert.NotNull(response?.ProcessList);
        Assert.Equal(2, response!.ProcessList!.Count);
        Assert.Contains(response.ProcessList, p => p.Id == 42 && p.Name == "chrome");
        Assert.Contains(response.ProcessList, p => p.Id == 99 && p.Name == "notepad");
    }

    [Fact]
    public async Task Command_KillProcess_ValidPid_KillSucceeds_ReturnsSuccess()
    {
        var mock = new MockProcessMonitorService(killResult: true);
        var wsClient = GetFactory(mock).Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        var cmd = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = "KillProcess",
            CommandParameters = new Dictionary<string, string> { ["ProcessId"] = "1234" }
        };
        await MessageSerializer.SendAsync(ws, cmd, CancellationToken.None);

        var response = await ReceiveMessageOfTypeAsync(ws, MessageTypes.CommandResponse);

        Assert.NotNull(response);
        Assert.True(response!.CommandSuccess);
        Assert.Equal("Process killed.", response.CommandMessage);
    }

    [Fact]
    public async Task Command_KillProcess_ValidPid_KillFails_ReturnsFailure()
    {
        var mock = new MockProcessMonitorService(killResult: false);
        var wsClient = GetFactory(mock).Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        var cmd = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = "KillProcess",
            CommandParameters = new Dictionary<string, string> { ["ProcessId"] = "1234" }
        };
        await MessageSerializer.SendAsync(ws, cmd, CancellationToken.None);

        var response = await ReceiveMessageOfTypeAsync(ws, MessageTypes.CommandResponse);

        Assert.NotNull(response);
        Assert.False(response!.CommandSuccess);
        Assert.Equal("Failed to kill process.", response.CommandMessage);
    }

    [Fact]
    public async Task Command_KillProcess_InvalidPid_ReturnsFailure()
    {
        var mock = new MockProcessMonitorService();
        var wsClient = GetFactory(mock).Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        var cmd = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = "KillProcess",
            CommandParameters = new Dictionary<string, string> { ["ProcessId"] = "not-a-number" }
        };
        await MessageSerializer.SendAsync(ws, cmd, CancellationToken.None);

        var response = await ReceiveMessageOfTypeAsync(ws, MessageTypes.CommandResponse);

        Assert.NotNull(response);
        Assert.False(response!.CommandSuccess);
        Assert.Equal("Missing or invalid ProcessId parameter.", response.CommandMessage);
    }

    [Fact]
    public async Task Command_KillProcess_MissingPid_ReturnsFailure()
    {
        var mock = new MockProcessMonitorService();
        var wsClient = GetFactory(mock).Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        var cmd = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = "KillProcess",
            CommandParameters = new Dictionary<string, string>()
        };
        await MessageSerializer.SendAsync(ws, cmd, CancellationToken.None);

        var response = await ReceiveMessageOfTypeAsync(ws, MessageTypes.CommandResponse);

        Assert.NotNull(response);
        Assert.False(response!.CommandSuccess);
        Assert.Equal("Missing or invalid ProcessId parameter.", response.CommandMessage);
    }

    [Fact]
    public async Task Command_KillProcessElevated_ValidPid_ReturnsSuccess()
    {
        var mock = new MockProcessMonitorService(killResult: true);
        var wsClient = GetFactory(mock).Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        var cmd = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = "KillProcessElevated",
            CommandParameters = new Dictionary<string, string> { ["ProcessId"] = "5678" }
        };
        await MessageSerializer.SendAsync(ws, cmd, CancellationToken.None);

        var response = await ReceiveMessageOfTypeAsync(ws, MessageTypes.CommandResponse);

        Assert.NotNull(response);
        Assert.True(response!.CommandSuccess);
        Assert.Equal("Process killed.", response.CommandMessage);
    }
}

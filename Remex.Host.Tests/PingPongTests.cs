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
}

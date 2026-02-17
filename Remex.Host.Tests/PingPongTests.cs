using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc.Testing;
using Remex.Core;
using Remex.Core.Messages;

namespace Remex.Host.Tests;

public class PingPongTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PingPongTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PingPong_SendPing_ReceivesPong()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        // Send ping
        var ping = new RemexMessage { Type = MessageTypes.Ping, Timestamp = 12345 };
        await MessageSerializer.SendAsync(ws, ping);

        // Receive pong
        var pong = await MessageSerializer.ReceiveAsync(ws);

        Assert.NotNull(pong);
        Assert.Equal(MessageTypes.Pong, pong!.Type);
    }

    [Fact]
    public async Task PingPong_EchoesTimestamp()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        long sentTimestamp = 987654321;
        var ping = new RemexMessage { Type = MessageTypes.Ping, Timestamp = sentTimestamp };
        await MessageSerializer.SendAsync(ws, ping);

        var pong = await MessageSerializer.ReceiveAsync(ws);

        Assert.NotNull(pong);
        Assert.Equal(sentTimestamp, pong!.Timestamp);
    }

    [Fact]
    public async Task PingPong_MultiplePings_AllGetPongs()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), CancellationToken.None);

        for (int i = 0; i < 5; i++)
        {
            var ping = new RemexMessage { Type = MessageTypes.Ping, Timestamp = i };
            await MessageSerializer.SendAsync(ws, ping);

            var pong = await MessageSerializer.ReceiveAsync(ws);

            Assert.NotNull(pong);
            Assert.Equal(MessageTypes.Pong, pong!.Type);
            Assert.Equal(i, pong.Timestamp);
        }

        // Note: Don't call CloseAsync — TestServer's TestWebSocket disposes
        // the connection when the test fixture tears down. Calling CloseAsync
        // on an already-disposed TestWebSocket throws ObjectDisposedException.
    }
}

using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Host.Services.Security;

namespace Remex.Host.Tests;

public sealed class PairingHandlerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PairingHandlerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PairingRequest_CanRestartAfterDisconnect()
    {
        using var firstSocket = await ConnectAndStartPairingAsync();
        if (firstSocket.State == WebSocketState.Open)
        {
            await firstSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
        }

        var pairingService = _factory.Services.GetRequiredService<PairingService>();
        for (var attempt = 0; attempt < 20 && pairingService.IsPairingActive; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.False(pairingService.IsPairingActive);

        using var secondSocket = await ConnectAndStartPairingAsync();
        Assert.Equal(WebSocketState.Open, secondSocket.State);

        if (secondSocket.State == WebSocketState.Open)
        {
            await secondSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
    }

    private async Task<WebSocket> ConnectAndStartPairingAsync()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pairingRequest = new RemexMessage
        {
            Type = MessageTypes.PairingRequest,
            ClientId = "test-client",
            PairingRequest = new PairingRequest
            {
                ClientPublicKeyBase64 = Convert.ToBase64String(clientEcdh.PublicKey.ExportSubjectPublicKeyInfo()),
                ClientName = "Test Client",
                ClientVersion = "2.0.0",
                ClientId = "test-client",
            },
        };

        await MessageSerializer.SendAsync(ws, pairingRequest, CancellationToken.None);

        while (true)
        {
            var response = await MessageSerializer.ReceiveAsync(ws, CancellationToken.None);
            Assert.NotNull(response);

            if (response!.Type == MessageTypes.PairingResponse)
            {
                return ws;
            }

            if (response.Type == MessageTypes.PairingError)
            {
                Assert.Fail(response.ErrorText ?? "Pairing request failed.");
            }
        }
    }
}

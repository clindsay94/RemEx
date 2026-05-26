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

        // Allow any asynchronous background Kestrel cleanup thread from a previous test
        // to fully complete and release the shared singleton lock.
        Task.Delay(150).Wait();

        var pairingService = _factory.Services.GetRequiredService<PairingService>();
        pairingService.CancelPairing();
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

    [Fact]
    public async Task PairingRequest_WhenSocketAbortsBeforeDrainingResponse_CancelsPairingSession()
    {
        // Regression coverage for the race in PingPongHandler.HandleAsync at the
        // PairingRequest case: previously `pairingStarted` was set AFTER SendAsync,
        // so a socket abort during/just after the response send (before the message
        // was actually read by the client) could leave the singleton pairing session
        // live for the full 120s timeout and block the next pairing attempt.
        // After the fix, `pairingStarted` is set before SendAsync, so the cleanup
        // path at the bottom of HandleAsync runs unconditionally.
        var pairingService = _factory.Services.GetRequiredService<PairingService>();

        var wsClient = _factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pairingRequest = new RemexMessage
        {
            Type = MessageTypes.PairingRequest,
            ClientId = "race-test-client",
            PairingRequest = new PairingRequest
            {
                ClientPublicKeyBase64 = Convert.ToBase64String(clientEcdh.PublicKey.ExportSubjectPublicKeyInfo()),
                ClientName = "Race Test Client",
                ClientVersion = "2.0.0",
                ClientId = "race-test-client",
            },
        };

        await MessageSerializer.SendAsync(ws, pairingRequest, CancellationToken.None);

        // Abort the socket immediately without reading the response. The server will see
        // the connection drop while still inside HandleAsync; the cleanup at lines 304-308
        // must fire to call CancelActivePairing.
        ws.Abort();
        ws.Dispose();

        // Poll up to ~2 seconds for the host-side cleanup to land. We expect it almost
        // immediately, but the host's receive loop can take a few ticks to observe the abort.
        for (var attempt = 0; attempt < 40 && pairingService.IsPairingActive; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.False(
            pairingService.IsPairingActive,
            "Pairing session should be cancelled after the socket aborts mid-handshake — otherwise the next attempt will be wedged until 120s expiry.");

        // Drain any residual semaphore-held state before yielding the shared
        // WebApplicationFactory singleton back to the test runner. IsPairingActive
        // can flip to false a few microseconds before the cleanup path releases
        // the SemaphoreSlim (the write to _activePin and the _lock.Release() are
        // not memory-ordered with each other from an outside reader). Forcing a
        // CancelPairing round-trip waits until the lock is acquirable again and
        // leaves it in a known-clean state so subsequent tests in the class
        // fixture start without "already in progress" interference.
        pairingService.CancelPairing();
    }

    [Fact]
    public async Task PairingRequest_WithMalformedClientPublicKey_CancelsStartedSession()
    {
        // Sibling regression of the same bug class as the SendAsync race: if
        // StartPairingAsync succeeds (live session in PairingService) but
        // DeriveSessionKeyAsync throws (malformed public key, HKDF failure, etc.),
        // PairingHandler must cancel the started session in its catch block.
        // Previously the catch only returned a PairingError and left the singleton
        // state live, blocking the next pairing attempt for 120 seconds.
        var pairingService = _factory.Services.GetRequiredService<PairingService>();

        var wsClient = _factory.Server.CreateWebSocketClient();
        using var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        var malformedRequest = new RemexMessage
        {
            Type = MessageTypes.PairingRequest,
            ClientId = "malformed-key-test-client",
            PairingRequest = new PairingRequest
            {
                // Not a base64-encoded SubjectPublicKeyInfo — DeriveSessionKeyAsync
                // will throw when it tries to ImportSubjectPublicKeyInfo on this.
                ClientPublicKeyBase64 = "this-is-not-a-public-key",
                ClientName = "Bad Public Key Client",
                ClientVersion = "2.0.0",
                ClientId = "malformed-key-test-client",
            },
        };

        await MessageSerializer.SendAsync(ws, malformedRequest, CancellationToken.None);

        // The server emits host_info / launcher_sync / layout_sync proactively on connect,
        // before any handshake response, so drain until the PairingError (or PairingResponse,
        // which would mean the fix failed and DeriveSessionKeyAsync didn't throw).
        RemexMessage? response = null;
        for (var i = 0; i < 16; i++)
        {
            response = await MessageSerializer.ReceiveAsync(ws, CancellationToken.None);
            if (response is null) break;
            if (response.Type == MessageTypes.PairingError ||
                response.Type == MessageTypes.PairingResponse)
            {
                break;
            }
        }

        Assert.NotNull(response);
        Assert.Equal(MessageTypes.PairingError, response!.Type);

        // The session that was started internally must have been cancelled in the catch.
        for (var attempt = 0; attempt < 40 && pairingService.IsPairingActive; attempt++)
        {
            await Task.Delay(50);
        }
        Assert.False(
            pairingService.IsPairingActive,
            "Pairing session must be cancelled when DeriveSessionKeyAsync throws — otherwise the next attempt is wedged until 120s expiry.");

        // Memory-fence the singleton state for the next test in this class fixture.
        pairingService.CancelPairing();

        if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
        {
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
            catch { }
        }
    }

    private async Task<WebSocket> ConnectAndStartPairingAsync()
    {
        var pairingService = _factory.Services.GetRequiredService<PairingService>();
        pairingService.CancelPairing();

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

using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Tests;

public sealed class PairingHandlerTests : IClassFixture<RemexHostFactory>
{
    private readonly RemexHostFactory _factory;

    public PairingHandlerTests(RemexHostFactory factory)
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
    public async Task PairingRequest_ReusesExistingDesktopGeneratedSession()
    {
        var pairingService = _factory.Services.GetRequiredService<PairingService>();
        try
        {
            var prestarted = await pairingService.StartPairingAsync(CancellationToken.None);

            var wsClient = _factory.Server.CreateWebSocketClient();
            using var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
            using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            var pairingRequest = new RemexMessage
            {
                Type = MessageTypes.PairingRequest,
                ClientId = "reuse-test-client",
                PairingRequest = new PairingRequest
                {
                    ClientPublicKeyBase64 = Convert.ToBase64String(clientEcdh.PublicKey.ExportSubjectPublicKeyInfo()),
                    ClientName = "Reuse Test Client",
                    ClientVersion = "2.0.0",
                    ClientId = "reuse-test-client",
                },
            };

            await MessageSerializer.SendAsync(ws, pairingRequest, CancellationToken.None);

            RemexMessage? response = null;
            for (var i = 0; i < 16; i++)
            {
                response = await MessageSerializer.ReceiveAsync(ws, CancellationToken.None);
                if (response?.Type is MessageTypes.PairingResponse or MessageTypes.PairingError)
                {
                    break;
                }
            }

            Assert.NotNull(response);
            Assert.Equal(MessageTypes.PairingResponse, response!.Type);
            Assert.NotNull(response.PairingResponse);
            Assert.Equal(prestarted.HostPublicKeyBase64, response.PairingResponse!.HostPublicKeyBase64);
            Assert.True(pairingService.IsPairingActive);
            Assert.Equal(prestarted.Pin, pairingService.GetActivePin());
        }
        finally
        {
            pairingService.CancelPairing();
        }
    }

    [Fact]
    public async Task PairingRequest_WithMalformedKeyOnReusedSession_PreservesExistingSession()
    {
        var pairingService = _factory.Services.GetRequiredService<PairingService>();
        try
        {
            var prestarted = await pairingService.StartPairingAsync(CancellationToken.None);

            var badClient = _factory.Server.CreateWebSocketClient();
            using var badSocket = await badClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

            await MessageSerializer.SendAsync(
                badSocket,
                new RemexMessage
                {
                    Type = MessageTypes.PairingRequest,
                    ClientId = "bad-reuse-client",
                    PairingRequest = new PairingRequest
                    {
                        ClientPublicKeyBase64 = "this-is-not-a-public-key",
                        ClientName = "Bad Reuse Client",
                        ClientVersion = "2.0.0",
                        ClientId = "bad-reuse-client",
                    },
                },
                CancellationToken.None);

            var error = await ReadHandshakeResponseAsync(badSocket);
            Assert.NotNull(error);
            Assert.Equal(MessageTypes.PairingError, error!.Type);
            Assert.True(pairingService.IsPairingActive);
            Assert.Equal(prestarted.Pin, pairingService.GetActivePin());

            var goodClient = _factory.Server.CreateWebSocketClient();
            using var goodSocket = await goodClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
            using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            await MessageSerializer.SendAsync(
                goodSocket,
                new RemexMessage
                {
                    Type = MessageTypes.PairingRequest,
                    ClientId = "good-reuse-client",
                    PairingRequest = new PairingRequest
                    {
                        ClientPublicKeyBase64 = Convert.ToBase64String(clientEcdh.PublicKey.ExportSubjectPublicKeyInfo()),
                        ClientName = "Good Reuse Client",
                        ClientVersion = "2.0.0",
                        ClientId = "good-reuse-client",
                    },
                },
                CancellationToken.None);

            var response = await ReadHandshakeResponseAsync(goodSocket);
            Assert.NotNull(response);
            Assert.Equal(MessageTypes.PairingResponse, response!.Type);
            Assert.Equal(prestarted.HostPublicKeyBase64, response.PairingResponse!.HostPublicKeyBase64);
            Assert.True(pairingService.IsPairingActive);
            Assert.Equal(prestarted.Pin, pairingService.GetActivePin());
        }
        finally
        {
            pairingService.CancelPairing();
        }
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
            catch { /* best-effort test cleanup: a teardown failure must not fail a passing test */ }
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

    private static async Task<RemexMessage?> ReadHandshakeResponseAsync(WebSocket socket)
    {
        for (var i = 0; i < 16; i++)
        {
            var response = await MessageSerializer.ReceiveAsync(socket, CancellationToken.None);
            if (response?.Type is MessageTypes.PairingResponse or MessageTypes.PairingError)
            {
                return response;
            }
        }

        return null;
    }

    private sealed class NonLoopbackStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                builder.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.100");
                    return nextMiddleware();
                });
                next(builder);
            };
        }
    }

    [Fact]
    public async Task PairingComplete_EnablesSecondSocket_OnlyWithSameClientId()
    {
        // 1. Create a non-loopback server factory by injecting NonLoopbackStartupFilter
        var nonLoopbackFactory = new RemexHostFactory().WithServices(services =>
        {
            services.AddSingleton<IStartupFilter, NonLoopbackStartupFilter>();
        });

        var pairingService = nonLoopbackFactory.Services.GetRequiredService<PairingService>();
        pairingService.CancelPairing();

        var wsClient = nonLoopbackFactory.Server.CreateWebSocketClient();

        // 2. Step A: Connect as unpaired, send LayoutRequest, and verify rejection
        using (var wsUnpaired = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None))
        {
            var unpairedRequest = new RemexMessage
            {
                Type = MessageTypes.LayoutRequest,
                CorrelationId = "unpaired-req-id"
            };
            await MessageSerializer.SendAsync(wsUnpaired, unpairedRequest, CancellationToken.None);

            RemexMessage? response = null;
            // Loop because host sends host_info, launcher_sync, layout_sync on connect
            for (int i = 0; i < 10; i++)
            {
                var msg = await MessageSerializer.ReceiveAsync(wsUnpaired, CancellationToken.None);
                if (msg?.Type == MessageTypes.CommandResponse && msg.CorrelationId == "unpaired-req-id")
                {
                    response = msg;
                    break;
                }
            }

            Assert.NotNull(response);
            Assert.False(response!.CommandSuccess);
            Assert.Contains("Pairing required", response.CommandMessage);
        }

        // 3. Step B: Perform a full pairing handshake
        using (var wsPairing = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None))
        {
            using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var clientPubBase64 = Convert.ToBase64String(clientEcdh.PublicKey.ExportSubjectPublicKeyInfo());

            var pairingRequest = new RemexMessage
            {
                Type = MessageTypes.PairingRequest,
                ClientId = "integration-test-client-1",
                PairingRequest = new PairingRequest
                {
                    ClientPublicKeyBase64 = clientPubBase64,
                    ClientName = "Integration Test Client",
                    ClientVersion = "2.0.0",
                    ClientId = "integration-test-client-1"
                }
            };
            await MessageSerializer.SendAsync(wsPairing, pairingRequest, CancellationToken.None);

            RemexMessage? pairingResponse = null;
            for (int i = 0; i < 10; i++)
            {
                var msg = await MessageSerializer.ReceiveAsync(wsPairing, CancellationToken.None);
                if (msg?.Type == MessageTypes.PairingResponse)
                {
                    pairingResponse = msg;
                    break;
                }
            }

            Assert.NotNull(pairingResponse);
            Assert.NotNull(pairingResponse!.PairingResponse);

            // Read the plaintext PIN from the pairing service in the DI container
            var activePin = pairingService.GetActivePin();

            // Perform client-side key agreement & HKDF derivation
            using var hostPeer = ECDiffieHellman.Create();
            hostPeer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pairingResponse.PairingResponse.HostPublicKeyBase64), out _);
            var clientSharedSecret = clientEcdh.DeriveRawSecretAgreement(hostPeer.PublicKey);
            var certSpkiHash = Convert.FromBase64String(pairingResponse.PairingResponse.CertificateSpkiHashBase64);

            var clientSessionKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                clientSharedSecret,
                outputLength: 32,
                salt: certSpkiHash,
                info: System.Text.Encoding.UTF8.GetBytes("remex-pair-v1"));

            // Compute the ack HMAC: HMAC-SHA256(sessionKey, "ack:" + PIN)
            var clientHmac = Convert.ToBase64String(
                HMACSHA256.HashData(clientSessionKey, System.Text.Encoding.UTF8.GetBytes("ack:" + activePin)));

            var pairingComplete = new RemexMessage
            {
                Type = MessageTypes.PairingComplete,
                ClientId = "integration-test-client-1",
                PairingComplete = new PairingComplete
                {
                    ClientId = "integration-test-client-1",
                    ClientPinHmacBase64 = clientHmac
                }
            };
            await MessageSerializer.SendAsync(wsPairing, pairingComplete, CancellationToken.None);

            RemexMessage? completeResponse = null;
            for (int i = 0; i < 10; i++)
            {
                var msg = await MessageSerializer.ReceiveAsync(wsPairing, CancellationToken.None);
                if (CompleteResponseIsPairingCompleteSuccess(msg))
                {
                    completeResponse = msg;
                    break;
                }
            }

            Assert.NotNull(completeResponse);
            Assert.True(completeResponse!.CommandSuccess);
            Assert.Equal("Pairing verified.", completeResponse.CommandMessage);
        }

        // 4. Step C: Open a second socket with the same ClientId and verify that LayoutRequest succeeds
        using (var wsSecond = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None))
        {
            var layoutRequest = new RemexMessage
            {
                Type = MessageTypes.LayoutRequest,
                ClientId = "integration-test-client-1",
                CorrelationId = "paired-req-id"
            };
            await MessageSerializer.SendAsync(wsSecond, layoutRequest, CancellationToken.None);

            RemexMessage? response = null;
            for (int i = 0; i < 10; i++)
            {
                var msg = await MessageSerializer.ReceiveAsync(wsSecond, CancellationToken.None);
                if (msg?.Type == MessageTypes.LayoutSync)
                {
                    response = msg;
                    break;
                }
            }

            Assert.NotNull(response);
            Assert.NotNull(response!.DashboardProfile);
        }

        // 5. Step D: Open a third socket with a different ClientId and verify that LayoutRequest is rejected
        using (var wsThird = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None))
        {
            var layoutRequest = new RemexMessage
            {
                Type = MessageTypes.LayoutRequest,
                ClientId = "integration-test-client-2",
                CorrelationId = "unregistered-req-id"
            };
            await MessageSerializer.SendAsync(wsThird, layoutRequest, CancellationToken.None);

            RemexMessage? response = null;
            for (int i = 0; i < 10; i++)
            {
                var msg = await MessageSerializer.ReceiveAsync(wsThird, CancellationToken.None);
                if (msg?.Type == MessageTypes.CommandResponse && msg.CorrelationId == "unregistered-req-id")
                {
                    response = msg;
                    break;
                }
            }

            Assert.NotNull(response);
            Assert.False(response!.CommandSuccess);
            Assert.Contains("Pairing required", response.CommandMessage);
        }

        // Cleanup pairing service state for future tests
        pairingService.CancelPairing();
    }

    private static bool CompleteResponseIsPairingCompleteSuccess(RemexMessage? msg)
    {
        return msg is { Type: MessageTypes.PairingComplete, CommandSuccess: true };
    }
}

using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Agent.Services.Security;
using Remex.Agent.Services;
using Remex.Core.Services.FileTransfer;
using Xunit.Sdk;

namespace Remex.Agent.Tests;

/// <summary>
/// Pairing over a real socket against a shared Kestrel host.
/// </summary>
/// <remarks>
/// <para>
/// SETUP IS ASYNCHRONOUS BECAUSE IT HAS TO WAIT (RemEx-7cq0). This class needs a pause between tests
/// to let background Kestrel cleanup from the previous one finish and release the shared singleton
/// lock, and a constructor cannot await — so it used to spell that as
/// <c>Task.Delay(150).Wait()</c>, which BLOCKS a thread-pool thread rather than yielding it.
/// </para>
/// <para>
/// The cost is not the 150ms, which is paid either way. It is one fewer pool thread available for
/// the whole wait, once per test in this class, in an assembly whose tests run concurrently with
/// every other assembly in a whole-solution run. That is the shape that produces pool starvation,
/// and pool starvation was the leading hypothesis for the RemEx-w7ei flake — which appeared ONLY in
/// whole-solution runs and never in project-only ones. Nobody measured the link, and this refactor
/// does not claim to have fixed that flake. Blocking on a Task in test setup is banned repo-wide
/// regardless of whether it is the sole cause, which is the decision recorded on the bead.
/// </para>
/// <para>
/// <c>IAsyncLifetime</c> is the async equivalent of the constructor here, not of a fixture: xUnit v2
/// builds a new instance of a test class per test, so <c>InitializeAsync</c> runs at exactly the
/// cadence the constructor did. The wait is per-test either way; only the blocking is gone.
/// </para>
/// <para>
/// WHAT IS STILL WRONG WITH IT, stated rather than quietly kept: a fixed sleep is a guess about how
/// long someone else's cleanup takes. It is preserved verbatim here because changing the duration or
/// polling a condition instead is a behavioural change to a test that guards a flake, and it belongs
/// in its own bead with its own evidence rather than riding along with a mechanical refactor.
/// </para>
/// </remarks>
public sealed class PairingHandlerTests : IClassFixture<RemexHostFactory>, IAsyncLifetime
{
    private readonly RemexHostFactory _factory;

    public PairingHandlerTests(RemexHostFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        // Allow any asynchronous background Kestrel cleanup thread from a previous test
        // to fully complete and release the shared singleton lock.
        await Task.Delay(150);

        var pairingService = _factory.Services.GetRequiredService<PairingService>();
        pairingService.CancelPairing();
    }

    public Task DisposeAsync() => Task.CompletedTask;

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


    /// <summary>
    /// A RECONNECTING device is still recognised by name (RemEx-yzqs).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bug this covers: a device name crosses the wire exactly once, on pairing_request, and
    /// initial pairing happens once in a device's life. Every connection after that presents a client
    /// id and nothing else — so the PC knew a phone's name for its first connection and never again.
    /// </para>
    /// <para>
    /// **THE ASSERTION HAS TO RUN WHILE THE SOCKET IS STILL OPEN**, because a session exists only for
    /// as long as its connection does. The layout round-trip is the synchronisation point: once the
    /// host has answered it, the reconnect proof it processed first has definitely been handled.
    /// </para>
    /// <para>
    /// Self-contained rather than folded into PairingComplete_EnablesSecondSocket_OnlyWithSameClientId,
    /// which would mean hoisting that test's derived session key out of the block that scopes it and
    /// editing assertions about the pairing gate itself. The ECDH/HKDF dance is duplicated knowingly:
    /// leaving the pairing-gate test untouched is worth more than the lines saved.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReconnectingDeviceIsStillKnownByTheNameItGaveAtPairing()
    {
        var factory = new RemexHostFactory().WithServices(services =>
            services.AddSingleton<IStartupFilter, NonLoopbackStartupFilter>());

        var pairingService = factory.Services.GetRequiredService<PairingService>();
        pairingService.CancelPairing();

        var nameStore = factory.Services.GetRequiredService<PairedClientNameStore>();
        var sessions = factory.Services.GetRequiredService<ClientSessionRegistry>();
        var wsClient = factory.Server.CreateWebSocketClient();

        const string clientId = "reconnect-name-client";
        const string deviceName = "Connor's Pixel";
        byte[] sessionKey;

        // Pair for the first and only time — the one connection that ever carries the name.
        using (var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None))
        {
            using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.PairingRequest,
                ClientId = clientId,
                PairingRequest = new PairingRequest
                {
                    ClientPublicKeyBase64 = Convert.ToBase64String(clientEcdh.PublicKey.ExportSubjectPublicKeyInfo()),
                    ClientName = deviceName,
                    ClientVersion = "2.0.0",
                    ClientId = clientId,
                },
            }, CancellationToken.None);

            var pairingResponse = await ReceiveOfTypeAsync(ws, MessageTypes.PairingResponse);
            Assert.NotNull(pairingResponse?.PairingResponse);

            using var hostPeer = ECDiffieHellman.Create();
            hostPeer.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(pairingResponse!.PairingResponse!.HostPublicKeyBase64), out _);

            sessionKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                clientEcdh.DeriveRawSecretAgreement(hostPeer.PublicKey),
                outputLength: 32,
                salt: Convert.FromBase64String(pairingResponse.PairingResponse.CertificateSpkiHashBase64),
                info: System.Text.Encoding.UTF8.GetBytes("remex-pair-v1"));

            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.PairingComplete,
                ClientId = clientId,
                PairingComplete = new PairingComplete
                {
                    ClientId = clientId,
                    ClientPinHmacBase64 = Convert.ToBase64String(HMACSHA256.HashData(
                        sessionKey,
                        System.Text.Encoding.UTF8.GetBytes("ack:" + pairingService.GetActivePin()))),
                },
            }, CancellationToken.None);

            var complete = await ReceiveOfTypeAsync(ws, MessageTypes.PairingComplete);
            Assert.True(complete?.CommandSuccess);
        }

        // The name outlived the connection that carried it. Everything below depends on this.
        //
        // POLLED, BECAUSE THE HOST ANSWERS BEFORE IT STORES. PingPongHandler sends the
        // pairing_complete response and only then records the name, so a client that asserts the
        // instant it sees "Pairing verified" is racing the host and wins about half the time. The
        // ordering is fine for the product — nothing reads the name until a later connection — but a
        // test has to wait for it rather than assume it.
        await WaitForAsync(() => nameStore.Resolve(clientId) is not null);
        Assert.Equal(deviceName, nameStore.Resolve(clientId));

        // WAIT FOR THE PAIRING SESSION TO DRAIN BEFORE RECONNECTING, or this test proves nothing.
        // Closing the client socket does not instantly unwind HandleAsync, so the pairing connection
        // lingers in the registry for a moment — still carrying the name it was told directly. A
        // reconnect asserted against that leftover would pass with the reconnect lookup deleted,
        // which is the exact line this test exists to protect.
        // Generous, because this waits on the SERVER side of a socket the client has already closed,
        // and the whole suite is running in parallel around it. It is a wait for a state, not a sleep:
        // it returns the moment the session is gone, and it says what it gave up at if it never is.
        await WaitForSessionCountAsync(sessions, 0, SessionDrainBudget);
        Assert.Empty(sessions.Snapshot());

        // Now reconnect exactly as a phone does the next morning: a client id, a proof, and no name.
        using (var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None))
        {
            await MessageSerializer.SendAsync(
                ws, new RemexMessage { Type = MessageTypes.Ping, ClientId = clientId }, CancellationToken.None);

            var challenge = await ReceiveOfTypeAsync(ws, MessageTypes.ReconnectChallenge);
            Assert.NotNull(challenge?.ReconnectChallenge);

            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.ReconnectProof,
                ClientId = clientId,
                ReconnectProof = new ReconnectProof
                {
                    ClientId = clientId,
                    ProofHmacBase64 = Convert.ToBase64String(HMACSHA256.HashData(
                        sessionKey,
                        Convert.FromBase64String(challenge!.ReconnectChallenge!.NonceBase64))),
                },
            }, CancellationToken.None);

            // Round-trip anything: once the host answers this, it has finished with the proof.
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.LayoutRequest,
                ClientId = clientId,
                CorrelationId = "reconnect-name-sync",
            }, CancellationToken.None);
            Assert.NotNull(await ReceiveOfTypeAsync(ws, MessageTypes.LayoutSync));

            // The ONLY session now is the reconnected one, and it never heard the name over the
            // wire — it can only have come from the store.
            await WaitForSessionCountAsync(sessions, 1);
            var session = Assert.Single(sessions.Snapshot());
            Assert.Equal(deviceName, session.DeviceName);
        }
    }

    /// <summary>How long a wait on something the host does on its own schedule may take.</summary>
    private static readonly TimeSpan DefaultWaitBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The drain wait gets longer than the rest, because it is the one that has been seen to expire.
    /// </summary>
    /// <remarks>
    /// It waits on the SERVER side of a socket the client has already closed — <c>HandleAsync</c>
    /// unwinding through its finally block — with every other test in the solution running in
    /// parallel around it, and it expired once mid whole-suite run while passing on its own
    /// (RemEx-w7ei). The extra budget costs nothing when things are working, because the wait
    /// returns the instant the session goes: only a real regression ever spends it.
    /// </remarks>
    private static readonly TimeSpan SessionDrainBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Waits for the registry to settle on a session count, and says what it saw if it never does.
    /// </summary>
    /// <remarks>
    /// **THE MESSAGE ON TIMEOUT IS THE POINT** (RemEx-w7ei). When this wait expired, the only
    /// evidence it left behind was the caller's next assertion printing one leftover session — which
    /// reads exactly like state leaked in from another test, and was read that way. It was not: the
    /// leftover was this test's own pairing connection, still unwinding. The count it gave up at,
    /// the counts it saw on the way, and the sessions themselves are what tell those two apart the
    /// next time without a trx dig. An elapsed time well past the budget is a third answer again —
    /// a starved thread pool rather than a host that never got there.
    /// </remarks>
    private static async Task WaitForSessionCountAsync(
        ClientSessionRegistry sessions, int expected, TimeSpan? budget = null)
    {
        var counts = new List<int>();
        var outcome = await WaitForAsync(
            () =>
            {
                var count = sessions.Snapshot().Count;
                if (counts.Count == 0 || counts[^1] != count) counts.Add(count);
                return count == expected;
            },
            budget);

        if (outcome.Satisfied) return;

        Assert.Fail(DescribeSessionCountTimeout(expected, counts, outcome.Elapsed, sessions.Snapshot()));
    }

    /// <summary>
    /// Builds the timeout message, separately from the waiting, so the NEAR MISS can be tested
    /// (RemEx-jye7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE HEADLINE NUMBER IS THE LAST COUNT THE WAIT SAW, NOT A FRESH ONE. A session that drains a
    /// moment AFTER the budget expired is the case this message exists to name, and re-reading the
    /// registry for the headline would report it as "gave up at 0" — the one thing that did not
    /// happen. What is there NOW is worth printing too, separately, because the two differing is
    /// itself the signal.
    /// </para>
    /// <para>
    /// EXTRACTED BECAUSE THAT BRANCH COULD NOT BE COVERED IN PLACE. Standing a near miss up against
    /// the live wait means disposing a registration slightly after the budget expires — a race, and
    /// on the starved pool this whole area is about, the wait overshoots, the last observation is 0,
    /// and the test fails spuriously. A flaky test for a flake bead is a bad trade, so it was left
    /// uncovered knowingly. As a pure function of what was observed it has no timing in it at all.
    /// </para>
    /// </remarks>
    private static string DescribeSessionCountTimeout(
        int expected, IReadOnlyList<int> counts, TimeSpan elapsed, IReadOnlyList<Remex.Desktop.Services.ClientSession> now) =>
        $"The session count never reached {expected}: gave up at {counts[^1]} after "
        + $"{elapsed.TotalSeconds:F1}s, having seen {string.Join(" -> ", counts)}. "
        + $"In the registry now ({now.Count}): "
        + string.Join(", ", now.Select(s => $"[{s.RemoteAddress} / {s.DeviceName}]")) + ".";

    /// <summary>
    /// The near miss: the session drained a moment AFTER the budget expired (RemEx-jye7).
    /// </summary>
    /// <remarks>
    /// This is the branch the message exists for and the one that had no coverage.
    /// TheDrainWaitReportsWhatItGaveUpAt uses a session that never drains, so the last observed
    /// count and the registry agree there — and a regression that re-read the registry for the
    /// headline would still pass it. Here they disagree on purpose, which is the only arrangement
    /// that can tell the two apart. No timing: the observations are handed in.
    /// </remarks>
    [Fact]
    public void TheDrainWaitReportsWhatTheWaitSaw_NotAFreshRead()
    {
        var message = DescribeSessionCountTimeout(
            expected: 0,
            counts: [2, 1],
            elapsed: TimeSpan.FromSeconds(3.2),
            now: Array.Empty<Remex.Desktop.Services.ClientSession>());

        Assert.Contains("gave up at 1", message);
        Assert.DoesNotContain("gave up at 0", message);
        Assert.Contains("In the registry now (0)", message);
        Assert.Contains("seen 2 -> 1", message);
    }

    /// <summary>What one <see cref="WaitForAsync"/> call ended up doing.</summary>
    /// <param name="Satisfied">Whether the condition held before the budget ran out.</param>
    /// <param name="Elapsed">How long the wait actually took, overshoot of the budget included.</param>
    private readonly record struct WaitOutcome(bool Satisfied, TimeSpan Elapsed);

    /// <summary>
    /// Waits for a condition the host reaches on its own schedule.
    /// </summary>
    /// <remarks>
    /// Returns the moment it holds, so the generous deadline costs nothing when things are working —
    /// it only buys tolerance for the whole suite running in parallel around this. Does NOT assert on
    /// timeout: what a timeout means is the caller's to say, and a caller waiting for a value asserts
    /// the real thing afterwards so a failure reports what was actually wrong rather than "timed
    /// out". <see cref="WaitForSessionCountAsync"/> is the one caller that does report it, because
    /// there the count IS the real thing.
    /// </remarks>
    private static async Task<WaitOutcome> WaitForAsync(Func<bool> condition, TimeSpan? budget = null)
    {
        var limit = budget ?? DefaultWaitBudget;
        var started = Stopwatch.StartNew();

        bool satisfied;
        while (!(satisfied = condition()) && started.Elapsed < limit)
            await Task.Delay(25);

        return new WaitOutcome(satisfied, started.Elapsed);
    }

    /// <summary>
    /// The drain wait says what it gave up at, rather than leaving the next reader to guess.
    /// </summary>
    /// <remarks>
    /// Pins the diagnostic, not the timing (RemEx-w7ei). The recorded occurrence of that flake left
    /// nothing behind but a single leftover session, and it was misread as state leaking in from
    /// another test — the leftover was in fact the same test's own pairing connection, still
    /// unwinding on the server. A message carrying the count, the elapsed wait and the sessions
    /// themselves is what makes those two readings distinguishable, so it is worth its own test.
    /// </remarks>
    [Fact]
    public async Task TheDrainWaitReportsWhatItGaveUpAt()
    {
        var registry = new ClientSessionRegistry();

        // Snapshot() reads the address and the name and never the socket, so an unconnected one is
        // enough to stand a session up here.
        using var socket = new ClientWebSocket();
        using var handle = registry.Register("192.168.1.100", socket);
        registry.Identify(handle, "reconnect-name-client", "Connor's Pixel");
        registry.MarkAuthenticated(handle, identityProven: true);

        var failure = await Assert.ThrowsAnyAsync<XunitException>(
            () => WaitForSessionCountAsync(registry, 0, TimeSpan.FromMilliseconds(250)));

        Assert.Contains("never reached 0", failure.Message);
        Assert.Contains("gave up at 1", failure.Message);
        Assert.Contains("192.168.1.100", failure.Message);
        Assert.Contains("Connor's Pixel", failure.Message);

        // The elapsed time and the trajectory are half the diagnostic — they are what separates a
        // near miss from a session that never moved, and from a wait that overshot its own budget
        // because the pool was starved. Pinned, so neither can be dropped quietly.
        Assert.Contains("having seen 1", failure.Message);
        Assert.Matches(@"after \d+[.,]\ds", failure.Message);
    }

    /// <summary>Reads until the wanted type arrives; the host also pushes host_info and syncs.</summary>
    private static async Task<RemexMessage?> ReceiveOfTypeAsync(WebSocket socket, string type)
    {
        for (var i = 0; i < 12; i++)
        {
            var message = await MessageSerializer.ReceiveAsync(socket, CancellationToken.None);
            if (message is null) return null;
            if (message.Type == type) return message;
        }

        return null;
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

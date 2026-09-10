using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Remex.Agent.Services;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// A real socket, driven through a real pairing, becomes visible in <see cref="ClientSessionRegistry"/>
/// (RemEx-ft3t).
/// </summary>
/// <remarks>
/// <para>
/// **EVERY OTHER TEST OF THIS REGISTRY CALLS IT DIRECTLY.** That proves the bookkeeping and proves
/// nothing about whether the handler ever performs it — the registry is what decides which device the
/// desktop lists, which one a consent question is routed to, and which one <c>DisconnectClient</c> can
/// cut off, and all of that is downstream of a call site no test reached.
/// </para>
/// <para>
/// THE TWO ROWS ARE THE POINT, and they are not redundant. Loopback is authenticated WITHOUT a proven
/// identity (RemEx-4215, frozen at no identity), so it appears in <c>Snapshot</c> and is deliberately
/// NOT findable by client id. A non-loopback client that completes pairing is both. A test that ran
/// only over <c>ws://localhost</c> — the obvious way to write this — would assert the weaker contract
/// and read as though it had covered the stronger one.
/// </para>
/// <para>
/// **THE WAIT IS NOT A FLAKE-DAMPENER, IT IS THE ORDERING.** The handler replies to
/// <c>pairing_complete</c> and marks the session authenticated afterwards, so a test that reads the
/// registry the instant the reply lands sees an EMPTY snapshot — reliably, not occasionally. Two
/// earlier attempts at this bead were abandoned over exactly that reading: one concluded the service
/// provider must differ from the one serving the connection, the other that the non-loopback pairing
/// path never reached <c>MarkAuthenticated</c>. Both were wrong, and both were plausible. Polling for
/// the transition, with a ceiling that fails loudly, is what tells them apart.
/// </para>
/// </remarks>
public sealed class ConnectionBecomesRegistryVisibleTests
{
    /// <summary>Makes every request look like it came from the LAN rather than from loopback.</summary>
    private sealed class NonLoopbackStartupFilter : IStartupFilter
    {
        public const string Address = "192.168.1.100";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            builder =>
            {
                builder.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(Address);
                    return nextMiddleware();
                });
                next(builder);
            };
    }

    [Fact]
    public async Task APairedLANClientIsListedAndFindableByItsId()
    {
        using var factory = new RemexHostFactory().WithServices(
            services => services.AddSingleton<IStartupFilter, NonLoopbackStartupFilter>());

        var registry = factory.Services.GetRequiredService<ClientSessionRegistry>();
        var clientId = $"lan-{Guid.NewGuid():N}";

        Assert.Empty(registry.Snapshot());

        using var ws = await ConnectAsync(factory);
        await PairAsync(ws, factory.Services.GetRequiredService<PairingService>(), clientId);
        await WaitUntilVisibleAsync(registry);

        var session = Assert.Single(registry.Snapshot());
        Assert.Equal(NonLoopbackStartupFilter.Address, session.RemoteAddress);
        Assert.Equal(clientId, session.DeviceName);

        // FINDABLE BY ID IS THE HALF THAT MATTERS DOWNSTREAM. Snapshot drives the desktop's device
        // list; this is what ConsentRoutePolicy and DisconnectClient go through, and it is the half a
        // loopback-only test can never reach.
        Assert.True(registry.IsConnected(clientId));
    }

    [Fact]
    public async Task ALoopbackClientIsListedButDeliberatelyNotFindableById()
    {
        // THE CONTRAST, and it is a contract rather than a shortcoming. Loopback is authenticated with
        // NO proven identity, so it counts as a session while being unreachable by client id — which
        // is what stops anything on this machine claiming to be a paired phone. Asserting it here
        // keeps the row above honest: without it, IsConnected returning true could be read as
        // something pairing does rather than something identity does.
        using var factory = new RemexHostFactory();

        var registry = factory.Services.GetRequiredService<ClientSessionRegistry>();
        var clientId = $"local-{Guid.NewGuid():N}";

        using var ws = await ConnectAsync(factory);
        await PairAsync(ws, factory.Services.GetRequiredService<PairingService>(), clientId);
        await WaitUntilVisibleAsync(registry);

        Assert.Single(registry.Snapshot());
        Assert.False(registry.IsConnected(clientId));
    }

    /// <summary>
    /// Waits for the handler to mark the session authenticated, which happens AFTER it replies.
    /// </summary>
    /// <remarks>
    /// Fails with a message naming the ordering rather than timing out anonymously: a future change
    /// that genuinely stopped registering a paired client should read as that, not as a slow machine.
    /// </remarks>
    private static async Task WaitUntilVisibleAsync(ClientSessionRegistry registry)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (registry.Snapshot().Count > 0) return;
            await Task.Delay(20);
        }

        Assert.Fail(
            "A pairing completed but no session became visible within two seconds. The handler marks "
            + "the session authenticated after it replies, so a short wait is expected — never "
            + "reaching it means the connection is not being registered at all.");
    }

    private static Task<WebSocket> ConnectAsync(RemexHostFactory factory) =>
        factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(factory.Server.BaseAddress, "/ws"), CancellationToken.None);

    private static async Task PairAsync(WebSocket ws, PairingService pairingService, string clientId)
    {
        using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        await MessageSerializer.SendAsync(ws, new RemexMessage
        {
            Type = MessageTypes.PairingRequest,
            ClientId = clientId,
            PairingRequest = new PairingRequest
            {
                ClientPublicKeyBase64 = Convert.ToBase64String(clientEcdh.PublicKey.ExportSubjectPublicKeyInfo()),
                ClientName = clientId,
                ClientVersion = "2.1.0",
                ClientId = clientId,
            },
        }, CancellationToken.None);

        var pairingResponse = await ReceiveOfTypeAsync(ws, MessageTypes.PairingResponse);
        Assert.NotNull(pairingResponse?.PairingResponse);

        using var hostPeer = ECDiffieHellman.Create();
        hostPeer.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(pairingResponse!.PairingResponse!.HostPublicKeyBase64), out _);

        var sessionKey = HKDF.DeriveKey(
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
        Assert.True(complete?.CommandSuccess, $"pairing did not complete for {clientId}");
    }

    private static async Task<RemexMessage?> ReceiveOfTypeAsync(WebSocket socket, string type)
    {
        for (var i = 0; i < 16; i++)
        {
            var message = await MessageSerializer.ReceiveAsync(socket, CancellationToken.None);
            if (message is null) return null;
            if (message.Type == type) return message;
        }

        return null;
    }
}

using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Remex.Agent.Services;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// A phone's reply to a relayed file request, sent through the REAL <c>PingPongHandler.HandleAsync</c>
/// of a real host (RemEx-xt0af): what the handler hands <see cref="PhoneFileRelay.TryDeliverReply"/>
/// about the sending connection is what decides whether the reply reaches the PC's screen.
/// </summary>
/// <remarks>
/// <para>
/// WHY THROUGH THE HANDLER. <see cref="PhoneFileRelayTests"/> calls <c>TryDeliverReply</c> directly,
/// with whatever flags the test chooses, so it proves the relay's rule and nothing about the call
/// site: a case block that passed <c>isLoopback: false</c> or <c>identityProven: true</c> regardless
/// of the connection would pass every one of those tests.
/// </para>
/// <para>
/// THE LOOPBACK CONNECTION PIN-PAIRS FIRST, ON PURPOSE. An unpaired loopback connection is frozen at
/// no client id (RemEx-4215), so the relay would refuse its reply on the blank id alone and the test
/// could not tell a correct call site from one that lied about loopback. A loopback connection that
/// completed a real PIN pairing has a proven id — the one case where only the loopback flag stands
/// between a local process and the phone's request.
/// </para>
/// </remarks>
public sealed class PhoneRelayReplyDispatchTests
{
    /// <summary>Makes every request look like it came from the LAN rather than from loopback.</summary>
    private sealed class NonLoopbackStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            builder =>
            {
                builder.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.100");
                    return nextMiddleware();
                });
                next(builder);
            };
    }

    private static RemexMessage BrowseRequest(string requestId) => new()
    {
        Type = MessageTypes.FileBrowseRequest,
        FileBrowseRequest = new FileBrowseRequest { RequestId = requestId, RootId = "root", RelativePath = "DCIM" },
    };

    private static RemexMessage BrowseReply(string requestId, string name, string? claimedClientId = null) => new()
    {
        Type = MessageTypes.FileBrowseResponse,
        ClientId = claimedClientId,
        FileBrowseResponse = new FileBrowseResponse
        {
            RequestId = requestId,
            Entries = [new FileEntry { Name = name, IsDirectory = false }],
        },
    };

    [Fact]
    public async Task AReplyFromAPinPairedLoopbackConnection_IsNotDelivered()
    {
        using var factory = new RemexHostFactory();
        var relay = factory.Services.GetRequiredService<PhoneFileRelay>();
        var sessions = factory.Services.GetRequiredService<ClientSessionRegistry>();
        var phoneId = $"phone-{Guid.NewGuid():N}";

        // A local process that completed a real PIN pairing over 127.0.0.1 under the phone's id.
        using var local = await ConnectAsync(factory);
        await PairAsync(local, factory.Services.GetRequiredService<PairingService>(), phoneId);

        // The real phone, proven and on the LAN, which the PC asks for a listing.
        var phoneSocket = new FakePhoneSocket();
        using var phoneSession = sessions.Register("192.168.1.50", phoneSocket);
        sessions.Identify(phoneSession, phoneId, null);
        sessions.MarkAuthenticated(phoneSession, identityProven: true);

        using var connection = relay.Open(phoneId);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;
        await connection.SendAsync(BrowseRequest("r1"));
        Assert.Single(phoneSocket.MessagesOfType(MessageTypes.FileBrowseRequest));

        // The local process answers the PC's question first, naming the right request id.
        await MessageSerializer.SendAsync(local, BrowseReply("r1", "planted.exe"), CancellationToken.None);
        await RoundTripAsync(local);

        Assert.Empty(received);

        // And the request was still waiting: the phone's own answer lands afterwards.
        Assert.True(relay.TryDeliverReply(phoneId, identityProven: true, isLoopback: false, BrowseReply("r1", "a.jpg")));
        Assert.Equal("a.jpg", Assert.Single(Assert.Single(received).FileBrowseResponse!.Entries).Name);
    }

    [Fact]
    public async Task AReplyFromAnUnprovenLanConnection_IsNotDelivered()
    {
        using var factory = new RemexHostFactory().WithServices(
            services => services.AddSingleton<IStartupFilter, NonLoopbackStartupFilter>());
        var relay = factory.Services.GetRequiredService<PhoneFileRelay>();
        var sessions = factory.Services.GetRequiredService<ClientSessionRegistry>();
        var phoneId = $"phone-{Guid.NewGuid():N}";
        factory.Services.GetRequiredService<PairedClientRegistry>().RegisterClient(phoneId, RandomNumberGenerator.GetBytes(32));

        var phoneSocket = new FakePhoneSocket();
        using var phoneSession = sessions.Register("192.168.1.50", phoneSocket);
        sessions.Identify(phoneSession, phoneId, null);
        sessions.MarkAuthenticated(phoneSession, identityProven: true);

        using var connection = relay.Open(phoneId);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;
        await connection.SendAsync(BrowseRequest("r1"));

        // Names the phone on a ping (and is challenged, which it never answers), then replies.
        using var impostor = await ConnectAsync(factory);
        await MessageSerializer.SendAsync(
            impostor, new RemexMessage { Type = MessageTypes.Ping, ClientId = phoneId, Timestamp = 1 }, CancellationToken.None);
        await MessageSerializer.SendAsync(impostor, BrowseReply("r1", "planted.exe", phoneId), CancellationToken.None);
        await RoundTripAsync(impostor);

        Assert.Empty(received);
        Assert.True(relay.TryDeliverReply(phoneId, identityProven: true, isLoopback: false, BrowseReply("r1", "a.jpg")));
        Assert.Single(received);
    }

    /// <summary>
    /// The control: the same path DOES deliver a proven LAN phone's answer, so the two refusals above
    /// are the handler's decision and not a dispatch that never reaches the relay.
    /// </summary>
    [Fact]
    public async Task AReplyFromAPinPairedLanPhone_IsDelivered()
    {
        using var factory = new RemexHostFactory().WithServices(
            services => services.AddSingleton<IStartupFilter, NonLoopbackStartupFilter>());
        var relay = factory.Services.GetRequiredService<PhoneFileRelay>();
        var sessions = factory.Services.GetRequiredService<ClientSessionRegistry>();
        var phoneId = $"phone-{Guid.NewGuid():N}";

        using var phone = await ConnectAsync(factory);
        await PairAsync(phone, factory.Services.GetRequiredService<PairingService>(), phoneId);
        await WaitForAsync(() => sessions.IsConnected(phoneId), "the paired phone never became reachable");

        using var connection = relay.Open(phoneId);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += message => { lock (received) received.Add(message); };
        await connection.SendAsync(BrowseRequest("r1"));

        var asked = await ReceiveOfTypeAsync(phone, MessageTypes.FileBrowseRequest);
        Assert.Equal("r1", asked?.FileBrowseRequest?.RequestId);
        await MessageSerializer.SendAsync(phone, BrowseReply("r1", "a.jpg"), CancellationToken.None);

        await WaitForAsync(() => { lock (received) return received.Count == 1; }, "the phone's reply was never delivered");
        lock (received)
            Assert.Equal("a.jpg", Assert.Single(received[0].FileBrowseResponse!.Entries).Name);
    }

    /// <summary>
    /// A ping and its pong: the handler processes one connection's messages in order, so once the
    /// pong is back, everything sent before the ping has been dispatched.
    /// </summary>
    private static async Task RoundTripAsync(WebSocket ws)
    {
        await MessageSerializer.SendAsync(
            ws, new RemexMessage { Type = MessageTypes.Ping, Timestamp = 42 }, CancellationToken.None);
        Assert.NotNull(await ReceiveOfTypeAsync(ws, MessageTypes.Pong));
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail(failure);
    }

    private static Task<WebSocket> ConnectAsync(RemexHostFactory factory) =>
        factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(factory.Server.BaseAddress, "/ws"), CancellationToken.None);

    /// <summary>A real PIN pairing, the same client side as <c>ConnectionBecomesRegistryVisibleTests</c>.</summary>
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 64; i++)
        {
            var message = await MessageSerializer.ReceiveAsync(socket, timeout.Token);
            if (message is null) return null;
            if (message.Type == type) return message;
        }

        return null;
    }
}

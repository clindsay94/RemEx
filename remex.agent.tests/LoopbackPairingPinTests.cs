using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Services.Security;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that the live pairing PIN never crosses a socket to a loopback caller (RemEx-fd7e, Q1=B).
/// </summary>
/// <remarks>
/// <para>
/// <c>pairing_pin_request</c> used to hand the active PIN to anything on 127.0.0.1, on the theory that
/// the PC's own UI was the loopback caller. It never was: the UI runs inside the agent process and
/// reads the PIN in-process through <see cref="IPairingService.TryGetActivePinInfo"/>
/// (<c>remex.desktop/Services/Security/IpcPairingPinQueryService.cs</c>). So the loopback branch served
/// exactly one population — every OTHER local process, including an unelevated one — during the one
/// window the user is least likely to notice a second pairing: while they are actively pairing.
/// </para>
/// <para>
/// Each test drives the same composition the <c>/ws</c> map site in <c>HostBootstrapper</c> runs:
/// <see cref="TransportTrust.IsTrustedForPinAutoFetch"/> on the connection's addresses, fed straight
/// into <see cref="PairingHandler.HandlePairingPinRequestAsync"/>. Every refusal is paired with a
/// legitimate path that must keep working — the in-process read, and Tailscale over the wire — so the
/// guard cannot be quietly over-tightened into breaking the PC UI or Tailscale auto-fill.
/// </para>
/// </remarks>
public sealed class LoopbackPairingPinTests
{
    private readonly Mock<ICertificateService> _certSvc = new();

    public LoopbackPairingPinTests()
    {
        _certSvc.Setup(c => c.GetSpkiSha256Base64()).Returns(Convert.ToBase64String(new byte[32]));
    }

    private (PairingHandler handler, PairingService svc) CreateHandler()
    {
        var svc = new PairingService(NullLogger<PairingService>.Instance, _certSvc.Object);
        var registry = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance,
            Path.Combine(Path.GetTempPath(), $"remex-loopback-pin-test-{Guid.NewGuid():N}.json"));
        return (new PairingHandler(NullLogger<PairingHandler>.Instance, svc, _certSvc.Object, registry), svc);
    }

    /// <summary>What <c>/ws</c> does with a <c>pairing_pin_request</c> arriving on these addresses.</summary>
    private static Task<RemexMessage> RequestPinOverWire(PairingHandler handler, string remote, string local) =>
        handler.HandlePairingPinRequestAsync(
            new RemexMessage
            {
                Type = MessageTypes.PairingPinRequest,
                ProtocolVersion = 2,
                ClientId = "local-process",
                CorrelationId = "corr-1",
            },
            TransportTrust.IsTrustedForPinAutoFetch(IPAddress.Parse(remote), IPAddress.Parse(local)),
            CancellationToken.None);

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "::1")]
    [InlineData("::ffff:127.0.0.1", "::ffff:127.0.0.1")]
    // A loopback caller cannot borrow trust from the host-side address it happened to reach.
    [InlineData("127.0.0.1", "100.64.0.9")]
    [InlineData("::1", "fd7a:115c:a1e0::9")]
    public async Task Loopback_WithAnOpenPairingWindow_IsNotHandedThePin(string remote, string local)
    {
        var (handler, svc) = CreateHandler();
        await svc.StartPairingAsync(CancellationToken.None);

        var resp = await RequestPinOverWire(handler, remote, local);

        Assert.Equal(MessageTypes.PairingPinResponse, resp.Type);
        Assert.Null(resp.PairingPin);
    }

    [Fact]
    public async Task Loopback_Refusal_IsIndistinguishableFromNoSession()
    {
        // A local process must not even learn that a pairing window is open: the refusal must be
        // byte-identical to "no active session", which is the existing contract for untrusted callers.
        var (open, openSvc) = CreateHandler();
        await openSvc.StartPairingAsync(CancellationToken.None);
        var refused = await RequestPinOverWire(open, "127.0.0.1", "127.0.0.1");

        var (idle, _) = CreateHandler();
        var noSession = await RequestPinOverWire(idle, "100.64.0.5", "100.64.0.9");

        Assert.Equal(MessageSerializer.Serialize(noSession), MessageSerializer.Serialize(refused));
    }

    [Fact]
    public async Task InProcessRead_TheWayThePcUiDoes_StillReturnsThePin()
    {
        // The legitimate caller the loopback branch was supposedly for. It never touches a socket.
        var (_, svc) = CreateHandler();
        var state = await svc.StartPairingAsync(CancellationToken.None);

        IPairingService inProcess = svc;
        Assert.True(inProcess.TryGetActivePinInfo(out var pin, out var expiresAtUnixMs));
        Assert.Equal(state.Pin, pin);
        Assert.Equal(state.ExpiresAtUnixMs, expiresAtUnixMs);
    }

    [Theory]
    [InlineData("100.64.0.5", "100.64.0.9")]
    [InlineData("fd7a:115c:a1e0::5", "fd7a:115c:a1e0::9")]
    [InlineData("::ffff:100.64.0.5", "::ffff:100.64.0.9")]
    public async Task Tailscale_BothEnds_StillGetsThePinOverTheWire(string remote, string local)
    {
        // The one out-of-process caller that legitimately still auto-fills: the phone over a live
        // Tailscale tunnel. Removing loopback must not take this with it.
        var (handler, svc) = CreateHandler();
        var state = await svc.StartPairingAsync(CancellationToken.None);

        var resp = await RequestPinOverWire(handler, remote, local);

        Assert.NotNull(resp.PairingPin);
        Assert.Equal(state.Pin, resp.PairingPin!.Pin);
    }

    // ── Over a real socket, through HostBootstrapper's /ws map site ──
    //
    // The tests above pin the gate and the handler. These pin the assembled path: the map site is
    // what reads Connection.RemoteIpAddress/LocalIpAddress and decides, and a handler-level test
    // passes with the map site computing the wrong bool. TestServer leaves both addresses null (which
    // fails closed and would prove nothing), so a startup filter stamps real ones on the connection.

    private sealed class ConnectionAddressStartupFilter(string remote, string local) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
                context.Connection.LocalIpAddress = IPAddress.Parse(local);
                return nextMiddleware();
            });
            next(builder);
        };
    }

    /// <returns>The <c>pairing_pin_response</c> (null on silence) and the PIN the host was showing.</returns>
    private static async Task<(RemexMessage? Response, string LivePin)> RequestPinOverRealSocketAsync(
        string remote, string local)
    {
        using var factory = new RemexHostFactory().WithServices(services =>
            services.AddSingleton<IStartupFilter>(new ConnectionAddressStartupFilter(remote, local)));
        var pairingService = factory.Services.GetRequiredService<PairingService>();
        pairingService.CancelPairing();

        // The user opened a pairing window on the PC; the PIN is on screen.
        var state = await pairingService.StartPairingAsync(CancellationToken.None);

        using var ws = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/ws"), CancellationToken.None);
        await MessageSerializer.SendAsync(ws, new RemexMessage
        {
            Type = MessageTypes.PairingPinRequest,
            ProtocolVersion = 2,
            ClientId = "local-process",
            CorrelationId = "corr-socket",
        }, CancellationToken.None);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 60; i++)
        {
            var message = await MessageSerializer.ReceiveAsync(ws, budget.Token);
            if (message is null) break;
            if (message.Type == MessageTypes.PairingPinResponse) return (message, state.Pin);
        }

        return (null, state.Pin);
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "::1")]
    public async Task Loopback_OverTheRealWsEndpoint_IsNotHandedThePin(string remote, string local)
    {
        var (resp, _) = await RequestPinOverRealSocketAsync(remote, local);

        // A reply must still arrive — silence would be a different bug — and it must carry no PIN.
        Assert.NotNull(resp);
        Assert.Null(resp!.PairingPin);
    }

    [Fact]
    public async Task Tailscale_OverTheRealWsEndpoint_StillGetsThePin()
    {
        var (resp, livePin) = await RequestPinOverRealSocketAsync("100.64.0.5", "100.64.0.9");

        Assert.NotNull(resp);
        Assert.NotNull(resp!.PairingPin);
        Assert.Equal(livePin, resp.PairingPin!.Pin);
    }
}

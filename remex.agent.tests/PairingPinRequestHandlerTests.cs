using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Core.Messages;
using Remex.Core.Services.Security;
using Remex.Agent.Handlers;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Tests;

/// <summary>
/// Gate behavior for <see cref="PairingHandler.HandlePairingPinRequestAsync"/> (RemEx-1t0b): the
/// host relays the active PIN over /ws only when the transport is trusted, and the pin-less "deny"
/// and "no session" responses must be indistinguishable (mirroring GET /pairing-pin's 404-for-both).
/// The handler only ever READS an active PIN via <see cref="PairingService.TryGetActivePinInfo"/> —
/// it never creates or mutates a session.
/// </summary>
public sealed class PairingPinRequestHandlerTests
{
    private readonly Mock<ICertificateService> _certSvc = new();

    public PairingPinRequestHandlerTests()
    {
        _certSvc.Setup(c => c.GetSpkiSha256Base64()).Returns(Convert.ToBase64String(new byte[32]));
    }

    private static PairedClientRegistry NewRegistry() =>
        new(NullLogger<PairedClientRegistry>.Instance,
            Path.Combine(Path.GetTempPath(), $"remex-pinreq-test-{Guid.NewGuid():N}.json"));

    private (PairingHandler handler, PairingService svc) CreateHandler()
    {
        var svc = new PairingService(NullLogger<PairingService>.Instance, _certSvc.Object);
        var handler = new PairingHandler(
            NullLogger<PairingHandler>.Instance, svc, _certSvc.Object, NewRegistry());
        return (handler, svc);
    }

    private static RemexMessage Request() => new()
    {
        Type = MessageTypes.PairingPinRequest,
        ProtocolVersion = 2,
        ClientId = "c1",
        CorrelationId = "corr-1",
    };

    [Fact]
    public async Task Trusted_WithActiveSession_ReturnsThePin()
    {
        var (handler, svc) = CreateHandler();
        var state = await svc.StartPairingAsync(CancellationToken.None);

        var resp = await handler.HandlePairingPinRequestAsync(
            Request(), isTrustedForPinAutoFetch: true, CancellationToken.None);

        Assert.Equal(MessageTypes.PairingPinResponse, resp.Type);
        Assert.Equal("corr-1", resp.CorrelationId);
        Assert.NotNull(resp.PairingPin);
        Assert.Equal(state.Pin, resp.PairingPin!.Pin);
        Assert.Equal(state.ExpiresAtUnixMs, resp.PairingPin.ExpiresAtUnixMs);
    }

    [Fact]
    public async Task Trusted_WithNoActiveSession_ReturnsNullPin()
    {
        var (handler, _) = CreateHandler(); // no StartPairingAsync ⇒ no active session

        var resp = await handler.HandlePairingPinRequestAsync(
            Request(), isTrustedForPinAutoFetch: true, CancellationToken.None);

        Assert.Equal(MessageTypes.PairingPinResponse, resp.Type);
        Assert.Null(resp.PairingPin);
    }

    [Fact]
    public async Task Untrusted_WithActiveSession_ReturnsNullPin()
    {
        var (handler, svc) = CreateHandler();
        await svc.StartPairingAsync(CancellationToken.None); // session IS active…

        var resp = await handler.HandlePairingPinRequestAsync(
            Request(), isTrustedForPinAutoFetch: false, CancellationToken.None); // …but transport isn't trusted

        Assert.Equal(MessageTypes.PairingPinResponse, resp.Type);
        Assert.Null(resp.PairingPin);
    }

    [Fact]
    public async Task DenyAndNoSession_AreByteIdentical_NoLeak()
    {
        // untrusted + active session
        var (h1, svc1) = CreateHandler();
        await svc1.StartPairingAsync(CancellationToken.None);
        var untrusted = await h1.HandlePairingPinRequestAsync(Request(), false, CancellationToken.None);

        // trusted + no session
        var (h2, _) = CreateHandler();
        var noSession = await h2.HandlePairingPinRequestAsync(Request(), true, CancellationToken.None);

        // Same correlationId in both requests ⇒ the two pin-less responses must serialize identically,
        // so an untrusted caller cannot tell "denied" from "no session exists".
        Assert.Equal(
            MessageSerializer.Serialize(untrusted),
            MessageSerializer.Serialize(noSession));
    }

    [Fact]
    public async Task DoesNotMutateSession_PinStillFetchableAfterward()
    {
        var (handler, svc) = CreateHandler();
        await svc.StartPairingAsync(CancellationToken.None);

        // Even an untrusted request must not tear the session down (non-destructive contract).
        await handler.HandlePairingPinRequestAsync(Request(), false, CancellationToken.None);

        Assert.True(svc.TryGetActivePinInfo(out var pin, out _));
        Assert.Equal(6, pin.Length);
    }
}

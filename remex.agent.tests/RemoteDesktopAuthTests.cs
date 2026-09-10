using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Tests;

/// <summary>
/// Unit coverage for <see cref="HostBootstrapper.EvaluateDesktopAuth"/>, the predicate
/// that governs which inbound /ws/desktop connections are accepted. We exercise the
/// helper directly because the default WebApplicationFactory TestServer reports a null
/// RemoteIpAddress (treated as loopback), which bypasses the auth check and makes the
/// rejection paths invisible to end-to-end socket tests.
/// </summary>
public sealed class RemoteDesktopAuthTests
{
    private static PairedClientRegistry NewRegistry()
    {
        // Disposable temp file so registry persistence doesn't pollute the test runner cwd.
        var tempPath = Path.Combine(Path.GetTempPath(), $"remex-desktop-auth-{Guid.NewGuid():N}.json");
        return new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, tempPath);
    }

    [Fact]
    public void Loopback_BypassesAuthCheck_EvenWithoutClientId()
    {
        var registry = NewRegistry();

        var (status, reason) = HostBootstrapper.EvaluateDesktopAuth(
            IPAddress.Loopback,
            clientId: "",
            protocolVersion: "",
            registry);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Null(reason);
    }

    [Fact]
    public void NullRemoteIp_IsTreatedAsLoopback()
    {
        // ASP.NET Core TestServer reports a null RemoteIpAddress for in-process requests.
        // The production code treats that as loopback, so the predicate must as well.
        var registry = NewRegistry();

        var (status, _) = HostBootstrapper.EvaluateDesktopAuth(
            remoteIp: null,
            clientId: "",
            protocolVersion: "",
            registry);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public void NonLoopback_WithoutClientId_Returns401()
    {
        var registry = NewRegistry();

        var (status, reason) = HostBootstrapper.EvaluateDesktopAuth(
            IPAddress.Parse("192.168.1.50"),
            clientId: "",
            protocolVersion: "",
            registry);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal("Paired client ID is required.", reason);
    }

    [Fact]
    public void NonLoopback_WithUnknownClientId_Returns403()
    {
        var registry = NewRegistry();

        var (status, reason) = HostBootstrapper.EvaluateDesktopAuth(
            IPAddress.Parse("192.168.1.50"),
            clientId: "unknown-client",
            protocolVersion: "",
            registry);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("Client is not paired.", reason);
    }

    [Fact]
    public void NonLoopback_WithPairedClientId_Returns200()
    {
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, reason) = HostBootstrapper.EvaluateDesktopAuth(
            IPAddress.Parse("192.168.1.50"),
            clientId: "paired-android-device",
            protocolVersion: "",
            registry);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Null(reason);
    }

    [Fact]
    public void ProtocolVersion_2_IsAccepted()
    {
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, _) = HostBootstrapper.EvaluateDesktopAuth(
            IPAddress.Parse("192.168.1.50"),
            clientId: "paired-android-device",
            protocolVersion: "2",
            registry);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public void ProtocolVersion_OlderValue_Returns400()
    {
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, reason) = HostBootstrapper.EvaluateDesktopAuth(
            IPAddress.Parse("192.168.1.50"),
            clientId: "paired-android-device",
            protocolVersion: "1",
            registry);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.NotNull(reason);
        Assert.Contains("'1'", reason);
    }

    [Fact]
    public void ProtocolVersion_NewerValue_IsAccepted()
    {
        // Forward-compat / accept-range policy (ProtocolVersionPolicy): a client advertising a
        // protocol version newer than the host minimum must NOT be bricked. The wire envelope is
        // additive, so a current host safely ignores any extra fields a future client adds.
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, _) = HostBootstrapper.EvaluateDesktopAuth(
            IPAddress.Parse("192.168.1.50"),
            clientId: "paired-android-device",
            protocolVersion: "3",
            registry);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public void ProtocolVersion_BadValueBeatsAllOtherChecks()
    {
        // Even on loopback with no clientId, a wrong protocolVersion should be rejected:
        // a malformed handshake is a malformed handshake regardless of who's connecting.
        var registry = NewRegistry();

        var (status, _) = HostBootstrapper.EvaluateDesktopAuth(
            IPAddress.Loopback,
            clientId: "",
            protocolVersion: "garbage",
            registry);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    // --- RemEx-4u0d: loopback may not ACT AS a paired phone ---------------------------------------
    // The bead was written about /ws/files. This endpoint carried the identical copy-pasted bypass:
    // loopback returned 200 with any ?clientId=, and the caller then cancels the prior
    // StreamFramesAsync loop for that id, so a local process naming a paired phone could kill that
    // phone's screen stream. Fixing only the reported endpoint would have left this one live.

    [Fact]
    public void Loopback_ClaimingAPairedClientId_IsRefused()
    {
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, reason) = HostBootstrapper.EvaluateDesktopAuth(
            IPAddress.Loopback,
            clientId: "paired-android-device",
            protocolVersion: "3",
            registry);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.NotNull(reason);
    }

    [Fact]
    public void NullRemoteIp_ClaimingAPairedClientId_IsRefused()
    {
        // A null RemoteIpAddress is treated as loopback here - it is what the TestServer reports -
        // so the guard has to cover it, or the bypass survives in the configuration the integration
        // tests actually run under.
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, _) = HostBootstrapper.EvaluateDesktopAuth(
            remoteIp: null,
            clientId: "paired-android-device",
            protocolVersion: "3",
            registry);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public void Loopback_WithAnUnknownClientId_IsStillAccepted()
    {
        // The direction that would break things if the guard were too broad: an id nobody is paired
        // with cannot displace anyone, so it stays allowed.
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, _) = HostBootstrapper.EvaluateDesktopAuth(
            IPAddress.Loopback,
            clientId: "some-local-tool",
            protocolVersion: "3",
            registry);

        Assert.Equal(StatusCodes.Status200OK, status);
    }
}

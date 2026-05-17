using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Host.Services.Security;

namespace Remex.Host.Tests;

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
    public void ProtocolVersion_OtherValue_Returns400()
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
}

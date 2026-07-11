using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Tests;

/// <summary>
/// WP4 unit coverage for <see cref="HostBootstrapper.EvaluateFilesAuth"/>, the predicate that governs which
/// inbound /ws/files (binary file-transfer channel) connections are accepted. Mirrors
/// <see cref="RemoteDesktopAuthTests"/>: exercised directly because the TestServer reports a null
/// RemoteIpAddress (treated as loopback), hiding the rejection paths from end-to-end socket tests. The
/// distinguishing rule from /ws/desktop is that the binary channel is v3-only — an explicit protocolVersion 2
/// is rejected here (plan §1.5).
/// </summary>
public sealed class FilesAuthTests
{
    private static PairedClientRegistry NewRegistry()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"remex-files-auth-{Guid.NewGuid():N}.json");
        return new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, tempPath);
    }

    [Fact]
    public void Loopback_BypassesAuthCheck_EvenWithoutClientId()
    {
        var (status, reason) = HostBootstrapper.EvaluateFilesAuth(
            IPAddress.Loopback, clientId: "", protocolVersion: "", NewRegistry());

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Null(reason);
    }

    [Fact]
    public void NullRemoteIp_IsTreatedAsLoopback()
    {
        var (status, _) = HostBootstrapper.EvaluateFilesAuth(
            remoteIp: null, clientId: "", protocolVersion: "", NewRegistry());

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public void NonLoopback_WithoutClientId_Returns401()
    {
        var (status, reason) = HostBootstrapper.EvaluateFilesAuth(
            IPAddress.Parse("192.168.1.50"), clientId: "", protocolVersion: "", NewRegistry());

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal("Paired client ID is required.", reason);
    }

    [Fact]
    public void NonLoopback_WithUnknownClientId_Returns403()
    {
        // The WP4 "unpaired /ws/files connection rejected" requirement.
        var (status, reason) = HostBootstrapper.EvaluateFilesAuth(
            IPAddress.Parse("192.168.1.50"), clientId: "unknown-client", protocolVersion: "", NewRegistry());

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("Client is not paired.", reason);
    }

    [Fact]
    public void NonLoopback_WithPairedClientId_NoVersion_Returns200()
    {
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, reason) = HostBootstrapper.EvaluateFilesAuth(
            IPAddress.Parse("192.168.1.50"), clientId: "paired-android-device", protocolVersion: "", registry);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Null(reason);
    }

    [Fact]
    public void NonLoopback_WithPairedClientId_Version3_Returns200()
    {
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, _) = HostBootstrapper.EvaluateFilesAuth(
            IPAddress.Parse("192.168.1.50"), clientId: "paired-android-device", protocolVersion: "3", registry);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public void ProtocolVersion_2_IsRejected_ForBinaryChannel()
    {
        // The core /ws/files difference from /ws/desktop: v2 peers must stay on the legacy base64 path and
        // are never admitted to the binary channel, even when otherwise paired.
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, reason) = HostBootstrapper.EvaluateFilesAuth(
            IPAddress.Parse("192.168.1.50"), clientId: "paired-android-device", protocolVersion: "2", registry);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.NotNull(reason);
        Assert.Contains("'2'", reason);
    }

    [Fact]
    public void ProtocolVersion_Garbage_Returns400_BeforeOtherChecks()
    {
        // Even on loopback with no clientId, a malformed handshake is a reject.
        var (status, _) = HostBootstrapper.EvaluateFilesAuth(
            IPAddress.Loopback, clientId: "", protocolVersion: "garbage", NewRegistry());

        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    [Fact]
    public void ProtocolVersion_NewerValue_IsAccepted()
    {
        // Forward-compat / accept-range policy: a client advertising a version newer than the minimum must
        // not be bricked.
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, _) = HostBootstrapper.EvaluateFilesAuth(
            IPAddress.Parse("192.168.1.50"), clientId: "paired-android-device", protocolVersion: "4", registry);

        Assert.Equal(StatusCodes.Status200OK, status);
    }
}

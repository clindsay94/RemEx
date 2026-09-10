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

    // --- RemEx-4u0d: loopback may not ACT AS a paired phone ---------------------------------------
    // The control plane was closed for this in RemEx-4215, but the binary channel never went through
    // connectionClientId at all: it takes its id straight from the query string, and the caller hands
    // that id to TransferSessionManager.RunChannelAsync, which supersedes the existing channel and
    // re-keys on it. So a local process naming a paired phone displaced that phone's binary channel
    // and could receive or inject the elevated agent's bulk file bytes mid-transfer.

    [Fact]
    public void Loopback_ClaimingAPairedClientId_IsRefused()
    {
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, reason) = HostBootstrapper.EvaluateFilesAuth(
            IPAddress.Loopback, clientId: "paired-android-device", protocolVersion: "3", registry);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.NotNull(reason);
    }

    [Fact]
    public void NullRemoteIp_ClaimingAPairedClientId_IsRefused()
    {
        // A null RemoteIpAddress is treated as loopback throughout this file - it is what the
        // TestServer reports - so the guard has to cover it too, or the bypass survives in the one
        // configuration the integration tests actually run under.
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, _) = HostBootstrapper.EvaluateFilesAuth(
            remoteIp: null, clientId: "paired-android-device", protocolVersion: "3", registry);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public void Loopback_WithNoClientId_IsStillAccepted()
    {
        // The other direction, and the one that would break things if the guard were too broad: a
        // local connection that claims nothing is not impersonating anyone. This is the shape the
        // TestServer uses, so over-tightening here would fail the integration suite rather than the
        // attack.
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, _) = HostBootstrapper.EvaluateFilesAuth(
            IPAddress.Loopback, clientId: "", protocolVersion: "3", registry);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public void Loopback_WithAnUnknownClientId_IsStillAccepted()
    {
        // An id nobody is paired with cannot collide with a phone's channel key, so it stays allowed.
        // Refusing it would be guessing at a threat that does not exist and would block any future
        // local consumer of this endpoint for no gain.
        var registry = NewRegistry();
        registry.RegisterClient("paired-android-device");

        var (status, _) = HostBootstrapper.EvaluateFilesAuth(
            IPAddress.Loopback, clientId: "some-local-tool", protocolVersion: "3", registry);

        Assert.Equal(StatusCodes.Status200OK, status);
    }
}

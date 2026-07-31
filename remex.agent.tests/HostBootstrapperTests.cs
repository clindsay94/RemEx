using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Remex.Agent;

namespace Remex.Agent.Tests;

public sealed class HostBootstrapperTests : IClassFixture<RemexHostFactory>
{
    private readonly RemexHostFactory _factory;

    public HostBootstrapperTests(RemexHostFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PairingQr_ReturnsStableHostId()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/pairing-qr");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HostBootstrapper.HostId, document.RootElement.GetProperty("hostId").GetString());
    }

    // VULN-1 (RemEx-s032.1): the unauthenticated /debug/logs endpoint — which served the in-memory log
    // buffer (retaining the live pairing PIN and full paired clientIds) to any network-reachable caller —
    // has been removed. An unmapped route yields 404.
    [Fact]
    public async Task DebugLogs_EndpointRemoved_Returns404()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/debug/logs");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // VULN-6 (RemEx-s032.6): the dead dev-only /download/apk endpoint (served a hardcoded Z:\ debug-APK
    // path) has been removed entirely.
    [Fact]
    public async Task DownloadApk_EndpointRemoved_Returns404()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/download/apk");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // VULN-6 (RemEx-s032.6): the anonymous root handshake must NOT leak host capabilities or the
    // remote-desktop diagnostic report (OS / capture backend / capability + failure detail) to unauth
    // callers — that detail aids targeting and now reaches paired clients over /ws only.
    [Fact]
    public async Task Root_HandshakeIsMinimal_NoCapabilitiesOrDiagnostics()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("running", document.RootElement.GetProperty("status").GetString());
        Assert.False(document.RootElement.TryGetProperty("capabilities", out _),
            "GET / must not expose host capabilities to anonymous callers.");
        Assert.False(document.RootElement.TryGetProperty("remoteDesktopDiagnostics", out _),
            "GET / must not expose the remote-desktop diagnostic report to anonymous callers.");
    }
}

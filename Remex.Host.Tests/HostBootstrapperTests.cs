using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Remex.Host;

namespace Remex.Host.Tests;

public sealed class HostBootstrapperTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HostBootstrapperTests(WebApplicationFactory<Program> factory)
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
}

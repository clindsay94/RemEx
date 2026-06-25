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
}

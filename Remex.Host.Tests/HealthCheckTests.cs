using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc.Testing;
using Remex.Core.Messages;

namespace Remex.Host.Tests;

public class HealthCheckTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthCheckTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetRoot_ReturnsOkWithServiceInfo()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Remex.Host", body);
        Assert.Contains("running", body);
    }

    [Fact]
    public async Task GetWsEndpoint_WithoutWebSocket_Returns400()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/ws");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

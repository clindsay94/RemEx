using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Remex.Agent.Tests;

/// <summary>
/// Verifies the headless command-agent mode (<c>--agent</c>) serves the command plane but disables
/// remote-desktop streaming.
/// </summary>
public class HostAgentModeTests
{
    [Fact]
    public async Task CommandAgent_DisablesDesktopStreamingEndpoint()
    {
        using var factory = new RemexHostFactory().WithMode(HostMode.CommandAgent);
        using var client = factory.CreateClient();

        // /ws/desktop is not served in agent mode -> 404, before any screen-capture/portal work.
        var desktop = await client.GetAsync("/ws/desktop");
        Assert.Equal(HttpStatusCode.NotFound, desktop.StatusCode);

        // The command plane still works: root info endpoint responds, and /ws is mapped
        // (a plain non-WebSocket GET is rejected with 400, not 404).
        var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);

        var ws = await client.GetAsync("/ws");
        Assert.Equal(HttpStatusCode.BadRequest, ws.StatusCode);
    }

    [Fact]
    public async Task Full_KeepsDesktopStreamingEndpoint()
    {
        using var factory = new RemexHostFactory().WithMode(HostMode.Full);
        using var client = factory.CreateClient();

        // In full mode /ws/desktop IS mapped; a non-WebSocket GET is rejected with 400 (not 404).
        var desktop = await client.GetAsync("/ws/desktop");
        Assert.Equal(HttpStatusCode.BadRequest, desktop.StatusCode);
    }
}

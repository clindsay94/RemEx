using System.Runtime.InteropServices;
using Remex.Core;
using Remex.Core.Services;
using Remex.Host.Handlers;
using Remex.Host.Services.Telemetry;

var builder = WebApplication.CreateBuilder(args);

if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<ITelemetryService, WindowsTelemetryService>();
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<ITelemetryService, LinuxTelemetryService>();
}

// Headless: suppress browser launch and Kestrel HTTPS dev-cert noise.
builder.WebHost.UseUrls($"http://0.0.0.0:{RemexConstants.DefaultPort}");

var app = builder.Build();

// Enable WebSocket support.
app.UseWebSockets();

// --- Minimal API endpoints ---

// Health-check / discovery
app.MapGet("/", () => Results.Ok(new { service = "Remex.Host", status = "running" }));

// WebSocket hub
app.Map(RemexConstants.WebSocketPath, async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket connections only.");
        return;
    }

    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    var logger = context.RequestServices.GetRequiredService<ILogger<PingPongHandler>>();
    var telemetry = context.RequestServices.GetRequiredService<ITelemetryService>();
    var handler = new PingPongHandler(logger, telemetry);
    await handler.HandleAsync(ws, context.RequestAborted);
});

app.Run();

// Needed for WebApplicationFactory<Program> in integration tests.
public partial class Program { }

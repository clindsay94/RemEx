using Remex.Core;
using Remex.Core.Services;
using Remex.Host.Handlers;
using Remex.Host.Services.Telemetry;

namespace Remex.Host;

/// <summary>
/// Encapsulates the Remex Host WebApplication setup so it can be started
/// both as a standalone server and embedded inside the Desktop client.
/// </summary>
public static class HostBootstrapper
{
    /// <summary>
    /// Builds and configures the Remex Host <see cref="WebApplication"/>
    /// without starting it. Call <c>Run()</c> or <c>StartAsync()</c> on
    /// the returned application to begin listening.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the builder.</param>
    /// <param name="port">
    /// Override the listening port. Defaults to <see cref="RemexConstants.DefaultPort"/>.
    /// </param>
    public static WebApplication CreateApplication(string[] args, int port = RemexConstants.DefaultPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Enable Windows Service lifetime (no-op when not running under SCM).
        builder.Host.UseWindowsService();

        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddSingleton<ITelemetryService, WindowsTelemetryService>();
        }
        else if (OperatingSystem.IsLinux())
        {
            builder.Services.AddSingleton<ITelemetryService, LinuxTelemetryService>();
        }

        // Headless: suppress browser launch and Kestrel HTTPS dev-cert noise.
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

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

        return app;
    }
}

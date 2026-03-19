using Remex.Core;
using Remex.Core.Services;
using Remex.Host.Handlers;
using Remex.Host.Services;
using Remex.Host.Services.Telemetry;
using Remex.Host.Services.ProcessMonitor;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace Remex.Host;

/// <summary>
/// Encapsulates the Remex Host WebApplication setup so it can be started
/// both as a standalone server and embedded inside the Desktop client.
/// </summary>
public static class HostBootstrapper
{
    /// <summary>
    /// Unique instance identifier for this host process.
    /// Used by remote desktop to detect self-connections (infinite mirror prevention).
    /// </summary>
    public static string InstanceId { get; } = Guid.NewGuid().ToString("N");
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

        builder.Services.AddSingleton<Remex.Core.Services.Network.IWakeOnLanService, Remex.Core.Services.Network.WakeOnLanService>();
        builder.Services.AddSingleton<Remex.Core.Services.Network.INetworkListener, Remex.Core.Services.Network.RemexNetworkListener>();
        builder.Services.AddHostedService<Remex.Host.Services.IPC.LocalIpcServerService>();
        builder.Services.AddHostedService<Remex.Host.Services.Network.ExternalNetworkListenerService>();

        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddSingleton<ITelemetryService, WindowsTelemetryService>();
            builder.Services.AddSingleton<Remex.Core.Services.Command.ISystemCommandService, Remex.Core.Services.Command.WindowsSystemCommandService>();
            builder.Services.AddSingleton<IProcessMonitorService, WindowsProcessMonitorService>();
            builder.Services.AddSingleton<IScreenCaptureService, Remex.Host.Services.ScreenCapture.WindowsScreenCaptureService>();
            builder.Services.AddSingleton<IInputSimulationService, Remex.Host.Services.Input.WindowsInputSimulationService>();
        }
        else if (OperatingSystem.IsLinux())
        {
            builder.Services.AddSingleton<ITelemetryService, LinuxTelemetryService>();
            builder.Services.AddSingleton<Remex.Core.Services.Command.ISystemCommandService, Remex.Core.Services.Command.LinuxSystemCommandService>();
            builder.Services.AddSingleton<IProcessMonitorService, LinuxProcessMonitorService>();
            builder.Services.AddSingleton<IScreenCaptureService, Remex.Host.Services.ScreenCapture.LinuxScreenCaptureService>();
            builder.Services.AddSingleton<IInputSimulationService, Remex.Host.Services.Input.LinuxInputSimulationService>();
        }

        builder.Services.AddSingleton<Remex.Core.Services.ILauncherStorageService, Remex.Core.Services.LauncherStorageService>();
        builder.Services.AddSingleton<Remex.Core.Services.IDashboardProfileStorageService, Remex.Core.Services.DashboardProfileStorageService>();
        builder.Services.AddSingleton<Remex.Core.Services.IAppLauncherService, Remex.Host.Services.AppLauncherService>();
        builder.Services.AddHostedService<IpcHostServer>();

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
            var handler = new PingPongHandler(
                logger, 
                telemetry, 
                context.RequestServices.GetRequiredService<Remex.Core.Services.Command.ISystemCommandService>(), 
                context.RequestServices.GetRequiredService<Remex.Core.Services.Network.IWakeOnLanService>(), 
                context.RequestServices.GetRequiredService<Remex.Core.Services.ILauncherStorageService>(), 
                context.RequestServices.GetRequiredService<Remex.Core.Services.IAppLauncherService>(),
                context.RequestServices.GetRequiredService<Remex.Core.Services.IDashboardProfileStorageService>(),
                context.RequestServices.GetRequiredService<Remex.Core.Services.IProcessMonitorService>());
            await handler.HandleAsync(ws, context.RequestAborted);
        });

        // Remote Desktop WebSocket endpoint (dedicated binary stream)
        app.Map("/ws/desktop", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket connections only.");
                return;
            }

            // Restrict remote desktop access: allow localhost by default, and require
            // explicit configuration to permit remote connections.
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            var allowRemoteConnections = configuration.GetValue<bool>("RemoteDesktop:AllowRemoteConnections");
            var remoteIp = context.Connection.RemoteIpAddress;
            var isLocalRequest = remoteIp == null || IPAddress.IsLoopback(remoteIp);

            if (!allowRemoteConnections && !isLocalRequest)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Remote desktop is only available from localhost unless explicitly enabled in configuration.");
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var handler = new RemoteDesktopHandler(
                context.RequestServices.GetRequiredService<ILogger<RemoteDesktopHandler>>(),
                context.RequestServices.GetRequiredService<IScreenCaptureService>(),
                context.RequestServices.GetRequiredService<IInputSimulationService>());
            await handler.HandleAsync(ws, context.RequestAborted);
        });

        return app;
    }
}

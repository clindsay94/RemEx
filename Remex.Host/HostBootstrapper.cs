using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Remex.Core;
using Remex.Core.Services;
using Remex.Host.Handlers;
using Remex.Host.Services;
using Remex.Host.Services.Telemetry;
using Remex.Host.Services.ProcessMonitor;

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
        builder.Services.AddSingleton<IHostCapabilitiesProvider, HostCapabilitiesProvider>();
        builder.Services.AddHostedService<Remex.Host.Services.IPC.LocalIpcServerService>();
        builder.Services.AddHostedService<Remex.Host.Services.Network.ExternalNetworkListenerService>();
        builder.Services.AddHostedService<Remex.Host.Services.Network.MdnsAdvertisingService>();

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

        builder.Services.AddSingleton<TelemetryBackgroundService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryBackgroundService>());

        builder.Services.AddSingleton<Remex.Core.Services.ILauncherStorageService, Remex.Core.Services.LauncherStorageService>();
        builder.Services.AddSingleton<Remex.Core.Services.IDashboardProfileStorageService, Remex.Core.Services.DashboardProfileStorageService>();
        builder.Services.AddSingleton<Remex.Core.Services.IAppLauncherService, Remex.Host.Services.AppLauncherService>();
        builder.Services.AddHostedService<IpcHostServer>();

        // Headless: suppress browser launch and Kestrel HTTPS dev-cert noise.
        // Try the requested port first; if it's unavailable, probe fallback ports.
        int actualPort = port;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int testPort = port + attempt;
            try
            {
                using var testSocket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                testSocket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, testPort));
                testSocket.Close();
                actualPort = testPort;
                break;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Port in use, try next
            }
        }
        builder.WebHost.UseUrls($"http://0.0.0.0:{actualPort}");

        var app = builder.Build();

        // Session 0 detection: warn when running as a non-interactive Windows service
        if (OperatingSystem.IsWindows() && Process.GetCurrentProcess().SessionId == 0)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Remex.Host");
            logger.LogWarning(
                "⚠ Remex.Host is running in Session 0 (non-interactive). " +
                "Screen capture and app launching will NOT work in this session. " +
                "Configure the service to 'Log on as' your Windows user account.");
        }

        // Read the access key from configuration (supports appsettings.json, env vars, CLI args).
        // Env var: Remex__AccessKey   CLI: --Remex:AccessKey=<value>
        var accessKey = app.Configuration["Remex:AccessKey"] ?? "";

        // Enable WebSocket support.
        app.UseWebSockets();

        // --- Minimal API endpoints ---

        // Health-check / discovery
        app.MapGet("/", (IHostCapabilitiesProvider hostCapabilitiesProvider) => Results.Ok(new
        {
            service = "Remex.Host",
            status = "running",
            capabilities = hostCapabilitiesProvider.GetCurrent(),
        }));

        // WebSocket hub
        app.Map(RemexConstants.WebSocketPath, async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket connections only.");
                return;
            }

            if (!ValidateAccessKey(context, accessKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid or missing access key.");
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var logger = context.RequestServices.GetRequiredService<ILogger<PingPongHandler>>();
            var telemetry = context.RequestServices.GetRequiredService<TelemetryBackgroundService>();
            var handler = new PingPongHandler(
                logger, 
                telemetry, 
                context.RequestServices.GetRequiredService<Remex.Core.Services.Command.ISystemCommandService>(), 
                context.RequestServices.GetRequiredService<Remex.Core.Services.Network.IWakeOnLanService>(), 
                context.RequestServices.GetRequiredService<Remex.Core.Services.ILauncherStorageService>(), 
                context.RequestServices.GetRequiredService<Remex.Core.Services.IAppLauncherService>(),
                context.RequestServices.GetRequiredService<Remex.Core.Services.IDashboardProfileStorageService>(),
                context.RequestServices.GetRequiredService<Remex.Core.Services.IProcessMonitorService>(),
                context.RequestServices.GetRequiredService<IHostCapabilitiesProvider>(),
                context.RequestServices.GetRequiredService<IInputSimulationService>());
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

            if (!ValidateAccessKey(context, accessKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid or missing access key.");
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            using var handler = new RemoteDesktopHandler(
                context.RequestServices.GetRequiredService<ILogger<RemoteDesktopHandler>>(),
                context.RequestServices.GetRequiredService<IScreenCaptureService>(),
                context.RequestServices.GetRequiredService<IInputSimulationService>(),
                context.RequestServices.GetRequiredService<IHostCapabilitiesProvider>());
            await handler.HandleAsync(ws, context.RequestAborted);
        });

        return app;
    }

    /// <summary>
    /// Validates the access key from the <c>key</c> query-string parameter.
    /// Returns <c>true</c> when no key is configured (feature disabled) or
    /// when the supplied key matches using a constant-time comparison.
    /// </summary>
    private static bool ValidateAccessKey(HttpContext context, string configuredKey)
    {
        if (string.IsNullOrEmpty(configuredKey))
            return true; // Access key not configured — open access.

        var suppliedKey = context.Request.Query["key"].ToString();
        if (string.IsNullOrEmpty(suppliedKey))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(configuredKey),
            Encoding.UTF8.GetBytes(suppliedKey));
    }
}

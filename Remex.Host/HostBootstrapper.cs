using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Remex.Core;
using Remex.Core.Services;
using Remex.Core.Services.Security;
using Remex.Host.Handlers;
using Remex.Host.Services;
using Remex.Host.Services.Security;
using Remex.Host.Services.Telemetry;
using Remex.Host.Services.ProcessMonitor;
using Remex.Core.Services.FileTransfer;
using Remex.Host.Services.FileTransfer;

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

        // ── 2.0 Security Services ──
        builder.Services.AddSingleton<ICertificateService, CertificateService>();
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton<IPairingService>(sp => sp.GetRequiredService<PairingService>());
        builder.Services.AddTransient<PairingHandler>();
        builder.Services.AddSingleton<IFileTransferService, FileTransferService>();
        builder.Services.AddTransient<FileTransferHandler>();

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
        // ── 2.0 TLS Configuration ──
        // Generate or load the self-signed certificate synchronously during startup.
        var certService = new CertificateService(
            Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole())
                .CreateLogger<CertificateService>());
        var tlsCert = certService.GetOrCreateCertificateAsync(CancellationToken.None)
            .GetAwaiter().GetResult();

        // Replace the default CertificateService singleton with the pre-initialized one.
        builder.Services.AddSingleton<ICertificateService>(certService);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(actualPort, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                listenOptions.UseHttps(httpsOptions =>
                {
                    httpsOptions.ServerCertificate = tlsCert;
                    httpsOptions.SslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12;
                });
            });
        });

        builder.Configuration["Host:Port"] = actualPort.ToString();

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

        app.MapGet("/pairing-qr", (ICertificateService certService, IConfiguration config) =>
        {
            var port = int.Parse(config["Host:Port"] ?? "5005");
            return Results.Ok(new
            {
                host = "0.0.0.0", // Client should substitute with actual host address
                port = port,
                hostId = HostBootstrapper.InstanceId,
                spkiHashBase64 = certService.GetSpkiSha256Base64()
            });
        });

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
                context.RequestServices.GetRequiredService<IInputSimulationService>(),
                context.RequestServices.GetRequiredService<PairingHandler>());
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
}

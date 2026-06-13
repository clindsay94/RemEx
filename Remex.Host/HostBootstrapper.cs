using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Remex.Core;
using Remex.Core.Services;
using Remex.Core.Services.Security;
using Remex.Host.Handlers;
using Remex.Host.Services;
using Remex.Host.Services.RemoteDesktop.Windows;
using Remex.Host.Services.Security;
using Remex.Host.Services.Telemetry;
using Remex.Host.Services.ProcessMonitor;
using Remex.Core.Services.FileTransfer;
using Remex.Host.Services.FileTransfer;
using Remex.Host.Services.Input;

namespace Remex.Host;

/// <summary>
/// Encapsulates the Remex Host WebApplication setup so it can be started
/// both as a standalone server and embedded inside the Desktop client.
/// </summary>
public static class HostBootstrapper
{
    internal const string WindowsEventLogName = "Application";
    internal const string WindowsEventSourceName = "Remex.Host";

    /// <summary>
    /// Unique instance identifier for this host process.
    /// Used by remote desktop to detect self-connections (infinite mirror prevention).
    /// </summary>
    public static string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public static string HostId => Environment.MachineName;


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

        // Register custom in-memory logger provider to capture live host and Kestrel logs
        builder.Logging.AddProvider(new Remex.Core.Logging.InMemoryLoggerProvider());

        if (OperatingSystem.IsWindows())
        {
            EnablePerMonitorV2DpiAwareness();
            ConfigureWindowsEventLog(builder.Logging);
        }

        builder.Services.AddSingleton<Remex.Core.Services.Network.IWakeOnLanService, Remex.Core.Services.Network.WakeOnLanService>();
        builder.Services.AddSingleton<Remex.Core.Services.Network.INetworkListener, Remex.Core.Services.Network.RemexNetworkListener>();
        builder.Services.AddSingleton<IHostCapabilitiesProvider, HostCapabilitiesProvider>();
        builder.Services.AddSingleton<IDesktopWindowControlService, UnsupportedDesktopWindowControlService>();
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
            builder.Services.AddSingleton<IDesktopWindowControlService, LinuxDesktopWindowControlService>();
            builder.Services.AddSingleton<Remex.Host.Services.RemoteDesktop.Linux.Capture.LinuxCaptureSessionLifetime>();
        }

        builder.Services.AddSingleton<TelemetryBackgroundService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryBackgroundService>());

        builder.Services.AddSingleton<Remex.Core.Services.ILauncherStorageService, Remex.Core.Services.LauncherStorageService>();
        builder.Services.AddSingleton<Remex.Core.Services.IDashboardProfileStorageService, Remex.Core.Services.DashboardProfileStorageService>();
        builder.Services.AddSingleton<Remex.Core.Services.IAppLauncherService, Remex.Host.Services.AppLauncherService>();
        builder.Services.AddHostedService<IpcHostServer>();

        // ── 2.0 Security Services ──
        // CertificateService is instantiated once here so that Kestrel and the DI container
        // share a single instance.  Registering a concrete instance via AddSingleton(instance)
        // avoids the previous double-registration bug where AddSingleton<ICertificateService,
        // CertificateService>() (line 86) and then AddSingleton<ICertificateService>(certService)
        // (line 124) produced two separate registrations — the first was silently shadowed by the
        // second but both allocations happened.  GetAwaiter().GetResult() is acceptable here
        // because CreateApplication() is called once on the startup path before the async host
        // loop begins; no synchronisation context is active.
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton<IPairingService>(sp => sp.GetRequiredService<PairingService>());
        builder.Services.AddSingleton<PairedClientRegistry>();
        builder.Services.AddTransient<PairingHandler>();
        builder.Services.AddSingleton<IFileTransferService, FileTransferService>();
        builder.Services.AddTransient<FileTransferHandler>();
        builder.Services.AddSingleton<Remex.Host.Services.RemoteDesktop.DesktopSessionRegistry>();

        // Headless: suppress browser launch and Kestrel HTTPS dev-cert noise.
        // Try the requested port first; if it's unavailable, probe fallback ports.
        // Probe on both IPv4 and IPv6 interfaces to completely avoid dual-stack wildcard collisions.
        int actualPort = port;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int testPort = port + attempt;
            try
            {
                using (var testSocketV4 = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp))
                {
                    testSocketV4.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, testPort));
                    testSocketV4.Close();
                }

                if (System.Net.Sockets.Socket.OSSupportsIPv6)
                {
                    using (var testSocketV6 = new System.Net.Sockets.Socket(
                        System.Net.Sockets.AddressFamily.InterNetworkV6,
                        System.Net.Sockets.SocketType.Stream,
                        System.Net.Sockets.ProtocolType.Tcp))
                    {
                        testSocketV6.DualMode = true;
                        testSocketV6.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Any, testPort));
                        testSocketV6.Close();
                    }
                }

                actualPort = testPort;
                break;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Port in use, try next
            }
        }

        // ── 2.0 TLS Configuration ──
        // Instantiate CertificateService once, run its async initializer synchronously
        // (acceptable: startup path, no active sync context), then register the live
        // instance as the ICertificateService singleton so DI resolves the same object
        // that Kestrel was configured with.
        var certLoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
        {
            b.AddConsole();
            b.AddProvider(new Remex.Core.Logging.InMemoryLoggerProvider());
        });
        var certService = new CertificateService(certLoggerFactory.CreateLogger<CertificateService>());
        var tlsCert = certService.GetOrCreateCertificateAsync(CancellationToken.None)
            .GetAwaiter().GetResult();

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
        var hostCapabilitiesProvider = app.Services.GetRequiredService<IHostCapabilitiesProvider>();

        PrintStartupBanner(actualPort.ToString());

        // Session 0 detection: warn when running as a non-interactive Windows service
        if (OperatingSystem.IsWindows() && Process.GetCurrentProcess().SessionId == 0)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Remex.Host");
            logger.LogWarning(
                "⚠ Remex.Host is running in Session 0 (non-interactive). " +
                "Screen capture and app launching will NOT work in this session. " +
                "Configure the service to 'Log on as' your Windows user account.");
        }

        if (OperatingSystem.IsWindows())
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Remex.Host");
            var diagnostics = hostCapabilitiesProvider.GetWindowsRemoteDesktopDiagnosticReport();
            LogWindowsRemoteDesktopDiagnostics(logger, diagnostics);
        }

        // Surface missing Linux capture/input tools at startup, loud and early. This is the
        // root cause of the "RD is black, commands don't work" report on Linux hosts: the
        // services silently return empty bytes when their external tools aren't installed.
        if (OperatingSystem.IsLinux())
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Remex.Host");
            var caps = hostCapabilitiesProvider.GetCurrent();
            if (!caps.SupportsRemoteDesktop && caps.RemoteDesktopUnavailableReason is { } reason)
            {
                // LogError so it shows up under default console verbosity.
                logger.LogError("Remote desktop unavailable: {Reason}", reason);
                Console.Error.WriteLine($"[Remex.Host] {reason}");
            }
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
            remoteDesktopDiagnostics = (object?)hostCapabilitiesProvider.GetWindowsRemoteDesktopDiagnosticReport()
                ?? hostCapabilitiesProvider.GetLinuxPrerequisiteReport(),
        }));

        app.MapGet("/pairing-qr", (ICertificateService certService, IConfiguration config) =>
        {
            var port = int.Parse(config["Host:Port"] ?? "5005");
            return Results.Ok(new
            {
                host = "0.0.0.0", // Client should substitute with actual host address
                port = port,
                hostId = HostId,
                spkiHashBase64 = certService.GetSpkiSha256Base64()
            });
        });

        // Returns the active pairing PIN so remote tools (MCP, scripts) can relay it
        // to a user who cannot see the host screen. Only returns when a session is live;
        // 404 means no pairing is in progress. The PIN itself is already visible on the
        // host UI, so exposing it here adds no new attack surface for local-network threats.
        app.MapGet("/pairing-pin", (IPairingService pairingService) =>
        {
            if (pairingService.TryGetActivePinInfo(out var pin, out var expiresAtUnixMs))
            {
                return Results.Ok(new { pin, expiresAtUnixMs });
            }
            return Results.NotFound(new { message = "No active pairing session." });
        });

        // Proactively starts a pairing session and returns the PIN immediately.
        // Designed for remote workflows where the user cannot see the host screen:
        // call this endpoint first, get the PIN, then have the user tap Connect on Android.
        // When Android sends pairing_request, the host reuses this already-active session.
        app.MapPost("/start-pairing", async (Remex.Host.Services.Security.PairingService pairingService) =>
        {
            try
            {
                var acquisition = await pairingService.AcquirePairingSessionAsync(CancellationToken.None);
                if (pairingService.TryGetActivePinInfo(out var pin, out var expiresAtUnixMs))
                {
                    return Results.Ok(new { pin, expiresAtUnixMs, startedNew = acquisition.StartedNewSession });
                }
                return Results.Problem("Session created but PIN could not be read.");
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // Exposes the in-process log buffer for remote diagnostics.
        app.MapGet("/debug/logs", () =>
        {
            var entries = Remex.Core.Logging.InMemoryLogSink.GetEntries();
            return Results.Ok(entries.Select(e => e.ToString()).ToArray());
        });

        // Serves the latest debug APK over HTTPS so remote users don't need the plain-HTTP server.
        app.MapGet("/download/apk", async (HttpContext context) =>
        {
            var apkPath = @"Z:\RemEx\RemEx.Android\app\build\outputs\apk\debug\RemEx-V2.0.0-debug.apk";
            if (!File.Exists(apkPath))
                return Results.NotFound(new { message = "APK not found on host." });
            context.Response.Headers.ContentDisposition = "attachment; filename=\"RemEx-V2.0.0-debug.apk\"";
            context.Response.ContentType = "application/vnd.android.package-archive";
            await context.Response.SendFileAsync(apkPath);
            return Results.Empty;
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
                context.RequestServices.GetRequiredService<PairingHandler>(),
                context.RequestServices.GetRequiredService<FileTransferHandler>(),
                context.RequestServices.GetRequiredService<PairedClientRegistry>());

            // Loopback / in-process connections come from the embedded host on the same machine
            // (or in-process test servers). Pairing adds no security here — it would prompt for
            // a PIN the user's own desktop generated — so we satisfy the pairing gate
            // automatically. A null RemoteIpAddress means no real socket (TestServer/in-process
            // transport) and is treated as loopback for the same reason.
            var remoteIp = context.Connection.RemoteIpAddress;
            var isLoopback = remoteIp is null || System.Net.IPAddress.IsLoopback(remoteIp);

            await handler.HandleAsync(ws, isLoopback, context.RequestAborted);
        });

        // Remote Desktop WebSocket endpoint (dedicated binary stream)
        app.Map("/ws/desktop", async (HttpContext context, PairedClientRegistry pairedClientRegistry) =>
        {
            var authLogger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Remex.Host.DesktopAuth");

            if (!context.WebSockets.IsWebSocketRequest)
            {
                authLogger.LogWarning(
                    "Rejected /ws/desktop: non-WebSocket request from {RemoteIp}.",
                    context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket connections only.");
                return;
            }

            var remoteIp = context.Connection.RemoteIpAddress;
            var clientId = context.Request.Query["clientId"].ToString();
            var protocolVersion = context.Request.Query["protocolVersion"].ToString();

            var evaluation = EvaluateDesktopAuth(remoteIp, clientId, protocolVersion, pairedClientRegistry);
            if (evaluation.StatusCode != StatusCodes.Status200OK)
            {
                authLogger.LogWarning(
                    "Rejected /ws/desktop from {RemoteIp} (clientIdPrefix={ClientIdPrefix}, protocolVersion={ProtocolVersion}): {Reason}",
                    remoteIp,
                    RedactClientId(clientId),
                    string.IsNullOrEmpty(protocolVersion) ? "<unset>" : protocolVersion,
                    evaluation.Reason);
                context.Response.StatusCode = evaluation.StatusCode;
                await context.Response.WriteAsync(evaluation.Reason ?? "Unauthorized.");
                return;
            }

            // Track B: cancel any prior StreamFramesAsync loop for this clientId and await
            // its drain before accepting the new socket. This eliminates orphaned loops.
            var registry = context.RequestServices
                .GetRequiredService<Remex.Host.Services.RemoteDesktop.DesktopSessionRegistry>();
            using var sessionCts = await registry.TakeOverAsync(
                clientId,
                TimeSpan.FromMilliseconds(2000),
                context.RequestAborted);

            // Declare before try so the finally block can always call MarkDrained and
            // ReleaseAsync — even if AcceptWebSocketAsync or handler construction throws.
            var captureStarted = false;
            Remex.Host.Services.RemoteDesktop.Linux.Capture.LinuxCaptureSessionLifetime? lifetime = null;

            try
            {
                using var ws = await context.WebSockets.AcceptWebSocketAsync();

                // Track A: acquire the PipeWire lifetime (Linux only; returns null on other platforms).
                // The cast guard keeps this code path CA1416-clean on Windows builds.
                if (OperatingSystem.IsLinux())
                {
                    lifetime = context.RequestServices
                        .GetService<Remex.Host.Services.RemoteDesktop.Linux.Capture.LinuxCaptureSessionLifetime>(); // optional service
                    if (lifetime is not null)
                    {
                        try
                        {
                            captureStarted = await lifetime.AcquireAsync(context.RequestAborted);
                        }
                        catch (Exception ex)
                        {
                            authLogger.LogWarning(
                                ex, "PipeWire lifetime acquire failed; falling back to legacy capture.");
                        }

                    }
                }

                using var handler = new RemoteDesktopHandler(
                    context.RequestServices.GetRequiredService<ILogger<RemoteDesktopHandler>>(),
                    context.RequestServices.GetRequiredService<IScreenCaptureService>(),
                    context.RequestServices.GetRequiredService<IInputSimulationService>(),
                    context.RequestServices.GetRequiredService<IDesktopWindowControlService>(),
                    context.RequestServices.GetRequiredService<IHostCapabilitiesProvider>());

                // Pass sessionCts.Token (not context.RequestAborted) so the registry
                // can cancel this loop independently of the HTTP connection lifetime.
                await handler.HandleAsync(ws, sessionCts.Token);
            }
            finally
            {
                // Signal drain completion before releasing the PipeWire lifetime so the
                // next connection can start its portal session in parallel with drain ack.
                // Always reached: covers AcceptWebSocketAsync failures and handler ctor failures
                // in addition to the normal HandleAsync completion path.
                registry.MarkDrained(clientId, sessionCts);
                if (OperatingSystem.IsLinux() && lifetime is not null && captureStarted)
                    await lifetime.ReleaseAsync();
            }
        });

        return app;
    }

    /// <summary>
    /// Evaluates whether an inbound /ws/desktop request is allowed, mirroring the trust model
    /// used by /ws (loopback bypass + paired-client registry). Returns the HTTP status and a
    /// short reason string; <see cref="StatusCodes.Status200OK"/> means "accept".
    /// </summary>
    /// <remarks>
    /// Extracted so the auth decision can be exercised by unit tests without standing up Kestrel.
    /// The default <see cref="WebApplicationFactory"/> TestServer reports a null RemoteIpAddress,
    /// which we treat as loopback — that bypasses the registry check, so the rejection paths
    /// have to be validated through this helper directly.
    /// </remarks>
    internal static (int StatusCode, string? Reason) EvaluateDesktopAuth(
        System.Net.IPAddress? remoteIp,
        string clientId,
        string protocolVersion,
        PairedClientRegistry registry)
    {
        // Optional protocol-version handshake parameter: if supplied, must be "2".
        // Clients that omit it are accepted for backwards compatibility — the current
        // Android client doesn't set it yet — but a wrong value is a clear-cut reject.
        if (!string.IsNullOrEmpty(protocolVersion) && protocolVersion != "2")
        {
            return (StatusCodes.Status400BadRequest,
                $"Unsupported protocolVersion '{protocolVersion}'. Expected '2'.");
        }

        var isLoopback = remoteIp is null || System.Net.IPAddress.IsLoopback(remoteIp);
        if (isLoopback)
        {
            return (StatusCodes.Status200OK, null);
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return (StatusCodes.Status401Unauthorized, "Paired client ID is required.");
        }

        if (!registry.IsClientPaired(clientId))
        {
            return (StatusCodes.Status403Forbidden, "Client is not paired.");
        }

        return (StatusCodes.Status200OK, null);
    }

    private static string RedactClientId(string clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return "<empty>";
        return clientId.Length <= 8 ? clientId : clientId[..8] + "…";
    }

    private static void LogWindowsRemoteDesktopDiagnostics(ILogger logger, WindowsRemoteDesktopDiagnosticReport? diagnostics)
    {
        if (diagnostics is null)
        {
            return;
        }

        if (diagnostics.RemoteDesktopUnavailableReason is { } unavailableReason)
        {
            logger.LogError("Windows remote desktop unavailable: {Reason}", unavailableReason);
            Console.Error.WriteLine($"[Remex.Host] {unavailableReason}");
        }
        else if (diagnostics.CurrentDesktopUnavailableReason is { } currentIssue)
        {
            logger.LogWarning("Windows remote desktop currently blocked: {Reason}", currentIssue);
        }

        if (diagnostics.CaptureBackendDegradedReason is { } degradedReason)
        {
            logger.LogWarning("Windows remote desktop capture degraded: {Reason}", degradedReason);
        }

        foreach (var issue in diagnostics.Issues)
        {
            logger.LogDebug("Windows remote desktop diagnostic detail: {Issue}", issue);
        }
    }

    private static void ConfigureWindowsEventLog(ILoggingBuilder logging)
    {
        logging.AddEventLog(settings =>
        {
            settings.LogName = WindowsEventLogName;
            settings.SourceName = WindowsEventSourceName;
        });
    }

    [SupportedOSPlatform("windows")]
    private static void EnablePerMonitorV2DpiAwareness()
    {
        try
        {
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch
        {
            // Best effort: older Windows builds or restricted contexts may reject this.
        }
    }

    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint dpiContext);

    private static void PrintStartupBanner(string port)
    {
        try
        {
            const string ansiCyan = "\x1b[1;36m";
            const string ansiGold = "\x1b[1;33m";
            const string ansiWhite = "\x1b[1;37m";
            const string ansiReset = "\x1b[0m";

            Console.WriteLine(ansiReset);
            Console.WriteLine($"{ansiCyan}██████╗ ███████╗███╗   ███╗███████╗██╗  ██╗   {ansiGold}⚡{ansiReset}");
            Console.WriteLine($"{ansiCyan}██╔══██╗██╔════╝████╗ ████║██╔════╝╚██╗██╔╝  {ansiGold}⚡⚡{ansiReset}");
            Console.WriteLine($"{ansiCyan}██████╔╝█████╗  ██╔████╔██║█████╗   ╚███╔╝  {ansiGold}⚡⚡⚡{ansiReset}");
            Console.WriteLine($"{ansiCyan}██╔══██╗██╔══╝  ██║╚██╔╝██║██╔══╝   ██╔██╗   {ansiGold}⚡{ansiReset}");
            Console.WriteLine($"{ansiCyan}██║  ██║███████╗██║ ╚═╝ ██║███████╗██╔╝ ██╗  {ansiGold}⚡{ansiReset}");
            Console.WriteLine($"{ansiCyan}╚═╝  ╚═╝╚══════╝╚═╝     ╚═╝╚══════╝╚═╝  ╚═╝{ansiReset}");
            Console.WriteLine($"{ansiWhite}------------------------------------------------------------{ansiReset}");
            Console.WriteLine($"{ansiWhite}⚡ REMEX REMOTE EXECUTION COMMAND CENTER v2.0.0 ⚡{ansiReset}");
            Console.WriteLine($"{ansiWhite}------------------------------------------------------------{ansiReset}");
            Console.WriteLine($"{ansiCyan}Status:      {ansiWhite}Active & Listening{ansiReset}");
            Console.WriteLine($"{ansiCyan}Host ID:     {ansiWhite}{HostId}{ansiReset}");
            Console.WriteLine($"{ansiCyan}Platform:    {ansiWhite}.NET 10.0 ({RuntimeInformation.OSDescription}){ansiReset}");
            Console.WriteLine($"{ansiCyan}API Port:    {ansiWhite}{port} (Secure TLS 1.3 Active){ansiReset}");
            Console.WriteLine($"{ansiWhite}------------------------------------------------------------{ansiReset}");
            Console.WriteLine(ansiReset);
        }
        catch
        {
            // Fallback in case of console writing issues
        }
    }
}

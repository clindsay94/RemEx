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
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.RemoteDesktop.Windows;
using Remex.Agent.Services.Security;
using Remex.Agent.Services.Telemetry;
using Remex.Agent.Services.ProcessMonitor;
using Remex.Core.Services.FileTransfer;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Input;

namespace Remex.Agent;

/// <summary>
/// Encapsulates the host WebApplication setup. remex.agent is a single process that is both the
/// host and the UI, so this runs in-process alongside the Avalonia app rather than as a separate
/// server. Kept as a standalone entry point so the host can also be started headless for tests.
/// </summary>
public static class HostBootstrapper
{
    internal const string WindowsEventLogName = "Application";
    internal const string WindowsEventSourceName = "Remex.Agent";

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
    /// <param name="configureWebHost">
    /// Test hook. When supplied, the canonical-port probing/reclaim and Kestrel HTTPS binding are
    /// skipped and this callback configures the web host instead (the integration tests use it to
    /// call <c>UseTestServer()</c>). Null in production — Kestrel binds the real port.
    /// </param>
    /// <param name="configureServices">
    /// Test hook applied to the service collection immediately before <c>Build()</c>, so tests can
    /// override registrations (e.g. mock <c>ISystemCommandService</c>). Null in production.
    /// </param>
    public static WebApplication CreateApplication(
        string[] args,
        int port = RemexConstants.DefaultPort,
        HostMode mode = HostMode.Full,
        Action<IWebHostBuilder>? configureWebHost = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        // No Windows Service (SCM) lifetime: RemEx is a single interactive user-session app that runs
        // the host in-process, always elevated, started by an elevated Task Scheduler logon task. This
        // host's lifetime is driven explicitly via StartAsync()/StopAsync() in Program.Main. The
        // `mode` parameter is retained purely as a test seam — HostAgentModeTests builds a non-Full
        // host directly via RemexHostFactory.WithMode(...) to exercise the /ws/desktop 404 path.

        // Register custom in-memory logger provider to capture live host and Kestrel logs
        builder.Logging.AddProvider(new Remex.Core.Logging.InMemoryLoggerProvider());

        if (OperatingSystem.IsWindows())
        {
            EnablePerMonitorV2DpiAwareness();
            ConfigureWindowsEventLog(builder.Logging);
        }

        builder.Services.AddSingleton<Remex.Core.Services.Network.IWakeOnLanService, Remex.Core.Services.Network.WakeOnLanService>();
        builder.Services.AddSingleton<Remex.Core.Services.Network.INetworkListener, Remex.Core.Services.Network.RemexNetworkListener>();
        // PROTO-1 (RemEx-htt): the 8338 command channel authenticates callers against the paired-client
        // registry. Without this registration the listener fails closed (rejects every command).
        builder.Services.AddSingleton<Remex.Core.Services.Network.ICommandChannelAuthenticator, Remex.Agent.Services.Network.PairedClientChannelAuthenticator>();
        builder.Services.AddSingleton<IHostCapabilitiesProvider, HostCapabilitiesProvider>();
        builder.Services.AddSingleton<IDesktopWindowControlService, UnsupportedDesktopWindowControlService>();
        // No LocalIpcServerService: the desktop UI runs in THIS process and resolves command/WoL/pairing
        // services straight from DI (see EmbeddedHostServiceLocator), so the RemExLocalIPC named pipe and
        // its cross-process identity gate were removed. (RemEx-aep Phase 3)
        builder.Services.AddHostedService<Remex.Agent.Services.Network.ExternalNetworkListenerService>();
        builder.Services.AddHostedService<Remex.Agent.Services.Network.MdnsAdvertisingService>();

        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddSingleton<ITelemetryService, WindowsTelemetryService>();
            // RemEx runs INSIDE the interactive user session, so lock / monitor-off / sign-out take
            // effect directly — the former SessionBridgingCommandService (which forwarded them into the
            // console session when headless in Session 0) is gone. Register the platform command service
            // straight up. (RemEx-aep Phase 4)
            builder.Services.AddSingleton<Remex.Core.Services.Command.ISystemCommandService,
                Remex.Core.Services.Command.WindowsSystemCommandService>();
            builder.Services.AddSingleton<IProcessMonitorService, WindowsProcessMonitorService>();
#if WGC_CAPTURE
            // Windows.Graphics.Capture backend (preferred). Only compiled/referenced on Windows — the
            // WGC_CAPTURE constant is defined alongside the Windows-only ProjectReference in Remex.Agent.csproj.
            builder.Services.AddSingleton<Remex.Core.Services.IWgcCaptureSource, Remex.Agent.Windows.WgcDesktopCapture>();
#endif
            // Resolve WGC as an OPTIONAL dependency: GetService returns null when the WGC project isn't
            // referenced (so the orchestrator falls through to its DXGI → GDI tiers unchanged).
            builder.Services.AddSingleton<IScreenCaptureService>(sp =>
                new Remex.Agent.Services.ScreenCapture.WindowsScreenCaptureService(
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Remex.Agent.Services.ScreenCapture.WindowsScreenCaptureService>>(),
                    sp.GetService<Remex.Core.Services.IWgcCaptureSource>()));
            builder.Services.AddSingleton<IInputSimulationService, Remex.Agent.Services.Input.WindowsInputSimulationService>();
            builder.Services.AddSingleton<IDesktopWindowControlService, Remex.Agent.Services.Input.WindowsDesktopWindowControlService>();
        }
        else if (OperatingSystem.IsLinux())
        {
            builder.Services.AddSingleton<ITelemetryService, LinuxTelemetryService>();
            builder.Services.AddSingleton<Remex.Core.Services.Command.ISystemCommandService, Remex.Core.Services.Command.LinuxSystemCommandService>();
            builder.Services.AddSingleton<IProcessMonitorService, LinuxProcessMonitorService>();
            builder.Services.AddSingleton<IScreenCaptureService, Remex.Agent.Services.ScreenCapture.LinuxScreenCaptureService>();
            // Factory rather than a plain type registration so the input service can be handed the
            // virtual-desktop origin. It needs one because ydotool has no absolute pointer mode: its
            // --absolute homes the pointer to the desktop's top-left and then moves by the operands,
            // so those operands are an OFFSET, and the two only coincide when the origin is (0,0)
            // (RemEx-dyvd). The type test lives here because this is the only place that knows the
            // concrete capture service; GetVirtualDesktopBounds is deliberately not on the shared
            // interface, where a default implementation would be wrong on Windows.
            builder.Services.AddSingleton<IInputSimulationService>(sp =>
                new Remex.Agent.Services.Input.LinuxInputSimulationService(
                    sp.GetRequiredService<ILogger<Remex.Agent.Services.Input.LinuxInputSimulationService>>(),
                    sp.GetService<Remex.Agent.Services.RemoteDesktop.Linux.Capture.LinuxCaptureSessionLifetime>(),
                    sp.GetRequiredService<IScreenCaptureService>() is Remex.Agent.Services.ScreenCapture.LinuxScreenCaptureService linuxCapture
                        ? () => { var (left, top, _, _) = linuxCapture.GetVirtualDesktopBounds(); return (left, top); }
                        : null));
            builder.Services.AddSingleton<IDesktopWindowControlService, LinuxDesktopWindowControlService>();
            builder.Services.AddSingleton<Remex.Agent.Services.RemoteDesktop.Linux.Capture.LinuxCaptureSessionLifetime>();
        }

        // Interactive session guard: opt-in (off by default) feature that keeps the signed-in session
        // AWAKE (no idle sleep / display-off) while a paired client is connected. Ref-counted across
        // clients; Windows-only mechanism (SetThreadExecutionState), no-op elsewhere. (RemEx-aep Phase 4)
        builder.Services.AddSingleton<Remex.Agent.Services.Session.IInteractiveSessionGuard>(sp =>
            OperatingSystem.IsWindows()
                ? new Remex.Agent.Services.Session.WindowsInteractiveSessionGuard(
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Remex.Agent.Services.Session.WindowsInteractiveSessionGuard>>())
                : new Remex.Agent.Services.Session.NoOpInteractiveSessionGuard());

        builder.Services.AddSingleton<TelemetryBackgroundService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryBackgroundService>());
        // Same instance again, under the interface the in-process UI can name (RemEx-ite8).
        builder.Services.AddSingleton<Remex.Core.Services.ITelemetryBroadcaster>(
            sp => sp.GetRequiredService<TelemetryBackgroundService>());

        builder.Services.AddSingleton<Remex.Core.Services.ILauncherStorageService, Remex.Core.Services.LauncherStorageService>();
        builder.Services.AddSingleton<Remex.Core.Services.IDashboardProfileStorageService, Remex.Core.Services.DashboardProfileStorageService>();
        builder.Services.AddSingleton<Remex.Core.Services.IAppLauncherService, Remex.Agent.Services.AppLauncherService>();
        // IAppLauncherService backs both the remote "LaunchApp" WebSocket command and the desktop UI's
        // offline launch path (the UI resolves it in-process via EmbeddedHostServiceLocator). The old
        // RemExLocalIPC pipe + LocalIpcServerService that used to bridge the UI here are gone. (RemEx-aep)

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
        // ── 2.1 File Sharing Overhaul (protocolVersion 3) — WP2: trust/consent + volume enumeration ──
        builder.Services.AddSingleton<IFileTrustService, FileTrustService>();
        builder.Services.AddSingleton<VolumeEnumerator>();
        // The one place a client-supplied rootId is turned into a readable location, shared by the v2
        // handler and the v3 sender so full-device browsing cannot be wired up on only one of them
        // (RemEx-hb1t.6).
        builder.Services.AddSingleton<SharedRootReadResolver>();
        builder.Services.AddTransient<FileTransferHandler>();
        // ── 2.1 File Sharing Overhaul (protocolVersion 3) — WP4: binary channel, sessions, resume, queue ──
        // Singletons on purpose: TransferSessionManager holds live per-transfer state that MUST be shared
        // between the /ws control plane (offer/ready/complete/result/control) and the /ws/files binary data
        // plane, and TransferQueueService owns the process-death-surviving transfer_queue.json.
        // Singleton: it IS the live-session list, so a per-request instance would be empty.
        builder.Services.AddSingleton<ClientSessionRegistry>();
        builder.Services.AddSingleton<TransferSessionManager>();
        builder.Services.AddSingleton<TransferQueueService>();
        builder.Services.AddSingleton<Remex.Agent.Services.RemoteDesktop.DesktopSessionRegistry>();

        // Headless: suppress browser launch and Kestrel HTTPS dev-cert noise.
        // Clients (Android + desktop) dial the canonical port. If a stale/duplicate Remex.Agent
        // is still holding it, we first reclaim it (terminate the stale instance) so we keep the
        // canonical port the clients expect — drifting onto a fallback port would silently desync
        // every client. Only an alternate (non-canonical) port is used as a last resort, and only
        // if the occupant isn't a Remex.Agent we can reclaim.
        // Probe on both IPv4 and IPv6 interfaces to completely avoid dual-stack wildcard collisions.
        int actualPort = port;
        // Skip real-port probing/reclaim under TestServer (no socket is bound, and we must not
        // terminate other Remex.Agent processes from a test run).
        if (configureWebHost is null)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int testPort = port + attempt;
                if (IsPortFree(testPort))
                {
                    actualPort = testPort;
                    break;
                }

                // On the canonical port only, try to reclaim it from a stale Remex.Agent before
                // drifting to a fallback port the clients would never dial.
                if (testPort == port
                    && Services.Network.StalePortReclaimer.TryReclaim(testPort)
                    && IsPortFree(testPort))
                {
                    actualPort = testPort;
                    break;
                }
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

        // SCREENSHOTS (RemEx-tjve), registered after the capture backends above so it picks up
        // whichever one this platform negotiated rather than opening a second one.
        //
        // PICTURES, NOT A REMEX-PRIVATE FOLDER. A screenshot is the user's, not the app's: they will
        // look for it where every other screenshot on the machine lives, and a gallery indexes that
        // location already. On Linux this follows the user's XDG PICTURES directory, falling back to
        // ~/Pictures - so on a localised desktop it is ~/Bilder or ~/Images, not literally ~/Pictures.
        builder.Services.AddSingleton<Remex.Agent.Services.FileTransfer.FilePushOriginator>();
        builder.Services.AddSingleton<Remex.Agent.Services.Screenshot.IScreenshotService>(sp =>
            new Remex.Agent.Services.Screenshot.ScreenshotService(
                sp.GetRequiredService<IScreenCaptureService>(),
                () =>
                {
                    // EMPTY IS A REAL ANSWER, not a theoretical one: GetFolderPath returns "" when
                    // the folder cannot be resolved - Pictures redirected to an unavailable share, or
                    // HOME unset under an XDG autostart. Path.Combine would then yield a RELATIVE
                    // path, which an elevated process resolves against a working directory it did not
                    // choose. The write would succeed in somewhere like System32 BECAUSE of the
                    // elevation, and the user would never find the file.
                    var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    if (string.IsNullOrEmpty(pictures))
                    {
                        throw new InvalidOperationException(
                            "This PC has no Pictures folder available, so there is nowhere sensible to "
                                + "save a screenshot.");
                    }

                    return Path.Combine(pictures, "RemEx Screenshots");
                },
                () => DateTimeOffset.Now));

        // THE READINESS REPORT, REGISTERED WHERE ITS INPUTS ALREADY LIVE (RemEx-id37). It needs the
        // certificate path and the port, and both are here - certService a line above, port the
        // parameter of this method - so building it anywhere else would mean recomputing one of them.
        //
        // AUTOSTART IS RESOLVED LAZILY, THROUGH THE OTHER CONTAINER. IStartupRegistrationService is
        // registered into App.Services (the Avalonia container) by Program.RegisterPlatformServices,
        // not into this one, and in the headless/--doctor path there is no Avalonia container at all.
        // A Func closed over App.Services is read at probe time rather than now, and TryIsEnabled's
        // own contract already returns null for "could not determine" - which the service maps to
        // Unknown, the honest answer when the UI is not there to ask.
        builder.Services.AddSingleton<Remex.Core.Services.Readiness.ISystemReadinessService>(_ =>
            new Remex.Agent.Services.Readiness.SystemReadinessService(
                new Remex.Agent.Services.Readiness.SystemReadinessProbe(
                    isAutostartRegistered: () =>
                        (Remex.Desktop.App.Services?.GetService(typeof(Remex.Desktop.Services.IStartupRegistrationService))
                            as Remex.Desktop.Services.IStartupRegistrationService)?.TryIsEnabled(),
                    certificatePath: certService.CertificatePath),
                port));

        if (configureWebHost is null)
        {
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
        }
        else
        {
            // Test mode: the caller supplies the server (e.g. TestServer); no real Kestrel binding.
            configureWebHost(builder.WebHost);
        }

        builder.Configuration["Host:Port"] = actualPort.ToString();

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        var hostCapabilitiesProvider = app.Services.GetRequiredService<IHostCapabilitiesProvider>();

        PrintStartupBanner(actualPort.ToString());

        if (OperatingSystem.IsWindows())
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Remex.Agent");
            var diagnostics = hostCapabilitiesProvider.GetWindowsRemoteDesktopDiagnosticReport();
            LogWindowsRemoteDesktopDiagnostics(logger, diagnostics);
        }

        // Surface missing Linux capture/input tools at startup, loud and early. This is the
        // root cause of the "RD is black, commands don't work" report on Linux hosts: the
        // services silently return empty bytes when their external tools aren't installed.
        if (OperatingSystem.IsLinux())
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Remex.Agent");
            var caps = hostCapabilitiesProvider.GetCurrent();
            if (!caps.SupportsRemoteDesktop && caps.RemoteDesktopUnavailableReason is { } reason)
            {
                // LogError so it shows up under default console verbosity.
                logger.LogError("Remote desktop unavailable: {Reason}", reason);
                Console.Error.WriteLine($"[Remex.Agent] {reason}");
            }
        }

        // Enable WebSocket support.
        app.UseWebSockets();

        // --- Minimal API endpoints ---

        // Minimal, unauthenticated health-check / discovery handshake. Deliberately does NOT expose
        // host capabilities or the remote-desktop diagnostic report (OS, capture backend, capability
        // and failure detail) to anonymous callers — that detail aids targeting and is delivered to
        // paired clients over the authenticated /ws channel instead (VULN-6, RemEx-s032.6).
        app.MapGet("/", () => Results.Ok(new
        {
            service = "Remex.Agent",
            status = "running",
        }));

        // Serves only public-by-design pairing metadata (host id + certificate SPKI hash, which the
        // client pins at pairing time) plus the listen port. No machine name or other identifying
        // detail is exposed here (VULN-6 review, RemEx-s032.6).
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

        // NOTE: The former HTTP PIN endpoints (GET /pairing-pin, POST /start-pairing) were retired in
        // 2.3.x+ (RemEx-0xp0). PIN auto-fetch now runs exclusively over the /ws pairing socket via
        // pairing_pin_request, gated by the identical TransportTrust.IsTrustedForPinAutoFetch check (see
        // the /ws map below). The only shipped client that used the HTTP path (Android ≤2.2.4) is past
        // the fleet's minimum, so the trust-all HTTP surface Google Play ASI flagged is gone entirely.

        // NOTE: GET /debug/logs was REMOVED (VULN-1, RemEx-s032.1). It served the in-memory log buffer
        // — which retained the live pairing PIN and full paired clientIds — to any network-reachable
        // caller with no auth or loopback gate (Kestrel binds 0.0.0.0:5005). Diagnostics read the buffer
        // in-process via InMemoryLogSink.GetEntries(); there is no remote consumer. Do NOT re-add a
        // network-exposed log endpoint. Precedent: PAIR-5 (RemEx-a75) loopback-gated the pairing-PIN
        // HTTP endpoints for the same reason.
        //
        // GET /download/apk was also REMOVED (VULN-6, RemEx-s032.6): dead dev-only code that served a
        // hardcoded Z:\ debug-APK path and never shipped a real artifact in production.

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
            using var handler = new PingPongHandler(
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
                context.RequestServices.GetRequiredService<Remex.Agent.Services.Screenshot.IScreenshotService>(),
                context.RequestServices.GetRequiredService<PairingHandler>(),
                context.RequestServices.GetRequiredService<FileTransferHandler>(),
                context.RequestServices.GetRequiredService<TransferSessionManager>(),
                context.RequestServices.GetRequiredService<PairedClientRegistry>(),
                context.RequestServices.GetRequiredService<Remex.Agent.Services.FileTransfer.FilePushOriginator>(),
                context.RequestServices.GetRequiredService<ClientSessionRegistry>());

            // Loopback / in-process connections come from the embedded host on the same machine
            // (or in-process test servers). Pairing adds no security here — it would prompt for
            // a PIN the user's own desktop generated — so we satisfy the pairing gate
            // automatically. A null RemoteIpAddress means no real socket (TestServer/in-process
            // transport) and is treated as loopback for the same reason.
            var remoteIp = context.Connection.RemoteIpAddress;
            var isLoopback = remoteIp is null || System.Net.IPAddress.IsLoopback(remoteIp);

            // Whether this connection may auto-fetch the pairing PIN over /ws (pairing_pin_request).
            // Computed here from the real Kestrel connection addresses via the same
            // TransportTrust.IsTrustedForPinAutoFetch gate that the retired GET /pairing-pin endpoint
            // used (RemEx-0xp0), so the WS path is exactly as powerful as that endpoint was — no more.
            // A null RemoteIpAddress (TestServer/in-process) fails closed.
            var isTrustedForPinAutoFetch = Remex.Agent.Services.Security.TransportTrust
                .IsTrustedForPinAutoFetch(remoteIp, context.Connection.LocalIpAddress);

            await handler.HandleAsync(
                ws, isLoopback, isTrustedForPinAutoFetch,
                remoteAddress: remoteIp?.ToString(), context.RequestAborted);
        });

        // Remote Desktop WebSocket endpoint (dedicated binary stream)
        app.Map("/ws/desktop", async (HttpContext context, PairedClientRegistry pairedClientRegistry) =>
        {
            // A non-Full host (test seam only; see CreateApplication) does not stream the desktop:
            // respond 404 before any auth/capture/portal work so no screen-capture or PipeWire
            // session is ever started.
            if (mode != HostMode.Full)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var authLogger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Remex.Agent.DesktopAuth");

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
                .GetRequiredService<Remex.Agent.Services.RemoteDesktop.DesktopSessionRegistry>();
            using var sessionCts = await registry.TakeOverAsync(
                clientId,
                TimeSpan.FromMilliseconds(2000),
                context.RequestAborted);

            // Declare before try so the finally block can always call MarkDrained and
            // ReleaseAsync — even if AcceptWebSocketAsync or handler construction throws.
            var captureStarted = false;
            Remex.Agent.Services.RemoteDesktop.Linux.Capture.LinuxCaptureSessionLifetime? lifetime = null;

            try
            {
                using var ws = await context.WebSockets.AcceptWebSocketAsync();

                // VULN-2 (RemEx-s032.2): a paired-registry presence check is NOT authentication. Before any
                // screen-capture/PipeWire session starts, require the client to prove possession of its
                // reconnect secret via the same PAIR-1 HMAC-over-nonce handshake /ws enforces. Loopback is
                // pre-paired and skips this. A client that can't prove is closed before capture begins.
                var isLoopbackDesktop = remoteIp is null || System.Net.IPAddress.IsLoopback(remoteIp);
                if (!isLoopbackDesktop
                    && !await Remex.Agent.Services.Security.ChannelReconnectAuth.ChallengeAndVerifyAsync(
                        ws, clientId, pairedClientRegistry, authLogger, context.RequestAborted))
                {
                    authLogger.LogWarning(
                        "Rejected /ws/desktop from {RemoteIp} (clientIdPrefix={ClientIdPrefix}): proof-of-possession failed.",
                        remoteIp, RedactClientId(clientId));
                    await ws.CloseAsync(
                        System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, "Proof-of-possession required.", context.RequestAborted);
                    return;
                }

                // Track A: acquire the PipeWire lifetime (Linux only; returns null on other platforms).
                // The cast guard keeps this code path CA1416-clean on Windows builds.
                if (OperatingSystem.IsLinux())
                {
                    lifetime = context.RequestServices
                        .GetService<Remex.Agent.Services.RemoteDesktop.Linux.Capture.LinuxCaptureSessionLifetime>(); // optional service
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
                    context.RequestServices.GetRequiredService<IHostCapabilitiesProvider>(),
                    context.RequestServices.GetRequiredService<Remex.Agent.Services.Session.IInteractiveSessionGuard>());

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

        // File-transfer binary WebSocket endpoint (dedicated binary stream, plan §1.1). Bulk chunk data and
        // acks flow here; the JSON control plane (offer/ready/complete/result/control, browse, consent) stays
        // on /ws. Auth mirrors /ws/desktop (loopback bypass + paired-registry presence check) but additionally
        // demands protocolVersion ≥ 3 — the binary channel is a v3-only feature, so v2 peers keep the legacy
        // base64 path on /ws and are never admitted here (plan §1.5).
        app.Map("/ws/files", async (HttpContext context, PairedClientRegistry pairedClientRegistry) =>
        {
            var authLogger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Remex.Agent.FilesAuth");

            if (!context.WebSockets.IsWebSocketRequest)
            {
                authLogger.LogWarning(
                    "Rejected /ws/files: non-WebSocket request from {RemoteIp}.",
                    context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket connections only.");
                return;
            }

            var remoteIp = context.Connection.RemoteIpAddress;
            var clientId = context.Request.Query["clientId"].ToString();
            var protocolVersion = context.Request.Query["protocolVersion"].ToString();

            var evaluation = EvaluateFilesAuth(remoteIp, clientId, protocolVersion, pairedClientRegistry);
            if (evaluation.StatusCode != StatusCodes.Status200OK)
            {
                authLogger.LogWarning(
                    "Rejected /ws/files from {RemoteIp} (clientIdPrefix={ClientIdPrefix}, protocolVersion={ProtocolVersion}): {Reason}",
                    remoteIp,
                    RedactClientId(clientId),
                    string.IsNullOrEmpty(protocolVersion) ? "<unset>" : protocolVersion,
                    evaluation.Reason);
                context.Response.StatusCode = evaluation.StatusCode;
                await context.Response.WriteAsync(evaluation.Reason ?? "Unauthorized.");
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();

            // VULN-2 (RemEx-s032.2): require reconnect proof-of-possession before any bulk file data flows,
            // mirroring /ws/desktop and the /ws control channel. Loopback is pre-paired and skips this.
            var isLoopbackFiles = remoteIp is null || System.Net.IPAddress.IsLoopback(remoteIp);
            if (!isLoopbackFiles
                && !await Remex.Agent.Services.Security.ChannelReconnectAuth.ChallengeAndVerifyAsync(
                    ws, clientId, pairedClientRegistry, authLogger, context.RequestAborted))
            {
                authLogger.LogWarning(
                    "Rejected /ws/files from {RemoteIp} (clientIdPrefix={ClientIdPrefix}): proof-of-possession failed.",
                    remoteIp, RedactClientId(clientId));
                await ws.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, "Proof-of-possession required.", context.RequestAborted);
                return;
            }

            var sessions = context.RequestServices.GetRequiredService<TransferSessionManager>();

            // RunChannelAsync owns the socket for its lifetime: it services inbound data frames (writing to the
            // matching receive session with periodic acks) and inbound acks (advancing send-session
            // backpressure), and on exit suspends this client's receive sessions (partials kept for resume)
            // and cancels its sends. It never throws out to here.
            await sessions.RunChannelAsync(clientId, ws, context.RequestAborted);
        });

        return app;
    }

    /// <summary>
    /// Returns true when <paramref name="port"/> can be bound on the IPv4 (and, when available,
    /// IPv6 dual-stack) wildcard interface — i.e. no other process holds that port.
    /// Used during startup to probe for port availability before reclaiming a stale instance.
    /// </summary>
    internal static bool IsPortFree(int port)
    {
        try
        {
            using (var v4 = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp))
            {
                v4.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));
            }

            if (System.Net.Sockets.Socket.OSSupportsIPv6)
            {
                using var v6 = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetworkV6,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp)
                {
                    DualMode = true
                };
                v6.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Any, port));
            }

            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Evaluates whether an inbound /ws/desktop request is allowed, mirroring the trust model
    /// used by /ws (loopback bypass + paired-client registry). Returns the HTTP status and a
    /// short reason string; <see cref="StatusCodes.Status200OK"/> means "accept".
    /// </summary>
    /// <remarks>
    /// Extracted so the auth decision can be exercised by unit tests without standing up Kestrel.
    /// The default <c>WebApplicationFactory</c> TestServer reports a null RemoteIpAddress,
    /// which we treat as loopback — that bypasses the registry check, so the rejection paths
    /// have to be validated through this helper directly.
    /// </remarks>
    internal static (int StatusCode, string? Reason) EvaluateDesktopAuth(
        System.Net.IPAddress? remoteIp,
        string clientId,
        string protocolVersion,
        PairedClientRegistry registry)
    {
        // Optional protocol-version handshake parameter. Clients that omit it are accepted for
        // backwards compatibility — the current Android client doesn't set it yet. When supplied it
        // is parsed once and run through the same ProtocolVersionPolicy rule as the /ws control
        // channel, so the two paths can never disagree on what counts as supported. A value that is
        // non-numeric or below the supported range is a clear-cut reject.
        if (!string.IsNullOrEmpty(protocolVersion))
        {
            if (!int.TryParse(protocolVersion, out var parsedVersion)
                || !Remex.Core.Messages.ProtocolVersionPolicy.IsSupported(parsedVersion))
            {
                return (StatusCodes.Status400BadRequest,
                    $"Unsupported protocolVersion '{protocolVersion}'. " +
                    $"Expected '{Remex.Core.Messages.ProtocolVersionPolicy.Minimum}' or later.");
            }
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

        // PAIR-1: this is a presence check, not the authentication. The desktop binary stream is
        // a secondary channel — a client must first authenticate on the /ws control channel via
        // the reconnect proof-of-possession handshake (HMAC over a host nonce), and the transport
        // is TLS with the host SPKI pinned by the client at pairing time. A bare clientId on /ws
        // no longer authenticates; this gate additionally requires the client to be a known paired
        // identity before any screen-capture/PipeWire session is started.
        if (!registry.IsClientPaired(clientId))
        {
            return (StatusCodes.Status403Forbidden, "Client is not paired.");
        }

        return (StatusCodes.Status200OK, null);
    }

    /// <summary>
    /// Evaluates whether an inbound /ws/files request is allowed. Mirrors <see cref="EvaluateDesktopAuth"/>
    /// (loopback bypass + paired-client registry presence check) with one deliberate difference: because the
    /// binary file channel is a v3-only feature, a supplied <paramref name="protocolVersion"/> must satisfy
    /// <see cref="Remex.Core.Messages.ProtocolVersionPolicy.SupportsBinaryFileTransfer"/> (≥ 3) rather than
    /// merely <see cref="Remex.Core.Messages.ProtocolVersionPolicy.IsSupported"/> (≥ 2). An explicit v2 is
    /// rejected here so v2 peers stay on the legacy base64 path on /ws and never reach the binary channel
    /// (plan §1.5). Extracted so the auth decision can be exercised by unit tests without standing up Kestrel.
    /// </summary>
    internal static (int StatusCode, string? Reason) EvaluateFilesAuth(
        System.Net.IPAddress? remoteIp,
        string clientId,
        string protocolVersion,
        PairedClientRegistry registry)
    {
        // v3-only channel. When the client advertises a version it MUST support the binary file transport; an
        // explicit v2 (or a non-numeric value) is a clear-cut reject. An omitted value is tolerated for the
        // same forward-compat reason as /ws/desktop — a genuine v2 client never dials this endpoint (it does
        // not know it exists), so in practice only paired v3 clients ever reach here.
        if (!string.IsNullOrEmpty(protocolVersion))
        {
            if (!int.TryParse(protocolVersion, out var parsedVersion)
                || !Remex.Core.Messages.ProtocolVersionPolicy.SupportsBinaryFileTransfer(parsedVersion))
            {
                return (StatusCodes.Status400BadRequest,
                    $"Unsupported protocolVersion '{protocolVersion}' for the binary file channel. " +
                    $"Expected '{Remex.Core.Messages.ProtocolVersionPolicy.BinaryFileTransferMinimum}' or later.");
            }
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

        // Presence check, not authentication: a client must first authenticate on the /ws control channel via
        // the reconnect proof-of-possession handshake, and the transport is TLS with the host SPKI pinned. This
        // gate additionally requires the client to be a known paired identity before any bulk data flows.
        if (!registry.IsClientPaired(clientId))
        {
            return (StatusCodes.Status403Forbidden, "Client is not paired.");
        }

        return (StatusCodes.Status200OK, null);
    }

    private static string RedactClientId(string clientId)
        => Remex.Agent.Services.Security.LogRedaction.RedactClientId(clientId);

    private static void LogWindowsRemoteDesktopDiagnostics(ILogger logger, WindowsRemoteDesktopDiagnosticReport? diagnostics)
    {
        if (diagnostics is null)
        {
            return;
        }

        if (diagnostics.RemoteDesktopUnavailableReason is { } unavailableReason)
        {
            logger.LogError("Windows remote desktop unavailable: {Reason}", unavailableReason);
            Console.Error.WriteLine($"[Remex.Agent] {unavailableReason}");
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

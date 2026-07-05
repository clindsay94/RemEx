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
/// Encapsulates the Remex Host WebApplication setup so it can be started
/// both as a standalone server and embedded inside the Desktop client.
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
            builder.Services.AddSingleton<IInputSimulationService, Remex.Agent.Services.Input.LinuxInputSimulationService>();
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
        builder.Services.AddTransient<FileTransferHandler>();
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

        // Health-check / discovery
        app.MapGet("/", (IHostCapabilitiesProvider hostCapabilitiesProvider) => Results.Ok(new
        {
            service = "Remex.Agent",
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

        // Returns the active pairing PIN so the local host UI/tray — or a phone reaching the host over
        // an authenticated Tailscale tunnel — can auto-fill it for the user.
        //
        // INTENTIONAL: the Tailscale path exists specifically so someone connecting to their PC remotely
        // (NOT on the same LAN, so they obviously can't see the PIN shown on the PC screen) can still
        // pair. Over a Tailscale tunnel the transport is already mutually authenticated and encrypted, so
        // auto-serving the PIN there is safe and is the only way a genuinely-remote user could ever read
        // it. Do not "harden" this back to loopback-only without a replacement remote-pairing path — that
        // is the regression this restores (RemEx-i9e).
        //
        // Gated to loopback OR a
        // genuine Tailscale path (caller AND the host-side address both in the 100.64.0.0/10 /
        // fd7a:115c:a1e0::/48 ranges; see TransportTrust): on those paths the channel is already
        // mutually-authenticated and MITM-resistant, so relaying the PIN leaks nothing an attacker could
        // use. On plain LAN/internet the PIN keeps its out-of-band, anti-MITM purpose and is never served.
        // This only relays the PIN of an already-active session a human started at the PC, so it adds no
        // remote-initiation capability. A disallowed caller gets a 404 (not a 403) so the endpoint is not
        // advertised to network scanners.
        app.MapGet("/pairing-pin", (HttpContext httpContext, IPairingService pairingService) =>
        {
            // Null remote/local (e.g. a non-IP transport) is treated as untrusted by TransportTrust, which
            // also avoids the ArgumentNullException that IsLoopback(null) would throw.
            var remoteIp = httpContext.Connection.RemoteIpAddress;
            var localIp = httpContext.Connection.LocalIpAddress;
            if (!Remex.Agent.Services.Security.TransportTrust.IsTrustedForPinAutoFetch(remoteIp, localIp))
            {
                return Results.NotFound();
            }

            if (pairingService.TryGetActivePinInfo(out var pin, out var expiresAtUnixMs))
            {
                return Results.Ok(new { pin, expiresAtUnixMs });
            }
            return Results.NotFound(new { message = "No active pairing session." });
        });

        // Proactively starts a pairing session and returns the PIN immediately.
        // Deliberately stays loopback-only — unlike /pairing-pin, which was widened to trusted Tailscale
        // paths. This endpoint *creates* a session and discloses its PIN with no host-side consent, so a
        // non-loopback caller (even over Tailscale) could otherwise begin pairing and read the PIN with
        // nobody present at the PC, enabling remote takeover. The auto-fill flow only needs /pairing-pin
        // (a human starts pairing at the PC; the phone reads the already-active PIN). If a genuine
        // remote-provisioning flow is ever required, it must require an already-paired client token.
        app.MapPost("/start-pairing", async (
            HttpContext httpContext,
            Remex.Agent.Services.Security.PairingService pairingService) =>
        {
            // A null RemoteIpAddress (e.g. a non-IP transport) is not loopback — reject it, and avoid
            // the ArgumentNullException that IsLoopback(null) would otherwise throw inside the handler.
            var remoteIp = httpContext.Connection.RemoteIpAddress;
            if (remoteIp is null || !System.Net.IPAddress.IsLoopback(remoteIp))
            {
                return Results.NotFound();
            }

            // Defense-in-depth: even though this endpoint is loopback-only, the per-IP throttle
            // bounds repeated session churn from any single source (loopback is never throttled).
            // Resolved optionally so the endpoint still works before the DI registration lands;
            // see the integration follow-up to register PairingThrottle as a singleton.
            var pairingThrottle = httpContext.RequestServices
                .GetService(typeof(Remex.Agent.Services.Security.PairingThrottle))
                as Remex.Agent.Services.Security.PairingThrottle;
            if (pairingThrottle is not null
                && !pairingThrottle.TryRegisterAttempt(httpContext.Connection.RemoteIpAddress, out var retryAfter))
            {
                httpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

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
            var apkPath = @"Z:\RemEx\remex.android\app\build\outputs\apk\debug\RemEx-V2.0.0-debug.apk";
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

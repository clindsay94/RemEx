using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core;
using Remex.Client;
using Remex.Client.Services;
using Remex.Host;
using Remex.Host.Services.IPC;

// Program is intentionally in the GLOBAL namespace so the host integration tests resolve it via
// WebApplicationFactory<Program> (HostFactoryResolver intercepts the embedded host build below).
//
// The consolidated RemEx.Host binary is the PC host. Launched interactively it runs the full GUI
// host (commands + remote-desktop streaming). `--doctor` runs the Linux prerequisite report. A
// headless `--agent` command-only mode (system service, logged out) is added in a later phase.
public partial class Program
{
    private static Microsoft.AspNetCore.Builder.WebApplication? _hostApp;
    private static HostControlClient? _hostControlClient;

    /// <summary>
    /// The port the embedded host actually started on. Passed to the Avalonia app so the client
    /// connects to the right endpoint.
    /// </summary>
    internal static int? EmbeddedHostPort { get; private set; }

    /// <summary>
    /// Stops the embedded host so a sibling instance (or the system service) can bind the port.
    /// Safe to call when no host is running (no-op).
    /// </summary>
    internal static async Task StopEmbeddedHostAsync()
    {
        if (_hostApp is not null)
        {
            await _hostApp.StopAsync();
            (_hostApp as IDisposable)?.Dispose();
            _hostApp = null;
            EmbeddedHostPort = null;
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // --doctor / doctor: Linux remote-desktop prerequisite report; exits without launching the UI.
        if (args.Length > 0 &&
            (args[0].Equals("--doctor", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("doctor", StringComparison.OrdinalIgnoreCase)))
        {
            if (OperatingSystem.IsLinux())
            {
                return HostDoctor.RunAsync().GetAwaiter().GetResult();
            }

            Console.Error.WriteLine("RemEx.Host --doctor is only supported on Linux.");
            return 2;
        }

        // --agent: run as the headless command agent (no GUI, no desktop streaming). The background
        // service launches the binary this way so remote power commands + telemetry/status work
        // without a logged-in desktop session. Blocks until the host stops (SIGTERM / Ctrl+C /
        // service stop).
        if (Array.Exists(args, a => a.Equals("--agent", StringComparison.OrdinalIgnoreCase)))
        {
            return RunAgentAsync(args).GetAwaiter().GetResult();
        }

        // Build the embedded host FIRST, before touching any Avalonia/App statics. Under the
        // integration tests, WebApplicationFactory<Program>'s HostFactoryResolver runs this Main and
        // intercepts the host build (throwing an internal StopTheHostException); keeping the build as
        // the first action means the resolver/test path never loads Avalonia. The build is
        // deliberately NOT wrapped in a catch-all (a swallowing catch would break the resolver); we
        // still degrade to client-only mode on a genuine startup failure. HostBootstrapper owns port
        // selection (canonical port, with stale-port reclaim + fallback probing).
        // Single-port handoff: if the headless agent is holding the canonical port, ask it to yield
        // (it stops its web host, releasing the port + the RemExLocalIPC mutex/pipe) so this GUI host
        // can own the full host on the canonical port. The control connection is held open for the
        // life of the process; on exit the agent observes the drop and reclaims the port. If no agent
        // is running this returns immediately and we just bind the port ourselves.
        _hostControlClient = new HostControlClient();
        try { _hostControlClient.RequestTakeoverAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
        catch { /* best-effort; StalePortReclaimer is the fallback inside CreateApplication */ }

        int preferredPort = RemexConstants.DefaultPort;

        try
        {
            _hostApp = HostBootstrapper.CreateApplication(args, preferredPort);
            _hostApp.StartAsync().GetAwaiter().GetResult();
            EmbeddedHostPort = preferredPort;
            Console.WriteLine($"[Remex] Embedded host started on port {preferredPort}.");
        }
        catch (Exception ex) when (!IsHostResolverProbe(ex))
        {
            Console.Error.WriteLine($"[Remex] Could not start embedded host on port {preferredPort}: {ex.Message}");
            _hostApp = null;
            EmbeddedHostPort = null;
        }

        // Register desktop-specific services consumed by the shared Remex.Client UI (GUI path only).
        App.RegisterPlatformServices = services =>
        {
            services.AddSingleton<Remex.Core.Services.IIconExtractionService, Remex.Client.Desktop.Services.DesktopIconExtractionService>();
            services.AddSingleton<Remex.Client.Services.IStartupRegistrationService, Remex.Client.Desktop.Services.StartupRegistrationService>();
        };

        if (EmbeddedHostPort.HasValue)
        {
            App.OverrideHostPort = EmbeddedHostPort.Value;
            App.EmbeddedHostInstanceId = HostBootstrapper.InstanceId;
            App.EmbeddedHostServices = _hostApp?.Services;
        }

        App.StopEmbeddedHostAsync = StopEmbeddedHostAsync;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Stop the background listener so the process can exit cleanly.
            CommandModeContext.Cleanup();

            // Gracefully shut down the embedded host when the UI exits.
            if (_hostApp is not null)
            {
                _hostApp.StopAsync().GetAwaiter().GetResult();
                (_hostApp as IDisposable)?.Dispose();
            }

            // Drop the control connection so a waiting agent reclaims the canonical port.
            _hostControlClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return 0;
    }

    /// <summary>
    /// Runs the process as the headless command agent: serves the command plane on the canonical port
    /// and coordinates the single-port handoff with an interactive GUI host. Blocks until SIGTERM /
    /// SIGINT (systemd stop / Ctrl+C).
    /// </summary>
    private static async Task<int> RunAgentAsync(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger("RemEx.Host.Agent");

        Microsoft.AspNetCore.Builder.WebApplication? app = null;
        var gate = new SemaphoreSlim(1, 1);

        async Task StartHostAsync()
        {
            await gate.WaitAsync();
            try
            {
                if (app is null)
                {
                    app = HostBootstrapper.CreateApplication(args, RemexConstants.DefaultPort, HostMode.CommandAgent);
                    await app.StartAsync();
                    logger.LogInformation("Command agent listening on the canonical port.");
                }
            }
            finally { gate.Release(); }
        }

        async Task StopHostAsync()
        {
            await gate.WaitAsync();
            try
            {
                if (app is not null)
                {
                    await app.StopAsync();
                    await app.DisposeAsync();
                    app = null;
                    logger.LogInformation("Command agent yielded the canonical port.");
                }
            }
            finally { gate.Release(); }
        }

        await StartHostAsync();

        await using var control = new HostControlServer(logger, onYield: StopHostAsync, onReclaim: StartHostAsync);
        control.Start();

        // Block until the process is asked to stop (systemd -> SIGTERM, Ctrl+C -> SIGINT).
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.TrySetResult(); });
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; shutdown.TrySetResult(); });
        await shutdown.Task;

        await StopHostAsync();
        return 0;
    }

    /// <summary>
    /// True when <paramref name="ex"/> is the internal exception WebApplicationFactory's
    /// HostFactoryResolver throws to stop the entry point after the host is built. It must propagate
    /// out of Main so integration-test host discovery works; only real startup failures are swallowed.
    /// </summary>
    private static bool IsHostResolverProbe(Exception ex)
        => ex.GetType().FullName?.Contains("StopTheHostException", StringComparison.Ordinal) == true;

    private static bool IsWindowsServiceRunning(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"query {serviceName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0
                && output.Contains("STATE", StringComparison.OrdinalIgnoreCase)
                && output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

// Required for WebApplicationFactory<Program> in the host integration tests.
public partial class Program { }

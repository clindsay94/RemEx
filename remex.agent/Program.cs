using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core;
using Remex.Desktop;
using Remex.Desktop.Services;
using Remex.Agent;

// Program is intentionally in the GLOBAL namespace so the host integration tests resolve it via
// WebApplicationFactory<Program> (HostFactoryResolver intercepts the embedded host build below).
//
// The consolidated Remex.Agent binary is the ENTIRE PC side: a single interactive user-session
// Avalonia app that runs the full GUI host (commands + remote-desktop streaming) in-process,
// always elevated. There is no Windows Service and no second process. `--doctor` runs the Linux
// prerequisite report.
public partial class Program
{
    private static Microsoft.AspNetCore.Builder.WebApplication? _hostApp;

    // Held for the process lifetime to enforce one interactive GUI host per session (see Main).
    private static Mutex? _guiInstanceLock;

    /// <summary>
    /// The port the embedded host actually started on. Passed to the Avalonia app so the client
    /// connects to the right endpoint.
    /// </summary>
    internal static int? EmbeddedHostPort { get; private set; }

    /// <summary>
    /// Stops the embedded host so a sibling instance can bind the port.
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

    // Remex.Agent is a WinExe (GUI subsystem) so launching it interactively does not pop a console
    // window. The trade-off: when launched FROM a terminal, Windows does not attach the process to
    // the parent console, so Console.WriteLine output is silently discarded. For the console-mode
    // command (--doctor) we explicitly attach to the parent console first so its output shows up in
    // the terminal the user ran us from. No-op / harmless on non-Windows.
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void AttachToParentConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // If we attach to the parent console, the inherited stdout/stderr handles need to be
        // re-pointed at it; otherwise Console keeps writing to the (discarded) original streams.
        if (AttachConsole(ATTACH_PARENT_PROCESS))
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // --session-task <verb>: perform a single desktop-bound action in the CURRENT session, then exit.
        // The Session-0 service launches this into the signed-in user's session (via CreateProcessAsUser,
        // see WindowsActiveSession) so actions like lock / monitor-off actually take effect on the user's
        // desktop. No host, no UI — this branch must stay cheap and return before any other startup work.
        if (args.Length >= 2 && args[0].Equals("--session-task", StringComparison.OrdinalIgnoreCase))
        {
            return RunSessionTask(args[1]);
        }

        // --doctor / doctor: Linux remote-desktop prerequisite report; exits without launching the UI.
        if (args.Length > 0 &&
            (args[0].Equals("--doctor", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("doctor", StringComparison.OrdinalIgnoreCase)))
        {
            AttachToParentConsole();

            // `--doctor --fix` additionally installs missing dependencies (Linux: via the system
            // package manager + sudo steps; Windows: FFmpeg via winget), after confirmation.
            var fix = Array.Exists(args, a => a.Equals("--fix", StringComparison.OrdinalIgnoreCase));

            if (OperatingSystem.IsLinux())
            {
                return HostDoctor.RunAsync(fix).GetAwaiter().GetResult();
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsHostDoctor.RunAsync(fix).GetAwaiter().GetResult();
            }

            Console.Error.WriteLine("Remex.Agent --doctor is not supported on this platform.");
            return 2;
        }

        // Single-instance guard for the interactive GUI host: at most one per session. A duplicate
        // launch (the logon task firing while one is already up, a dev run, or a stale instance)
        // would collide on the canonical port — and StalePortReclaimer would then terminate the
        // running host to grab it. If another GUI host in this session already holds the guard, exit
        // quietly. Local\ scopes it per session so fast-user-switching sessions each get their own
        // GUI host.
        _guiInstanceLock = new Mutex(initiallyOwned: true, @"Local\RemExGuiHost", out bool createdNewGuiInstance);
        if (!createdNewGuiInstance)
        {
            AttachToParentConsole();
            Console.Error.WriteLine("[Remex] Another RemEx host is already running in this session; exiting.");
            return 0;
        }

        // Build the embedded host FIRST, before touching any Avalonia/App statics. Under the
        // integration tests, WebApplicationFactory<Program>'s HostFactoryResolver runs this Main and
        // intercepts the host build (throwing an internal StopTheHostException); keeping the build as
        // the first action means the resolver/test path never loads Avalonia. The build is
        // deliberately NOT wrapped in a catch-all (a swallowing catch would break the resolver); we
        // still degrade to client-only mode on a genuine startup failure. HostBootstrapper owns port
        // selection (canonical port, with stale-port reclaim + fallback probing). With a single
        // process there is no port handoff partner — StalePortReclaimer inside CreateApplication is
        // the sole backstop for a stale instance still holding the canonical port.
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

        // Register desktop-specific services consumed by the shared Remex.Desktop UI (GUI path only).
        App.RegisterPlatformServices = services =>
        {
            services.AddSingleton<Remex.Core.Services.IIconExtractionService, Remex.Agent.Services.DesktopIconExtractionService>();
            services.AddSingleton<Remex.Desktop.Services.IStartupRegistrationService, Remex.Agent.Services.StartupRegistrationService>();
            services.AddSingleton<Remex.Desktop.Services.ISessionKeepUnlockedService, Remex.Agent.Services.SessionKeepUnlockedService>();
        };

        if (EmbeddedHostPort.HasValue)
        {
            App.OverrideHostPort = EmbeddedHostPort.Value;
            App.EmbeddedHostInstanceId = HostBootstrapper.InstanceId;
            App.EmbeddedHostServices = _hostApp?.Services;
        }

        App.StopEmbeddedHostAsync = StopEmbeddedHostAsync;

        // One-time migration: remove any legacy HKCU Run "RemEx" launch-at-login entry. Auto-start is
        // now the elevated Task Scheduler logon task; a lingering Run key would start a competing
        // medium-integrity host that wins the single-instance guard and reintroduces the UIPI input
        // block. Runs in the interactive user's session (HKCU = the signed-in user's hive). No-op off
        // Windows. (RemEx-hmk)
        Remex.Agent.Services.StartupRegistrationService.RemoveLegacyWindowsRunKey();

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
        }

        return 0;
    }

    /// <summary>
    /// Executes one desktop-bound action in the current session, then exits. Invoked as
    /// <c>--session-task &lt;verb&gt;</c> when the Session-0 service relaunches this binary into the
    /// signed-in user's session (see <c>WindowsActiveSession</c>), so the action runs where it has a
    /// desktop. Returns 0 on success, 1 on failure, 2 for an unknown verb or unsupported platform.
    /// </summary>
    private static int RunSessionTask(string verb)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 2;
        }

        try
        {
            // This process already runs in the user's interactive session, so the platform command
            // service performs the action directly (no session bridging — that is the caller's job).
            var commandService = new Remex.Core.Services.Command.WindowsSystemCommandService();
            switch (verb.ToLowerInvariant())
            {
                case "lock":
                    commandService.Lock().GetAwaiter().GetResult();
                    return 0;
                case "monitoroff":
                    commandService.MonitorOff().GetAwaiter().GetResult();
                    return 0;
                default:
                    Console.Error.WriteLine($"[Remex] Unknown --session-task verb: {verb}");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Remex] --session-task {verb} failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> is the internal exception WebApplicationFactory's
    /// HostFactoryResolver throws to stop the entry point after the host is built. It must propagate
    /// out of Main so integration-test host discovery works; only real startup failures are swallowed.
    /// </summary>
    private static bool IsHostResolverProbe(Exception ex)
        => ex.GetType().FullName?.Contains("StopTheHostException", StringComparison.Ordinal) == true;

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

// Required for WebApplicationFactory<Program> in the host integration tests.
public partial class Program { }

using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// How long Main's finally waits for the embedded host to stop before abandoning it and letting
    /// the process exit anyway.
    /// </summary>
    private static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs <paramref name="work"/> to completion WITHOUT this thread's
    /// <see cref="SynchronizationContext"/>, giving up after <paramref name="timeout"/>.
    /// Returns false only when the work was still running when the timeout expired.
    /// </summary>
    /// <remarks>
    /// RemEx-e3pn. Main's finally used to call <c>StopAsync().GetAwaiter().GetResult()</c> straight
    /// on the [STAThread] main thread. The reasoning written next to it was that the thread reaches
    /// the finally only after StartWithClassicDesktopLifetime has returned — i.e. once the dispatcher
    /// loop is over and its context no longer matters. A process dump disproved that: on Windows x64
    /// a finally runs as an unwind funclet during the SECOND PASS of structured exception handling,
    /// while the frames being unwound are still on the stack. So an exception escaping the Avalonia
    /// dispatcher arrives here with the dispatcher's SynchronizationContext still installed and
    /// permanently unpumped. StopAsync's continuation is posted to a context that will never run it,
    /// and the process hangs forever instead of exiting. A user hit exactly that and had to kill it
    /// from Task Manager (RemEx-wdqx).
    ///
    /// Task.Run is the fix the original comment itself named: the work starts on a thread-pool
    /// thread, which carries no SynchronizationContext, so every await inside resumes on the pool
    /// rather than posting back here. NOT ConfigureAwait(false), which is banned repo-wide.
    ///
    /// Deliberately exception-free. This runs inside a finally during exception unwinding, and
    /// anything thrown here would REPLACE the exception being unwound — turning a diagnosable crash
    /// into a confusing secondary failure with the real cause gone. A faulted task counts as
    /// completed: it finished, it just finished badly, and the process is exiting either way.
    /// </remarks>
    internal static bool RunOffCapturedContext(Func<Task> work, TimeSpan timeout)
    {
        if (work is null)
        {
            // A null delegate is a programming error and would normally deserve a throw. It does not
            // get one HERE, because the no-throw guarantee above is the whole point of this method:
            // an ArgumentNullException raised from inside an unwinding finally would replace the
            // exception being unwound, which is precisely the failure this exists to prevent. There
            // is nothing running, so say so and let the process exit with its real error intact.
            return true;
        }

        try
        {
            return Task.Run(work).Wait(timeout);
        }
        catch (Exception)
        {
            // Faulted, cancelled, or the pool refused to schedule it. All of those mean "not still
            // running", which is the only thing this method reports on.
            return true;
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

        // One-time migration: replace a legacy remex-client.desktop autostart entry with the
        // remex-agent.desktop one pointing at this executable, so login never launches a stale
        // pre-rename install. No-op off Linux. (RemEx-u0oc)
        Remex.Agent.Services.StartupRegistrationService.MigrateLegacyLinuxAutostartEntry();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Stop the background listener so the process can exit cleanly.
            CommandModeContext.Cleanup();

            // Gracefully shut down the embedded host when the UI exits.
            //
            // This block runs on the [STAThread] Main thread, and it does NOT get here only after
            // the dispatcher loop has cleanly ended — an earlier version of this comment claimed it
            // did, and that claim cost a user an unkillable process. A finally also runs during
            // exception unwinding, with the Avalonia dispatcher's SynchronizationContext still
            // installed and already past pumping anything. Blocking here directly deadlocks forever.
            // The whole story is on RemEx-e3pn; the mechanism and the reasoning are on
            // RunOffCapturedContext above, which is where any change to this belongs.
            var hostApp = _hostApp;
            if (hostApp is not null)
            {
                if (RunOffCapturedContext(() => hostApp.StopAsync(), HostShutdownTimeout))
                {
                    (hostApp as IDisposable)?.Dispose();
                }
                else
                {
                    // Abandoning a wedged shutdown is the right trade at process exit: the OS
                    // reclaims everything, and a bounded slow exit beats an unbounded hang. Do NOT
                    // dispose in this branch — the host is still inside StopAsync, and disposing it
                    // from under itself can throw, which in this finally would replace whatever
                    // exception brought us here.
                    try
                    {
                        Console.Error.WriteLine(
                            $"RemEx: the embedded host did not stop within {HostShutdownTimeout.TotalSeconds:0}s; exiting anyway.");
                    }
                    catch
                    {
                        // Remex.Agent is a WinExe with no console attached in the normal launch, so
                        // this write is usually discarded rather than failing. Guarded anyway: a
                        // broken pipe here would throw from an unwinding finally and take the real
                        // exception with it, which is a bad trade for a diagnostic line.
                    }
                }
            }
        }

        return 0;
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
            // EXPLICIT SINCE AVALONIA 12, and its absence is silent. Up to 11 the Skia backend
            // brought HarfBuzz in on its own; in 12 it does not. Drop this call and Latin text still
            // renders perfectly, so a smoke test says nothing is wrong — what breaks is complex
            // script shaping, and RemEx ships Hindi among its nine locales.
            .UseHarfBuzz()
            .WithInterFont()
            .LogToTrace();
}

// Required for WebApplicationFactory<Program> in the host integration tests.
public partial class Program { }

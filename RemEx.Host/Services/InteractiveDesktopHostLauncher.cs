using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Remex.Host.Services;

/// <summary>
/// Runs inside the Session-0 LocalSystem service and keeps an interactive GUI host alive in the
/// signed-in user's console session, launched at HIGH integrity via the user's linked admin token.
///
/// Why this exists: the GUI host is the process that injects remote mouse/keyboard input through
/// <c>SendInput</c>. Windows UIPI silently drops input from a medium-integrity process against an
/// elevated (admin) foreground window, so a GUI host started by the per-user HKCU Run key (medium
/// integrity) cannot control e.g. Windows Terminal launched "as administrator". Spawning it from the
/// service with the user's elevated linked token gives it the integrity needed to drive elevated
/// windows. This replaces the HKCU Run-key autostart on Windows (see
/// <see cref="StartupRegistrationService"/>) so there is no competing medium-integrity instance.
///
/// The poll loop also relaunches the GUI host after a sign-out/sign-in (the old process dies with its
/// session) and after a crash. It is stateless: each tick it checks whether a GUI host is already
/// running in a user session and only launches one when none is present.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class InteractiveDesktopHostLauncher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger<InteractiveDesktopHostLauncher> _logger;

    public InteractiveDesktopHostLauncher(ILogger<InteractiveDesktopHostLauncher> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Interactive desktop host launcher started (elevated GUI host autostart).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EnsureHostRunning();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Interactive desktop host launcher tick failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void EnsureHostRunning()
    {
        // Nothing to do until a user is actually signed in to the console session.
        if (!WindowsActiveSession.IsUserSessionAvailable)
        {
            return;
        }

        // A GUI host already running in a user session — leave it be. (TryLaunch returns only a bool
        // and closes the process handle, so we detect the running host by enumeration rather than by
        // tracking a PID; this also catches a host the user started manually.)
        if (IsInteractiveHostRunning())
        {
            return;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            _logger.LogWarning("Cannot launch the interactive desktop host: process path is unavailable.");
            return;
        }

        var workingDir = System.IO.Path.GetDirectoryName(exePath);
        // --minimized starts the full GUI host into the tray without stealing focus, matching the
        // legacy launch-at-login command line.
        var commandLine = $"\"{exePath}\" --minimized";

        // creationFlags 0: the host is a GUI (WinExe) subsystem app, so it needs no console; the
        // environment-block path inside TryLaunch adds CREATE_UNICODE_ENVIRONMENT on top.
        bool launched = WindowsActiveSession.TryLaunch(
            applicationName: exePath,
            commandLine: commandLine,
            workingDirectory: workingDir,
            creationFlags: 0,
            showWindow: WindowsActiveSession.SW_SHOW,
            logger: _logger,
            elevate: true);

        if (launched)
        {
            _logger.LogInformation("Launched the interactive (elevated) GUI host into the active user session.");
        }
    }

    /// <summary>
    /// True when another instance of this binary is running in an interactive (non-zero) session —
    /// i.e. a GUI host is already up. Session-0 processes (this service, short-lived session tasks
    /// run from Session 0) are ignored. Short-lived <c>--session-task</c> helpers run in a user
    /// session too, but only for milliseconds, so the worst case is skipping one 5s tick.
    /// </summary>
    private static bool IsInteractiveHostRunning()
    {
        Process self = Process.GetCurrentProcess();
        string processName = self.ProcessName;
        foreach (Process p in Process.GetProcessesByName(processName))
        {
            try
            {
                if (p.Id != self.Id && p.SessionId != 0)
                {
                    return true;
                }
            }
            catch
            {
                // Process exited between enumeration and query, or is not queryable — ignore.
            }
            finally
            {
                p.Dispose();
            }
        }

        return false;
    }
}

using System;
using System.Collections.Generic;
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

        // Only treat the host as "already running" when it is in the session that currently owns the
        // active input desktop (the console session). A GUI host left in a different, now-disconnected
        // session — e.g. after a Microsoft "Windows App" (RDP) client disconnects — cannot inject
        // input there, so we relaunch into the active session rather than trusting the stranded one.
        // (We detect the host by enumeration rather than a tracked PID; this also catches a manual start.)
        if (IsInteractiveHostRunningInSession(WindowsActiveSession.ActiveConsoleSessionId))
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
    /// True when another instance of this binary (a GUI host) is running in
    /// <paramref name="activeSessionId"/> — the session that owns the active input desktop. A host in
    /// any other (e.g. disconnected) session does not count, because it cannot inject input there.
    /// Session-0 processes (this service, short-lived <c>--session-task</c> helpers) are ignored.
    /// </summary>
    private static bool IsInteractiveHostRunningInSession(uint activeSessionId)
        => HasHostInSession(CollectInteractiveHostSessionIds(), activeSessionId);

    /// <summary>Session IDs of all other running instances of this binary that are in a non-zero session.</summary>
    private static List<uint> CollectInteractiveHostSessionIds()
    {
        var ids = new List<uint>();
        Process self = Process.GetCurrentProcess();
        string processName = self.ProcessName;
        foreach (Process p in Process.GetProcessesByName(processName))
        {
            try
            {
                if (p.Id != self.Id && p.SessionId != 0)
                {
                    ids.Add((uint)p.SessionId);
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

        return ids;
    }

    /// <summary>
    /// Pure membership check (unit-testable): is a GUI host present in the active session? When the
    /// active session id matches none of the hosts — including the 0xFFFFFFFF "no console session"
    /// value — this is false, so the caller will (correctly) not skip launching.
    /// </summary>
    internal static bool HasHostInSession(IReadOnlyCollection<uint> hostSessionIds, uint activeSessionId)
        => hostSessionIds.Contains(activeSessionId);
}

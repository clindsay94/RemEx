using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Services.Command;

namespace Remex.Agent.Services.Command;

/// <summary>
/// Wraps the platform <see cref="ISystemCommandService"/> and redirects the desktop-bound commands
/// (<see cref="Lock"/>, <see cref="MonitorOff"/>, <see cref="SignOut"/>) into the active console
/// session when the host is running headless in Session 0 (the LocalSystem Windows Service).
///
/// Background: <c>LockWorkStation</c> and the monitor-off <c>WM_SYSCOMMAND</c> broadcast only affect
/// the caller's session, and <c>shutdown /l</c> logs off the caller's session — all of which no-op
/// from Session 0, where the service has no desktop. So a remote "lock" from the phone appeared to do
/// nothing right after boot. Here we detect Session 0 and forward those actions to the signed-in
/// user's session (via <see cref="WindowsActiveSession"/>: a short-lived <c>--session-task</c> helper
/// for lock/monitor-off, and <c>WTSLogoffSession</c> for sign-out).
///
/// Every other command (shutdown/restart/sleep/hibernate) already works machine-wide as SYSTEM and is
/// passed straight through to <paramref name="inner"/>. When not in Session 0 (the GUI host running in
/// the user's own session) we also pass through, since the direct APIs work there.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionBridgingCommandService : ISystemCommandService
{
    private readonly ILogger<SessionBridgingCommandService> _logger;
    private readonly ISystemCommandService _inner;

    public SessionBridgingCommandService(ILogger<SessionBridgingCommandService> logger, ISystemCommandService inner)
    {
        _logger = logger;
        _inner = inner;
    }

    /// <summary>True when this process is the non-interactive Session 0 service.</summary>
    private static bool IsSession0 => Process.GetCurrentProcess().SessionId == 0;

    // ── Desktop-bound commands: bridge into the interactive session when headless ──

    public Task Lock()
    {
        if (IsSession0)
        {
            if (WindowsActiveSession.TryLaunchSessionTask("lock", _logger))
            {
                return Task.CompletedTask;
            }

            _logger.LogWarning("Lock could not be bridged to the interactive session (no signed-in user?); attempting direct call.");
        }

        return _inner.Lock();
    }

    public Task MonitorOff()
    {
        if (IsSession0)
        {
            if (WindowsActiveSession.TryLaunchSessionTask("monitoroff", _logger))
            {
                return Task.CompletedTask;
            }

            _logger.LogWarning("MonitorOff could not be bridged to the interactive session; attempting direct call.");
        }

        return _inner.MonitorOff();
    }

    public Task SignOut()
    {
        if (IsSession0)
        {
            if (WindowsActiveSession.TryLogoffActiveSession(_logger))
            {
                return Task.CompletedTask;
            }

            _logger.LogWarning("SignOut could not log off the active session directly; attempting direct call.");
        }

        return _inner.SignOut();
    }

    // ── Machine-wide commands: SYSTEM can perform these from Session 0; pass through ──

    public Task Shutdown(int delaySeconds = 0) => _inner.Shutdown(delaySeconds);
    public Task ForceShutdown(int delaySeconds = 0) => _inner.ForceShutdown(delaySeconds);
    public Task Restart(int delaySeconds = 0) => _inner.Restart(delaySeconds);
    public Task ForceRestart(int delaySeconds = 0) => _inner.ForceRestart(delaySeconds);
    public Task RestartToUefi(int delaySeconds = 0) => _inner.RestartToUefi(delaySeconds);
    public Task Sleep() => _inner.Sleep();
    public Task Hibernate() => _inner.Hibernate();
}

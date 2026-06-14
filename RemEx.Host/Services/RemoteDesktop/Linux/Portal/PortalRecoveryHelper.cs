using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Remex.Host.Services.RemoteDesktop.Linux.Portal;

/// <summary>
/// One-shot recovery for the case where <c>xdg-desktop-portal</c>'s running
/// frontend doesn't expose <c>org.freedesktop.portal.RemoteDesktop</c> even
/// though the matching backend (<c>xdg-desktop-portal-kde</c>,
/// <c>xdg-desktop-portal-gnome</c>, ...) is installed. The fix is to push the
/// current desktop session env into <c>systemd --user</c> and restart the
/// portal frontend so it rebuilds its interface table.
///
/// <para>
/// Gated by a process-wide <see cref="Interlocked"/> flag — both
/// <see cref="LinuxPortalRemoteDesktopSessionService"/> and the input injector
/// can race to recover during a single connection attempt, but the actual
/// restart runs at most once per process to avoid thrashing
/// <c>xdg-desktop-portal.service</c>.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
internal static class PortalRecoveryHelper
{
    private static int _attempted; // 0 = never, 1 = attempted (Interlocked)

    /// <summary>
    /// Returns true exactly once per process; subsequent callers get false.
    /// Use to decide whether to actually invoke recovery vs. just wait for
    /// another caller's attempt to finish.
    /// </summary>
    public static bool ShouldAttempt()
        => Interlocked.CompareExchange(ref _attempted, 1, 0) == 0;

    /// <summary>
    /// Test-only: reset the one-shot guard. Not exposed publicly.
    /// </summary>
    internal static void ResetForTests() => Interlocked.Exchange(ref _attempted, 0);

    /// <summary>
    /// Attempts to recover a stale portal frontend by running only the
    /// <see cref="LinuxRepairActionKind.RestartPortalFrontend"/> action from a
    /// freshly evaluated prerequisite report, then waits briefly and re-probes
    /// the portal to confirm <c>RemoteDesktop</c> is exposed.
    /// </summary>
    /// <returns>
    /// True when the post-restart re-evaluation reports
    /// <c>PortalRemoteDesktopAvailable=true</c>. False when the action wasn't
    /// applicable, the script failed, or the frontend still doesn't expose
    /// the interface afterwards.
    /// </returns>
    public static async Task<bool> TryRecoverAsync(
        ILogger logger,
        CancellationToken ct = default)
    {
        try
        {
            var prereqs = new LinuxRemoteDesktopPrerequisites();
            var report = await prereqs.EvaluateAsync(ct);

            // Only attempt recovery when this is genuinely the stale-frontend case.
            // For "backend not installed" we have nothing to fix here.
            if (report.PortalRemoteDesktopAvailable)
            {
                logger.LogDebug("PortalRecoveryHelper: portal already exposes RemoteDesktop; nothing to do.");
                return true;
            }
            if (!report.PortalBackendInstalled || !report.PortalBackendImplementsRemoteDesktop)
            {
                logger.LogWarning(
                    "PortalRecoveryHelper: portal backend is not installed (or does not " +
                    "implement RemoteDesktop). Cannot recover by restarting the frontend; " +
                    "user must install the appropriate xdg-desktop-portal backend.");
                return false;
            }

            var plan = prereqs.BuildRepairPlan(report);
            var restartAction = plan.Actions.FirstOrDefault(
                a => a.Kind == LinuxRepairActionKind.RestartPortalFrontend);
            if (restartAction is null)
            {
                logger.LogWarning("PortalRecoveryHelper: no RestartPortalFrontend action in plan.");
                return false;
            }

            logger.LogWarning(
                "Portal frontend is stale (backend {Backend} installed but " +
                "RemoteDesktop interface missing). Restarting xdg-desktop-portal.service " +
                "with re-imported desktop environment...",
                report.PortalBackendPackageName ?? "(unknown)");

            var repair = new LinuxDependencyRepairService(prereqs);
            var results = await repair.RepairAsync(
                new LinuxPrerequisiteRepairPlan
                {
                    Actions = new[] { restartAction },
                    HasAutomatedRepair = true,
                },
                allowPackageInstall: false,
                allowElevated: false,
                ct);

            var firstResult = results.FirstOrDefault();
            if (firstResult is null || !firstResult.Success)
            {
                logger.LogWarning(
                    "PortalRecoveryHelper: restart script failed. Output: {Output}",
                    firstResult?.Output ?? firstResult?.ErrorMessage ?? "(no output)");
                return false;
            }

            // The portal frontend takes a moment to come back up and accept
            // introspection; give it ~2 seconds before re-probing.
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            var postReport = await prereqs.EvaluateAsync(ct);
            if (postReport.PortalRemoteDesktopAvailable)
            {
                logger.LogInformation(
                    "Portal frontend recovered; RemoteDesktop interface is now exposed.");
                return true;
            }

            logger.LogWarning(
                "Portal frontend restarted but RemoteDesktop is still missing. " +
                "Reason: {Reason}",
                postReport.PortalUnavailableReason ?? "(unknown)");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PortalRecoveryHelper: unexpected error during recovery.");
            return false;
        }
    }
}

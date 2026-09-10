using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Remex.Agent.Services.RemoteDesktop.Linux;

/// <summary>
/// Executes repair actions produced by <see cref="LinuxRemoteDesktopPrerequisites.BuildRepairPlan"/>
/// to fix missing or misconfigured Linux remote desktop dependencies.
///
/// Safety constraints (non-negotiable):
///   - Never runs pacman or any package manager without explicit user approval.
///   - Never runs any command that requires elevated privileges without the
///     <c>allowElevated</c> flag being explicitly set by the caller.
///   - Service restarts (systemctl --user restart) run without elevation and are
///     always attempted automatically (safe, user-service scope only).
///   - udev rule creation writes to /etc/udev/rules.d which requires sudo;
///     this is only attempted when the caller explicitly passes allowElevated=true.
///   - Group membership changes (usermod) require sudo and explicit approval.
///
/// The repair service reports progress via <see cref="RepairProgressChanged"/> so the
/// host UI can show a repair dialog with real-time status updates.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxDependencyRepairService
{
    private readonly ILogger<LinuxDependencyRepairService> _logger;
    private readonly LinuxRemoteDesktopPrerequisites _prerequisites;

    /// <summary>
    /// Raised for each repair action as it starts and completes.
    /// (action, isStarting, result_or_null)
    /// </summary>
    public event Action<LinuxRepairAction, bool, LinuxDependencyRepairResult?>? RepairProgressChanged;

    public LinuxDependencyRepairService(
        LinuxRemoteDesktopPrerequisites? prerequisites = null,
        ILogger<LinuxDependencyRepairService>? logger = null)
    {
        _prerequisites = prerequisites ?? new LinuxRemoteDesktopPrerequisites();
        _logger = logger ?? NullLogger<LinuxDependencyRepairService>.Instance;
    }

    /// <summary>
    /// Evaluates the current prerequisite state and returns a list of issues
    /// suitable for display in the repair UI.
    /// </summary>
    public async Task<IReadOnlyList<LinuxDependencyIssue>> GetIssuesAsync(CancellationToken ct = default)
    {
        var report = await Task.Run(() => _prerequisites.Evaluate(), ct);
        return BuildIssueList(report);
    }

    /// <summary>
    /// Executes all automated repair actions from the given plan.
    ///
    /// <paramref name="allowPackageInstall"/>: must be true to run pacman installs.
    ///   This flag should only be set when the user has explicitly confirmed
    ///   they want packages installed.
    ///
    /// <paramref name="allowElevated"/>: must be true to run sudo commands
    ///   (udev rule creation, group membership changes). Only set when the user
    ///   has explicitly approved elevated operations.
    /// </summary>
    public async Task<IReadOnlyList<LinuxDependencyRepairResult>> RepairAsync(
        LinuxPrerequisiteRepairPlan plan,
        bool allowPackageInstall = false,
        bool allowElevated = false,
        CancellationToken ct = default)
    {
        var results = new List<LinuxDependencyRepairResult>();

        foreach (var action in plan.Actions)
        {
            ct.ThrowIfCancellationRequested();

            // Gate: skip package installs without explicit approval.
            if (action.Kind == LinuxRepairActionKind.InstallPackage && !allowPackageInstall)
            {
                _logger.LogInformation(
                    "Skipping package install (approval not given): {Description}", action.Description);
                continue;
            }

            // Gate: skip elevated operations without explicit approval.
            if (action.RequiresElevation && !allowElevated)
            {
                _logger.LogInformation(
                    "Skipping elevated action (approval not given): {Description}", action.Description);
                continue;
            }

            // Manual actions cannot be executed automatically.
            if (action.Kind == LinuxRepairActionKind.Manual)
            {
                results.Add(new LinuxDependencyRepairResult
                {
                    Action = action,
                    Success = false,
                    ErrorMessage = "Manual action — cannot be executed automatically.",
                });
                continue;
            }

            RepairProgressChanged?.Invoke(action, true, null);

            var result = await ExecuteActionAsync(action, ct);
            results.Add(result);

            RepairProgressChanged?.Invoke(action, false, result);

            _logger.LogInformation(
                "Repair action '{Description}': {Status}",
                action.Description,
                result.Success ? "OK" : $"FAILED — {result.ErrorMessage}");
        }

        return results;
    }

    /// <summary>
    /// Convenience method: evaluates prerequisites, builds a repair plan, and
    /// executes all safe (non-elevated, non-install) actions automatically.
    /// </summary>
    public async Task<(LinuxPrerequisiteReport Report, IReadOnlyList<LinuxDependencyRepairResult> Results)>
        AutoRepairSafeActionsAsync(CancellationToken ct = default)
    {
        var report = await Task.Run(() => _prerequisites.Evaluate(), ct);
        var plan = _prerequisites.BuildRepairPlan(report);

        var results = await RepairAsync(
            plan,
            allowPackageInstall: false,
            allowElevated: false,
            ct);

        return (report, results);
    }

    // ── Private implementation ─────────────────────────────────────────

    private async Task<LinuxDependencyRepairResult> ExecuteActionAsync(
        LinuxRepairAction action, CancellationToken ct)
    {
        if (action.ArgumentList is null && action.Command is null)
        {
            return new LinuxDependencyRepairResult
            {
                Action = action,
                Success = false,
                ErrorMessage = "No command specified for this action.",
            };
        }

        try
        {
            ProcessStartInfo psi;
            if (action.ArgumentList is { Count: > 0 } argList)
            {
                // Structured args — preferred path. No shell parsing happens; each
                // argument reaches the process verbatim, so embedded spaces and
                // shell metacharacters are safe.
                psi = new ProcessStartInfo(argList[0])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                for (var i = 1; i < argList.Count; i++)
                    psi.ArgumentList.Add(argList[i]);
            }
            else
            {
                // Legacy path: whitespace-split. Used by simple "systemctl --user start X"
                // style commands. Does not understand single-quote shell quoting.
                var parts = action.Command!.Split(' ', 2, StringSplitOptions.TrimEntries);
                var fileName = parts[0];
                var arguments = parts.Length > 1 ? parts[1] : string.Empty;
                psi = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120)); // package installs can be slow

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new LinuxDependencyRepairResult
                {
                    Action = action,
                    Success = false,
                    ErrorMessage = $"Failed to start process: {psi.FileName}",
                };
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            await proc.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            bool ok = proc.ExitCode == 0;
            return new LinuxDependencyRepairResult
            {
                Action = action,
                Success = ok,
                Output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout,
                ErrorMessage = ok ? null : $"Exit {proc.ExitCode}: {stderr}",
            };
        }
        catch (OperationCanceledException)
        {
            return new LinuxDependencyRepairResult
            {
                Action = action,
                Success = false,
                ErrorMessage = "Operation was cancelled.",
            };
        }
        catch (Exception ex)
        {
            return new LinuxDependencyRepairResult
            {
                Action = action,
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    private static IReadOnlyList<LinuxDependencyIssue> BuildIssueList(LinuxPrerequisiteReport report)
    {
        var issues = new List<LinuxDependencyIssue>();

        if (!report.SessionBusAvailable)
            issues.Add(new LinuxDependencyIssue
            {
                Component = "D-Bus",
                Description = "Session D-Bus is not available. xdg-desktop-portal requires a running session bus.",
                Severity = LinuxDependencyIssueSeverity.Error,
                RepairAvailable = false,
            });

        if (!report.PortalRemoteDesktopAvailable || !report.PortalScreenCastAvailable)
        {
            // Stale-frontend case: backend on disk implements RemoteDesktop but
            // the running frontend doesn't expose it. Recoverable without sudo
            // and on any distro by restarting xdg-desktop-portal.service.
            var stale = report.PortalBackendInstalled &&
                        report.PortalBackendImplementsRemoteDesktop;
            issues.Add(new LinuxDependencyIssue
            {
                Component = "xdg-desktop-portal",
                Description = report.PortalUnavailableReason ?? "xdg-desktop-portal is unavailable.",
                Severity = LinuxDependencyIssueSeverity.Error,
                RepairAvailable = stale || IsArchFamily(),
                RepairDescription = stale
                    ? "Restart xdg-desktop-portal frontend (no install required)"
                    : IsArchFamily()
                        ? $"Install xdg-desktop-portal and {report.PortalBackendPackageName ?? "the portal backend"} via pacman"
                        : null,
            });
        }

        if (!report.PipeWireRunning)
            issues.Add(new LinuxDependencyIssue
            {
                Component = "PipeWire",
                Description = "PipeWire service is not running. Screen capture requires PipeWire.",
                Severity = LinuxDependencyIssueSeverity.Error,
                RepairAvailable = true,
                RepairDescription = "Restart PipeWire user service (systemctl --user restart pipewire)",
            });

        if (!report.PipeWireLibraryAvailable)
            issues.Add(new LinuxDependencyIssue
            {
                Component = "libpipewire-0.3",
                Description = report.PipeWireUnavailableReason ?? "libpipewire-0.3.so is not installed.",
                Severity = LinuxDependencyIssueSeverity.Error,
                RepairAvailable = IsArchFamily(),
                RepairDescription = IsArchFamily() ? "Install pipewire via pacman" : null,
            });

        if (!report.LibeiAvailable)
            issues.Add(new LinuxDependencyIssue
            {
                Component = "libei",
                Description = "libei is not installed. Wayland keyboard/pointer injection will use portal fallback.",
                Severity = LinuxDependencyIssueSeverity.Warning,
                RepairAvailable = IsArchFamily(),
                RepairDescription = IsArchFamily() ? "Install libei via pacman" : null,
            });

        if (report.UinputNodeExists && !report.UinputWritable)
            issues.Add(new LinuxDependencyIssue
            {
                Component = "uinput",
                Description = report.UinputUnavailableReason ?? "Cannot write to /dev/uinput. Pen/stylus injection unavailable.",
                Severity = LinuxDependencyIssueSeverity.Warning,
                RepairAvailable = true,
                RepairDescription = "Add udev rule for /dev/uinput write access (requires sudo)",
            });

        return issues.AsReadOnly();
    }

    private static bool IsArchFamily()
    {
        return System.IO.File.Exists("/etc/arch-release");
    }
}

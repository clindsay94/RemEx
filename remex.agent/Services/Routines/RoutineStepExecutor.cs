using System.ComponentModel;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Routines;

namespace Remex.Agent.Services.Routines;

/// <summary>One host-side step to execute (routines spec §7.3.3, §8.4).</summary>
/// <param name="OwnerClientId">The phone that owns the run: the session's PROVEN client id, never a payload field.</param>
/// <param name="RunId">The run's id.</param>
/// <param name="RoutineId">The routine's id.</param>
/// <param name="RoutineName">The routine's name, sanitized, ≤ 40.</param>
/// <param name="StepIndex">0-11.</param>
/// <param name="Step">The step itself.</param>
/// <param name="Source">The run source (§8.8); display and history only.</param>
/// <param name="TestRun">
/// An in-app Test (D7). A destructive verb is NEVER issued under it: the countdown runs, the step is
/// <c>simulated</c> (T22).
/// </param>
/// <param name="PresenceConfirmed">
/// <b>IN-PROCESS ONLY (T21).</b> True only for a PC Run now the person at the PC confirmed in
/// <c>ConfirmationDialogHost</c> (routines S4's <c>RoutineRunNow</c>). It skips the countdown. No field
/// of any wire message maps to it; <see cref="RoutineStepRequestHandler"/> always passes false.
/// </param>
public sealed record RoutineStepExecution(
    string OwnerClientId,
    string RunId,
    string RoutineId,
    string RoutineName,
    int StepIndex,
    RoutineStep? Step,
    string? Source,
    bool TestRun,
    bool PresenceConfirmed = false);

/// <summary>
/// Executes the host-side routine steps (routines spec §8.4): <c>power</c> through
/// <see cref="Remex.Core.Services.Command.SharedCommandVerbs"/>, <c>launchApp</c> through the launcher
/// allowlist and <see cref="IAppLauncherService.LaunchAppAsync"/>, <c>media</c> through the media-key
/// VKs, and <c>notify(pc)</c> through <see cref="NotificationService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>EVERYTHING IS REVALIDATED HERE, AT RUN TIME (T6).</b> A step that was valid when the phone saved
/// it may not be now: the owner may have been revoked, the app removed from the launcher list, the
/// verb dropped from <c>routinePowerVerbs</c>. The phone's validator is never trusted (T5).
/// </para>
/// <para>
/// <b>A DESTRUCTIVE VERB ALWAYS COUNTS DOWN</b> unless <see cref="RoutineStepExecution.PresenceConfirmed"/>,
/// and <b>A TEST RUN NEVER ISSUES ONE</b> (docs/REGRESSION-GUARDS.md). Under
/// <c>--routines-dry-run</c> the verb is logged instead.
/// </para>
/// <para>In-process only. Nothing here reaches <c>RemexNetworkListener</c> or TCP 8338 (T15).</para>
/// </remarks>
public sealed class RoutineStepExecutor
{
    private static readonly TimeSpan PowerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(15);

    private readonly IRoutinePowerExecutor _power;
    private readonly ILauncherStorageService _launchers;
    private readonly IAppLauncherService _launcher;
    private readonly IRoutineMediaKeys _media;
    private readonly IRoutineUi _ui;
    private readonly IRoutineOwnerDirectory _owners;
    private readonly IHostCapabilitiesProvider _capabilities;
    private readonly RoutineCountdownCoordinator _countdown;
    private readonly RoutineDryRunMode _dryRun;
    private readonly TimeProvider _time;
    private readonly ILogger<RoutineStepExecutor> _logger;

    public RoutineStepExecutor(
        IRoutinePowerExecutor power,
        ILauncherStorageService launchers,
        IAppLauncherService launcher,
        IRoutineMediaKeys media,
        IRoutineUi ui,
        IRoutineOwnerDirectory owners,
        IHostCapabilitiesProvider capabilities,
        RoutineCountdownCoordinator countdown,
        RoutineDryRunMode dryRun,
        TimeProvider time,
        ILogger<RoutineStepExecutor> logger)
    {
        _power = power;
        _launchers = launchers;
        _launcher = launcher;
        _media = media;
        _ui = ui;
        _owners = owners;
        _capabilities = capabilities;
        _countdown = countdown;
        _dryRun = dryRun;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Executes one step and returns its <c>routine_step_result</c>. Never throws.
    /// </summary>
    /// <param name="execution">The step.</param>
    /// <param name="beforeDestructiveIssue">
    /// Invoked with the <c>succeeded</c> result immediately BEFORE a destructive verb is issued, because
    /// the socket dies with the machine (§7.3.4). A failure inside it never blocks the verb.
    /// </param>
    public async Task<RoutineStepResultPayload> ExecuteAsync(
        RoutineStepExecution execution,
        Func<RoutineStepResultPayload, Task>? beforeDestructiveIssue = null)
    {
        try
        {
            return await ExecuteCoreAsync(execution, beforeDestructiveIssue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Routine step {Index} of run {RunId} failed unexpectedly.",
                execution.StepIndex, execution.RunId);
            return Failed(execution, RoutineReasonCodes.InternalError, "unexpected error");
        }
    }

    private async Task<RoutineStepResultPayload> ExecuteCoreAsync(
        RoutineStepExecution execution,
        Func<RoutineStepResultPayload, Task>? beforeDestructiveIssue)
    {
        // T6: the owner must still be paired. A revoked phone's queued or retried request dies here.
        if (string.IsNullOrWhiteSpace(execution.OwnerClientId) || !_owners.IsPaired(execution.OwnerClientId))
        {
            return Failed(execution, RoutineReasonCodes.PcNotPaired, null);
        }

        var step = execution.Step;
        if (step is null)
        {
            return Failed(execution, RoutineReasonCodes.InvalidField, "step");
        }

        if (!RoutineStepTypes.IsKnown(step.Type))
        {
            return Failed(execution, RoutineReasonCodes.UnsupportedStep, "step.type");
        }

        // Only power, launchApp, media and notify(pc) execute on the PC. wake / waitOnline / delay /
        // notify(phone) belong to the phone runner and are refused rather than guessed at.
        if (!step.IsHostExecuted)
        {
            return Failed(execution, RoutineReasonCodes.StepNotAllowedOnPc, "step.type");
        }

        // The same schema rules the phone's editor applied, re-run against what actually arrived.
        var verdict = RoutineValidator.ValidateStep(step, hostRun: false);
        if (!verdict.IsValid)
        {
            return Failed(execution, verdict.ReasonCode, verdict.Detail);
        }

        return step.Type switch
        {
            RoutineStepTypes.Power => await ExecutePowerAsync(execution, step, beforeDestructiveIssue),
            RoutineStepTypes.LaunchApp => await ExecuteLaunchAsync(execution, step),
            RoutineStepTypes.Media => ExecuteMedia(execution, step),
            _ => ExecuteNotifyPc(execution, step),
        };
    }

    private async Task<RoutineStepResultPayload> ExecutePowerAsync(
        RoutineStepExecution execution,
        RoutineStep step,
        Func<RoutineStepResultPayload, Task>? beforeDestructiveIssue)
    {
        var verb = step.Verb!;

        // T6: the verb must still be one this PC advertises. WAKEONLAN never is (D5).
        var advertised = _capabilities.GetCurrent().RoutinePowerVerbs;
        if (!RoutinePowerVerbs.IsAllowed(verb) || advertised is null || !advertised.Contains(verb, StringComparer.Ordinal))
        {
            return Failed(execution, RoutineReasonCodes.PowerUnsupported, "step.verb");
        }

        if (!RoutinePowerVerbs.IsDestructive(verb))
        {
            // LOCK and MONITOROFF: no countdown, and a test run carries them out for real (§8.9).
            return await IssuePowerAsync(execution, verb, step.DelaySeconds, countdownShown: false);
        }

        var countdownShown = false;
        if (!execution.PresenceConfirmed)
        {
            var result = await _countdown.RunAsync(new RoutineCountdownRequest(
                execution.RunId,
                execution.OwnerClientId,
                new RoutineCountdownPrompt(
                    execution.RunId,
                    execution.RoutineName,
                    verb,
                    _owners.DisplayName(execution.OwnerClientId),
                    execution.Source,
                    (int)RoutineCountdownCoordinator.Length.TotalSeconds,
                    execution.TestRun,
                    _dryRun.IsEnabled)));

            switch (result.Status)
            {
                case RoutineCountdownStatus.Conflict:
                    return Failed(execution, RoutineReasonCodes.ConflictCountdownActive, null);

                case RoutineCountdownStatus.Cancelled:
                    AnnounceCancelled(execution, verb);
                    return new RoutineStepResultPayload
                    {
                        RunId = execution.RunId,
                        StepIndex = execution.StepIndex,
                        Outcome = RoutineStepOutcomes.Cancelled,
                        ReasonCode = result.CancelledBy == RoutineCancelledBy.Pc
                            ? RoutineReasonCodes.CancelledOnPc
                            : RoutineReasonCodes.CancelledOnPhone,
                        CountdownShown = result.Shown,
                        CancelledBy = result.CancelledBy,
                    };
            }

            countdownShown = result.Shown;
            if (!result.Shown)
            {
                _logger.LogInformation("Routine countdown for run {RunId} ran unseen (locked or no desktop).", execution.RunId);
            }
        }

        // T22: a test run NEVER issues a destructive verb, whatever else is true.
        if (execution.TestRun)
        {
            _logger.LogInformation("Routine test run {RunId}: {Verb} simulated, not issued.", execution.RunId, verb);
            return new RoutineStepResultPayload
            {
                RunId = execution.RunId,
                StepIndex = execution.StepIndex,
                Outcome = RoutineStepOutcomes.Simulated,
                ReasonCode = RoutineReasonCodes.Simulated,
                CountdownShown = countdownShown,
            };
        }

        if (_dryRun.IsEnabled)
        {
            _logger.LogWarning("Routine dry run: {Verb} for run {RunId} was logged, not issued.", verb, execution.RunId);
            return new RoutineStepResultPayload
            {
                RunId = execution.RunId,
                StepIndex = execution.StepIndex,
                Outcome = RoutineStepOutcomes.Succeeded,
                ReasonCode = RoutineReasonCodes.DryRun,
                CountdownShown = countdownShown,
            };
        }

        // §7.3.4: "succeeded" goes out BEFORE the verb, because the socket dies with the machine.
        if (beforeDestructiveIssue is not null)
        {
            try
            {
                await beforeDestructiveIssue(Succeeded(execution, countdownShown));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pre-issue result for run {RunId} could not be sent.", execution.RunId);
            }
        }

        return await IssuePowerAsync(execution, verb, step.DelaySeconds, countdownShown);
    }

    private async Task<RoutineStepResultPayload> IssuePowerAsync(
        RoutineStepExecution execution, string verb, int? delaySeconds, bool countdownShown)
    {
        var action = RoutineStrings.ActionLabel(verb);
        try
        {
            var outcome = await _power.ExecuteAsync(verb, delaySeconds).WaitAsync(PowerTimeout, _time);
            if (outcome is null)
            {
                return Failed(execution, RoutineReasonCodes.PowerUnsupported, "step.verb", countdownShown);
            }

            if (!outcome.Value.Success)
            {
                Problem(execution, RoutineStrings.Format("Routine_Tray_PowerFailed", action));
                return Failed(execution, RoutineReasonCodes.PowerFailed, Truncate(outcome.Value.Message), countdownShown);
            }

            _logger.LogInformation("Routine run {RunId} issued {Verb}.", execution.RunId, verb);
            return Succeeded(execution, countdownShown);
        }
        catch (Exception ex) when (IsDeniedByOs(ex))
        {
            _logger.LogWarning(ex, "Routine run {RunId}: the system refused {Verb}.", execution.RunId, verb);
            Problem(execution, RoutineStrings.Format("Routine_Tray_PowerDenied", action));
            return Failed(execution, RoutineReasonCodes.PowerDeniedByOs, null, countdownShown);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routine run {RunId}: {Verb} failed.", execution.RunId, verb);
            Problem(execution, RoutineStrings.Format("Routine_Tray_PowerFailed", action));
            return Failed(execution, RoutineReasonCodes.PowerFailed, ex is TimeoutException ? "timeout" : null, countdownShown);
        }
    }

    private async Task<RoutineStepResultPayload> ExecuteLaunchAsync(RoutineStepExecution execution, RoutineStep step)
    {
        var appLabel = string.IsNullOrWhiteSpace(step.AppLabel) ? string.Empty : RoutineText.Sanitize(step.AppLabel);

        // T6: the appId must still name an entry in launchers.json. The phone's label is display only;
        // the host launches ITS OWN TargetPath for that id and nothing the wire supplied.
        if (!Guid.TryParse(step.AppId, out var appId))
        {
            return Failed(execution, RoutineReasonCodes.LaunchNotAllowed, "step.appId");
        }

        var entries = await _launchers.LoadEntriesAsync();
        var entry = entries.FirstOrDefault(e => e is not null && e.Id == appId);
        if (entry is null || string.IsNullOrWhiteSpace(entry.TargetPath))
        {
            return Failed(execution, RoutineReasonCodes.LaunchNotAllowed, "step.appId");
        }

        var appName = string.IsNullOrWhiteSpace(entry.DisplayName) ? appLabel : entry.DisplayName;
        try
        {
            // IsLaunchAllowed + IsRejectedNetworkPath run INSIDE LaunchAppAsync, the same guards the
            // phone's LAUNCHAPP command passes (VULN-3).
            await _launcher.LaunchAppAsync(entry.TargetPath).WaitAsync(LaunchTimeout, _time);
            return Succeeded(execution, countdownShown: false);
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(execution, RoutineReasonCodes.LaunchNotAllowed, "path rejected");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routine run {RunId}: the launch failed.", execution.RunId);
            Problem(execution, RoutineStrings.Format("Routine_Tray_LaunchFailed", appName));
            return Failed(execution, RoutineReasonCodes.LaunchFailed, ex is TimeoutException ? "timeout" : null);
        }
    }

    private RoutineStepResultPayload ExecuteMedia(RoutineStepExecution execution, RoutineStep step)
    {
        var virtualKey = MediaVirtualKey(step.MediaAction);
        if (virtualKey is null)
        {
            return Failed(execution, RoutineReasonCodes.InvalidField, "step.mediaAction");
        }

        return _media.TrySend(virtualKey.Value)
            ? Succeeded(execution, countdownShown: false)
            : Failed(execution, RoutineReasonCodes.MediaUnavailable, null);
    }

    private RoutineStepResultPayload ExecuteNotifyPc(RoutineStepExecution execution, RoutineStep step)
    {
        // Phone-authored text: sanitized for presentation, never logged (T11).
        var title = RoutineText.Sanitize(step.Title);
        var body = RoutineText.Sanitize(step.Body);
        _ui.Notify(NotificationImportance.Outcome, title, body);
        return Succeeded(execution, countdownShown: false);
    }

    /// <summary>The virtual key for a <c>media.mediaAction</c>: the same VKs the phone's media row sends.</summary>
    internal static int? MediaVirtualKey(string? action) => action switch
    {
        RoutineMediaActions.PlayPause => 0xB3,
        RoutineMediaActions.Next => 0xB0,
        RoutineMediaActions.Previous => 0xB1,
        _ => null,
    };

    private void AnnounceCancelled(RoutineStepExecution execution, string verb)
    {
        _ui.Notify(
            NotificationImportance.Outcome,
            execution.RoutineName,
            RoutineStrings.Format("Routine_Tray_Cancelled", RoutineStrings.ActionLabel(verb)));
    }

    private void Problem(RoutineStepExecution execution, string message) =>
        _ui.Notify(NotificationImportance.Problem, execution.RoutineName, message);

    private static bool IsDeniedByOs(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is Win32Exception win32 && win32.NativeErrorCode is 5 or 1314);

    private static RoutineStepResultPayload Succeeded(RoutineStepExecution execution, bool countdownShown) => new()
    {
        RunId = execution.RunId,
        StepIndex = execution.StepIndex,
        Outcome = RoutineStepOutcomes.Succeeded,
        ReasonCode = RoutineReasonCodes.Ok,
        CountdownShown = countdownShown,
    };

    internal static RoutineStepResultPayload Failed(
        RoutineStepExecution execution, string reasonCode, string? detail, bool countdownShown = false) => new()
    {
        RunId = execution.RunId,
        StepIndex = execution.StepIndex,
        Outcome = RoutineStepOutcomes.Failed,
        ReasonCode = reasonCode,
        CountdownShown = countdownShown,
        Detail = Truncate(detail),
    };

    private static string? Truncate(string? detail) =>
        detail is null ? null : detail.Length <= 120 ? detail : detail[..120];
}

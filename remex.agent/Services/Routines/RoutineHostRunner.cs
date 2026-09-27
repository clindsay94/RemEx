using Remex.Agent.Services.Security;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Routines;

namespace Remex.Agent.Services.Routines;

/// <summary>What starts a PC run.</summary>
/// <param name="OwnerClientId">The owner phone. For a wire request, the session's PROVEN id, never a payload field.</param>
/// <param name="RoutineId">One of that owner's STORED routines (T24): the PC runs its own copy.</param>
/// <param name="Source">A <see cref="RoutineRunSources"/> value.</param>
/// <param name="TestRun">D7: destructive steps are simulated.</param>
/// <param name="PresenceConfirmed">
/// <b>IN-PROCESS ONLY (T21).</b> Set by <c>IRoutinesHost.RunNowAsync</c> for a Run now the person at the PC
/// confirmed. Every trigger and every wire request passes false.
/// </param>
/// <param name="RunId">A run id chosen by the phone (<c>routine_run_request</c>); null = a new one.</param>
/// <param name="Detail">Trigger detail for history.</param>
public sealed record RoutineRunStart(
    string OwnerClientId,
    string RoutineId,
    string Source,
    bool TestRun = false,
    bool PresenceConfirmed = false,
    string? RunId = null,
    RoutineRunSourceDetail? Detail = null);

/// <summary>A started (or refused) run.</summary>
/// <param name="Initial">The record as it started (<c>running</c>) or the <c>skipped</c> record.</param>
/// <param name="Completion">Completes with the final record; already complete for a skip.</param>
public sealed record RoutineRunHandle(RoutineRun Initial, Task<RoutineRun> Completion)
{
    public bool Started => Initial.Outcome == RoutineRunOutcomes.Running;
}

/// <summary>
/// The PC runner (routines spec §8.1, §8.4): preconditions, single-flight, limits, the step loop, history
/// and reports.
/// </summary>
/// <remarks>
/// <para>
/// <b>PERSON-INITIATED RUNS IGNORE PAUSE, AUTOMATIC ONES DO NOT (§8.7).</b> Pause all (phone or PC), the
/// phone's own enable switch and the owner-absent suspension stop automatic sources only; a phone
/// <c>routine_run_request</c> and a PC Run now still run. The PC's own switches (<c>pcDisabled</c>, block)
/// stop everything except a PC Run now, which is the person at the PC overriding their own setting.
/// </para>
/// <para>
/// <b>THE COUNTDOWN IS THE EXECUTOR'S, AND ONLY <see cref="RoutineRunStart.PresenceConfirmed"/> SKIPS IT.</b>
/// This class never decides whether a destructive step counts down; it passes the flag through, and no
/// wire path can set it (T21, docs/REGRESSION-GUARDS.md).
/// </para>
/// <para>
/// Limits (§7.4.4, §8.4, T9): 4 concurrent runs host-wide; 30 runs per owner per hour; single-flight per
/// routine (<c>already_running</c>, coalesced to one record per routine per 60 s); automatic sources at most
/// once per routine per 60 s (<c>cooldown</c>); pause skips coalesced to one record per routine per hour.
/// </para>
/// </remarks>
public sealed class RoutineHostRunner
{
    public const int MaxConcurrentRuns = 4;
    public const int MaxRunsPerOwnerPerHour = 30;
    public static readonly TimeSpan AutomaticMinInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AlreadyRunningCoalesce = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PauseSkipCoalesce = TimeSpan.FromHours(1);

    private readonly RoutineHostStore _store;
    private readonly RoutineRunStore _runs;
    private readonly RoutineStepExecutor _executor;
    private readonly RoutineCountdownCoordinator _countdown;
    private readonly IRoutineOwnerDirectory _owners;
    private readonly IRoutinePhoneChannel _channel;
    private readonly IRoutinePhoneNotifier _notifier;
    private readonly IRoutineUi _ui;
    private readonly Func<string?> _hostIdentity;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveRun> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<long>> _ownerStarts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Routine), long> _lastAutomaticStart = new();
    private readonly Dictionary<(string Owner, string Routine, string Reason), long> _lastSkipRecord = new();

    public RoutineHostRunner(
        RoutineHostStore store,
        RoutineRunStore runs,
        RoutineStepExecutor executor,
        RoutineCountdownCoordinator countdown,
        IRoutineOwnerDirectory owners,
        IRoutinePhoneChannel channel,
        IRoutinePhoneNotifier notifier,
        IRoutineUi ui,
        Func<string?> hostIdentity,
        TimeProvider time,
        ILogger<RoutineHostRunner> logger)
    {
        _store = store;
        _runs = runs;
        _executor = executor;
        _countdown = countdown;
        _owners = owners;
        _channel = channel;
        _notifier = notifier;
        _ui = ui;
        _hostIdentity = hostIdentity;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised when a run starts, changes step, or ends.</summary>
    public event Action? Changed;

    /// <summary>Whether a run of that routine is active.</summary>
    public bool IsRunning(string ownerClientId, string routineId)
    {
        lock (_gate)
        {
            return _active.Values.Any(a => a.Owner == ownerClientId && a.RoutineId == routineId);
        }
    }

    /// <summary>
    /// Starts a run, or records why it could not start. Returns once the run record exists (persisted in
    /// state <c>running</c>, §8.1); the steps continue in the background. Never throws.
    /// </summary>
    public async Task<RoutineRunHandle> StartAsync(RoutineRunStart start)
    {
        try
        {
            return await StartCoreAsync(start);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Starting a routine run failed unexpectedly.");
            var failed = SkippedRecord(start, null, RoutineReasonCodes.InternalError) with { Outcome = RoutineRunOutcomes.Failed };
            return new RoutineRunHandle(failed, Task.FromResult(failed));
        }
    }

    /// <summary>
    /// A phone's <c>routine_cancel</c> for a host run: only the sender's own run (T17). Stops a countdown at
    /// once and any other run before its next step.
    /// </summary>
    public bool CancelFromPhone(string ownerClientId, string runId, string cancelledBy)
    {
        ActiveRun? active;
        lock (_gate)
        {
            if (!_active.TryGetValue(runId, out active) || active.Owner != ownerClientId)
            {
                return false;
            }
        }

        active.RequestCancel(cancelledBy, fromPc: false);
        _countdown.TryCancelFromPhone(runId, ownerClientId, cancelledBy);
        return true;
    }

    /// <summary>Cancels a host run from the PC (<c>cancelled_on_pc</c>).</summary>
    public bool CancelFromPc(string runId, string cancelledBy = RoutineCancelledBy.Pc)
    {
        ActiveRun? active;
        lock (_gate)
        {
            if (!_active.TryGetValue(runId, out active))
            {
                return false;
            }
        }

        active.RequestCancel(cancelledBy, fromPc: true);
        if (string.Equals(_countdown.ActiveRunId, runId, StringComparison.Ordinal))
        {
            _countdown.CancelActive(cancelledBy);
        }

        return true;
    }

    /// <summary>
    /// Pause all: cancels the AUTOMATIC runs (of one owner, or of everyone when null) and their countdowns
    /// (§8.7). Person-initiated runs continue.
    /// </summary>
    public void CancelAutomaticRuns(string? ownerClientId, bool fromPc)
    {
        List<ActiveRun> targets;
        lock (_gate)
        {
            targets = _active.Values
                .Where(a => a.Automatic && (ownerClientId is null || a.Owner == ownerClientId))
                .ToList();
        }

        foreach (var target in targets)
        {
            target.RequestCancel(RoutineCancelledBy.Pause, fromPc);
            if (string.Equals(_countdown.ActiveRunId, target.RunId, StringComparison.Ordinal))
            {
                if (fromPc)
                {
                    _countdown.CancelActive(RoutineCancelledBy.Pause);
                }
                else
                {
                    _countdown.CancelForOwner(target.Owner, RoutineCancelledBy.Pause);
                }
            }
        }
    }

    /// <summary>Revoke / forget / block: cancels every run of that owner (T7).</summary>
    public void CancelOwner(string ownerClientId) => _ = CancelOwnerAsync(ownerClientId);

    /// <summary>
    /// <see cref="CancelOwner"/>, then waits (bounded) for those runs to write their final record. A forget
    /// awaits this BEFORE deleting history, or the cancelled run's last save would put a record straight
    /// back for an owner that no longer exists.
    /// </summary>
    public async Task CancelOwnerAsync(string ownerClientId)
    {
        List<ActiveRun> targets;
        lock (_gate)
        {
            targets = _active.Values.Where(a => a.Owner == ownerClientId).ToList();
        }

        foreach (var target in targets)
        {
            target.RequestCancel(RoutineCancelledBy.Pc, fromPc: true, RoutineReasonCodes.PcNotPaired);
            if (string.Equals(_countdown.ActiveRunId, target.RunId, StringComparison.Ordinal))
            {
                _countdown.CancelActive(RoutineCancelledBy.Pc);
            }
        }

        var pending = targets.Select(t => t.Completion).OfType<Task>().ToList();
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(OwnerCancelWait, _time);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("A cancelled routine run of a removed owner did not stop within {Wait}.", OwnerCancelWait);
        }
    }

    /// <summary><c>reasonArgs.detail</c> of a trigger edge the loop guard suppressed.</summary>
    public const string CausedByRunDetail = "caused_by_run";

    /// <summary>
    /// Records (saves and reports) a trigger that did not start a run, for a skip decided outside
    /// <see cref="StartAsync"/> (the loop guard, a sensor gone missing). Never throws.
    /// </summary>
    public async Task<RoutineRun?> RecordSkipAsync(RoutineRunStart start, string reason, string? detail, RoutineReasonArgs? args = null)
    {
        try
        {
            var routine = _store.Current.Owner(start.OwnerClientId)?.Find(start.RoutineId);
            var skipped = SkippedRecord(start, routine, reason) with
            {
                ReasonArgs = detail is null ? args : (args ?? new RoutineReasonArgs()) with { Detail = detail },
            };
            skipped = await _runs.UpsertAsync(skipped);
            await ReportAsync(skipped, live: false);
            _logger.LogInformation(
                "Routine trigger recorded as skipped ({Reason}, {Detail}) for {Owner}.",
                reason, detail, LogRedaction.RedactClientId(start.OwnerClientId));
            RaiseChanged();
            return skipped;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A skipped routine trigger could not be recorded.");
            return null;
        }
    }

    /// <summary>How long a forget waits for the owner's cancelled runs to stop (a launch step can take 15 s).</summary>
    public static readonly TimeSpan OwnerCancelWait = TimeSpan.FromSeconds(20);

    private async Task<RoutineRunHandle> StartCoreAsync(RoutineRunStart start)
    {
        var document = _store.Current;
        var owner = document.Owner(start.OwnerClientId);
        var routine = owner?.Find(start.RoutineId);
        var automatic = IsAutomatic(start.Source);
        var pcRunNow = start.Source == RoutineRunSources.ManualPcRunNow;

        string? skip = null;
        if (owner is null || routine is null)
        {
            skip = RoutineReasonCodes.RoutineNotFound;
        }
        else if (!_owners.IsPaired(start.OwnerClientId))
        {
            skip = RoutineReasonCodes.PcNotPaired;
        }
        else if (owner.BlockedByPc && !pcRunNow)
        {
            skip = RoutineReasonCodes.BlockedByPc;
        }
        else if (automatic && _store.IsSuspended(owner))
        {
            skip = RoutineReasonCodes.OwnerAbsent;
        }
        else if (owner.IsPcDisabled(routine.Id) && !pcRunNow)
        {
            skip = RoutineReasonCodes.DisabledOnPc;
        }
        else if (automatic && !routine.Enabled)
        {
            skip = RoutineReasonCodes.SkippedDisabled;
        }
        else if (automatic && document.HostPaused)
        {
            skip = RoutineReasonCodes.PausedOnPc;
        }
        else if (automatic && owner.Paused)
        {
            skip = RoutineReasonCodes.PausedOnPhone;
        }
        else if (!RoutineValidator.ValidateRoutine(routine).IsValid)
        {
            // §R-SEC-06: revalidated before it runs; the stored copy is never trusted blind.
            skip = RoutineReasonCodes.InvalidField;
        }

        ActiveRun? active = null;
        RoutineRun? record = null;
        if (skip is null)
        {
            lock (_gate)
            {
                skip = CheckLimitsLocked(start, automatic);
                if (skip is null)
                {
                    record = NewRunRecord(start, routine!);
                    active = new ActiveRun(record.RunId!, start.OwnerClientId, routine!.Id!, automatic);
                    _active[active.RunId] = active;
                    RecordStartLocked(start, automatic);
                }
            }
        }

        if (skip is not null)
        {
            var skipped = SkippedRecord(start, routine, skip);
            if (ShouldRecordSkip(start, skip))
            {
                skipped = await _runs.UpsertAsync(skipped);
                await ReportAsync(skipped, live: false);
            }

            _logger.LogInformation(
                "Routine run skipped ({Reason}) for {Owner}, source {Source}.",
                skip, LogRedaction.RedactClientId(start.OwnerClientId), start.Source);
            RaiseChanged();
            return new RoutineRunHandle(skipped, Task.FromResult(skipped));
        }

        // §8.1: the record exists and is saved BEFORE any step runs, so an interruption is visible.
        record = await _runs.UpsertAsync(record!);
        active!.Record = record;
        _logger.LogInformation(
            "Routine run {RunId} started for {Owner}, source {Source}{Test}.",
            record.RunId, LogRedaction.RedactClientId(start.OwnerClientId), start.Source, start.TestRun ? " [test]" : string.Empty);
        await ReportAsync(record, live: true);
        RaiseChanged();

        var completion = Task.Run(() => ExecuteAsync(start, routine!, active));
        active.Completion = completion;
        return new RoutineRunHandle(record, completion);
    }

    private async Task<RoutineRun> ExecuteAsync(RoutineRunStart start, Routine routine, ActiveRun active)
    {
        var steps = routine.Steps!;
        var finalized = false;
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                if (active.CancelRequested)
                {
                    return await FinishCancelledAsync(active, i, stepWasRunning: false);
                }

                // T6: a revoked owner's run stops here even between host steps.
                if (!_owners.IsPaired(active.Owner))
                {
                    return await FinishFailedAsync(active, i, RoutineReasonCodes.PcNotPaired, stepStatus: RoutineStepStatuses.Skipped);
                }

                var step = steps[i];
                UpdateStep(active, i, s => s with { Status = RoutineStepStatuses.Running, StartedAtUnixMs = NowMs() });
                var destructiveWithCountdown = step.IsDestructive && !start.PresenceConfirmed;
                if (destructiveWithCountdown)
                {
                    active.Record = active.Record with
                    {
                        Countdown = new RoutineRunCountdown { Shown = false, StartedAtUnixMs = NowMs() },
                    };
                }

                await ReportAsync(active.Record, live: true);
                RaiseChanged();

                var outcome = await RunStepAsync(start, routine, active, i, step, () => finalized = true);
                switch (outcome.Kind)
                {
                    case StepKind.Ok:
                        break;
                    case StepKind.Cancelled:
                        return await FinishCancelledAsync(active, i, stepWasRunning: true, outcome.CancelledBy, outcome.ReasonCode);
                    case StepKind.Skipped:
                        return await FinishFailedAsync(active, i, outcome.ReasonCode!, RoutineStepStatuses.Skipped, RoutineRunOutcomes.Skipped);
                    default:
                        return await FinishFailedAsync(active, i, outcome.ReasonCode!, RoutineStepStatuses.Failed);
                }

                if (i < steps.Count - 1)
                {
                    await ReportAsync(active.Record, live: true);
                    RaiseChanged();
                }
            }

            if (finalized)
            {
                // Already saved and reported as succeeded right before a destructive verb went out.
                return active.Record;
            }

            return await FinishAsync(active, active.Record with
            {
                Outcome = RoutineRunOutcomes.Succeeded,
                ReasonCode = RoutineReasonCodes.Ok,
                EndedAtUnixMs = NowMs(),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Routine run {RunId} failed unexpectedly.", active.RunId);
            var index = active.Record.Steps?.FindIndex(s => s.Status == RoutineStepStatuses.Running) ?? -1;
            return await FinishFailedAsync(active, Math.Max(0, index), RoutineReasonCodes.InternalError, RoutineStepStatuses.Failed);
        }
        finally
        {
            lock (_gate)
            {
                _active.Remove(active.RunId);
            }

            RaiseChanged();
        }
    }

    private async Task<StepResult> RunStepAsync(
        RoutineRunStart start, Routine routine, ActiveRun active, int index, RoutineStep step, Action markFinalized)
    {
        switch (step.Type)
        {
            case RoutineStepTypes.Delay:
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(step.Seconds ?? 0), _time, active.Token);
                }
                catch (OperationCanceledException)
                {
                    return StepResult.Cancelled(active.CancelledBy, active.CancelReason);
                }

                UpdateStep(active, index, s => s with { Status = RoutineStepStatuses.Succeeded, EndedAtUnixMs = NowMs(), ReasonCode = RoutineReasonCodes.Ok });
                return StepResult.Ok;

            case RoutineStepTypes.Notify when step.Target == RoutineNotifyTargets.Phone:
                return await NotifyPhoneAsync(routine, active, index, step);
        }

        // The loop guard (T9) is marked around the verb issue alone, by CausalityMarkingPowerExecutor; a
        // 15 s countdown here is NOT a window in which the person's own lock is ignored.
        var result = await _executor.ExecuteAsync(
            new RoutineStepExecution(
                active.Owner, active.RunId, routine.Id!, Name(routine), index, step, start.Source, start.TestRun,
                start.PresenceConfirmed,
                CountdownStarted: () => SendCountdownHeadsUpAsync(routine, active)),
            async early =>
            {
                // §7.3.4: the machine may be gone a moment from now. Record and report success first.
                ApplyStepResult(active, index, step, early, start.PresenceConfirmed);
                await FinishAsync(active, CompleteRemaining(active.Record, index) with
                {
                    Outcome = RoutineRunOutcomes.Succeeded,
                    ReasonCode = RoutineReasonCodes.Ok,
                    EndedAtUnixMs = NowMs(),
                });
                markFinalized();
            });

        ApplyStepResult(active, index, step, result, start.PresenceConfirmed);
        return result.Outcome switch
        {
            RoutineStepOutcomes.Succeeded or RoutineStepOutcomes.Simulated => StepResult.Ok,
            // A runner-side reason (pc_not_paired on revoke) outranks the countdown's side-derived one.
            RoutineStepOutcomes.Cancelled => StepResult.Cancelled(result.CancelledBy, active.CancelReason ?? result.ReasonCode),
            _ when result.ReasonCode == RoutineReasonCodes.ConflictCountdownActive => StepResult.Skipped(result.ReasonCode),
            _ => StepResult.Failed(result.ReasonCode ?? RoutineReasonCodes.InternalError),
        };
    }

    private async Task<StepResult> NotifyPhoneAsync(Routine routine, ActiveRun active, int index, RoutineStep step)
    {
        var title = RoutineText.Sanitize(step.Title);
        var body = RoutineText.Sanitize(step.Body);

        // §7.3.5: every notify is also shown on the PC.
        _ui.Notify(NotificationImportance.Outcome, title, body);

        var now = NowMs();
        var outcome = await _notifier.NotifyAsync(active.Owner, new RoutineNotifyPayload
        {
            NotifyId = Guid.NewGuid().ToString(),
            Kind = RoutineNotifyKinds.Step,
            RoutineId = routine.Id,
            RoutineName = Name(routine),
            RunId = active.RunId,
            Title = title,
            Body = body,
            QueuedAtUnixMs = now,
            ExpiresAtUnixMs = now + (long)RoutineNotifyQueue.Expiry.TotalMilliseconds,
        }, index);

        switch (outcome)
        {
            case RoutinePhoneNotifyOutcome.Delivered:
                UpdateStep(active, index, s => s with { Status = RoutineStepStatuses.Succeeded, EndedAtUnixMs = NowMs(), ReasonCode = RoutineReasonCodes.Ok });
                break;
            case RoutinePhoneNotifyOutcome.Queued:
                UpdateStep(active, index, s => s with { Status = RoutineStepStatuses.Succeeded, EndedAtUnixMs = NowMs(), ReasonCode = RoutineReasonCodes.Ok });
                AddAttribute(active, RoutineRunAttributes.NotifyQueued);
                break;
            default:
                // Not delivered and nothing holds it (a live-only notifier). Said as it is; the run goes on.
                UpdateStep(active, index, s => s with { Status = RoutineStepStatuses.Expired, EndedAtUnixMs = NowMs(), ReasonCode = RoutineReasonCodes.NotifyExpired });
                break;
        }

        return StepResult.Ok;
    }

    /// <summary>
    /// The phone mirror of a PC run's countdown (§8.6, R-UX-34): <c>routine_notify{kind: countdown}</c> to
    /// the owner, live only, sent when the PC countdown actually starts (never for a refused one). The
    /// phone's Cancel sends <c>routine_cancel</c> for this run. A phone step request never gets one: the
    /// phone already shows its own countdown for the step it sent. Never throws.
    /// </summary>
    private async Task SendCountdownHeadsUpAsync(Routine routine, ActiveRun active)
    {
        try
        {
            if (!_channel.CanReach(active.Owner))
            {
                return;
            }

            var now = NowMs();
            var name = Name(routine);
            await _notifier.NotifyAsync(active.Owner, new RoutineNotifyPayload
            {
                NotifyId = Guid.NewGuid().ToString(),
                Kind = RoutineNotifyKinds.Countdown,
                RoutineId = routine.Id,
                RoutineName = name,
                RunId = active.RunId,
                Title = RoutineText.Sanitize(name),
                Body = RoutineStrings.Format(
                    "Routine_Countdown_PhoneBody", (int)RoutineCountdownCoordinator.Length.TotalSeconds),
                CountdownEndsAtUnixMs = now + (long)RoutineCountdownCoordinator.Length.TotalMilliseconds,
                QueuedAtUnixMs = now,
                ExpiresAtUnixMs = now + (long)RoutineCountdownCoordinator.Length.TotalMilliseconds,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The countdown heads-up for run {RunId} could not be sent.", active.RunId);
        }
    }

    private void ApplyStepResult(ActiveRun active, int index, RoutineStep step, RoutineStepResultPayload result, bool presenceConfirmed)
    {
        var status = result.Outcome switch
        {
            RoutineStepOutcomes.Succeeded => RoutineStepStatuses.Succeeded,
            RoutineStepOutcomes.Simulated => RoutineStepStatuses.Simulated,
            RoutineStepOutcomes.Cancelled => RoutineStepStatuses.Cancelled,
            _ when result.ReasonCode == RoutineReasonCodes.ConflictCountdownActive => RoutineStepStatuses.Skipped,
            _ => RoutineStepStatuses.Failed,
        };

        UpdateStep(active, index, s => s with { Status = status, EndedAtUnixMs = NowMs(), ReasonCode = result.ReasonCode });

        if (result.Outcome == RoutineStepOutcomes.Simulated)
        {
            AddAttribute(active, RoutineRunAttributes.Simulated);
        }

        if (result.ReasonCode == RoutineReasonCodes.DryRun)
        {
            AddAttribute(active, RoutineRunAttributes.DryRun);
        }

        if (step.IsDestructive && !presenceConfirmed && result.ReasonCode != RoutineReasonCodes.ConflictCountdownActive)
        {
            if (!result.CountdownShown)
            {
                AddAttribute(active, RoutineRunAttributes.CountdownUnseen);
            }

            active.Record = active.Record with
            {
                Countdown = (active.Record.Countdown ?? new RoutineRunCountdown { StartedAtUnixMs = NowMs() }) with
                {
                    Shown = result.CountdownShown,
                    CancelledBy = result.CancelledBy,
                },
            };
        }
    }

    private async Task<RoutineRun> FinishCancelledAsync(
        ActiveRun active, int index, bool stepWasRunning, string? cancelledBy = null, string? reasonCode = null)
    {
        cancelledBy ??= active.CancelledBy ?? RoutineCancelledBy.Pc;
        reasonCode ??= active.CancelReason
            ?? (active.CancelledOnPc ? RoutineReasonCodes.CancelledOnPc : RoutineReasonCodes.CancelledOnPhone);
        var now = NowMs();
        var record = active.Record with
        {
            Outcome = RoutineRunOutcomes.Cancelled,
            ReasonCode = reasonCode,
            CancelledBy = cancelledBy,
            EndedAtUnixMs = now,
            // §8.6: the step that was cancelled and every step after it record "cancelled".
            Steps = active.Record.Steps?.Select(s =>
                s.Index < index || s.Status == RoutineStepStatuses.Cancelled
                    ? s
                    : s with
                    {
                        Status = RoutineStepStatuses.Cancelled,
                        EndedAtUnixMs = s.Index == index && stepWasRunning ? now : s.EndedAtUnixMs,
                    })
                .ToList(),
        };

        return await FinishAsync(active, record);
    }

    private async Task<RoutineRun> FinishFailedAsync(
        ActiveRun active, int index, string reasonCode, string stepStatus, string outcome = RoutineRunOutcomes.Failed)
    {
        var now = NowMs();
        var record = active.Record with
        {
            Outcome = outcome,
            ReasonCode = reasonCode,
            EndedAtUnixMs = now,
            Steps = active.Record.Steps?.Select(s =>
                s.Index < index ? s
                : s.Index == index ? s with { Status = s.Status is RoutineStepStatuses.Pending or RoutineStepStatuses.Running ? stepStatus : s.Status, ReasonCode = s.ReasonCode ?? reasonCode, EndedAtUnixMs = s.EndedAtUnixMs ?? now }
                : s with { Status = RoutineStepStatuses.Skipped })
                .ToList(),
        };

        return await FinishAsync(active, record);
    }

    private async Task<RoutineRun> FinishAsync(ActiveRun active, RoutineRun record)
    {
        active.Record = await _runs.UpsertAsync(record);
        _logger.LogInformation("Routine run {RunId} ended: {Outcome} ({Reason}).", active.RunId, record.Outcome, record.ReasonCode);
        await ReportAsync(active.Record, live: false);

        // A held message that expired while this run was still going is recorded now (S5 review): after
        // the final report, so the phone receives the newer record last.
        if (_notifier is IRoutineRunEndObserver observer)
        {
            try
            {
                await observer.RunEndedAsync(active.Record);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recording expired messages for run {RunId} failed.", active.RunId);
            }
        }

        RaiseChanged();
        return active.Record;
    }

    /// <summary>The final report goes to a connected owner; a disconnected one catches up by cursor (§7.3.6).</summary>
    private async Task ReportAsync(RoutineRun record, bool live)
    {
        if (record.OwnerClientId is { } owner && _channel.CanReach(owner))
        {
            await _channel.TrySendAsync(owner, RoutineMessages.Report([record], more: false, live));
        }
    }

    private string? CheckLimitsLocked(RoutineRunStart start, bool automatic)
    {
        if (_active.Values.Any(a => a.Owner == start.OwnerClientId && a.RoutineId == start.RoutineId))
        {
            return RoutineReasonCodes.AlreadyRunning;
        }

        var now = _time.GetTimestamp();
        if (automatic
            && _lastAutomaticStart.TryGetValue((start.OwnerClientId, start.RoutineId), out var last)
            && _time.GetElapsedTime(last, now) < AutomaticMinInterval)
        {
            return RoutineReasonCodes.Cooldown;
        }

        var window = OwnerWindowLocked(start.OwnerClientId, now);
        if (window.Count >= MaxRunsPerOwnerPerHour || _active.Count >= MaxConcurrentRuns)
        {
            return RoutineReasonCodes.RateLimited;
        }

        return null;
    }

    private void RecordStartLocked(RoutineRunStart start, bool automatic)
    {
        var now = _time.GetTimestamp();
        OwnerWindowLocked(start.OwnerClientId, now).Enqueue(now);
        if (automatic)
        {
            _lastAutomaticStart[(start.OwnerClientId, start.RoutineId)] = now;
        }
    }

    private Queue<long> OwnerWindowLocked(string owner, long now)
    {
        if (!_ownerStarts.TryGetValue(owner, out var window))
        {
            window = new Queue<long>();
            _ownerStarts[owner] = window;
        }

        while (window.Count > 0 && _time.GetElapsedTime(window.Peek(), now) >= TimeSpan.FromHours(1))
        {
            window.Dequeue();
        }

        return window;
    }

    /// <summary>Skip records are coalesced so a flapping or paused trigger cannot flood the history.</summary>
    private bool ShouldRecordSkip(RoutineRunStart start, string reason)
    {
        var window = reason switch
        {
            RoutineReasonCodes.AlreadyRunning or RoutineReasonCodes.Cooldown => AlreadyRunningCoalesce,
            RoutineReasonCodes.PausedOnPc or RoutineReasonCodes.PausedOnPhone
                or RoutineReasonCodes.SkippedDisabled or RoutineReasonCodes.OwnerAbsent
                or RoutineReasonCodes.DisabledOnPc or RoutineReasonCodes.BlockedByPc => PauseSkipCoalesce,
            _ => TimeSpan.Zero,
        };

        if (window == TimeSpan.Zero || !IsAutomatic(start.Source))
        {
            return true;
        }

        lock (_gate)
        {
            var key = (start.OwnerClientId, start.RoutineId, reason);
            var now = _time.GetTimestamp();
            if (_lastSkipRecord.TryGetValue(key, out var last) && _time.GetElapsedTime(last, now) < window)
            {
                return false;
            }

            _lastSkipRecord[key] = now;
            return true;
        }
    }

    private RoutineRun NewRunRecord(RoutineRunStart start, Routine routine)
    {
        var now = NowMs();
        return new RoutineRun
        {
            RunId = start.RunId ?? Guid.NewGuid().ToString(),
            OwnerClientId = start.OwnerClientId,
            RoutineId = routine.Id,
            RoutineName = Name(routine),
            RoutineRevision = routine.Revision,
            Origin = RoutineRunOrigins.Pc,
            HostIdentity = _hostIdentity(),
            Source = start.Source,
            TestRun = start.TestRun,
            SourceDetail = start.Detail,
            TriggeredAtUnixMs = now,
            StartedAtUnixMs = now,
            Outcome = RoutineRunOutcomes.Running,
            Attributes = [],
            Steps = routine.Steps!.Select((s, i) => new RoutineRunStep
            {
                Index = i,
                Kind = s.Type,
                Status = RoutineStepStatuses.Pending,
            }).ToList(),
        };
    }

    private RoutineRun SkippedRecord(RoutineRunStart start, Routine? routine, string reason)
    {
        var now = NowMs();
        return new RoutineRun
        {
            RunId = start.RunId ?? Guid.NewGuid().ToString(),
            OwnerClientId = start.OwnerClientId,
            RoutineId = start.RoutineId,
            RoutineName = routine is null ? string.Empty : Name(routine),
            RoutineRevision = routine?.Revision ?? 0,
            Origin = RoutineRunOrigins.Pc,
            HostIdentity = _hostIdentity(),
            Source = start.Source,
            TestRun = start.TestRun,
            SourceDetail = start.Detail,
            TriggeredAtUnixMs = now,
            StartedAtUnixMs = now,
            EndedAtUnixMs = now,
            Outcome = RoutineRunOutcomes.Skipped,
            ReasonCode = reason,
            Attributes = [],
            Steps = [],
        };
    }

    private static RoutineRun CompleteRemaining(RoutineRun record, int index) => record with
    {
        Steps = record.Steps?.Select(s => s.Index > index && s.Status == RoutineStepStatuses.Pending
            ? s with { Status = RoutineStepStatuses.Skipped }
            : s).ToList(),
    };

    private static void UpdateStep(ActiveRun active, int index, Func<RoutineRunStep, RoutineRunStep> change)
    {
        var steps = active.Record.Steps ?? [];
        active.Record = active.Record with
        {
            Steps = steps.Select(s => s.Index == index ? change(s) : s).ToList(),
        };
    }

    private static void AddAttribute(ActiveRun active, string attribute)
    {
        var attributes = active.Record.Attributes ?? [];
        if (!attributes.Contains(attribute, StringComparer.Ordinal))
        {
            active.Record = active.Record with { Attributes = [.. attributes, attribute] };
        }
    }

    private static bool IsAutomatic(string source) =>
        source is RoutineRunSources.PcIdle or RoutineRunSources.PcSession or RoutineRunSources.PcSensor
            or RoutineRunSources.HomeArrive or RoutineRunSources.HomeLeave;

    private static string Name(Routine routine)
    {
        var clean = RoutineText.Sanitize(routine.Name);
        return clean.Length <= RoutineLimits.MaxNameLength ? clean : clean[..RoutineLimits.MaxNameLength];
    }

    private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A routine run listener failed.");
        }
    }

    private enum StepKind
    {
        Ok,
        Failed,
        Cancelled,
        Skipped,
    }

    private readonly record struct StepResult(StepKind Kind, string? ReasonCode, string? CancelledBy)
    {
        public static StepResult Ok => new(StepKind.Ok, null, null);

        public static StepResult Failed(string reason) => new(StepKind.Failed, reason, null);

        public static StepResult Skipped(string? reason) => new(StepKind.Skipped, reason, null);

        public static StepResult Cancelled(string? by, string? reason) => new(StepKind.Cancelled, reason, by);
    }

    private sealed class ActiveRun(string runId, string owner, string routineId, bool automatic)
    {
        private readonly CancellationTokenSource _cts = new();
        private volatile RoutineRun _record = new();

        public string RunId { get; } = runId;

        public string Owner { get; } = owner;

        public string RoutineId { get; } = routineId;

        public bool Automatic { get; } = automatic;

        public RoutineRun Record
        {
            get => _record;
            set => _record = value;
        }

        public CancellationToken Token => _cts.Token;

        /// <summary>The background execution, once started.</summary>
        public Task<RoutineRun>? Completion { get; set; }

        public bool CancelRequested => _cts.IsCancellationRequested;

        public string? CancelledBy { get; private set; }

        public bool CancelledOnPc { get; private set; }

        /// <summary>An explicit run reason (<c>pc_not_paired</c> on revoke), overriding the side-derived one.</summary>
        public string? CancelReason { get; private set; }

        public void RequestCancel(string cancelledBy, bool fromPc, string? reason = null)
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            CancelledBy = cancelledBy;
            CancelledOnPc = fromPc;
            CancelReason = reason ?? (fromPc ? RoutineReasonCodes.CancelledOnPc : RoutineReasonCodes.CancelledOnPhone);
            _cts.Cancel();
        }
    }
}

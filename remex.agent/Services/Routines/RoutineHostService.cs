using Microsoft.Extensions.Hosting;
using Remex.Agent.Services.Media;
using Remex.Agent.Services.Security;
using Remex.Core.Models;
using Remex.Core.Routines;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Routines;

namespace Remex.Agent.Services.Routines;

/// <summary>What the revoke flow needs from routines (T7, <c>PairedDeviceRevoker.RevokeAsync</c>).</summary>
public interface IRoutineOwnerLifecycle
{
    /// <summary>
    /// Deletes one owner's routines and run history and cancels its active runs (<c>pc_not_paired</c>).
    /// Throws when the definitions could not be removed from disk, so the revocation reports itself
    /// incomplete rather than leaving a revoked phone's routines to run after the next start.
    /// </summary>
    Task ForgetOwnerAsync(string clientId);
}

/// <summary>
/// The PC-run routines host (routines spec §8.4 <c>RoutineHostService</c>): loads the stores, resolves the
/// idle and session sources, arms the triggers from the stored routines, and is the backend of the PC
/// Routines page (<see cref="IRoutinesHost"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>ARMED = STORED, ENABLED ON THE PHONE, NOT DISABLED ON THE PC, OWNER NOT BLOCKED, NOT SUSPENDED, AND
/// NOTHING PAUSED (§8.4 state machine).</b> Re-armed on every store change. The runner re-checks all of it
/// when a trigger fires, so arming is what keeps idle polling off when nothing needs it, not the safety gate.
/// </para>
/// <para>
/// The session source is started for the life of the process even with no <c>pc.session</c> routine: it
/// is event-driven and cheap, it answers the countdown's "is the PC locked" question on Linux
/// (<see cref="SessionLockProbe"/>), and its id is what the phone's editor gates the trigger on.
/// </para>
/// </remarks>
public sealed class RoutineHostService : BackgroundService, IRoutinesHost, IRoutineOwnerLifecycle
{
    private readonly RoutineHostStore _store;
    private readonly RoutineRunStore _runs;
    private readonly RoutineHostRunner _runner;
    private readonly RoutineSyncHandler _sync;
    private readonly RoutineTriggerAvailability _availability;
    private readonly IRoutinePlatformSources _platform;
    private readonly RoutineCountdownCoordinator _countdown;
    private readonly IRoutineOwnerDirectory _owners;
    private readonly RoutineDryRunMode _dryRun;
    private readonly IRoutineUi _ui;
    private readonly ILogger _logger;
    private readonly IdleTriggerSource _idle;
    private readonly SessionTriggerSource _session;
    private readonly List<string> _warnings = [];
    private readonly object _gate = new();
    private readonly RoutineHostReadiness _readiness;
    private ISessionStateSource? _sessionSource;
    private IIdleSource? _idleSource;

    public RoutineHostService(
        RoutineHostStore store,
        RoutineRunStore runs,
        RoutineHostRunner runner,
        RoutineSyncHandler sync,
        RoutineTriggerAvailability availability,
        IRoutinePlatformSources platform,
        RoutineCountdownCoordinator countdown,
        IRoutineOwnerDirectory owners,
        RoutineDryRunMode dryRun,
        IRoutineUi ui,
        RoutineCausality causality,
        IMediaSessionMonitor media,
        RoutineHostReadiness readiness,
        TimeProvider time,
        ILoggerFactory loggers)
    {
        _readiness = readiness;
        _store = store;
        _runs = runs;
        _runner = runner;
        _sync = sync;
        _availability = availability;
        _platform = platform;
        _countdown = countdown;
        _owners = owners;
        _dryRun = dryRun;
        _ui = ui;
        _logger = loggers.CreateLogger<RoutineHostService>();
        _idle = new IdleTriggerSource(
            time, () => media.Current?.Status == MediaPlaybackStatus.Playing, loggers.CreateLogger<IdleTriggerSource>());
        _session = new SessionTriggerSource(time, causality, loggers.CreateLogger<SessionTriggerSource>());
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>The idle rule, exposed for tests.</summary>
    internal IdleTriggerSource Idle => _idle;

    /// <summary>The session rule, exposed for tests.</summary>
    internal SessionTriggerSource Session => _session;

    /// <summary>
    /// Loads the stores, sweeps interrupted runs, starts the sources and arms. Separate from
    /// <see cref="ExecuteAsync"/> so tests can run it with fake sources.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        _store.Load();
        await _runs.LoadAndSweepAsync();

        if (_store.LoadWarning is not null)
        {
            // Also covers a file that could not even be set aside: the store then refuses every save
            // (internal_error to the phone) until it can, so the original is never overwritten.
            Warn(RoutineStrings.Format("Routine_Warning_StoreUnreadable"));
        }

        if (_runs.LoadWarning is not null)
        {
            Warn(RoutineStrings.Format("Routine_Warning_HistoryUnreadable"));
        }

        _store.Changed += OnStoreChanged;
        _runner.Changed += RaiseChanged;
        _idle.Fired += OnFired;
        _session.Fired += OnFired;
        _session.Suppressed += OnSuppressed;

        var idleSource = await _platform.ResolveIdleAsync(ct);
        lock (_gate)
        {
            _idleSource = idleSource;
        }

        _idle.SetSource(idleSource);
        _availability.SetIdleSource(idleSource?.Id);

        var sessionSource = await _platform.ResolveSessionAsync(ct);
        if (sessionSource is not null)
        {
            _session.SetInitialState(sessionSource.IsLocked);
            _availability.SetSessionLocked(sessionSource.IsLocked);
            sessionSource.Changed += OnSessionEdge;
            lock (_gate)
            {
                _sessionSource = sessionSource;
            }
        }

        _availability.SetSessionSource(sessionSource?.Id);
        Rearm();

        // Only now may a sync or run request touch state (RoutineHostReadiness). A start that failed
        // above never gets here, and every routine message is answered "try again" instead.
        _readiness.MarkReady();
        RaiseChanged();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await InitializeAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // A routines failure must never take the host down with it.
            _logger.LogError(ex, "Routines failed to start; PC-run routines are unavailable this session.");
        }
    }

    public override void Dispose()
    {
        _idle.Dispose();
        _session.Dispose();
        lock (_gate)
        {
            _sessionSource?.Dispose();
            _sessionSource = null;

            // The idle source may hold a D-Bus connection (Mutter, ScreenSaver, logind) or an X display.
            (_idleSource as IDisposable)?.Dispose();
            _idleSource = null;
        }

        base.Dispose();
    }

    /// <inheritdoc />
    public RoutinesHostSnapshot GetSnapshot()
    {
        var document = _store.Current;
        var owners = new List<RoutineOwnerView>();
        foreach (var (clientId, owner) in document.Owners ?? [])
        {
            owners.Add(new RoutineOwnerView(
                clientId,
                _owners.DisplayName(clientId),
                owner.Paused,
                owner.BlockedByPc,
                _store.IsSuspended(owner),
                owner.LastSeenUnixMs,
                (owner.Routines ?? [])
                    .Select(r => new RoutineHostEntry(r, owner.IsPcDisabled(r.Id), _runner.IsRunning(clientId, r.Id!)))
                    .ToList()));
        }

        List<string> warnings;
        lock (_gate)
        {
            warnings = [.. _warnings];
        }

        return new RoutinesHostSnapshot(
            document.HostPaused, _dryRun.IsEnabled, _availability.IdleSourceId, _availability.SessionSourceId, owners, warnings);
    }

    /// <inheritdoc />
    public IReadOnlyList<RoutineRun> GetHistory(string? ownerClientId = null, string? routineId = null) =>
        _runs.Query(ownerClientId, routineId);

    /// <inheritdoc />
    public async Task SetHostPausedAsync(bool paused)
    {
        var (saved, _) = await _store.UpdateAsync(doc => (doc.HostPaused == paused ? null : doc with { HostPaused = paused }, 0));
        ThrowIfNotSaved(saved);
        if (paused)
        {
            // §8.7: a countdown in progress is cancelled from the PC side, and automatic runs stop.
            _countdown.CancelActive(RoutineCancelledBy.Pause);
            _runner.CancelAutomaticRuns(null, fromPc: true);
        }

        await _sync.SendUnsolicitedToAllAsync();
    }

    /// <inheritdoc />
    public async Task SetDisabledOnPcAsync(string ownerClientId, string routineId, bool disabled)
    {
        var (saved, _) = await _store.UpdateAsync(doc =>
        {
            if (doc.Owner(ownerClientId) is not { } owner || owner.Find(routineId) is null || owner.IsPcDisabled(routineId) == disabled)
            {
                return (null, 0);
            }

            var ids = (owner.PcDisabled ?? []).Where(id => !string.Equals(id, routineId, StringComparison.Ordinal)).ToList();
            if (disabled)
            {
                ids.Add(routineId);
            }

            return (doc.WithOwner(ownerClientId, owner with { PcDisabled = ids }), 0);
        });
        ThrowIfNotSaved(saved);
        await _sync.SendUnsolicitedAsync(ownerClientId);
    }

    /// <inheritdoc />
    public async Task SetBlockedAsync(string ownerClientId, bool blocked)
    {
        var (saved, _) = await _store.UpdateAsync(doc =>
            doc.Owner(ownerClientId) is { } owner && owner.BlockedByPc != blocked
                ? (doc.WithOwner(ownerClientId, owner with { BlockedByPc = blocked }), 0)
                : (null, 0));
        ThrowIfNotSaved(saved);
        if (blocked)
        {
            _runner.CancelOwner(ownerClientId);
        }

        await _sync.SendUnsolicitedAsync(ownerClientId);
    }

    /// <inheritdoc />
    public async Task<RoutineRun> RunNowAsync(string ownerClientId, string routineId, bool presenceConfirmed)
    {
        var handle = await _runner.StartAsync(new RoutineRunStart(
            ownerClientId, routineId, RoutineRunSources.ManualPcRunNow, TestRun: false, PresenceConfirmed: presenceConfirmed));
        return handle.Initial;
    }

    /// <inheritdoc />
    public bool CancelRun(string runId) => _runner.CancelFromPc(runId);

    /// <inheritdoc />
    public async Task ForgetOwnerAsync(string clientId)
    {
        await _runner.CancelOwnerAsync(clientId);
        var (saved, _) = await _store.UpdateAsync(doc => (doc.Owner(clientId) is null ? null : doc.WithOwner(clientId, null), 0));
        ThrowIfNotSaved(saved);
        await _runs.ForgetOwnerAsync(clientId);
        _logger.LogInformation("Routines and routine history cleared for {ClientId}.", LogRedaction.RedactClientId(clientId));
    }

    private void OnStoreChanged()
    {
        Rearm();
        RaiseChanged();
    }

    private void OnSessionEdge(bool locked)
    {
        _availability.SetSessionLocked(locked);
        _session.OnEdge(locked);
    }

    private void OnFired(RoutineTriggerFire fire)
    {
        _ = _runner.StartAsync(new RoutineRunStart(fire.OwnerClientId, fire.RoutineId, fire.Source, Detail: fire.Detail));
    }

    /// <summary>
    /// An edge the loop guard suppressed is recorded, never dropped in silence (§8.1, "nothing fails
    /// silently", §10). The v1 catalog has no dedicated code, so it is <c>flap_suppressed</c> with the detail
    /// <c>caused_by_run</c>.
    /// </summary>
    private void OnSuppressed(RoutineTriggerFire fire)
    {
        _ = _runner.RecordSkipAsync(
            new RoutineRunStart(fire.OwnerClientId, fire.RoutineId, fire.Source, Detail: fire.Detail),
            RoutineReasonCodes.FlapSuppressed,
            RoutineHostRunner.CausedByRunDetail);
    }

    /// <summary>Recomputes which <c>pc.idle</c> / <c>pc.session</c> routines are armed.</summary>
    internal void Rearm()
    {
        var document = _store.Current;
        var idle = new List<IdleArming>();
        var session = new List<SessionArming>();
        if (!document.HostPaused)
        {
            foreach (var (clientId, owner) in document.Owners ?? [])
            {
                if (owner.BlockedByPc || owner.Paused || _store.IsSuspended(owner))
                {
                    continue;
                }

                foreach (var routine in owner.Routines ?? [])
                {
                    if (!routine.Enabled || owner.IsPcDisabled(routine.Id) || routine.Id is null || routine.Trigger is not { } trigger)
                    {
                        continue;
                    }

                    if (trigger.Type == RoutineTriggerTypes.PcIdle && trigger.IdleMinutes is { } minutes)
                    {
                        idle.Add(new IdleArming(clientId, routine.Id, minutes, trigger.IgnoreWhileMediaPlaying ?? true));
                    }
                    else if (trigger.Type == RoutineTriggerTypes.PcSession && RoutineSessionStates.IsKnown(trigger.SessionState))
                    {
                        session.Add(new SessionArming(clientId, routine.Id, trigger.SessionState == RoutineSessionStates.Locked));
                    }
                }
            }
        }

        _idle.SetArmed(idle);
        _session.SetArmed(session);
    }

    private void Warn(string message)
    {
        lock (_gate)
        {
            _warnings.Add(message);
        }

        try
        {
            _ui.Notify(NotificationImportance.Problem, RoutineStrings.Format("Routine_Warning_Title"), message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The routines warning could not be shown.");
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A routines page listener failed.");
        }
    }

    private static void ThrowIfNotSaved(bool saved)
    {
        if (!saved)
        {
            throw new IOException("The routines file could not be saved; nothing was changed.");
        }
    }
}

/// <summary>
/// The phone-facing routine messages this slice adds to <c>PingPongHandler</c>: <c>routines_sync</c>,
/// <c>routine_run_request</c>, and the host-run half of <c>routine_cancel</c> (routines spec §7.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>NO WIRE FIELD REACHES <c>presenceConfirmed</c> (T21).</b> A run request always starts with it false, so
/// its destructive step counts down on the PC: a phone request is never presence at the PC (D1).
/// </para>
/// <para>
/// <b>THE PC RUNS ITS OWN STORED COPY (T24).</b> A run request names a routine id of the SENDER's stored
/// set; nothing in it can supply a definition.
/// </para>
/// </remarks>
public sealed class RoutineHostMessageHandler
{
    public static readonly TimeSpan RunRequestDedup = TimeSpan.FromMinutes(10);

    private readonly RoutineSyncHandler _sync;
    private readonly RoutineHostRunner _runner;
    private readonly RoutineRunStore _runs;
    private readonly RoutineHostReadiness _readiness;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<(string ClientId, string RunId), long> _seenRunIds = new();

    public RoutineHostMessageHandler(
        RoutineSyncHandler sync,
        RoutineHostRunner runner,
        RoutineRunStore runs,
        RoutineHostReadiness readiness,
        TimeProvider time,
        ILogger<RoutineHostMessageHandler> logger)
    {
        _sync = sync;
        _runner = runner;
        _runs = runs;
        _readiness = readiness;
        _time = time;
        _logger = logger;
    }

    /// <summary><c>routines_sync</c> from the proven client. Never throws.</summary>
    public Task HandleSyncAsync(string clientId, Remex.Core.Messages.Routines.RoutinesSyncPayload? payload, Func<Remex.Core.Messages.RemexMessage, Task> send) =>
        _sync.HandleSyncAsync(clientId, payload, send);

    /// <summary>
    /// <c>routine_run_request</c> from the proven client (§7.3.8): answered with a live
    /// <c>routine_run_report</c> of the started run, or of the skipped record. Never throws.
    /// </summary>
    public async Task HandleRunRequestAsync(
        string clientId, Remex.Core.Messages.Routines.RoutineRunRequestPayload? request, Func<Remex.Core.Messages.RemexMessage, Task> send)
    {
        try
        {
            if (request is null || !Guid.TryParse(request.RunId, out _) || string.IsNullOrWhiteSpace(request.RoutineId))
            {
                _logger.LogWarning("Ignored a routine_run_request without a valid runId/routineId from {ClientId}.", LogRedaction.RedactClientId(clientId));
                return;
            }

            var runId = request.RunId!;

            // Ownership FIRST, before anything is remembered or answered (T17): a run id that belongs to
            // another phone is refused outright, on the first request and on every resend, so no reply can
            // ever carry that phone's record.
            if (_runs.Find(runId) is { } clash && !string.Equals(clash.OwnerClientId, clientId, StringComparison.Ordinal))
            {
                _logger.LogWarning("Refused a routine_run_request reusing another owner's run id from {ClientId}.", LogRedaction.RedactClientId(clientId));
                return;
            }

            if (!await _readiness.WaitAsync(_time))
            {
                // Before start-up finished nothing may run; the phone may simply ask again.
                await SendAsync(send, NotReady(clientId, request));
                return;
            }

            RoutineRun? existing;
            lock (_gate)
            {
                Prune();
                existing = _seenRunIds.ContainsKey((clientId, runId)) ? _runs.Find(runId) : null;
                _seenRunIds[(clientId, runId)] = _time.GetTimestamp();
            }

            if (existing is not null && string.Equals(existing.OwnerClientId, clientId, StringComparison.Ordinal))
            {
                // A resend: the same run's record, never a second run (idempotent for 10 minutes).
                await SendAsync(send, existing);
                return;
            }

            var handle = await _runner.StartAsync(new RoutineRunStart(
                clientId,
                request.RoutineId!,
                RoutineRunSources.ManualApp,
                TestRun: request.TestRun,
                PresenceConfirmed: false,
                RunId: runId));
            await SendAsync(send, handle.Initial);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "routine_run_request from {ClientId} failed unexpectedly.", LogRedaction.RedactClientId(clientId));
        }
    }

    /// <summary>The host-run half of <c>routine_cancel</c>: the sender's own running host run (T17).</summary>
    public bool HandleCancel(string clientId, Remex.Core.Messages.Routines.RoutineCancelPayload? cancel)
    {
        if (cancel is null || string.IsNullOrWhiteSpace(cancel.RunId))
        {
            return false;
        }

        var by = string.Equals(cancel.Reason, RoutineCancelReasons.Pause, StringComparison.Ordinal)
            ? RoutineCancelledBy.Pause
            : RoutineCancelledBy.Phone;
        return _runner.CancelFromPhone(clientId, cancel.RunId, by);
    }

    /// <summary>The answer to a run request that arrived before start-up finished: skipped, retryable, not stored.</summary>
    private RoutineRun NotReady(string clientId, Remex.Core.Messages.Routines.RoutineRunRequestPayload request)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        return new RoutineRun
        {
            RunId = request.RunId,
            OwnerClientId = clientId,
            RoutineId = request.RoutineId,
            Origin = RoutineRunOrigins.Pc,
            Source = RoutineRunSources.ManualApp,
            TestRun = request.TestRun,
            TriggeredAtUnixMs = now,
            StartedAtUnixMs = now,
            EndedAtUnixMs = now,
            Outcome = RoutineRunOutcomes.Skipped,
            ReasonCode = RoutineReasonCodes.RateLimited,
            Attributes = [],
            Steps = [],
        };
    }

    private async Task SendAsync(Func<Remex.Core.Messages.RemexMessage, Task> send, RoutineRun record)
    {
        try
        {
            await send(RoutineMessages.Report([record], more: false, live: true));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The run request answer could not be sent.");
        }
    }

    private void Prune()
    {
        var now = _time.GetTimestamp();
        foreach (var key in _seenRunIds.Where(kv => _time.GetElapsedTime(kv.Value, now) >= RunRequestDedup).Select(kv => kv.Key).ToList())
        {
            _seenRunIds.Remove(key);
        }
    }
}

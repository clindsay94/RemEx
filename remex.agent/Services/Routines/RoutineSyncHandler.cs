using System.Security.Cryptography;
using System.Text.Json;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;

namespace Remex.Agent.Services.Routines;

/// <summary>
/// Set once <c>RoutineHostService.InitializeAsync</c> has loaded both stores and probed the trigger sources.
/// Every phone-facing routine handler waits on it (bounded) before touching state.
/// </summary>
public sealed class RoutineHostReadiness
{
    /// <summary>How long a message waits for start-up before it is answered "try again".</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _ready.Task.IsCompleted;

    public void MarkReady() => _ready.TrySetResult();

    /// <summary>True once ready; false when <see cref="MaxWait"/> passed first.</summary>
    public async Task<bool> WaitAsync(TimeProvider time)
    {
        if (_ready.Task.IsCompleted)
        {
            return true;
        }

        try
        {
            await _ready.Task.WaitAsync(MaxWait, time);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

/// <summary>
/// The host end of <c>routines_sync</c> (routines spec §7.3.1, §7.3.2, §7.4.2) and every
/// <c>routine_sync_result</c> the PC sends, solicited or not.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE ORDER IS THE ALGORITHM (§7.4.2).</b> Size, then block, then schema version, then forget, then the
/// revision rules (older = <c>stale_revision</c>; equal with the same content = the stored result again;
/// equal with different content = <c>revision_conflict</c>), then per-routine validation, then an atomic
/// save, and only then the reply. A save that fails answers <c>internal_error</c> and keeps the old set in
/// force: the phone is never told <c>ok</c> for a set that is not on disk.
/// </para>
/// <para>
/// <b>A REJECTED ROUTINE IS NOT STORED, AND ITS OLD VERSION GOES TOO.</b> The PC never runs a definition
/// the phone no longer holds.
/// </para>
/// <para>
/// <b>AT MOST ONE SYNC PER 2 s PER PHONE, AND THE LATEST WINS.</b> A burst of edits is coalesced: the ones
/// in between are superseded by a higher revision of the same full state and get no reply, which is what
/// the phone's revision tracking expects (§7.4.1).
/// </para>
/// <para>
/// Never throws, and never closes the socket for a bad sync (§7.3.1): every failure is a result.
/// </para>
/// </remarks>
public sealed class RoutineSyncHandler
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    private readonly RoutineHostStore _store;
    private readonly RoutineRunStore _runs;
    private readonly RoutineHostValidator _validator;
    private readonly RoutineHostRunner _runner;
    private readonly RoutineCountdownCoordinator _countdown;
    private readonly RoutineTriggerAvailability _availability;
    private readonly IRoutinePhoneChannel _channel;
    private readonly RoutineHostReadiness _readiness;
    private readonly IRoutineOwnerDirectory _owners;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly RoutineNotifyQueue? _notifyQueue;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <param name="notifyQueue">
    /// The S5 notify queue, flushed right after the sync result of an accepted sync (§7.3.5). Null: no
    /// queue (the pre-S5 live-only notifier), nothing to flush.
    /// </param>
    public RoutineSyncHandler(
        RoutineHostStore store,
        RoutineRunStore runs,
        RoutineHostValidator validator,
        RoutineHostRunner runner,
        RoutineCountdownCoordinator countdown,
        RoutineTriggerAvailability availability,
        IRoutinePhoneChannel channel,
        RoutineHostReadiness readiness,
        IRoutineOwnerDirectory owners,
        TimeProvider time,
        ILogger<RoutineSyncHandler> logger,
        RoutineNotifyQueue? notifyQueue = null)
    {
        _notifyQueue = notifyQueue;
        _store = store;
        _runs = runs;
        _validator = validator;
        _runner = runner;
        _countdown = countdown;
        _availability = availability;
        _channel = channel;
        _readiness = readiness;
        _owners = owners;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Reason codes that only mean "this PC's trigger source was not found YET" (the D-Bus probes finish
    /// after start; a sensor can appear once its driver loads). A stored result carrying one is never
    /// replayed for an equal revision: the set is validated again, so a sync that raced the probes cannot
    /// keep a routine rejected for good.
    /// </summary>
    internal static bool IsTransientRejection(RoutineSyncItemResult result) =>
        !result.Accepted
        && result.ReasonCode is RoutineReasonCodes.IdleSourceUnavailable or RoutineReasonCodes.SessionSourceUnavailable
            or RoutineReasonCodes.SensorUnavailable;

    /// <summary>
    /// Handles one <c>routines_sync</c> from the proven client <paramref name="clientId"/>, replying through
    /// <paramref name="send"/>. Returns when this sync (or the later one that superseded it) is answered.
    /// </summary>
    public async Task HandleSyncAsync(string clientId, RoutinesSyncPayload? payload, Func<RemexMessage, Task> send)
    {
        try
        {
            // §7.4.2 step 2: size first, and answered at once; a 1 MB sync must not sit in the coalescing slot.
            if (payload is not null && SerializedSize(payload) > RoutineLimits.MaxSyncPayloadBytes)
            {
                await ReplyAsync(send, Result(clientId, payload.Revision, RoutineSyncStatuses.PayloadTooLarge));
                return;
            }

            // NOTHING IS APPLIED BEFORE THE STORE IS LOADED AND THE SOURCES ARE PROBED. A sync that beat
            // Load() would save an empty document over every other owner (and their block flags); one that
            // beat the probes would reject pc.idle / pc.session. Past the bounded wait the phone is told to
            // try again (rate_limited) and nothing is validated, stored or rejected.
            if (!await _readiness.WaitAsync(_time))
            {
                _logger.LogWarning("routines_sync from {ClientId} arrived before routines were ready; asked to retry.", LogRedaction.RedactClientId(clientId));
                await ReplyAsync(send, Result(clientId, payload?.Revision ?? 0, RoutineSyncStatuses.RateLimited));
                return;
            }

            Pending slot;
            bool startWorker;
            lock (_gate)
            {
                if (_pending.TryGetValue(clientId, out var existing))
                {
                    slot = existing;
                }
                else
                {
                    slot = new Pending();
                    _pending[clientId] = slot;
                }

                slot.Next = (payload, send);
                startWorker = !slot.Working;
                slot.Working = true;
            }

            if (startWorker)
            {
                try
                {
                    await DrainAsync(clientId, slot);
                }
                catch
                {
                    // Never leave the slot marked busy: every later sync from this phone would queue forever.
                    lock (_gate)
                    {
                        slot.Working = false;
                        slot.Next = null;
                    }

                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "routines_sync from {ClientId} failed unexpectedly.", LogRedaction.RedactClientId(clientId));
            await ReplyAsync(send, Result(clientId, payload?.Revision ?? 0, RoutineSyncStatuses.InternalError));
        }
    }

    /// <summary>
    /// Sends the PC's current state to a connected owner without being asked (§7.3.2 <c>unsolicited</c>): the
    /// enable toggle, block and Pause all reach the phone at once (R-UX-37).
    /// </summary>
    public async Task SendUnsolicitedAsync(string clientId)
    {
        if (!_channel.CanReach(clientId) || _store.Current.Owner(clientId) is not { } owner)
        {
            return;
        }

        // A blocked phone must hear "blocked", not the ok/partial of its last accepted sync (§7.4.1 step 3:
        // blocked_by_pc is what stops the phone and surfaces the state).
        var result = owner.BlockedByPc
            ? Result(clientId, owner.Revision, RoutineSyncStatuses.BlockedByPc) with { Unsolicited = true }
            : Result(clientId, owner.Revision, StatusOf(owner.LastResults)) with
            {
                Results = owner.LastResults ?? [],
                Unsolicited = true,
            };
        await _channel.TrySendAsync(clientId, RoutineMessages.SyncResult(result));
    }

    /// <summary><see cref="SendUnsolicitedAsync"/> for every owner (PC Pause all is PC-wide).</summary>
    public async Task SendUnsolicitedToAllAsync()
    {
        foreach (var clientId in (_store.Current.Owners ?? []).Keys.ToList())
        {
            await SendUnsolicitedAsync(clientId);
        }
    }

    private async Task DrainAsync(string clientId, Pending slot)
    {
        while (true)
        {
            // Nothing waiting: done, without sitting out the interval for nobody.
            lock (_gate)
            {
                if (slot.Next is null)
                {
                    slot.Working = false;
                    return;
                }
            }

            if (slot.LastProcessed is { } last)
            {
                var wait = MinInterval - _time.GetElapsedTime(last);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time);
                }
            }

            (RoutinesSyncPayload? Payload, Func<RemexMessage, Task> Send)? next;
            lock (_gate)
            {
                next = slot.Next;
                slot.Next = null;
                if (next is null)
                {
                    slot.Working = false;
                    return;
                }
            }

            RoutineSyncResultPayload result;
            try
            {
                result = await ProcessAsync(clientId, next.Value.Payload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "routines_sync from {ClientId} failed unexpectedly.", LogRedaction.RedactClientId(clientId));
                result = Result(clientId, next.Value.Payload?.Revision ?? 0, RoutineSyncStatuses.InternalError);
            }

            slot.LastProcessed = _time.GetTimestamp();
            await ReplyAsync(next.Value.Send, result);

            // §7.4.2 step 9: after an ok/partial, the history the phone has not seen, then the messages
            // held for it (§7.3.5: the queue flush follows the routine_sync_result of a connection).
            if (result.Status is RoutineSyncStatuses.Ok or RoutineSyncStatuses.Partial && next.Value.Payload is { Forget: false } applied)
            {
                await FlushReportsAsync(clientId, applied.RunCursor, next.Value.Send);
                if (_notifyQueue is not null)
                {
                    await _notifyQueue.FlushAsync(clientId, next.Value.Send);
                }
            }
        }
    }

    /// <summary>§7.4.2 steps 3-10 for one sync. Internal so tests can drive it without the 2 s coalescing.</summary>
    internal async Task<RoutineSyncResultPayload> ProcessAsync(string clientId, RoutinesSyncPayload? payload)
    {
        if (payload is null)
        {
            return Result(clientId, 0, RoutineSyncStatuses.InternalError);
        }

        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var owner = _store.Current.Owner(clientId);

        // Step 3: a blocked phone is still "seen" (it is not absent), but nothing else changes.
        if (owner is { BlockedByPc: true })
        {
            await TouchAsync(clientId, nowMs);
            return Result(clientId, payload.Revision, RoutineSyncStatuses.BlockedByPc);
        }

        // Step 4.
        if (payload.SchemaVersion > RoutineSchema.CurrentVersion)
        {
            await TouchAsync(clientId, nowMs);
            return Result(clientId, payload.Revision, RoutineSyncStatuses.SchemaTooNew);
        }

        // Step 5: the phone forgot this PC. Everything of this owner goes: definitions, runs, active runs.
        if (payload.Forget)
        {
            await _runner.CancelOwnerAsync(clientId);
            var (forgotten, _) = await _store.UpdateAsync(doc => (doc.Owner(clientId) is null ? null : doc.WithOwner(clientId, null), 0));
            try
            {
                await _runs.ForgetOwnerAsync(clientId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Forgetting routine history for {ClientId} failed.", LogRedaction.RedactClientId(clientId));
            }

            _logger.LogInformation("Routines forgotten for {ClientId} at the phone's request.", LogRedaction.RedactClientId(clientId));
            return forgotten
                ? Result(clientId, payload.Revision, RoutineSyncStatuses.Ok) with { StoredRevision = 0, Results = [] }
                : Result(clientId, payload.Revision, RoutineSyncStatuses.InternalError);
        }

        var stored = owner?.Revision ?? 0;
        var routines = payload.Routines ?? [];
        var hash = ContentHash(payload.Paused, routines);

        // Step 6.
        if (payload.Revision < stored)
        {
            await TouchAsync(clientId, nowMs);
            return Result(clientId, payload.Revision, RoutineSyncStatuses.StaleRevision);
        }

        if (payload.Revision == stored && owner is not null)
        {
            if (!string.Equals(owner.ContentHash, hash, StringComparison.Ordinal))
            {
                await TouchAsync(clientId, nowMs);
                return Result(clientId, payload.Revision, RoutineSyncStatuses.RevisionConflict);
            }

            // An idempotent retry: the stored result again, unless it rejected something only because a
            // trigger source was not probed yet. Then the same content is validated again below.
            if (!(owner.LastResults ?? []).Any(IsTransientRejection))
            {
                await TouchAsync(clientId, nowMs);
                return Result(clientId, payload.Revision, StatusOf(owner.LastResults)) with { Results = owner.LastResults ?? [] };
            }
        }

        // Step 7: validate, keep only what passes.
        var results = await _validator.ValidateAsync(routines);
        var accepted = new List<Routine>();
        for (var i = 0; i < routines.Count; i++)
        {
            if (results[i].Accepted)
            {
                accepted.Add(routines[i]);
            }
        }

        var acceptedIds = accepted.Select(r => r.Id!).ToHashSet(StringComparer.Ordinal);
        var wasPaused = owner?.Paused ?? false;

        // Step 8: saved before the reply.
        var (saved, stillPaired) = await _store.UpdateAsync<bool>(doc =>
        {
            // UNDER THE STORE'S WRITE GATE, THE SAME ONE A REVOKE'S FORGET TAKES. The revoker unpairs first
            // and forgets second, so a queued or in-flight sync either lands before the forget (which then
            // removes it) or sees the phone unpaired here; it can never re-create a revoked owner.
            if (!_owners.IsPaired(clientId))
            {
                return (null, false);
            }

            var current = doc.Owner(clientId);
            var next = new RoutineOwnerRecord
            {
                Revision = payload.Revision,
                ContentHash = hash,
                ReceivedAtUnixMs = nowMs,
                LastSeenUnixMs = nowMs,
                Paused = payload.Paused,
                BlockedByPc = current?.BlockedByPc ?? false,
                // The PC's own switches survive a sync; ids the phone no longer has are dropped with them.
                PcDisabled = (current?.PcDisabled ?? []).Where(acceptedIds.Contains).ToList(),
                Routines = accepted,
                LastResults = results,
            };
            return (doc.WithOwner(clientId, next), true);
        });

        if (!saved)
        {
            return Result(clientId, payload.Revision, RoutineSyncStatuses.InternalError);
        }

        if (!stillPaired)
        {
            _logger.LogWarning("routines_sync from {ClientId} dropped: the phone is no longer paired.", LogRedaction.RedactClientId(clientId));
            return Result(clientId, payload.Revision, RoutineSyncStatuses.InternalError);
        }

        _logger.LogInformation(
            "routines_sync revision {Revision} from {ClientId}: {Accepted}/{Total} accepted{Paused}.",
            payload.Revision, LogRedaction.RedactClientId(clientId), accepted.Count, routines.Count,
            payload.Paused ? ", paused" : string.Empty);

        // Step 10: the phone's Pause all takes effect here, cancelling its automatic runs and countdown.
        if (payload.Paused && !wasPaused)
        {
            _countdown.CancelForOwner(clientId, RoutineCancelledBy.Pause);
            _runner.CancelAutomaticRuns(clientId, fromPc: false);
        }

        return Result(clientId, payload.Revision, accepted.Count == routines.Count ? RoutineSyncStatuses.Ok : RoutineSyncStatuses.Partial)
            with { Results = results };
    }

    private async Task FlushReportsAsync(string clientId, long cursor, Func<RemexMessage, Task> send)
    {
        while (true)
        {
            var (page, more) = _runs.Page(clientId, cursor);
            if (page.Count == 0)
            {
                return;
            }

            try
            {
                await send(RoutineMessages.Report(page, more, live: false));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "A routine_run_report page could not be sent.");
                return;
            }

            if (!more)
            {
                return;
            }

            cursor = page[^1].Seq ?? cursor;
        }
    }

    /// <summary>Updates last-seen (lifts an owner-absent suspension, §7.4.4) without touching anything else.</summary>
    private async Task TouchAsync(string clientId, long nowMs) =>
        await _store.UpdateAsync(doc => doc.Owner(clientId) is { } owner
            ? (doc.WithOwner(clientId, owner with { LastSeenUnixMs = nowMs }), 0)
            : (null, 0));

    private RoutineSyncResultPayload Result(string clientId, long revision, string status)
    {
        var document = _store.Current;
        var owner = document.Owner(clientId);
        return new RoutineSyncResultPayload
        {
            Revision = revision,
            StoredRevision = owner?.Revision ?? 0,
            Status = status,
            Results = [],
            HostPaused = document.HostPaused,
            OwnerPaused = owner?.Paused ?? false,
            PcDisabled = owner?.PcDisabled ?? [],
            OwnerSuspended = owner is not null && _store.IsSuspended(owner) ? RoutineReasonCodes.OwnerAbsent : null,
            IdleSource = _availability.IdleSourceId,
            SessionSource = _availability.SessionSourceId,
            SensorTrigger = _availability.SensorAvailable,
        };
    }

    private static string StatusOf(List<RoutineSyncItemResult>? results) =>
        results is null || results.All(r => r.Accepted) ? RoutineSyncStatuses.Ok : RoutineSyncStatuses.Partial;

    private async Task ReplyAsync(Func<RemexMessage, Task> send, RoutineSyncResultPayload result)
    {
        try
        {
            await send(RoutineMessages.SyncResult(result));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "routine_sync_result could not be sent.");
        }
    }

    internal static string ContentHash(bool paused, IReadOnlyList<Routine> routines)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new HashInput(paused, routines), RoutineHostStore.JsonOptions);
        return Convert.ToBase64String(SHA256.HashData(bytes));
    }

    private static int SerializedSize(RoutinesSyncPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, RoutineHostStore.JsonOptions).Length;

    private sealed record HashInput(bool Paused, IReadOnlyList<Routine> Routines);

    private sealed class Pending
    {
        public (RoutinesSyncPayload? Payload, Func<RemexMessage, Task> Send)? Next { get; set; }

        public bool Working { get; set; }

        public long? LastProcessed { get; set; }
    }
}

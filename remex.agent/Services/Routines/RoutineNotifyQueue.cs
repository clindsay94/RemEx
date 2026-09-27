using System.Text.Json;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;

namespace Remex.Agent.Services.Routines;

/// <summary>One held <c>routine_notify</c> (routines spec §7.3.5).</summary>
public sealed record RoutineQueuedNotify
{
    /// <summary>The owner phone; the only one it is ever sent to (T17).</summary>
    public string? OwnerClientId { get; init; }

    /// <summary>The <c>notify(phone)</c> step, so an expiry can mark it in the run's history.</summary>
    public int? StepIndex { get; init; }

    /// <summary>Sent at least once (live or by a flush). An item stays until it is acknowledged.</summary>
    public bool Sent { get; init; }

    public RoutineNotifyPayload? Notify { get; init; }
}

/// <summary>The host's <c>routine_notify_queue.json</c>.</summary>
public sealed record RoutineNotifyQueueDocument
{
    public int FileVersion { get; init; } = RoutineHostDocument.CurrentFileVersion;

    public List<RoutineQueuedNotify>? Items { get; init; }

    /// <summary>Expired items whose run was still running: applied to history when that run ends.</summary>
    public List<RoutineQueuedNotify>? DeferredExpiries { get; init; }
}

/// <summary>Told when a PC run has reached its final record (after it was saved and reported).</summary>
public interface IRoutineRunEndObserver
{
    Task RunEndedAsync(RoutineRun run);
}

/// <summary>
/// The PC-to-phone message queue (routines spec §7.3.5, §17 Q6): live when the owner is connected,
/// otherwise held for an hour and delivered right after the next accepted <c>routine_sync_result</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>PERSISTED, BECAUSE "NOTIFY THE PHONE, THEN SHUT DOWN" IS THE HEADLINE PATTERN (Q6).</b> The item is
/// written before the live send is tried, atomically and with the same ACL and ownership rules as
/// <c>routines.json</c> (T14): the message must survive the shutdown the routine's next step causes.
/// </para>
/// <para>
/// <b>AT LEAST ONCE, REMOVED ONLY ON ACK OR EXPIRY.</b> A live send is not proof the phone showed it, so a
/// sent item stays until <c>routine_notify_ack</c> names it; the phone de-duplicates by <c>notifyId</c>.
/// An item never sent within the hour (or dropped by the 20-per-owner cap) turns its step <c>expired</c>
/// with <c>notify_expired</c>, and the run record is re-sent with a new <c>seq</c> (§7.3.6).
/// </para>
/// <para>
/// A <c>countdown</c> heads-up is never queued: it means nothing 15 s later.
/// </para>
/// </remarks>
public sealed class RoutineNotifyQueue : IRoutinePhoneNotifier, IRoutineRunEndObserver, IDisposable
{
    public const string FileName = "routine_notify_queue.json";
    public const int MaxPerOwner = 20;
    public static readonly TimeSpan Expiry = TimeSpan.FromHours(1);
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly IRoutineStateFiles _files;
    private readonly RoutineRunStore _runs;
    private readonly IRoutinePhoneChannel _channel;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private List<RoutineQueuedNotify> _items = [];
    private List<RoutineQueuedNotify> _deferred = [];
    private ITimer? _timer;
    private bool _writesBlocked;
    private bool _disposed;

    public RoutineNotifyQueue(
        IRoutineStateFiles files,
        RoutineRunStore runs,
        IRoutinePhoneChannel channel,
        TimeProvider time,
        ILogger<RoutineNotifyQueue> logger)
    {
        _files = files;
        _runs = runs;
        _channel = channel;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised when an expiry rewrote a run record (the PC Routines page refreshes its history).</summary>
    public event Action? Changed;

    /// <summary>Set when the queue file had to be set aside at load; null otherwise.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>The held items of one owner, oldest first. For tests and diagnostics.</summary>
    public IReadOnlyList<RoutineQueuedNotify> Pending(string ownerClientId)
    {
        lock (_gate)
        {
            return _items.Where(i => string.Equals(i.OwnerClientId, ownerClientId, StringComparison.Ordinal)).ToList();
        }
    }

    /// <summary>Loads the file (T14 trust rules) and expires what the downtime outlived. Never throws.</summary>
    public async Task LoadAsync()
    {
        _files.SweepStagingOrphans(FileName);
        RoutineNotifyQueueDocument? loaded = null;
        try
        {
            if (_files.TrustProblem(FileName) is { } untrusted)
            {
                throw new UnauthorizedAccessException($"not protected: {untrusted}");
            }

            var text = _files.Read(FileName);
            if (text is not null)
            {
                loaded = JsonSerializer.Deserialize<RoutineNotifyQueueDocument>(text, RoutineHostStore.JsonOptions);
                if (loaded is null || loaded.FileVersion != RoutineHostDocument.CurrentFileVersion)
                {
                    throw new JsonException("unsupported notify queue file");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // Never loaded, never overwritten blind: set aside first, like routines.json.
            var aside = _files.Quarantine(FileName, _time.GetUtcNow());
            _logger.LogError(ex, "{File} could not be read or trusted; set aside as {Aside}. The queue starts empty.", FileName, aside);
            LoadWarning = ex.GetType().Name;
            loaded = null;
            _writesBlocked = aside is null && _files.Exists(FileName);
        }

        lock (_gate)
        {
            _items = (loaded?.Items ?? [])
                .Where(i => i is { Notify.NotifyId: not null } && !string.IsNullOrEmpty(i.OwnerClientId))
                .ToList();
            _deferred = (loaded?.DeferredExpiries ?? [])
                .Where(i => i is { Notify.RunId: not null } && !string.IsNullOrEmpty(i.OwnerClientId))
                .ToList();
            UpdateTimerLocked();
        }

        // History was loaded and swept first (RoutineHostService): a run still "running" at shutdown is
        // "interrupted" now, so every expiry that waited for its run can be applied.
        foreach (var item in await TakeDeferredAsync(null))
        {
            await ExpireAsync(item);
        }

        await SweepAsync();
    }

    /// <inheritdoc />
    public async Task<RoutinePhoneNotifyOutcome> NotifyAsync(string ownerClientId, RoutineNotifyPayload notify, int? stepIndex = null)
    {
        var message = RoutineMessages.Notify(notify);
        if (notify.Kind == RoutineNotifyKinds.Countdown)
        {
            return await _channel.TrySendAsync(ownerClientId, message)
                ? RoutinePhoneNotifyOutcome.Delivered
                : RoutinePhoneNotifyOutcome.NotDelivered;
        }

        List<RoutineQueuedNotify> dropped;
        var item = new RoutineQueuedNotify { OwnerClientId = ownerClientId, StepIndex = stepIndex, Notify = notify };
        lock (_gate)
        {
            _items.Add(item);
            var mine = _items.Where(i => string.Equals(i.OwnerClientId, ownerClientId, StringComparison.Ordinal))
                .OrderBy(i => i.Notify!.QueuedAtUnixMs)
                .ToList();
            dropped = mine.Take(Math.Max(0, mine.Count - MaxPerOwner)).ToList();
            _items.RemoveAll(i => dropped.Contains(i));
            UpdateTimerLocked();
        }

        // Written BEFORE the live attempt: if the next step shuts the PC down, the message is on disk.
        await SaveAsync();

        var sent = await _channel.TrySendAsync(ownerClientId, message);
        if (sent)
        {
            MarkSent(ownerClientId, [notify.NotifyId!]);
            await SaveAsync();
        }

        foreach (var old in dropped)
        {
            _logger.LogInformation("Routine message queue for {Owner} is full; the oldest message was dropped.", LogRedaction.RedactClientId(ownerClientId));
            await ExpireAsync(old);
        }

        return sent ? RoutinePhoneNotifyOutcome.Delivered : RoutinePhoneNotifyOutcome.Queued;
    }

    /// <summary>
    /// Sends every held item of <paramref name="clientId"/>, oldest first, through <paramref name="send"/>
    /// (the connection that just synced). Never throws.
    /// </summary>
    public async Task FlushAsync(string clientId, Func<RemexMessage, Task> send)
    {
        try
        {
            await SweepAsync();
            var sent = new List<string>();
            foreach (var item in Pending(clientId).OrderBy(i => i.Notify!.QueuedAtUnixMs))
            {
                try
                {
                    await send(RoutineMessages.Notify(item.Notify!));
                    sent.Add(item.Notify!.NotifyId!);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "A held routine message could not be sent; it stays queued.");
                    break;
                }
            }

            if (sent.Count > 0)
            {
                MarkSent(clientId, sent);
                await SaveAsync();
                _logger.LogInformation("{Count} held routine message(s) sent to {Owner}.", sent.Count, LogRedaction.RedactClientId(clientId));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flushing held routine messages failed.");
        }
    }

    /// <summary>
    /// <c>routine_notify_ack</c> from the proven client: removes only ITS items (T17). Returns how many.
    /// Never throws.
    /// </summary>
    public async Task<int> AckAsync(string clientId, IReadOnlyCollection<string>? notifyIds)
    {
        if (notifyIds is null || notifyIds.Count == 0)
        {
            return 0;
        }

        int removed;
        lock (_gate)
        {
            var ids = notifyIds.ToHashSet(StringComparer.Ordinal);
            removed = _items.RemoveAll(i =>
                string.Equals(i.OwnerClientId, clientId, StringComparison.Ordinal) && ids.Contains(i.Notify!.NotifyId!));
            UpdateTimerLocked();
        }

        if (removed > 0)
        {
            await SaveAsync();
        }

        return removed;
    }

    /// <summary>Expires everything older than an hour. Public so tests drive it with a fake clock. Never throws.</summary>
    public async Task SweepAsync()
    {
        List<RoutineQueuedNotify> expired;
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_gate)
        {
            expired = _items.Where(i => i.Notify!.ExpiresAtUnixMs <= nowMs).ToList();
            if (expired.Count == 0)
            {
                return;
            }

            _items.RemoveAll(i => expired.Contains(i));
            UpdateTimerLocked();
        }

        await SaveAsync();
        foreach (var item in expired)
        {
            await ExpireAsync(item);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Called by the runner after the final record was saved and reported, so the expired step's record
    /// (with a newer <c>seq</c>) reaches the phone after the run's own final report. Never throws.
    /// </remarks>
    public async Task RunEndedAsync(RoutineRun run)
    {
        if (run.RunId is not { } runId)
        {
            return;
        }

        foreach (var item in await TakeDeferredAsync(runId))
        {
            await ExpireAsync(item);
        }
    }

    /// <summary>Removes (and saves) the deferred expiries of one run, or of every run when null.</summary>
    private async Task<List<RoutineQueuedNotify>> TakeDeferredAsync(string? runId)
    {
        List<RoutineQueuedNotify> taken;
        lock (_gate)
        {
            taken = _deferred.Where(i => runId is null || string.Equals(i.Notify!.RunId, runId, StringComparison.Ordinal)).ToList();
            if (taken.Count == 0)
            {
                return taken;
            }

            _deferred.RemoveAll(i => taken.Contains(i));
        }

        await SaveAsync();
        return taken;
    }

    /// <summary>Deletes one owner's items (revoke, T7). Throws when the save fails.</summary>
    public async Task ForgetOwnerAsync(string ownerClientId)
    {
        int removed;
        lock (_gate)
        {
            removed = _items.RemoveAll(i => string.Equals(i.OwnerClientId, ownerClientId, StringComparison.Ordinal))
                + _deferred.RemoveAll(i => string.Equals(i.OwnerClientId, ownerClientId, StringComparison.Ordinal));
            UpdateTimerLocked();
        }

        if (removed > 0)
        {
            await SaveCoreAsync();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void MarkSent(string ownerClientId, IReadOnlyCollection<string> notifyIds)
    {
        lock (_gate)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (!item.Sent && string.Equals(item.OwnerClientId, ownerClientId, StringComparison.Ordinal)
                    && notifyIds.Contains(item.Notify!.NotifyId!))
                {
                    _items[i] = item with { Sent = true };
                }
            }
        }
    }

    /// <summary>
    /// An item that left the queue unacknowledged. Never sent: its step becomes <c>expired</c> and the run
    /// record is re-sent (§8.8). Sent but not acknowledged: the phone had it; history is left as it is.
    /// </summary>
    private async Task ExpireAsync(RoutineQueuedNotify item)
    {
        try
        {
            if (item.Sent || item.StepIndex is not { } index || item.Notify?.RunId is not { } runId)
            {
                return;
            }

            if (_runs.Find(runId) is not { } run
                || !string.Equals(run.OwnerClientId, item.OwnerClientId, StringComparison.Ordinal)
                || run.Steps is null
                || index < 0 || index >= run.Steps.Count)
            {
                return;
            }

            if (run.Outcome == RoutineRunOutcomes.Running)
            {
                // The runner still owns this record and would overwrite the change with its next save.
                // Held (on disk) until the run ends, then applied by RunEndedAsync.
                lock (_gate)
                {
                    _deferred.Add(item);
                }

                await SaveAsync();
                _logger.LogInformation("A routine message expired while its run {RunId} is still going; recorded when it ends.", runId);
                return;
            }

            var steps = run.Steps.Select(s => s.Index == index
                ? s with { Status = RoutineStepStatuses.Expired, ReasonCode = RoutineReasonCodes.NotifyExpired }
                : s).ToList();
            var stored = await _runs.UpsertAsync(run with { Steps = steps });
            _logger.LogInformation("A routine message for {Owner} expired undelivered (run {RunId}).", LogRedaction.RedactClientId(item.OwnerClientId!), runId);
            if (_channel.CanReach(item.OwnerClientId!))
            {
                await _channel.TrySendAsync(item.OwnerClientId!, RoutineMessages.Report([stored], more: false, live: false));
            }

            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A routines history listener failed.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An expired routine message could not be recorded.");
        }
    }

    // Caller holds _gate. A sweep every minute, only while something is held (§12).
    private void UpdateTimerLocked()
    {
        var wanted = !_disposed && _items.Count > 0;
        if (wanted && _timer is null)
        {
            _timer = _time.CreateTimer(_ => _ = SweepAsync(), null, SweepInterval, SweepInterval);
        }
        else if (!wanted && _timer is not null)
        {
            _timer.Dispose();
            _timer = null;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            await SaveCoreAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving {File} failed; the queue stays in memory.", FileName);
        }
    }

    private async Task SaveCoreAsync()
    {
        await _writeGate.WaitAsync();
        try
        {
            // The original could not be set aside at load: never overwrite it with what is in memory.
            if (_writesBlocked)
            {
                if (_files.Exists(FileName) && _files.Quarantine(FileName, _time.GetUtcNow()) is null)
                {
                    throw new IOException($"{FileName} could not be set aside; refusing to save over it.");
                }

                _writesBlocked = false;
            }

            string json;
            lock (_gate)
            {
                json = JsonSerializer.Serialize(
                    new RoutineNotifyQueueDocument { Items = [.. _items], DeferredExpiries = _deferred.Count == 0 ? null : [.. _deferred] },
                    RoutineHostStore.JsonOptions);
            }

            await _files.WriteAsync(FileName, json);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

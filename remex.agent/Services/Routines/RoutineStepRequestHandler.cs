using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;

namespace Remex.Agent.Services.Routines;

/// <summary>
/// The host end of <c>routine_step_request</c> and <c>routine_cancel</c> (routines spec §7.3.3, §7.3.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>IDEMPOTENT PER (clientId, runId, stepIndex) FOR 10 MINUTES.</b> The phone resends the SAME request
/// up to twice on transport loss (§8.2). A duplicate never executes again: it gets the cached result,
/// or <c>in_progress</c> while the first is still running (a countdown takes 15 s). Only a new request
/// counts against the 60 per minute per client limit.
/// </para>
/// <para>
/// <b>EXECUTION OUTLIVES THE SOCKET.</b> The step runs on no connection token: a phone that drops during
/// a countdown has not cancelled it, and the verb must not be silently abandoned. The phone learns the
/// outcome by resending the request, which hits the cache. Only sends are best-effort.
/// </para>
/// <para>
/// <b>NO WIRE FIELD SETS presenceConfirmed (T21).</b> Every request from here runs with it false, so every
/// phone-initiated destructive step counts down.
/// </para>
/// </remarks>
public sealed class RoutineStepRequestHandler
{
    /// <summary>How long a result stays answerable for a resend.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>New step requests accepted per client per minute.</summary>
    public const int MaxRequestsPerMinute = 60;

    private readonly RoutineStepExecutor _executor;
    private readonly RoutineCountdownCoordinator _countdown;
    private readonly TimeProvider _time;
    private readonly ILogger<RoutineStepRequestHandler> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<(string ClientId, string RunId, int StepIndex), Entry> _entries = new();
    private readonly Dictionary<string, Queue<long>> _recentRequests = new(StringComparer.Ordinal);

    private readonly RoutineCausality? _causality;

    public RoutineStepRequestHandler(
        RoutineStepExecutor executor,
        RoutineCountdownCoordinator countdown,
        TimeProvider time,
        ILogger<RoutineStepRequestHandler> logger,
        RoutineCausality? causality = null)
    {
        _executor = executor;
        _countdown = countdown;
        _time = time;
        _logger = logger;
        _causality = causality;
    }

    /// <summary>
    /// Handles one <c>routine_step_request</c> from the proven client <paramref name="clientId"/>, and
    /// answers through <paramref name="send"/>. Never throws; returns when the step has finished.
    /// </summary>
    public async Task HandleStepRequestAsync(
        string clientId, RoutineStepRequestPayload? request, Func<RemexMessage, Task> send)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RunId))
        {
            await TrySendAsync(send, new RoutineStepResultPayload
            {
                RunId = request?.RunId,
                StepIndex = request?.StepIndex ?? 0,
                Outcome = RoutineStepOutcomes.Failed,
                ReasonCode = RoutineReasonCodes.InvalidField,
                Detail = "runId",
            });
            return;
        }

        var key = (clientId, request.RunId, request.StepIndex);
        Entry? entry = null;
        RoutineStepResultPayload? immediate;
        lock (_gate)
        {
            Prune();
            if (_entries.TryGetValue(key, out var existing))
            {
                immediate = existing.Result ?? new RoutineStepResultPayload
                {
                    RunId = request.RunId,
                    StepIndex = request.StepIndex,
                    Outcome = RoutineStepOutcomes.InProgress,
                    ReasonCode = RoutineReasonCodes.Ok,
                };
            }
            else if (!TryConsumeRate(clientId))
            {
                immediate = new RoutineStepResultPayload
                {
                    RunId = request.RunId,
                    StepIndex = request.StepIndex,
                    Outcome = RoutineStepOutcomes.Failed,
                    ReasonCode = RoutineReasonCodes.RateLimited,
                };
            }
            else
            {
                immediate = null;
                entry = new Entry();
                _entries[key] = entry;
            }
        }

        if (immediate is not null)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Routine step {Index} of run {RunId} answered from cache/limit: {Outcome}.",
                    request.StepIndex, request.RunId, immediate.Outcome);
            }

            await TrySendAsync(send, immediate);
            return;
        }

        var running = entry!;

        _logger.LogInformation(
            "Routine step request: run {RunId} step {Index} ({Type}) from {ClientId}, routine \"{Name}\"{Test}.",
            request.RunId, request.StepIndex, request.Step?.Type, LogRedaction.RedactClientId(clientId),
            LogName(request.RoutineName), request.TestRun ? " [test]" : string.Empty);

        var announced = false;

        // A phone run's LOCK is a routine's own step too: the edge it causes must trigger nothing (T9).
        using var causedByRun = _causality?.BeginStep();
        var result = await _executor.ExecuteAsync(
            new RoutineStepExecution(
                clientId,
                request.RunId,
                request.RoutineId ?? string.Empty,
                DisplayName(request.RoutineName),
                request.StepIndex,
                request.Step,
                request.Source,
                request.TestRun,
                PresenceConfirmed: false),
            async early =>
            {
                lock (_gate)
                {
                    running.Result = early;
                }

                announced = true;
                await TrySendAsync(send, early);
            });

        lock (_gate)
        {
            running.Result = result;
            running.CompletedAt = _time.GetTimestamp();
        }

        // A destructive step already answered "succeeded" before the verb went out; a second result for
        // the same (runId, stepIndex) would be ignored by the phone, so it is only cached.
        if (!announced)
        {
            await TrySendAsync(send, result);
        }
    }

    /// <summary>
    /// Handles one <c>routine_cancel</c> from the proven client <paramref name="clientId"/>. Only a run
    /// that client owns is cancelled; anything else is ignored and logged (§7.3.7, T17).
    /// </summary>
    public bool HandleCancel(string clientId, RoutineCancelPayload? cancel)
    {
        if (cancel is null || string.IsNullOrWhiteSpace(cancel.RunId))
        {
            _logger.LogWarning("Ignored a routine_cancel with no runId from {ClientId}.", LogRedaction.RedactClientId(clientId));
            return false;
        }

        var cancelledBy = string.Equals(cancel.Reason, RoutineCancelReasons.Pause, StringComparison.Ordinal)
            ? RoutineCancelledBy.Pause
            : RoutineCancelledBy.Phone;

        if (_countdown.TryCancelFromPhone(cancel.RunId, clientId, cancelledBy))
        {
            _logger.LogInformation("Routine run {RunId} cancelled from the phone ({By}).", cancel.RunId, cancelledBy);
            return true;
        }

        _logger.LogInformation(
            "Ignored routine_cancel for run {RunId} from {ClientId}: no countdown of that run owned by that phone.",
            cancel.RunId, LogRedaction.RedactClientId(clientId));
        return false;
    }

    private bool TryConsumeRate(string clientId)
    {
        var now = _time.GetTimestamp();
        if (!_recentRequests.TryGetValue(clientId, out var window))
        {
            window = new Queue<long>();
            _recentRequests[clientId] = window;
        }

        while (window.Count > 0 && _time.GetElapsedTime(window.Peek(), now) >= TimeSpan.FromMinutes(1))
        {
            window.Dequeue();
        }

        if (window.Count >= MaxRequestsPerMinute)
        {
            return false;
        }

        window.Enqueue(now);
        return true;
    }

    private void Prune()
    {
        var now = _time.GetTimestamp();
        List<(string, string, int)>? expired = null;
        foreach (var (key, entry) in _entries)
        {
            // Measured from completion, so a long countdown's result still lives the full 10 minutes.
            // A running entry is never pruned.
            if (entry.CompletedAt is { } done && _time.GetElapsedTime(done, now) >= CacheLifetime)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is not null)
        {
            foreach (var key in expired)
            {
                _entries.Remove(key);
            }
        }

        List<string>? idle = null;
        foreach (var (clientId, window) in _recentRequests)
        {
            if (window.Count == 0 || _time.GetElapsedTime(window.Last(), now) >= TimeSpan.FromMinutes(1))
            {
                (idle ??= []).Add(clientId);
            }
        }

        if (idle is not null)
        {
            foreach (var clientId in idle)
            {
                _recentRequests.Remove(clientId);
            }
        }
    }

    private async Task TrySendAsync(Func<RemexMessage, Task> send, RoutineStepResultPayload result)
    {
        try
        {
            await send(new RemexMessage { Type = MessageTypes.RoutineStepResult, RoutineStepResult = result });
        }
        catch (Exception ex)
        {
            // The socket may be gone (the phone dropped, or the PC is going down after the verb). The
            // result is cached; a resend of the request gets it.
            _logger.LogDebug(ex, "routine_step_result for run {RunId} could not be sent.", result.RunId);
        }
    }

    private static string DisplayName(string? name)
    {
        var clean = Remex.Core.Routines.RoutineText.Sanitize(name);
        return clean.Length <= 40 ? clean : clean[..40];
    }

    // T11: routine names are truncated to 16 characters in logs.
    private static string LogName(string? name)
    {
        var clean = Remex.Core.Routines.RoutineText.Sanitize(name);
        return clean.Length <= 16 ? clean : clean[..16];
    }

    private sealed class Entry
    {
        public RoutineStepResultPayload? Result { get; set; }

        public long? CompletedAt { get; set; }
    }
}

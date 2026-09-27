using Remex.Core.Routines;
using Remex.Desktop.Services.Routines;

namespace Remex.Agent.Services.Routines;

/// <summary>How a countdown ended.</summary>
public enum RoutineCountdownStatus
{
    /// <summary>The full length elapsed; the destructive step may proceed.</summary>
    Elapsed,

    /// <summary>Someone cancelled it; see <see cref="RoutineCountdownResult.CancelledBy"/>.</summary>
    Cancelled,

    /// <summary>Another countdown was already running on this PC (<c>conflict_countdown_active</c>).</summary>
    Conflict,
}

/// <summary>The outcome of one countdown.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Shown">
/// Whether the PC countdown was actually visible: the window opened AND the session was not locked.
/// False is the <c>countdown_unseen</c> attribute.
/// </param>
/// <param name="CancelledBy">A <see cref="RoutineCancelledBy"/> value when cancelled, else null.</param>
/// <param name="CancelledOnPc">
/// Which SIDE cancelled, separately from <paramref name="CancelledBy"/>: a <c>pause</c> counts as a cancel
/// from the side that paused (§8.6, §8.7), so PC Pause all is <c>cancelled_on_pc</c> and phone Pause all is
/// <c>cancelled_on_phone</c>, while both report <c>cancelledBy = pause</c>.
/// </param>
public sealed record RoutineCountdownResult(
    RoutineCountdownStatus Status, bool Shown, string? CancelledBy, bool CancelledOnPc = false);

/// <summary>One countdown to run.</summary>
/// <param name="RunId">The run it belongs to; what a phone's <c>routine_cancel</c> names.</param>
/// <param name="OwnerClientId">The phone that owns the run. Only it may cancel from the phone side (T17).</param>
/// <param name="Prompt">What the PC surfaces show.</param>
/// <param name="Started">
/// Called once the countdown has really started (never for a <see cref="RoutineCountdownStatus.Conflict"/>),
/// without being awaited: the phone heads-up of a PC run (routines S5) must not delay the 15 s.
/// </param>
public sealed record RoutineCountdownRequest(
    string RunId, string OwnerClientId, RoutineCountdownPrompt Prompt, Func<Task>? Started = null);

/// <summary>
/// The destructive-step countdown (routines spec §8.6): 15 s, one at a time on the PC, cancellable
/// from the window, the tray, the owner phone, and Pause all.
/// </summary>
/// <remarks>
/// <para>
/// <b>A LOCKED PC STILL COUNTS DOWN AND STILL PROCEEDS.</b> The window cannot draw over the secure
/// desktop, but a <c>pc.session locked → SLEEP</c> routine must work, so the 15 s elapse anyway and
/// the result says <see cref="RoutineCountdownResult.Shown"/> = false (<c>countdown_unseen</c>). A
/// window that fails to open is the same case, never an exception and never a skipped countdown.
/// </para>
/// <para>
/// Time is a <see cref="TimeProvider"/> so tests drive the 15 s without waiting for it (§8.1: durations
/// use monotonic time on the host).
/// </para>
/// </remarks>
public sealed class RoutineCountdownCoordinator
{
    /// <summary>Fixed in v1.</summary>
    public static readonly TimeSpan Length = TimeSpan.FromSeconds(15);

    private readonly IRoutineUi _ui;
    private readonly ISessionLockProbe _lockProbe;
    private readonly TimeProvider _time;
    private readonly ILogger<RoutineCountdownCoordinator> _logger;
    private readonly object _gate = new();
    private Active? _active;

    public RoutineCountdownCoordinator(
        IRoutineUi ui,
        ISessionLockProbe lockProbe,
        TimeProvider time,
        ILogger<RoutineCountdownCoordinator> logger)
    {
        _ui = ui;
        _lockProbe = lockProbe;
        _time = time;
        _logger = logger;
    }

    /// <summary>True while a countdown is running.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _active is not null;
            }
        }
    }

    /// <summary>The run id of the running countdown, or null.</summary>
    public string? ActiveRunId
    {
        get
        {
            lock (_gate)
            {
                return _active?.Request.RunId;
            }
        }
    }

    /// <summary>
    /// Runs one countdown to its end. Never throws for a UI failure. Returns
    /// <see cref="RoutineCountdownStatus.Conflict"/> at once when another countdown is running.
    /// </summary>
    public async Task<RoutineCountdownResult> RunAsync(RoutineCountdownRequest request)
    {
        var active = new Active(request);
        lock (_gate)
        {
            if (_active is not null)
            {
                return new RoutineCountdownResult(RoutineCountdownStatus.Conflict, Shown: false, CancelledBy: null);
            }

            _active = active;
        }

        try
        {
            var locked = SafeIsLocked();
            bool windowShown;
            try
            {
                windowShown = await _ui.ShowCountdownAsync(
                    request.Prompt, () => Cancel(active, RoutineCancelledBy.Pc, fromPc: true));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Routine countdown surface failed to open; counting down unseen.");
                windowShown = false;
            }

            var shown = windowShown && !locked;
            _logger.LogInformation(
                "Routine countdown started for run {RunId} ({Verb}); shown={Shown}.",
                request.RunId, request.Prompt.Verb, shown);

            var elapsed = Task.Delay(Length, _time);
            if (request.Started is { } started)
            {
                _ = RaiseStartedAsync(started, request.RunId);
            }
            var finished = await Task.WhenAny(elapsed, active.Cancelled.Task);

            if (finished == active.Cancelled.Task)
            {
                var (by, fromPc) = await active.Cancelled.Task;
                _logger.LogInformation("Routine countdown for run {RunId} cancelled by {By}.", request.RunId, by);
                return new RoutineCountdownResult(RoutineCountdownStatus.Cancelled, shown, by, fromPc);
            }

            // A cancel that raced the deadline still wins: the person pressed Cancel, and "it went
            // ahead anyway because the timer fired in the same millisecond" is not an answer. Closing
            // under the same lock Cancel takes makes the two outcomes mutually exclusive.
            lock (_gate)
            {
                if (!active.Cancelled.Task.IsCompleted)
                {
                    active.Close();
                    return new RoutineCountdownResult(RoutineCountdownStatus.Elapsed, shown, null);
                }
            }

            var (lateBy, lateFromPc) = await active.Cancelled.Task;
            return new RoutineCountdownResult(RoutineCountdownStatus.Cancelled, shown, lateBy, lateFromPc);
        }
        finally
        {
            active.Close();
            lock (_gate)
            {
                if (ReferenceEquals(_active, active))
                {
                    _active = null;
                }
            }

            try
            {
                _ui.CloseCountdown(request.RunId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Routine countdown surface failed to close.");
            }
        }
    }

    private async Task RaiseStartedAsync(Func<Task> started, string runId)
    {
        try
        {
            await started();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The countdown-started callback for run {RunId} failed.", runId);
        }
    }

    /// <summary>
    /// A phone's <c>routine_cancel</c>: cancels the running countdown only when it belongs to
    /// <paramref name="runId"/> AND <paramref name="ownerClientId"/> owns it (§7.3.7, T17).
    /// </summary>
    public bool TryCancelFromPhone(string runId, string ownerClientId, string cancelledBy)
    {
        Active? active;
        lock (_gate)
        {
            active = _active;
        }

        if (active is null
            || !string.Equals(active.Request.RunId, runId, StringComparison.Ordinal)
            || !string.Equals(active.Request.OwnerClientId, ownerClientId, StringComparison.Ordinal))
        {
            return false;
        }

        return Cancel(active, cancelledBy, fromPc: false);
    }

    /// <summary>
    /// Cancels whatever countdown is running, from the PC side: PC Pause all (<c>pause</c>) or any
    /// other PC surface (<c>pc</c>). Returns whether there was one.
    /// </summary>
    public bool CancelActive(string cancelledBy)
    {
        Active? active;
        lock (_gate)
        {
            active = _active;
        }

        return active is not null && Cancel(active, cancelledBy, fromPc: true);
    }

    /// <summary>
    /// Phone Pause all (<c>routines_sync{paused:true}</c>, S4): cancels the running countdown when it
    /// belongs to <paramref name="ownerClientId"/>.
    /// </summary>
    public bool CancelForOwner(string ownerClientId, string cancelledBy)
    {
        Active? active;
        lock (_gate)
        {
            active = _active;
        }

        return active is not null
            && string.Equals(active.Request.OwnerClientId, ownerClientId, StringComparison.Ordinal)
            && Cancel(active, cancelledBy, fromPc: false);
    }

    private bool Cancel(Active active, string cancelledBy, bool fromPc)
    {
        lock (_gate)
        {
            return !active.IsClosed && active.Cancelled.TrySetResult((cancelledBy, fromPc));
        }
    }

    private bool SafeIsLocked()
    {
        try
        {
            return _lockProbe.IsLocked();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session lock probe failed; assuming unlocked.");
            return false;
        }
    }

    private sealed class Active(RoutineCountdownRequest request)
    {
        private int _closed;

        public RoutineCountdownRequest Request { get; } = request;

        public TaskCompletionSource<(string By, bool FromPc)> Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public void Close() => Interlocked.Exchange(ref _closed, 1);
    }
}

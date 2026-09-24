namespace Remex.Agent.Handlers;

/// <summary>
/// One connection's telemetry pause switch, flipped by <c>telemetry_pause</c> /
/// <c>telemetry_resume</c> from the client and read by that connection's telemetry loop
/// (perf audit P0-5).
/// </summary>
/// <remarks>
/// <para>
/// Before this the host pushed a full 60-100 KB envelope every second to a phone that was
/// backgrounded or had its screen off, with no way for the phone to ask it to stop - waking the
/// radio and burning CPU on both ends for nobody. The phone now says when it leaves and returns.
/// </para>
/// <para>
/// **ONE INSTANCE PER CONNECTION, NEVER SHARED.** <c>PingPongHandler.HandleAsync</c> creates it as a
/// local beside the telemetry loop it gates, so one phone pausing cannot silence any other client,
/// and a reconnect always starts unpaused - the phone re-sends its pause if it is still backgrounded.
/// </para>
/// <para>
/// **WAITING, NOT POLLING.** While paused the loop blocks on <see cref="WaitWhilePausedAsync"/>, which
/// completes the moment <see cref="Resume"/> runs, so a paused connection costs nothing per tick and
/// a resumed one sends the current sample immediately rather than up to a second later.
/// </para>
/// <para>
/// Thread-safety: <see cref="Pause"/> and <see cref="Resume"/> run on the connection's receive loop,
/// while <see cref="IsPaused"/> and <see cref="WaitWhilePausedAsync"/> run on its telemetry loop.
/// A lock keeps the flag and the wait handle in step; continuations run asynchronously so resuming
/// never runs the telemetry loop's send inline on the receive loop.
/// </para>
/// </remarks>
internal sealed class TelemetryPauseGate
{
    private readonly object _lock = new();

    // Non-null exactly while paused; completed (and dropped) by Resume.
    private TaskCompletionSource? _resumed;

    /// <summary>Whether telemetry to this connection is currently paused.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_lock) return _resumed is not null;
        }
    }

    /// <summary>Pauses telemetry to this connection. Idempotent.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Resumes telemetry to this connection and releases any waiter. Idempotent.</summary>
    public void Resume()
    {
        TaskCompletionSource? resumed;
        lock (_lock)
        {
            resumed = _resumed;
            _resumed = null;
        }

        resumed?.TrySetResult();
    }

    /// <summary>
    /// Completes when this connection is not paused: immediately if it is not paused now, otherwise
    /// when <see cref="Resume"/> is called. Throws <see cref="OperationCanceledException"/> on
    /// <paramref name="ct"/>.
    /// </summary>
    public Task WaitWhilePausedAsync(CancellationToken ct)
    {
        Task resumed;
        lock (_lock)
        {
            if (_resumed is null) return Task.CompletedTask;
            resumed = _resumed.Task;
        }

        return resumed.WaitAsync(ct);
    }
}

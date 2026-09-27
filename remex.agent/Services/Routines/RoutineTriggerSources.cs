using Remex.Core.Routines;

namespace Remex.Agent.Services.Routines;

/// <summary>
/// What this PC can trigger on right now (routines spec §7.3.2 <c>idleSource</c> / <c>sessionSource</c> /
/// <c>sensorTrigger</c>, §7.5).
/// </summary>
/// <remarks>
/// Dynamic, which is why it is not in <c>HostCapabilitiesProvider</c>'s cached record: the D-Bus probes
/// complete after start. Every <c>routine_sync_result</c> carries the current values, and the sync
/// validator rejects a trigger whose source is null here (§17 Q5).
/// </remarks>
public sealed class RoutineTriggerAvailability
{
    private volatile string? _idle;
    private volatile string? _session;
    private volatile object? _locked;

    /// <summary>The idle source id (<c>win32.lastinput</c>, <c>gnome.idlemonitor</c>, …), or null.</summary>
    public string? IdleSourceId => _idle;

    /// <summary>The session source id (<c>win32.wts</c>, <c>logind.lockedhint</c>, …), or null.</summary>
    public string? SessionSourceId => _session;

    /// <summary>
    /// <c>pc.sensor</c> (routines S5). False until the sensor source lands, so a sensor routine is refused
    /// with <c>sensor_unavailable</c> rather than stored and never fired.
    /// </summary>
    public bool SensorAvailable => false;

    /// <summary>The last lock state the session source reported, or null when unknown.</summary>
    public bool? SessionLocked => _locked as bool?;

    public void SetIdleSource(string? id) => _idle = id;

    public void SetSessionSource(string? id) => _session = id;

    public void SetSessionLocked(bool? locked) => _locked = locked;
}

/// <summary>A way to read how long the PC has had no input (§8.5.2).</summary>
public interface IIdleSource
{
    /// <summary>The source id reported to the phone.</summary>
    string Id { get; }

    /// <summary>Current idle time, or null when the source did not answer.</summary>
    Task<TimeSpan?> GetIdleAsync(CancellationToken ct);
}

/// <summary>A source of lock/unlock state (§8.5.3).</summary>
public interface ISessionStateSource : IDisposable
{
    /// <summary>The source id reported to the phone.</summary>
    string Id { get; }

    /// <summary>The current state, or null when unknown.</summary>
    bool? IsLocked { get; }

    /// <summary>Raised on a lock (true) or unlock (false) edge, on any thread.</summary>
    event Action<bool>? Changed;

    /// <summary>Starts listening. False when this source is not available on this PC.</summary>
    Task<bool> StartAsync(CancellationToken ct);
}

/// <summary>One armed <c>pc.idle</c> routine.</summary>
public sealed record IdleArming(string OwnerClientId, string RoutineId, int IdleMinutes, bool IgnoreWhileMediaPlaying);

/// <summary>One armed <c>pc.session</c> routine.</summary>
public sealed record SessionArming(string OwnerClientId, string RoutineId, bool OnLocked);

/// <summary>A trigger that fired.</summary>
public sealed record RoutineTriggerFire(string OwnerClientId, string RoutineId, string Source, RoutineRunSourceDetail Detail);

/// <summary>
/// The <c>pc.idle</c> fire rule (routines spec §8.5.2): fire at <c>idleMinutes</c>, once per idle period,
/// re-arm when input is seen (idle below 5 s), and hold off while media plays if asked to.
/// </summary>
/// <remarks>
/// <para>
/// <b>A ROUTINE THAT BECOMES ARMED WHILE THE PC IS ALREADY IDLE WAITS FOR INPUT FIRST.</b> Syncing a
/// "sleep after 10 minutes" routine to a PC that has sat untouched for an hour, or un-pausing one, must not
/// sleep the PC on the spot (§8.4: never fire on the edge of un-pausing). New arming starts in the fired
/// state unless the last reading already showed input.
/// </para>
/// <para>
/// Polled every 15 s, and only while at least one <c>pc.idle</c> routine is armed (§12 budget).
/// </para>
/// </remarks>
public sealed class IdleTriggerSource : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan InputSeen = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private readonly Func<bool> _mediaPlaying;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<(string, string), State> _armed = new();
    private IIdleSource? _source;
    private ITimer? _timer;
    private TimeSpan? _lastIdle;
    private int _polling;

    public IdleTriggerSource(TimeProvider time, Func<bool> mediaPlaying, ILogger logger)
    {
        _time = time;
        _mediaPlaying = mediaPlaying;
        _logger = logger;
    }

    /// <summary>Raised when a routine's idle threshold is reached.</summary>
    public event Action<RoutineTriggerFire>? Fired;

    /// <summary>The source in use, or null when this PC has none.</summary>
    public string? SourceId => _source?.Id;

    public void SetSource(IIdleSource? source)
    {
        lock (_gate)
        {
            _source = source;
            UpdateTimer();
        }
    }

    /// <summary>Replaces the armed set. Kept entries keep their per-period state.</summary>
    public void SetArmed(IReadOnlyList<IdleArming> armings)
    {
        lock (_gate)
        {
            var next = new Dictionary<(string, string), State>();
            var inputJustSeen = _lastIdle is { } idle && idle < InputSeen;
            foreach (var arming in armings)
            {
                var key = (arming.OwnerClientId, arming.RoutineId);
                next[key] = _armed.TryGetValue(key, out var existing)
                    ? existing with { Arming = arming }
                    : new State(arming, FiredThisPeriod: !inputJustSeen);
            }

            _armed.Clear();
            foreach (var (key, state) in next)
            {
                _armed[key] = state;
            }

            UpdateTimer();
        }
    }

    /// <summary>One poll: read the idle time and fire what is due. Public so tests drive it directly.</summary>
    public async Task PollOnceAsync()
    {
        IIdleSource? source;
        lock (_gate)
        {
            source = _source;
        }

        if (source is null)
        {
            return;
        }

        TimeSpan? idle;
        try
        {
            idle = await source.GetIdleAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Idle source {Source} failed.", source.Id);
            return;
        }

        if (idle is null)
        {
            return;
        }

        List<RoutineTriggerFire>? fires = null;
        lock (_gate)
        {
            _lastIdle = idle;
            var keys = _armed.Keys.ToList();
            foreach (var key in keys)
            {
                var state = _armed[key];
                if (idle.Value < InputSeen)
                {
                    _armed[key] = state with { FiredThisPeriod = false };
                    continue;
                }

                if (state.FiredThisPeriod || idle.Value < TimeSpan.FromMinutes(state.Arming.IdleMinutes))
                {
                    continue;
                }

                // Media playing holds the fire without spending it: when the film ends and the PC stays
                // idle, the routine still fires this period.
                if (state.Arming.IgnoreWhileMediaPlaying && SafeMediaPlaying())
                {
                    continue;
                }

                _armed[key] = state with { FiredThisPeriod = true };
                (fires ??= []).Add(new RoutineTriggerFire(
                    state.Arming.OwnerClientId,
                    state.Arming.RoutineId,
                    RoutineRunSources.PcIdle,
                    new RoutineRunSourceDetail { IdleMinutes = state.Arming.IdleMinutes }));
            }
        }

        foreach (var fire in fires ?? [])
        {
            try
            {
                Fired?.Invoke(fire);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A pc.idle fire handler failed.");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private bool SafeMediaPlaying()
    {
        try
        {
            return _mediaPlaying();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Media state probe failed; treating as not playing.");
            return false;
        }
    }

    // Caller holds _gate.
    private void UpdateTimer()
    {
        var wanted = _source is not null && _armed.Count > 0;
        if (wanted && _timer is null)
        {
            _timer = _time.CreateTimer(_ => OnTimer(), null, PollInterval, PollInterval);
        }
        else if (!wanted && _timer is not null)
        {
            _timer.Dispose();
            _timer = null;

            // The last reading goes stale the moment polling stops. Forgetting it is what makes a routine
            // armed again later (un-paused, un-blocked) wait for input rather than trust an old "input
            // was just seen" and fire on the edge of un-pausing (§8.4).
            _lastIdle = null;
        }
    }

    private void OnTimer()
    {
        // One poll at a time: a slow D-Bus answer must not stack up behind the next tick.
        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await PollOnceAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        });
    }

    private sealed record State(IdleArming Arming, bool FiredThisPeriod);
}

/// <summary>
/// The <c>pc.session</c> fire rule (routines spec §8.5.3, T9): an edge must hold 3 s before it fires
/// (a lock → unlock → lock flap collapses into one edge), at most 20 session runs per routine per hour, and
/// an edge a routine's own step caused triggers nothing.
/// </summary>
public sealed class SessionTriggerSource : IDisposable
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    public const int MaxFiresPerRoutinePerHour = 20;

    private readonly TimeProvider _time;
    private readonly RoutineCausality _causality;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<(string, string), SessionArming> _armed = new();
    private readonly Dictionary<(string, string), Queue<long>> _recentFires = new();
    private bool? _settled;
    private bool? _pending;
    private bool _pendingCaused;
    private ITimer? _settleTimer;

    public SessionTriggerSource(TimeProvider time, RoutineCausality causality, ILogger logger)
    {
        _time = time;
        _causality = causality;
        _logger = logger;
    }

    /// <summary>Raised when a settled edge fires a routine.</summary>
    public event Action<RoutineTriggerFire>? Fired;

    /// <summary>
    /// Raised, per routine that WOULD have fired, when the loop guard suppressed a settled edge. The listener
    /// records it: a suppressed edge is never a silent drop.
    /// </summary>
    public event Action<RoutineTriggerFire>? Suppressed;

    /// <summary>Seeds the settled state from the source's initial reading. Not an edge.</summary>
    public void SetInitialState(bool? locked)
    {
        lock (_gate)
        {
            _settled ??= locked;
        }
    }

    public void SetArmed(IReadOnlyList<SessionArming> armings)
    {
        lock (_gate)
        {
            _armed.Clear();
            foreach (var arming in armings)
            {
                _armed[(arming.OwnerClientId, arming.RoutineId)] = arming;
            }
        }
    }

    /// <summary>A raw edge from the source. Tagged <c>causedByRun</c> at arrival (§8.1).</summary>
    public void OnEdge(bool locked)
    {
        var caused = _causality.IsCausedByRunNow();
        lock (_gate)
        {
            _settleTimer?.Dispose();
            _settleTimer = null;

            if (_settled == locked)
            {
                // Back where it settled: the flap collapsed, nothing fires.
                _pending = null;
                _pendingCaused = false;
                return;
            }

            _pendingCaused = _pending is not null ? _pendingCaused || caused : caused;
            _pending = locked;
            _settleTimer = _time.CreateTimer(_ => OnSettled(), null, Settle, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _settleTimer?.Dispose();
            _settleTimer = null;
        }
    }

    private void OnSettled()
    {
        List<RoutineTriggerFire>? fires = null;
        var suppressed = false;
        lock (_gate)
        {
            if (_pending is not { } locked)
            {
                return;
            }

            _pending = null;
            _settled = locked;
            _settleTimer?.Dispose();
            _settleTimer = null;
            suppressed = _pendingCaused;
            _pendingCaused = false;

            if (suppressed)
            {
                _logger.LogInformation(
                    "Session {State} edge was caused by a routine step; no routine triggered (loop guard).",
                    locked ? "lock" : "unlock");
            }

            var now = _time.GetTimestamp();
            foreach (var (key, arming) in _armed)
            {
                if (arming.OnLocked != locked)
                {
                    continue;
                }

                if (suppressed)
                {
                    // Recorded by the listener, and not counted against the hourly cap: nothing ran.
                    (fires ??= []).Add(Fire(arming, locked));
                    continue;
                }

                if (!_recentFires.TryGetValue(key, out var window))
                {
                    window = new Queue<long>();
                    _recentFires[key] = window;
                }

                while (window.Count > 0 && _time.GetElapsedTime(window.Peek(), now) >= TimeSpan.FromHours(1))
                {
                    window.Dequeue();
                }

                if (window.Count >= MaxFiresPerRoutinePerHour)
                {
                    _logger.LogWarning("A pc.session routine reached {Max} runs this hour; edge ignored.", MaxFiresPerRoutinePerHour);
                    continue;
                }

                window.Enqueue(now);
                (fires ??= []).Add(Fire(arming, locked));
            }
        }

        foreach (var fire in fires ?? [])
        {
            try
            {
                (suppressed ? Suppressed : Fired)?.Invoke(fire);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A pc.session fire handler failed.");
            }
        }
    }

    private static RoutineTriggerFire Fire(SessionArming arming, bool locked) => new(
        arming.OwnerClientId,
        arming.RoutineId,
        RoutineRunSources.PcSession,
        new RoutineRunSourceDetail
        {
            SessionState = locked ? RoutineSessionStates.Locked : RoutineSessionStates.Unlocked,
        });
}

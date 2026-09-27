using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Routines;
using Remex.Core.Services;

namespace Remex.Agent.Services.Routines;

/// <summary>One armed <c>pc.sensor</c> routine.</summary>
public sealed record SensorArming(
    string OwnerClientId,
    string RoutineId,
    string SensorId,
    string? SensorLabel,
    AlertDirection Direction,
    double Threshold,
    int SustainSeconds);

/// <summary>
/// The <c>pc.sensor</c> fire rule (routines spec §8.5.1, T9): the shared threshold with its 2% clear band
/// (<see cref="SensorThreshold.IsLive"/>), a sustain window, and a 300 s cooldown that also needs a
/// non-live sample before the routine can fire again.
/// </summary>
/// <remarks>
/// <para>
/// <b>SENSOR ROUTINES MUST HOLD TELEMETRY DEMAND OR THEY NEVER SEE A SAMPLE.</b> The sampler idles when
/// nothing holds a lease (perf audit P0-10), and an idle sampler publishes nothing: a sensor routine on a
/// PC with no window open and no phone streaming would sit armed forever and never fire, with no error
/// anywhere. This source takes one lease while at least one <c>pc.sensor</c> routine is armed and drops it
/// when none is, so an idle PC with no sensor routine still samples nothing (§12 budget).
/// </para>
/// <para>
/// <b>A MISSING SENSOR IS RECORDED, NOT SILENT.</b> A reading that stops appearing clears the sustain
/// window; after 10 minutes continuously missing, one <c>sensor_unavailable</c> record per routine
/// (<see cref="Unavailable"/>) until the sensor comes back.
/// </para>
/// </remarks>
public sealed class SensorTriggerSource : IDisposable
{
    /// <summary>After a fire, the routine re-arms only after this long AND a non-live sample.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(300);

    /// <summary>How long a sensor must be missing before it is recorded as unavailable.</summary>
    public static readonly TimeSpan MissingReportAfter = TimeSpan.FromMinutes(10);

    private readonly ITelemetryBroadcaster _feed;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<(string, string), State> _armed = new();
    private IDisposable? _demand;
    private bool _disposed;

    public SensorTriggerSource(ITelemetryBroadcaster feed, TimeProvider time, ILogger logger)
    {
        _feed = feed;
        _time = time;
        _logger = logger;
        _feed.TelemetryPublished += OnSample;
    }

    /// <summary>Raised when a routine's breach has been sustained and it is armed.</summary>
    public event Action<RoutineTriggerFire>? Fired;

    /// <summary>Raised once per routine when its sensor has been missing for <see cref="MissingReportAfter"/>.</summary>
    public event Action<RoutineTriggerFire>? Unavailable;

    /// <summary>True while this source holds a telemetry lease. Exposed for tests.</summary>
    public bool HoldsDemand
    {
        get
        {
            lock (_gate)
            {
                return _demand is not null;
            }
        }
    }

    /// <summary>
    /// Replaces the armed set. An entry whose sensor, direction, threshold and sustain are unchanged keeps
    /// its breach, cooldown and missing state; anything else starts fresh.
    /// </summary>
    public void SetArmed(IReadOnlyList<SensorArming> armings)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var next = new Dictionary<(string, string), State>();
            foreach (var arming in armings)
            {
                var key = (arming.OwnerClientId, arming.RoutineId);
                next[key] = _armed.TryGetValue(key, out var existing) && SameRule(existing.Arming, arming)
                    ? existing with { Arming = arming }
                    : new State(arming);
            }

            _armed.Clear();
            foreach (var (key, state) in next)
            {
                _armed[key] = state;
            }

            if (_armed.Count > 0 && _demand is null)
            {
                _demand = _feed.AcquireDemand();
                _logger.LogInformation("Sensor routines armed ({Count}); telemetry sampling held.", _armed.Count);
            }
            else if (_armed.Count == 0 && _demand is not null)
            {
                _demand.Dispose();
                _demand = null;
                _logger.LogInformation("No sensor routine armed; telemetry lease released.");
            }
        }
    }

    /// <summary>One telemetry sample. Public so tests drive it directly; raised on the sampler's thread.</summary>
    public void OnSample(TelemetryPayload payload)
    {
        List<(RoutineTriggerFire Fire, bool Missing)>? events = null;
        lock (_gate)
        {
            if (_armed.Count == 0)
            {
                return;
            }

            var now = _time.GetTimestamp();
            Dictionary<string, SensorReading>? byId = null;
            foreach (var key in _armed.Keys.ToList())
            {
                var state = _armed[key];
                var arming = state.Arming;
                byId ??= Index(payload);
                if (!byId.TryGetValue(arming.SensorId, out var reading) || !double.IsFinite(reading.Value))
                {
                    var missingSince = state.MissingSince ?? now;
                    var report = !state.MissingReported && _time.GetElapsedTime(missingSince, now) >= MissingReportAfter;
                    _armed[key] = state with
                    {
                        BreachSince = null,
                        WasLive = false,
                        MissingSince = missingSince,
                        MissingReported = state.MissingReported || report,
                    };
                    if (report)
                    {
                        (events ??= []).Add((Fire(arming, null), true));
                    }

                    continue;
                }

                var live = SensorThreshold.IsLive(arming.Direction, arming.Threshold, reading.Value, state.WasLive);
                if (!live)
                {
                    // A non-live sample clears the breach and satisfies the "went back to normal" half of
                    // the re-arm rule; the cooldown half is checked at the next breach.
                    _armed[key] = state with
                    {
                        BreachSince = null,
                        WasLive = false,
                        NeedsClear = false,
                        MissingSince = null,
                        MissingReported = false,
                    };
                    continue;
                }

                var breachSince = state.BreachSince ?? now;
                var cooled = state.FiredAt is not { } firedAt || _time.GetElapsedTime(firedAt, now) >= Cooldown;
                var sustained = _time.GetElapsedTime(breachSince, now) >= TimeSpan.FromSeconds(arming.SustainSeconds);
                if (!state.NeedsClear && cooled && sustained)
                {
                    _armed[key] = state with
                    {
                        BreachSince = breachSince,
                        WasLive = true,
                        FiredAt = now,
                        NeedsClear = true,
                        MissingSince = null,
                        MissingReported = false,
                    };
                    (events ??= []).Add((Fire(arming, reading), false));
                }
                else
                {
                    _armed[key] = state with
                    {
                        BreachSince = breachSince,
                        WasLive = true,
                        MissingSince = null,
                        MissingReported = false,
                    };
                }
            }
        }

        foreach (var (fire, missing) in events ?? [])
        {
            try
            {
                (missing ? Unavailable : Fired)?.Invoke(fire);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A pc.sensor fire handler failed.");
            }
        }
    }

    public void Dispose()
    {
        _feed.TelemetryPublished -= OnSample;
        lock (_gate)
        {
            _disposed = true;
            _armed.Clear();
            _demand?.Dispose();
            _demand = null;
        }
    }

    /// <summary>Maps a trigger's wire direction; null for an unknown one (the validator rejects those).</summary>
    public static AlertDirection? DirectionOf(string? direction) => direction switch
    {
        RoutineSensorDirections.Above => AlertDirection.Above,
        RoutineSensorDirections.Below => AlertDirection.Below,
        _ => null,
    };

    private static Dictionary<string, SensorReading> Index(TelemetryPayload payload)
    {
        var byId = new Dictionary<string, SensorReading>(StringComparer.Ordinal);
        foreach (var reading in payload.Sensors)
        {
            if (reading is not null && !string.IsNullOrEmpty(reading.Id))
            {
                byId.TryAdd(reading.Id, reading);
            }
        }

        return byId;
    }

    private static bool SameRule(SensorArming a, SensorArming b) =>
        string.Equals(a.SensorId, b.SensorId, StringComparison.Ordinal)
        && a.Direction == b.Direction
        && a.Threshold.Equals(b.Threshold)
        && a.SustainSeconds == b.SustainSeconds;

    private static RoutineTriggerFire Fire(SensorArming arming, SensorReading? reading) => new(
        arming.OwnerClientId,
        arming.RoutineId,
        RoutineRunSources.PcSensor,
        new RoutineRunSourceDetail
        {
            SensorName = arming.SensorLabel ?? reading?.Name,
            Value = reading is null ? null : Math.Round(reading.Value, 2),
            Unit = reading?.Unit,
        });

    private sealed record State(
        SensorArming Arming,
        long? BreachSince = null,
        bool WasLive = false,
        long? FiredAt = null,
        bool NeedsClear = false,
        long? MissingSince = null,
        bool MissingReported = false);
}

/// <summary>
/// This PC's sensor catalog for the sync validator (routines spec §8.5.1): <c>sensor_unavailable</c> only
/// for a sensor id the PC does not have. When nothing is sampling, it takes a lease for up to
/// <see cref="SampleWait"/> to read one sample rather than rejecting on an idle sampler.
/// </summary>
public sealed class RoutineSensorCatalog(ITelemetryBroadcaster telemetry, TimeProvider time)
{
    public static readonly TimeSpan SampleWait = TimeSpan.FromSeconds(3);

    /// <summary>The sensor ids of the current (or next) sample, or null when no sample came in time.</summary>
    public async Task<IReadOnlySet<string>?> GetSensorIdsAsync()
    {
        if (telemetry.CurrentTelemetry is { } current)
        {
            return IdsOf(current);
        }

        var next = new TaskCompletionSource<TelemetryPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPublished(TelemetryPayload payload) => next.TrySetResult(payload);

        telemetry.TelemetryPublished += OnPublished;
        try
        {
            using var lease = telemetry.AcquireDemand();
            if (telemetry.CurrentTelemetry is { } raced)
            {
                return IdsOf(raced);
            }

            var finished = await Task.WhenAny(next.Task, Task.Delay(SampleWait, time));
            return finished == next.Task ? IdsOf(await next.Task) : null;
        }
        finally
        {
            telemetry.TelemetryPublished -= OnPublished;
        }
    }

    private static HashSet<string> IdsOf(TelemetryPayload payload) =>
        payload.Sensors.Where(s => s is not null && !string.IsNullOrEmpty(s.Id)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
}

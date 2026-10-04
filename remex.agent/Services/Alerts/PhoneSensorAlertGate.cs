namespace Remex.Agent.Services.Alerts;

/// <summary>
/// One connection's pacing for the phone's sensor alert requests (<c>sensor_alerts_get</c>,
/// <c>sensor_alert_set</c>, <c>sensor_alert_remove</c>, RemEx-pp4cm.12). Create one per session; it is
/// not shared between phones. Same idea as <c>PhoneDiagnosticsService.SessionGate</c>, but a window
/// rather than a minimum gap, because a person editing several rules legitimately sends a few requests
/// close together.
/// </summary>
/// <remarks>
/// Without it, a paired phone in a loop makes the PC do a UI-thread pass, a profile save and a broadcast
/// to every phone per message. The three kinds share one budget: they all end in the same work.
/// </remarks>
public sealed class PhoneSensorAlertGate
{
    /// <summary>Requests one session may make inside <see cref="Window"/>.</summary>
    public const int MaxRequestsPerWindow = 10;

    /// <summary>The length of the pacing window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly Queue<long> _stamps = new();
    private readonly object _lock = new();

    public PhoneSensorAlertGate(TimeProvider time) => _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <summary>True when a request may run now, and records that it did.</summary>
    public bool TryEnter()
    {
        var now = _time.GetTimestamp();
        var windowTicks = (long)(Window.TotalSeconds * _time.TimestampFrequency);
        lock (_lock)
        {
            while (_stamps.Count > 0 && now - _stamps.Peek() >= windowTicks)
            {
                _stamps.Dequeue();
            }

            if (_stamps.Count >= MaxRequestsPerWindow)
            {
                return false;
            }

            _stamps.Enqueue(now);
            return true;
        }
    }
}

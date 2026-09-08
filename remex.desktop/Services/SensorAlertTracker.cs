using System;
using System.Collections.Generic;
using Remex.Core.Models;

namespace Remex.Desktop.Services;

/// <summary>A sensor that has crossed its configured alert threshold and not yet been acknowledged.</summary>
public sealed record TrippedAlert(string SensorName, DateTimeOffset At, double Value, SensorAlert Alert);

/// <summary>
/// Runtime trip state and the per-sensor notification cooldown. Session-only: nothing here is
/// persisted. Takes a clock so the 60-second cooldown is deterministic under test.
/// </summary>
public sealed class SensorAlertTracker
{
    private static readonly TimeSpan NotificationCooldown = TimeSpan.FromSeconds(60);

    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, TrippedAlert> _tripped = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastNotified = new(StringComparer.OrdinalIgnoreCase);

    public SensorAlertTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Fires whenever the tripped set changes: a trip, an acknowledge, or acknowledge-all.</summary>
    public event Action? TrippedChanged;

    /// <summary>Every sensor currently tripped and not yet acknowledged.</summary>
    public IReadOnlyCollection<TrippedAlert> Tripped => _tripped.Values;

    /// <summary>Count of sensors currently tripped. Backs the sidebar badge.</summary>
    public int TrippedCount => _tripped.Count;

    /// <summary>Whether <paramref name="sensorName"/> is currently tripped.</summary>
    public bool IsTripped(string sensorName) => _tripped.ContainsKey(sensorName);

    /// <summary>Convenience overload that reads <paramref name="sensorName"/>'s trip time from
    /// the injected clock instead of requiring the caller to supply it.</summary>
    public bool Trip(string sensorName, double value, SensorAlert alert) =>
        Trip(sensorName, value, alert, _clock());

    /// <summary>
    /// Records or updates the trip for <paramref name="sensorName"/> (idempotent: a repeat
    /// crossing before acknowledgement just updates the time and value) and fires
    /// <see cref="TrippedChanged"/>. The last-notified time lives separately from the tripped
    /// entry so <see cref="Acknowledge"/> never resets the cooldown.
    /// </summary>
    /// <returns><c>true</c> when no notification has been sent for this sensor in the last 60
    /// seconds, i.e. a notification should be sent now.</returns>
    public bool Trip(string sensorName, double value, SensorAlert alert, DateTimeOffset now)
    {
        _tripped[sensorName] = new TrippedAlert(sensorName, now, value, alert);
        TrippedChanged?.Invoke();

        var shouldNotify = !_lastNotified.TryGetValue(sensorName, out var last)
            || now - last >= NotificationCooldown;
        if (shouldNotify)
        {
            _lastNotified[sensorName] = now;
        }
        return shouldNotify;
    }

    /// <summary>Acknowledges a single tripped sensor. Does not touch the notification cooldown.</summary>
    public void Acknowledge(string sensorName)
    {
        _tripped.Remove(sensorName);
        TrippedChanged?.Invoke();
    }

    /// <summary>Acknowledges every tripped sensor. Does not touch the notification cooldown.</summary>
    public void AcknowledgeAll()
    {
        _tripped.Clear();
        TrippedChanged?.Invoke();
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Remex.Core.Models;

namespace Remex.Desktop.Services;

/// <summary>
/// The single in-memory source of configured sensor alerts. Depends on nothing; persistence
/// stays with the caller (<c>CanvasDashboardViewModel</c> reads/writes <c>DashboardProfile.SensorAlerts</c>).
/// UI-thread affine, not thread-safe: callers are the sensor tick and view models, both of which
/// run on the Avalonia UI thread. <see cref="All"/> is a live dictionary view, so enumerate it on
/// that thread only.
/// </summary>
public sealed class SensorAlertStore
{
    private readonly Dictionary<string, SensorAlert> _alerts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fires once per mutating call (<see cref="Set"/>, <see cref="Remove"/>,
    /// <see cref="Clear"/>) and once for <see cref="ReplaceAll"/>, regardless of whether the
    /// resulting state actually differs from before.</summary>
    public event Action? Changed;

    /// <summary>All configured alerts, in no particular order.</summary>
    public IReadOnlyCollection<SensorAlert> All => _alerts.Values;

    /// <summary>Looks up the alert configured for <paramref name="sensorName"/>, case-insensitively.</summary>
    public bool TryGet(string sensorName, [MaybeNullWhen(false)] out SensorAlert alert) => _alerts.TryGetValue(sensorName, out alert);

    /// <summary>Upserts by <see cref="SensorAlert.SensorName"/>. Fires <see cref="Changed"/> even
    /// when the new record is identical to the stored one, since callers re-apply to sensors.</summary>
    public void Set(SensorAlert alert)
    {
        _alerts[alert.SensorName] = alert;
        Changed?.Invoke();
    }

    /// <summary>Removes the alert configured for <paramref name="sensorName"/>, if any. Fires
    /// <see cref="Changed"/> unconditionally.</summary>
    public void Remove(string sensorName)
    {
        _alerts.Remove(sensorName);
        Changed?.Invoke();
    }

    /// <summary>Removes every configured alert. Fires <see cref="Changed"/> unconditionally.</summary>
    public void Clear()
    {
        _alerts.Clear();
        Changed?.Invoke();
    }

    /// <summary>Replaces the entire set of configured alerts, used on profile load and after
    /// import. Fires <see cref="Changed"/> exactly once.</summary>
    public void ReplaceAll(IEnumerable<SensorAlert> alerts)
    {
        _alerts.Clear();
        foreach (var alert in alerts)
        {
            _alerts[alert.SensorName] = alert;
        }
        Changed?.Invoke();
    }
}

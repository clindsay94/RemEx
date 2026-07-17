namespace Remex.Core.Messages;

/// <summary>
/// Represents a near-realtime snapshot of system hardware stats.
/// Kept deliberately flat to minimize JSON serialization overhead over WebSockets.
/// </summary>
public record TelemetryPayload
{
    public System.Collections.Generic.List<SensorReading> Sensors { get; init; } = new();

    public string UptimeText { get; init; } = string.Empty;
}

public record SensorReading
{
    public string Name { get; init; } = string.Empty;
    public double Value { get; init; }
    public string Unit { get; init; } = string.Empty;
    public string Category { get; init; } = "Other";

    /// <summary>
    /// Telemetry data source: "HWInfo", "WindowsPerf", or "Linux".
    /// Used to display source badges and to deduplicate overlapping sensors.
    /// </summary>
    public string Source { get; init; } = "Unknown";

    /// <summary>
    /// Semantic metric role (Dashboard 2.0). Drives canonical units and client-side card
    /// matching so a card binds by meaning, not by a fragile host name/unit string.
    /// Additive/optional (no protocolVersion bump) — older hosts leave it
    /// <see cref="MetricKind.Unknown"/> and older clients ignore it.
    /// </summary>
    public MetricKind Kind { get; init; } = MetricKind.Unknown;

    /// <summary>
    /// Stable per-sensor id, e.g. "hwi:{dwSensorID:x}:{dwReadingID:x}" (Windows HWiNFO) or a
    /// canonical slug such as "linux:cpu:load". Additive/optional; empty from older hosts.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Device group for UI grouping of the sensor picker (Dashboard 2.0), derived from HWiNFO's
    /// parent-sensor name — e.g. "GPU", "Samsung 980 PRO 2TB [C:]", "Motherboard", "Memory".
    /// Additive/optional; empty from older hosts (clients fall back to <see cref="Category"/>).
    /// </summary>
    public string Group { get; init; } = string.Empty;
}

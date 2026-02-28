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
}

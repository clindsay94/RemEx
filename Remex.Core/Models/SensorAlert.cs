namespace Remex.Core.Models;

/// <summary>Direction in which a sensor value must cross a threshold to fire an alert.</summary>
public enum AlertDirection
{
    Above,
    Below,
}

/// <summary>How urgently the alert should be flagged.</summary>
public enum AlertSeverity
{
    Warning,
    Critical,
}

/// <summary>
/// Configures a threshold alert for a named sensor.
/// Stored in DashboardProfile and evaluated on every telemetry update.
/// </summary>
public record SensorAlert
{
    /// <summary>Sensor name this alert applies to (matches SensorReading.Name).</summary>
    public string SensorName { get; init; } = string.Empty;

    /// <summary>The numeric threshold value.</summary>
    public double Threshold { get; init; }

    /// <summary>Whether the value must be above or below the threshold to trigger.</summary>
    public AlertDirection Direction { get; init; }

    /// <summary>How urgently the alert is classified.</summary>
    public AlertSeverity Severity { get; init; }
}

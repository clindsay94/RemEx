namespace Remex.Core.Models;

/// <summary>
/// Defines the sparkline visualization mode for a sensor card.
/// </summary>
public enum GraphType
{
    /// <summary>Auto-selects based on sensor unit.</summary>
    Auto,

    /// <summary>Vertical bars — good for discrete / RPM data.</summary>
    Bar,

    /// <summary>Connected polyline — good for temperature / voltage trends.</summary>
    Line,

    /// <summary>Filled area under a line — good for frequency / power data.</summary>
    Area,

    /// <summary>Horizontal fill bar — good for percentage utilisation.</summary>
    Gauge,
}

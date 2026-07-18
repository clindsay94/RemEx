using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// Defines the sparkline visualization mode for a sensor card.
/// </summary>
/// <remarks>
/// String-serialized on the wire and in persisted layouts via the generic
/// <see cref="JsonStringEnumConverter{TEnum}"/> (NativeAOT-safe). Declaration order is therefore
/// not load-bearing; members are append-only. Verified safe to add the converter: no code path
/// serialized this enum as an int (it was previously an in-memory-only UI enum).
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<GraphType>))]
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

    /// <summary>Circular / radial progress path — expressive NOC style.</summary>
    Radial,

    // ── Dashboard 2.0 additions (append-only; string-serialized, so order is not load-bearing) ──

    /// <summary>Full 360° ring gauge with a centered value.</summary>
    Ring,

    /// <summary>Big numeric value plus a trend delta; no chart.</summary>
    BigValue,

    /// <summary>Value headline plus a compact mini-sparkline (the smart default).</summary>
    ValueSpark,

    /// <summary>Ambient: tile hue shifts cool → warm with load.</summary>
    HuePulse,

    /// <summary>Ambient: segmented LED-column meter.</summary>
    LedMeter,

    /// <summary>Ambient: two metrics overlaid on one card (requires a secondary sensor).</summary>
    DualMetric,

    /// <summary>Filled area with a vertical gradient and a neon-glow top edge.</summary>
    GlowArea,
}

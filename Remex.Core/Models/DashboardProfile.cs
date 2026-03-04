using System.Collections.Generic;

namespace Remex.Core.Models;

/// <summary>
/// Persisted state of a single card on the Canvas dashboard.
/// </summary>
public record CardState
{
    /// <summary>Unique identifier for this card instance.</summary>
    public string CardId { get; init; } = string.Empty;

    /// <summary>Type discriminator: "Connection", "Actions", "Latency", or "Sensor".</summary>
    public string CardType { get; init; } = string.Empty;

    /// <summary>HWiNFO sensor name this card is bound to (null for non-sensor cards).</summary>
    public string? SensorId { get; init; }

    /// <summary>Canvas.Left coordinate.</summary>
    public double PositionX { get; init; }

    /// <summary>Canvas.Top coordinate.</summary>
    public double PositionY { get; init; }

    /// <summary>Current card width in pixels.</summary>
    public double Width { get; init; } = 220;

    /// <summary>Current card height in pixels.</summary>
    public double Height { get; init; } = 160;

    /// <summary>Stacking order — higher values render on top.</summary>
    public int ZIndex { get; init; }
}

/// <summary>
/// A complete dashboard layout profile, serialised to/from JSON.
/// </summary>
public record DashboardProfile
{
    /// <summary>Human-readable profile name (e.g. "Gaming Mode", "Idle Monitoring").</summary>
    public string ProfileName { get; init; } = "Default";

    /// <summary>Whether cards snap to the nearest grid line on drop.</summary>
    public bool IsSnapToGridEnabled { get; init; }

    /// <summary>Grid cell size in pixels (used when snapping is enabled).</summary>
    public int GridSize { get; init; } = 50;

    /// <summary>Persisted WebSocket host address for the remote connection.</summary>
    public string HostAddress { get; init; } = "ws://localhost:5005/ws";

    /// <summary>All card positions and sizes.</summary>
    public List<CardState> Cards { get; init; } = new();

    /// <summary>Sensor names pinned to the Home overview.</summary>
    public List<string> PinnedSensorIds { get; init; } = new();
}

using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;

namespace Remex.Client.ViewModels;

/// <summary>
/// ViewModel representing a single card on the Canvas workspace.
/// Holds position, size, z-order, and drag state — plus a reference to the
/// concrete content VM (SensorViewModel or ConnectionViewModel).
/// </summary>
public partial class CanvasCardViewModel : ObservableObject
{
    // ═══════════════ Identity ═══════════════

    /// <summary>Unique identifier for this card instance.</summary>
    public string CardId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Type discriminator: "Connection", "Actions", "Latency", or "Sensor".</summary>
    public string CardType { get; init; } = string.Empty;

    /// <summary>Display title shown in the card header.</summary>
    [ObservableProperty]
    private string _cardTitle = string.Empty;

    // ═══════════════ Spatial ═══════════════

    /// <summary>Canvas.Left coordinate.</summary>
    [ObservableProperty]
    private double _positionX;

    /// <summary>Canvas.Top coordinate.</summary>
    [ObservableProperty]
    private double _positionY;

    /// <summary>Card width in pixels.</summary>
    [ObservableProperty]
    private double _width = 220;

    /// <summary>Card height in pixels.</summary>
    [ObservableProperty]
    private double _height = 160;

    /// <summary>Stacking order — higher values render on top.</summary>
    [ObservableProperty]
    private int _zIndex;

    // ═══════════════ Drag State ═══════════════

    /// <summary>True while the card is being actively dragged.</summary>
    [ObservableProperty]
    private bool _isDragging;

    // ═══════════════ Content References ═══════════════

    /// <summary>The sensor VM for sensor-type cards (null for non-sensor).</summary>
    [ObservableProperty]
    private SensorViewModel? _sensor;

    /// <summary>The connection VM for connection/actions/latency cards.</summary>
    [ObservableProperty]
    private ConnectionViewModel? _connection;

    /// <summary>Whether this sensor is pinned to the Home overview.</summary>
    [ObservableProperty]
    private bool _isPinnedToHome;

    // ═══════════════ Serialisation ═══════════════

    /// <summary>
    /// Snapshots the current state into a serialisable <see cref="CardState"/>.
    /// </summary>
    public CardState ToCardState() => new()
    {
        CardId = CardId,
        CardType = CardType,
        SensorId = Sensor?.Name,
        PositionX = PositionX,
        PositionY = PositionY,
        Width = Width,
        Height = Height,
        ZIndex = ZIndex,
    };

    /// <summary>
    /// Creates a CanvasCardViewModel from a persisted <see cref="CardState"/>.
    /// The Sensor/Connection references must be wired separately.
    /// </summary>
    public static CanvasCardViewModel FromCardState(CardState state) => new()
    {
        CardId = state.CardId,
        CardType = state.CardType,
        PositionX = state.PositionX,
        PositionY = state.PositionY,
        Width = state.Width,
        Height = state.Height,
        ZIndex = state.ZIndex,
    };
}

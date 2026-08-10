using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// ViewModel representing a single card on the Canvas workspace.
/// Holds position, size, z-order, and drag state — plus a reference to the
/// concrete content VM (SensorViewModel or ConnectionViewModel).
/// </summary>
public partial class CanvasCardViewModel : ObservableObject
{
    /// <summary>Whether a phone is attached, shared with every other indicator (RemEx-7zzw).</summary>
    /// <remarks>
    /// The Connection card's dot lives in a card-scoped DataTemplate, so it resolves against THIS
    /// type rather than the dashboard. Same singleton as everywhere else, so the card cannot
    /// disagree with the sidebar about whether a phone is there.
    /// </remarks>
    public PhonePresenceMonitor Presence => PhonePresenceMonitor.Instance;

    // ═══════════════ Identity ═══════════════

    /// <summary>Unique identifier for this card instance.</summary>
    public string CardId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Type discriminator: "Connection", "Actions", "Latency", or "Sensor".</summary>
    public string CardType { get; init; } = string.Empty;

    /// <summary>Display title shown in the card header.</summary>
    [ObservableProperty]
    private bool _isSelected;

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

    /// <summary>True while the card's sensor has an active threshold alert.</summary>
    [ObservableProperty]
    private bool _isAlertActive;

    // ═══════════════ Content References ═══════════════

    /// <summary>The sensor VM for sensor-type cards (null for non-sensor).</summary>
    [ObservableProperty]
    private SensorViewModel? _sensor;

    /// <summary>The connection VM for connection/actions/latency cards.</summary>
    [ObservableProperty]
    private ConnectionViewModel? _connection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePinToHomeCommand))]
    private bool _isPinnedToHome;

    /// <summary>
    /// This staged sensor was not in the tick that just arrived (RemEx-yqpa).
    /// </summary>
    /// <remarks>
    /// Marked rather than removed, deliberately: the host alternates its sensor set by category, so a
    /// drawer that evicted on absence would flicker entries out and back in once a second. Keeping
    /// them is also what makes this incapable of orphaning the <see cref="SensorViewModel"/> a placed
    /// card may still be bound to. Only meaningful for staged templates; a placed card is never
    /// marked.
    /// </remarks>
    [ObservableProperty]
    private bool _isStale;

    /// <summary>Action to request a pin toggle, typically wired to the dashboard.</summary>
    private Action? _requestPinToggle;
    public Action? RequestPinToggle
    {
        get => _requestPinToggle;
        set
        {
            if (SetProperty(ref _requestPinToggle, value))
            {
                TogglePinToHomeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanTogglePinToHome))]
    private void TogglePinToHome() => RequestPinToggle?.Invoke();

    private bool CanTogglePinToHome() => RequestPinToggle != null;

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
        // Per-sensor customization (previously dropped on save, incl. the chosen graph type).
        DisplayMode = Sensor?.SelectedGraphType ?? GraphType.Auto,
        SecondarySensorId = Sensor?.SecondarySensorId,
        CustomTitle = Sensor?.CustomTitle,
        ShowValueOverlay = Sensor?.ShowValueOverlay ?? true,
        CardTheme = Sensor?.Theme,
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

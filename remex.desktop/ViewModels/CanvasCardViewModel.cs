using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Desktop.Services;

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

    /// <summary>
    /// True while the card's sensor has an active threshold alert. A pure mirror of
    /// <see cref="SensorViewModel.IsAlertActive"/> — set only by the sensor-change and sensor-property
    /// mirroring below, never by the dashboard directly. Replaces the old 2-second flash timer
    /// (RemEx-8wpvr.2): the card is "hot" for exactly as long as its sensor is.
    /// </summary>
    [ObservableProperty]
    private bool _isAlertActive;

    /// <summary>
    /// True while this card's sensor has crossed its threshold and not yet been acknowledged. Set by
    /// the dashboard from <c>SensorAlertTracker</c>; cleared by <see cref="AcknowledgeAlertCommand"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeAlertCommand))]
    private bool _isAlertTripped;

    /// <summary>True while this card's sensor has a configured alert. Mirrors
    /// <see cref="SensorViewModel.HasAlert"/>, the same way <see cref="IsAlertActive"/> does.</summary>
    [ObservableProperty]
    private bool _hasAlert;

    /// <summary>Trip snapshot backing <see cref="AlertTooltip"/> while <see cref="IsAlertTripped"/> is
    /// true. Set by the dashboard alongside <see cref="IsAlertTripped"/> — this VM does not read the
    /// tracker itself.</summary>
    private TrippedAlert? _trippedAlert;

    /// <summary>
    /// Sets the trip snapshot the dashboard just read from <c>SensorAlertTracker</c>. Called alongside
    /// setting <see cref="IsAlertTripped"/>; pass null when the sensor is not tripped.
    /// </summary>
    public void SetTrippedAlert(TrippedAlert? trip)
    {
        _trippedAlert = trip;
        OnPropertyChanged(nameof(AlertTooltip));
    }

    /// <summary>
    /// Alert bell tooltip: the configured threshold when armed, the trip snapshot when tripped, empty
    /// when there is no alert. Localized from <c>Canvas_AlertBellConfigured</c> /
    /// <c>Canvas_AlertBellTripped</c>, reusing <see cref="AlertDirection"/>/<see cref="AlertSeverity"/>
    /// keys the way <c>ShellViewModel.OnSensorAlertFired</c> does, and
    /// <see cref="SensorReadingFormat.FormatReading"/> for the value/threshold text (RemEx-8wpvr.4).
    /// </summary>
    public string AlertTooltip
    {
        get
        {
            if (IsAlertTripped && _trippedAlert is { } trip)
            {
                var trippedValue = SensorReadingFormat.FormatReading(trip.Value, Sensor?.Unit);
                var localTime = trip.At.LocalDateTime.ToString("t", LocalizationService.Instance.Culture);
                return string.Format(
                    LocalizationService.Instance["Canvas_AlertBellTripped"], localTime, trippedValue);
            }

            if (Sensor?.Alert is { } alert)
            {
                var directionKey = $"{nameof(AlertDirection)}_{alert.Direction}";
                var severityKey = $"{nameof(AlertSeverity)}_{alert.Severity}";
                var direction = LocalizationService.Instance[directionKey];
                var severityText = LocalizationService.Instance[severityKey];
                var threshold = SensorReadingFormat.FormatReading(alert.Threshold, Sensor.Unit);
                return string.Format(
                    LocalizationService.Instance["Canvas_AlertBellConfigured"], direction, threshold, severityText);
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Wires/unwires the live mirror to whichever <see cref="SensorViewModel"/> this card currently
    /// points at. Takes the (old, new) overload the source generator also offers, so unsubscribing
    /// from the previous sensor needs no extra field — a reassignment (a card's sensor reference
    /// changes, e.g. on restore) never leaves a stale subscription alongside the new one.
    /// </summary>
    partial void OnSensorChanged(SensorViewModel? oldValue, SensorViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnSensorPropertyChanged;

        if (newValue is not null)
            newValue.PropertyChanged += OnSensorPropertyChanged;

        IsAlertActive = newValue?.IsAlertActive ?? false;
        HasAlert = newValue?.HasAlert ?? false;
        OnPropertyChanged(nameof(AlertTooltip));
    }

    private void OnSensorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SensorViewModel.IsAlertActive):
                IsAlertActive = Sensor?.IsAlertActive ?? false;
                break;
            case nameof(SensorViewModel.HasAlert):
                HasAlert = Sensor?.HasAlert ?? false;
                OnPropertyChanged(nameof(AlertTooltip));
                break;
        }
    }

    /// <summary>Re-raises <see cref="AlertTooltip"/> on a language change (RemEx-8wpvr.4) — its
    /// getter now reads <c>LocalizationService.Instance</c>, so without this the tooltip would keep
    /// rendering the previous language until <see cref="HasAlert"/> or <see cref="IsAlertTripped"/>
    /// next changed for an unrelated reason (guarded by <c>LocalizedPropertyRefreshTests</c>).
    /// </summary>
    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(AlertTooltip));

    public CanvasCardViewModel()
    {
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
    }

    /// <summary>
    /// Unsubscribes from the current <see cref="Sensor"/>'s <c>PropertyChanged</c> (via
    /// <c>OnSensorChanged</c>, triggered by setting it null), from <c>LocalizationService.Instance</c>
    /// (RemEx-8wpvr.4), and drops the <see cref="Sensor"/> reference. Call this at every site that
    /// removes a card from the canvas for good — both are process-lifetime singletons, so a
    /// discarded card left subscribed to either stays rooted forever (RemEx-8wpvr.2, MEDIUM). A
    /// no-op for the sensor half on non-sensor cards, which never have a <see cref="Sensor"/> set.
    /// </summary>
    public void Detach()
    {
        Sensor = null;
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
    }

    /// <summary>Action to acknowledge this card's tripped alert, wired by the dashboard to
    /// <c>SensorAlertTracker.Acknowledge(Sensor.Name)</c> — the same delegate shape as
    /// <see cref="RequestPinToggle"/>, since this VM does not hold the tracker itself.</summary>
    public Action? RequestAcknowledgeAlert { get; set; }

    [RelayCommand(CanExecute = nameof(IsAlertTripped))]
    private void AcknowledgeAlert() => RequestAcknowledgeAlert?.Invoke();

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
    /// <remarks>
    /// The non-visual half of the stale mark (RemEx-lki2r) - the screen-reader HelpText a screen
    /// reader gets instead of the 0.55 opacity a sighted user sees - lives entirely in
    /// <c>CanvasView.axaml</c>'s <c>material|Card.stale</c> style, as a second Setter alongside the
    /// Opacity one, rather than as a computed property here. A per-card computed property needed a
    /// per-card subscription to <c>LocalizationService.Instance.PropertyChanged</c> to refresh on a
    /// language switch (review, RemEx-lki2r) - and with ~470 staged templates in a session and no
    /// removal path that calls Dispose, that subscription rooted every card and its
    /// <see cref="SensorViewModel"/> history buffers for the process lifetime. The XAML setter needs
    /// none of that: <c>{local:Localize}</c> already refreshes on its own, and the SAME style
    /// selector that gates the opacity gates the HelpText, so the two cannot disagree by
    /// construction - zero per-item state either way.
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
        // Spec B (RemEx-4kv0g.3.2): a themed (follow-theme) card persists as null, not the "Default"
        // preset it happens to hold, so it keeps following the theme after a restart instead of
        // freezing on whatever the theme resolved to at save time.
        CardTheme = Sensor is { IsThemed: false } ? Sensor.Theme : null,
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

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Desktop.Services;
using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Canvas workspace. Manages placed cards, the staging
/// drawer for new sensors, snap-to-grid logic, and persistence triggers.
/// </summary>
public partial class CanvasDashboardViewModel : ObservableObject, IDisposable
{
    private readonly DashboardLayoutService _layoutService;
    private readonly ShellViewModel _shell;
    private DashboardProfile _profile = new();
    private int _nextZIndex = 1;
    private bool _isInitialized;
    private DashboardProfile? _pendingSyncProfile;
    private bool _isRefreshingSensorActivation;

    // ═══════════════ Undo / Redo ═══════════════

    private readonly Stack<ICanvasOperation> _undoStack = new();
    private readonly Stack<ICanvasOperation> _redoStack = new();
    private readonly Dictionary<string, (double X, double Y)> _dragStartPositions = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool _canRedo;

    // ═══════════════ Sensor Alerts ═══════════════

    private readonly Dictionary<string, SensorAlert> _sensorAlerts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _subscribedSensorNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a sensor crosses its configured threshold.</summary>
    public event Action<SensorAlert>? SensorAlertFired;

    /// <summary>Raised when the view should open the "Set Alert" dialog for a sensor.</summary>
    public event Action<string, SensorAlert?>? ShowSetAlertRequested;

    // ═══════════════ Minimap ═══════════════

    [ObservableProperty]
    private bool _isMinimapVisible;

    [ObservableProperty]
    private Rect _minimapViewportRect;

    public ConnectionViewModel Connection { get; }

    /// <summary>Raised when the view should reset the canvas pan/zoom to origin.</summary>
    public event EventHandler? ResetViewRequested;

    [RelayCommand]
    private void ResetView() => ResetViewRequested?.Invoke(this, EventArgs.Empty);

    // ═══════════════ Undo / Redo Commands ═══════════════

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        var op = _undoStack.Pop();
        op.Undo();
        _redoStack.Push(op);
        UpdateUndoRedoState();
        TriggerSave();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        var op = _redoStack.Pop();
        op.Execute();
        _undoStack.Push(op);
        UpdateUndoRedoState();
        TriggerSave();
    }

    private void PushOperation(ICanvasOperation op)
    {
        _undoStack.Push(op);
        _redoStack.Clear();
        UpdateUndoRedoState();
    }

    private void UpdateUndoRedoState()
    {
        CanUndo = _undoStack.Count > 0;
        CanRedo = _redoStack.Count > 0;
    }

    /// <summary>
    /// Records the drag-start position for a card (and all currently selected cards).
    /// Called by DraggableCard before a drag begins.
    /// </summary>
    public void BeginCardDrag(CanvasCardViewModel card)
    {
        _dragStartPositions[card.CardId] = (card.PositionX, card.PositionY);
        foreach (var sel in SelectedCards)
            _dragStartPositions[sel.CardId] = (sel.PositionX, sel.PositionY);
    }

    /// <summary>
    /// After a drop, compares final positions with recorded start positions and pushes
    /// a MoveCardOperation (or GroupMoveOperation) onto the undo stack if anything moved.
    /// </summary>
    public void RecordMoveIfChanged(CanvasCardViewModel primaryCard, double dragStartX, double dragStartY)
    {
        var moved = new List<MoveCardOperation>();

        // Check the primary card using the caller-supplied start position
        if (Math.Abs(primaryCard.PositionX - dragStartX) > 1 ||
            Math.Abs(primaryCard.PositionY - dragStartY) > 1)
        {
            moved.Add(new MoveCardOperation(primaryCard, dragStartX, dragStartY,
                primaryCard.PositionX, primaryCard.PositionY));
        }

        // Check selected cards from the dictionary
        foreach (var sel in SelectedCards)
        {
            if (sel == primaryCard) continue;
            if (!_dragStartPositions.TryGetValue(sel.CardId, out var start)) continue;
            if (Math.Abs(sel.PositionX - start.X) > 1 || Math.Abs(sel.PositionY - start.Y) > 1)
            {
                moved.Add(new MoveCardOperation(sel, start.X, start.Y,
                    sel.PositionX, sel.PositionY));
            }
        }

        _dragStartPositions.Clear();

        if (moved.Count == 0) return;
        PushOperation(moved.Count == 1 ? (ICanvasOperation)moved[0] : new GroupMoveOperation(moved));
    }

    // ═══════════════ Minimap ═══════════════

    [RelayCommand]
    private void ToggleMinimap() => IsMinimapVisible = !IsMinimapVisible;

    /// <summary>
    /// Called by the view whenever the ZoomableCanvas pan/zoom state changes.
    /// Recomputes the minimap viewport rectangle.
    /// </summary>
    public void UpdateMinimapViewport(double offsetX, double offsetY, double zoom,
                                      double viewportWidth, double viewportHeight)
    {
        if (zoom <= 0 || viewportWidth <= 0 || viewportHeight <= 0) return;

        const double worldW = 4000, worldH = 4000;
        const double mmW = 200, mmH = 160;

        double worldLeft  = -offsetX / zoom;
        double worldTop   = -offsetY / zoom;
        double worldViewW = viewportWidth  / zoom;
        double worldViewH = viewportHeight / zoom;

        double x = Math.Max(0, worldLeft  * mmW / worldW);
        double y = Math.Max(0, worldTop   * mmH / worldH);
        double w = Math.Min(mmW - x, worldViewW * mmW / worldW);
        double h = Math.Min(mmH - y, worldViewH * mmH / worldH);

        MinimapViewportRect = new Rect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>
    /// Raised when the minimap was clicked; the view should pan the ZoomableCanvas
    /// so that (WorldX, WorldY) is centred in the viewport.
    /// </summary>
    public event Action<double, double>? MinimapPanRequested;

    /// <summary>Called by the CanvasMinimap control when the user clicks inside it.</summary>
    public void OnMinimapClicked(double worldX, double worldY)
        => MinimapPanRequested?.Invoke(worldX, worldY);

    // ═══════════════ Sensor Alerts ═══════════════

    [RelayCommand]
    private void SetAlert(CanvasCardViewModel? card)
    {
        if (card?.Sensor is null) return;
        var sensorName = card.Sensor.Name;
        _sensorAlerts.TryGetValue(sensorName, out var existing);
        ShowSetAlertRequested?.Invoke(sensorName, existing);
    }

    /// <summary>
    /// Applies an alert result returned from the SetAlertDialog.
    /// Pass null to remove the alert; pass a SensorAlert with empty SensorName to clear.
    /// </summary>
    public void ApplySensorAlert(string sensorName, SensorAlert? result)
    {
        if (result is null || string.IsNullOrEmpty(result.SensorName))
        {
            // Clear alert
            _sensorAlerts.Remove(sensorName);
            ApplyAlertToSensors(sensorName, null);
        }
        else
        {
            _sensorAlerts[sensorName] = result;
            ApplyAlertToSensors(sensorName, result);
        }
        TriggerSave();
    }

    private void ApplyAlertToSensors(string sensorName, SensorAlert? alert)
    {
        foreach (var card in Cards.Concat(StagedCards)
            .Where(c => c.CardType == "Sensor" &&
                   string.Equals(c.Sensor?.Name, sensorName, StringComparison.OrdinalIgnoreCase)))
        {
            if (card.Sensor is not null)
                card.Sensor.Alert = alert;
        }
    }

    private void OnSensorAlertTriggered(SensorAlert alert)
    {
        // Flash all canvas cards for the sensor
        var affected = Cards
            .Where(c => c.CardType == "Sensor" &&
                   string.Equals(c.Sensor?.Name, alert.SensorName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var c in affected) c.IsAlertActive = true;

        SensorAlertFired?.Invoke(alert);

        // Auto-reset the flash after 2 s
        _ = Task.Delay(2000).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var c in affected) c.IsAlertActive = false;
            }));
    }

    /// <summary>Cards currently placed on the canvas.</summary>
    public ObservableCollection<CanvasCardViewModel> Cards { get; } = new();

    /// <summary>Newly discovered sensors waiting to be placed by the user.</summary>
    public ObservableCollection<CanvasCardViewModel> StagedCards { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<CanvasCardViewModel> SelectedCards { get; } = new();
    public ObservableCollection<SensorActivationItem> SensorActivationItems { get; } = new();

    [RelayCommand]
    public void ToggleCardSelection(CanvasCardViewModel card)
    {
        card.IsSelected = !card.IsSelected;
        if (card.IsSelected)
            SelectedCards.Add(card);
        else
            SelectedCards.Remove(card);
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var c in SelectedCards)
            c.IsSelected = false;
        SelectedCards.Clear();
    }


    [ObservableProperty]
    private bool _isSnapToGridEnabled;

    [ObservableProperty]
    private int _gridSize = 50;

    [ObservableProperty]
    private bool _hasStagedCards;

    [ObservableProperty]
    private bool _isStagingDrawerOpen;

    public CanvasDashboardViewModel(
        ConnectionViewModel connection,
        DashboardLayoutService layoutService,
        ShellViewModel shell)
    {
        Connection = connection;
        _layoutService = layoutService;
        _shell = shell;

        // Listen for telemetry updates to create/update sensor cards.
        Connection.PropertyChanged += OnConnectionPropertyChanged;

        Connection.LayoutProfileReceived += OnLayoutProfileReceived;

        Cards.CollectionChanged += OnCardsCollectionChanged;

        StagedCards.CollectionChanged += OnStagedCardsCollectionChanged;

        SelectedCards.CollectionChanged += OnSelectedCardsChanged;
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Connection.Telemetry) && Connection.Telemetry != null)
        {
            ProcessTelemetry(Connection.Telemetry);
        }
        else if (e.PropertyName == nameof(Connection.IsConnected) && !Connection.IsConnected)
        {
            CleanupSensorSubscriptions();
        }
    }

    private void OnLayoutProfileReceived(DashboardProfile profile)
        => _ = OnLayoutProfileReceivedAsync(profile).ContinueWith(
            t => Debug.WriteLine($"[Remex] OnLayoutProfileReceivedAsync failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);

    private async Task OnLayoutProfileReceivedAsync(DashboardProfile profile)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_isInitialized)
            {
                _pendingSyncProfile = profile;
                return;
            }
            ApplyProfile(profile);
        });
    }

    private void OnCardsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RefreshSensorActivationItems();
    }

    private void OnStagedCardsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (CanvasCardViewModel card in e.NewItems)
            {
                card.RequestPinToggle = () => TogglePinToHome(card);
            }
        }
        HasStagedCards = StagedCards.Count > 0;
        RefreshSensorActivationItems();
    }

    /// <summary>
    /// Loads the persisted profile and restores card positions.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        var localProfile = await _layoutService.LoadAsync();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _isInitialized = true;

            // Seed the first-visit coach mark from whatever profile is now loaded.
            InitCoachMark();

            // If a host sync arrived before we finished local load, prioritize the host.
            if (_pendingSyncProfile != null)
            {
                ApplyProfile(_pendingSyncProfile);
                _pendingSyncProfile = null;
                return;
            }

            // Otherwise, use the local profile.
            _profile = localProfile;
            IsSnapToGridEnabled = _profile.IsSnapToGridEnabled;
            GridSize = _profile.GridSize;

            // Restore sensor alerts from profile.
            foreach (var alert in _profile.SensorAlerts ?? Enumerable.Empty<SensorAlert>())
                _sensorAlerts[alert.SensorName] = alert;

            // Restore non-sensor cards from profile.
            foreach (var state in (_profile.Cards ?? Enumerable.Empty<CardState>()).Where(c => c.CardType != "Sensor"))
            {
                var card = CanvasCardViewModel.FromCardState(state);
                card.CardTitle = state.CardType;
                card.Connection = Connection;
                card.RequestPinToggle = () => TogglePinToHome(card);
                Cards.Add(card);
                TrackZIndex(card.ZIndex);
            }

            // Create default cards if this is a fresh profile.
            EnsureDefaultCards();

            // If we're already connected, maybe the host is empty?
            // We should probably push our local layout if the host didn't send anything.
            // But for now, let's just ensure we stay in sync.

            RefreshSensorActivationItems();

            // Final refresh of home pinned sensors after local load.
            if (_shell.CurrentView is HomeViewModel home)
                home.RefreshPinnedSensors();
        });
    }

    [RelayCommand]
    private void MoveSensorUp(SensorActivationItem item)
    {
        if (item is null || !item.IsActive) return;

        var activeOrder = SensorActivationItems.Where(i => i.IsActive).ToList();
        var index = activeOrder.FindIndex(i => string.Equals(i.SensorName, item.SensorName, StringComparison.OrdinalIgnoreCase));
        if (index <= 0) return;

        var previous = activeOrder[index - 1];
        SwapPrimarySensorCardY(item.SensorName, previous.SensorName);
        TriggerSave();
        RefreshSensorActivationItems();
    }

    [RelayCommand]
    private void MoveSensorDown(SensorActivationItem item)
    {
        if (item is null || !item.IsActive) return;

        var activeOrder = SensorActivationItems.Where(i => i.IsActive).ToList();
        var index = activeOrder.FindIndex(i => string.Equals(i.SensorName, item.SensorName, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index >= activeOrder.Count - 1) return;

        var next = activeOrder[index + 1];
        SwapPrimarySensorCardY(item.SensorName, next.SensorName);
        TriggerSave();
        RefreshSensorActivationItems();
    }

    private void EnsureDefaultCards()
    {
        if (!Cards.Any(c => c.CardType == "Connection"))
        {
            var card = new CanvasCardViewModel
            {
                CardType = "Connection",
                CardTitle = LocalizationService.Instance["Canvas_Connection"],
                Connection = Connection,
                PositionX = 20,
                PositionY = 20,
                Width = 240,
                Height = 180,
                ZIndex = _nextZIndex++,
            };
            card.RequestPinToggle = () => TogglePinToHome(card);
            Cards.Add(card);
        }

        if (!Cards.Any(c => c.CardType == "Actions"))
        {
            var card = new CanvasCardViewModel
            {
                CardType = "Actions",
                CardTitle = LocalizationService.Instance["Canvas_Actions"],
                Connection = Connection,
                PositionX = 280,
                PositionY = 20,
                Width = 240,
                Height = 180,
                ZIndex = _nextZIndex++,
            };
            card.RequestPinToggle = () => TogglePinToHome(card);
            Cards.Add(card);
        }

        if (!Cards.Any(c => c.CardType == "Latency"))
        {
            var card = new CanvasCardViewModel
            {
                CardType = "Latency",
                CardTitle = LocalizationService.Instance["Canvas_Latency"],
                Connection = Connection,
                PositionX = 540,
                PositionY = 20,
                Width = 360,
                Height = 220,
                ZIndex = _nextZIndex++,
            };
            card.RequestPinToggle = () => TogglePinToHome(card);
            Cards.Add(card);
        }
    }

    // ═══════════════ Card Interactions ═══════════════

    /// <summary>Bumps a card to the top of the Z-order stack.</summary>
    public void BringToFront(CanvasCardViewModel card)
    {
        card.ZIndex = _nextZIndex++;
    }

    /// <summary>
    /// Called when a card is released after dragging.
    /// If dropped over the staging drawer (right side), returns card to staging.
    /// Otherwise applies snap-to-grid if enabled, then triggers a debounced save.
    /// </summary>
    public void OnCardDropped(CanvasCardViewModel card, double dropXInView)
    {
        // If the drawer is open and the card was dropped in the rightmost region,
        // return it to staging.
        if (IsStagingDrawerOpen && CanvasViewWidth > 0 && dropXInView > CanvasViewWidth - 260)
        {
            ReturnToStaging(card);
            return;
        }

        if (IsSnapToGridEnabled && GridSize > 0)
        {
            card.PositionX = Math.Round(card.PositionX / GridSize) * GridSize;
            card.PositionY = Math.Round(card.PositionY / GridSize) * GridSize;

            // Also snap all other selected cards.
            if (card.IsSelected)
            {
                foreach (var other in SelectedCards)
                {
                    if (other == card) continue;
                    other.PositionX = Math.Round(other.PositionX / GridSize) * GridSize;
                    other.PositionY = Math.Round(other.PositionY / GridSize) * GridSize;
                }
            }
        }

        TriggerSave();
    }

    /// <summary>
    /// Returns a card from the canvas back to the staging drawer.
    /// </summary>
    public void ReturnToStaging(CanvasCardViewModel card)
    {
        PushOperation(new RemoveCardOperation(Cards, card));
        Cards.Remove(card);

        // Sensor templates remain in staging; removing from canvas is enough.
        if (card.CardType == "Sensor")
        {
            TriggerSave();
            return;
        }

        card.PositionX = 0;
        card.PositionY = 0;
        StagedCards.Add(card);
        TriggerSave();
    }

    /// <summary>Width of the canvas view area in pixels, set by the view.</summary>
    public double CanvasViewWidth { get; set; }

    /// <summary>Called when a card finishes resizing.</summary>
    public void OnCardResized(CanvasCardViewModel card)
    {
        TriggerSave();
    }

    // ═══════════════ Staging Drawer ═══════════════

    /// <summary>
    /// Places a card from the staging drawer onto the canvas at a default position.
    /// </summary>
    [RelayCommand]
    private void PlaceFromStaging(CanvasCardViewModel card)
    {
        // Sensor entries in the staging drawer act as reusable templates,
        // so users can add multiple cards for the same hardware metric.
        if (card.CardType == "Sensor" && card.Sensor is not null)
        {
            var lastSensorCard = Cards.LastOrDefault(c => c.CardType == "Sensor");

            var clone = new CanvasCardViewModel
            {
                CardType = "Sensor",
                CardTitle = card.CardTitle,
                Sensor = card.Sensor,
                IsPinnedToHome = card.IsPinnedToHome,
                Width = card.Width,
                Height = card.Height,
                PositionX = (lastSensorCard?.PositionX ?? 0) + 40,
                PositionY = (lastSensorCard?.PositionY ?? 200) + 40,
                ZIndex = _nextZIndex++,
            };

            Cards.Add(clone);
            TriggerSave();
            return;
        }

        if (!StagedCards.Remove(card)) return;

        // Cascade new cards diagonally from the last placed card.
        var lastCard = Cards.LastOrDefault();
        card.PositionX = (lastCard?.PositionX ?? 0) + 40;
        card.PositionY = (lastCard?.PositionY ?? 200) + 40;
        card.ZIndex = _nextZIndex++;

        Cards.Add(card);
        PushOperation(new AddCardOperation(Cards, card));
        TriggerSave();
    }

    /// <summary>
    /// Toggles a sensor card's pinned state on the Home overview.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTogglePinToHome))]
    private void TogglePinToHome(CanvasCardViewModel card)
    {
        if (card.CardType != "Sensor" || card.Sensor is null) return;
        SetCardPinned(card, !card.IsPinnedToHome);
    }

    /// <summary>
    /// Sets a sensor card's pinned-to-home state to a specific value (not a toggle), updating the
    /// profile's pinned-id list, persisting, and refreshing Home. The single mutation point so both
    /// the per-card toggle and the undoable group pin (<see cref="PinCardOperation"/>) stay consistent.
    /// </summary>
    private void SetCardPinned(CanvasCardViewModel card, bool pinned)
    {
        if (card.CardType != "Sensor" || card.Sensor is null) return;

        var sensorName = card.Sensor.Name;
        if (string.IsNullOrWhiteSpace(sensorName)) return;

        card.IsPinnedToHome = pinned;

        if (_profile.PinnedSensorIds == null)
        {
            _profile = _profile with { PinnedSensorIds = new() };
        }

        if (pinned)
        {
            if (!_profile.PinnedSensorIds.Contains(sensorName, StringComparer.OrdinalIgnoreCase))
                _profile.PinnedSensorIds.Add(sensorName);
        }
        else
        {
            _profile.PinnedSensorIds.RemoveAll(id => string.Equals(id, sensorName, StringComparison.OrdinalIgnoreCase));
        }

        TriggerSave();

        // Refresh home pinned sensors immediately.
        if (_shell.CurrentView is HomeViewModel home)
            home.RefreshPinnedSensors();
    }

    // ═══════════════ Floating action bar (multi-select group ops) ═══════════════

    /// <summary>True while one or more cards are selected — drives the floating Pin/Remove/Done bar.</summary>
    [ObservableProperty]
    private bool _hasSelection;

    /// <summary>Number of currently selected cards (shown on the floating action bar).</summary>
    [ObservableProperty]
    private int _selectionCount;

    private void OnSelectedCardsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        SelectionCount = SelectedCards.Count;
        HasSelection = SelectedCards.Count > 0;
        OnPropertyChanged(nameof(CoachMarkVisible));
    }

    /// <summary>Removes every selected card as one undoable operation (routed through RemoveCardOperation).</summary>
    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedCards.Count == 0) return;

        var ops = new List<ICanvasOperation>();
        foreach (var card in SelectedCards.ToList())
        {
            ops.Add(new RemoveCardOperation(Cards, card));
            Cards.Remove(card);
        }

        ClearSelection();
        PushOperation(ops.Count == 1 ? ops[0] : new CompositeOperation(ops));
        TriggerSave();
    }

    /// <summary>
    /// Pins (or unpins) every selected sensor card as one undoable operation. Pins all unless every
    /// selected card is already pinned, in which case it unpins them — a single intuitive toggle.
    /// </summary>
    [RelayCommand]
    private void PinSelected()
    {
        var sensorCards = SelectedCards
            .Where(c => c.CardType == "Sensor" && c.Sensor is not null && !string.IsNullOrWhiteSpace(c.Sensor!.Name))
            .ToList();
        if (sensorCards.Count == 0) return;

        bool targetPinned = !sensorCards.All(c => c.IsPinnedToHome);

        var ops = new List<ICanvasOperation>();
        foreach (var card in sensorCards)
        {
            if (card.IsPinnedToHome == targetPinned) continue;
            ops.Add(new PinCardOperation(card, targetPinned, SetCardPinned));
            SetCardPinned(card, targetPinned);
        }

        if (ops.Count == 0) return;
        PushOperation(ops.Count == 1 ? ops[0] : new CompositeOperation(ops));
    }

    // ═══════════════ Coach marks (first-visit contextual hint) ═══════════════

    private const string CanvasCoachKey = "canvas";

    /// <summary>Raw first-visit flag — true until the user dismisses the canvas coach mark.</summary>
    [ObservableProperty]
    private bool _showCoachMark;

    /// <summary>
    /// The coach hint is shown only on first visit AND while nothing is selected — the action bar and
    /// the hint must never fight for the same corner (spec 5.7 visibility guard).
    /// </summary>
    public bool CoachMarkVisible => ShowCoachMark && !HasSelection;

    partial void OnShowCoachMarkChanged(bool value) => OnPropertyChanged(nameof(CoachMarkVisible));

    /// <summary>Seeds coach visibility from the persisted <see cref="DashboardProfile.SeenCoachMarks"/>.</summary>
    private void InitCoachMark()
    {
        var seen = _layoutService.CurrentProfile.SeenCoachMarks ?? new();
        ShowCoachMark = !seen.Contains(CanvasCoachKey);
    }

    /// <summary>Dismisses the hint ("Got it") and records it as seen so it never auto-shows again.</summary>
    [RelayCommand]
    private void DismissCoachMark()
    {
        ShowCoachMark = false;

        var baseProfile = _layoutService.CurrentProfile ?? _profile;
        var seen = baseProfile.SeenCoachMarks ?? new();
        if (!seen.Contains(CanvasCoachKey)) seen.Add(CanvasCoachKey);
        _profile = baseProfile with { SeenCoachMarks = seen };
        _layoutService.RequestSave(_profile);
    }

    /// <summary>Replays the contextual hint on demand (the "?" affordance).</summary>
    [RelayCommand]
    private void ReplayCoachMark() => ShowCoachMark = true;

    private bool CanTogglePinToHome(CanvasCardViewModel? card)
    {
        return card is not null &&
               card.CardType == "Sensor" &&
               card.Sensor is not null &&
               !string.IsNullOrWhiteSpace(card.Sensor.Name);
    }

    // ═══════════════ Navigation ═══════════════

    [RelayCommand]
    private void NavigateBack()
    {
        _shell.NavigateToHome();
        // Refresh Home's pinned sensors when returning.
        if (_shell.CurrentView is HomeViewModel home)
            home.RefreshPinnedSensors();
    }

    [RelayCommand]
    private void ToggleStagingDrawer()
    {
        IsStagingDrawerOpen = !IsStagingDrawerOpen;
    }

    // ═══════════════ Layout Save / Sync ═══════════════

    [ObservableProperty]
    private string _layoutStatus = string.Empty;

    // ═══════════════ P8-I: Snapshot Export Status ═══════════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshotStatus))]
    private string _snapshotStatus = string.Empty;

    public bool HasSnapshotStatus => !string.IsNullOrEmpty(SnapshotStatus);

    /// <summary>Called by the view after a successful snapshot export or clipboard copy.</summary>
    public void SetSnapshotStatus(string message)
    {
        SnapshotStatus = message;
        _ = Task.Delay(4000).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() => SnapshotStatus = string.Empty));
    }

    /// <summary>
    /// Explicitly saves the current layout to disk and pushes it to the host (if connected).
    /// </summary>
    [RelayCommand]
    private async Task SaveLayoutAsync()
    {
        TriggerSave();
        await _layoutService.FlushAsync();
        LayoutStatus = "Layout Saved!";
        _ = Task.Delay(3000).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() => LayoutStatus = string.Empty));
    }

    /// <summary>
    /// Requests the layout from the connected host. Falls back to local storage if offline.
    /// </summary>
    [RelayCommand]
    private async Task SyncLayoutAsync()
    {
        if (Connection.IsConnected)
        {
            // Request the host to re-send its stored layout.
            var msg = new RemexMessage
            {
                Type = MessageTypes.LayoutRequest,
            };
            await Connection.SendAsync(msg);
            LayoutStatus = "Sync requested…";
        }
        else
        {
            // Offline — reload from local storage.
            var profile = await _layoutService.LoadAsync();
            await Dispatcher.UIThread.InvokeAsync(() => ApplyProfile(profile));
            LayoutStatus = "Loaded from local storage";
        }

        _ = Task.Delay(3000).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() => LayoutStatus = string.Empty));
    }

    // ═══════════════ Telemetry Processing ═══════════════

    /// <summary>
    /// The stable identity for a telemetry sensor: the host-stamped <see cref="SensorReading.Id"/>
    /// when present (it survives host-side relabels and disambiguates two same-named sensors),
    /// otherwise the display name. Binding cards by this rather than the raw name is the PC mirror of
    /// the host's Id/Kind stamping (RemEx-km0i.14): a renamed or duplicate-named sensor no longer
    /// spawns an orphan card. Note this is a <em>runtime-match</em> refinement only — persisted
    /// <see cref="CardState.SensorId"/> stays the name because it also drives the card title and the
    /// pin key, so re-keying storage would be a separate, breaking migration.
    /// </summary>
    private static string SensorIdentity(SensorReading reading) =>
        !string.IsNullOrWhiteSpace(reading.Id) ? reading.Id!
        : string.IsNullOrWhiteSpace(reading.Name) ? "Unknown" : reading.Name;

    private static string? SensorIdentity(SensorViewModel? sensor) =>
        sensor?.RawReading is { } r ? SensorIdentity(r) : sensor?.Name;

    private void ProcessTelemetry(TelemetryPayload payload)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (payload.Sensors == null) return;

            foreach (var reading in payload.Sensors)
            {
                var sensorName = string.IsNullOrWhiteSpace(reading.Name) ? "Unknown" : reading.Name;
                // Match by stable identity (host-stamped Id, name fallback), not raw name, so a live
                // relabel or two same-named sensors bind correctly (RemEx-km0i.14).
                var identity = SensorIdentity(reading);

                var sensorVms = Cards
                    .Where(c => c.CardType == "Sensor" && SensorIdentity(c.Sensor) == identity)
                    .Select(c => c.Sensor)
                    .Concat(
                        StagedCards
                            .Where(c => c.CardType == "Sensor" && SensorIdentity(c.Sensor) == identity)
                            .Select(c => c.Sensor))
                    .Where(s => s is not null)
                    .Select(s => s!)
                    .Distinct()
                    .ToList();

                // First time seeing this sensor in the current session.
                if (sensorVms.Count == 0)
                {
                    var sensor = new SensorViewModel();

                    // Persist per-sensor customization (color, title, overlay, graph type) as it changes.
                    sensor.PropertyChanged += OnSensorCustomizationChanged;

                    // Apply any persisted alert for this sensor.
                    if (_sensorAlerts.TryGetValue(sensorName, out var existingAlert))
                        sensor.Alert = existingAlert;

                    // Subscribe once per stable sensor identity to prevent duplicate firings on
                    // reconnect or a live host relabel (see SensorIdentity; RemEx-km0i.14).
                    if (_subscribedSensorNames.Add(identity))
                    {
                        sensor.AlertTriggered -= OnSensorAlertTriggered;
                        sensor.AlertTriggered += OnSensorAlertTriggered;
                    }

                    // Keep one reusable template in staging so users can add more cards.
                    // Default size is a clean multiple of the 50px snap grid (4×3 cells) so
                    // freshly-dropped cards tile edge-to-edge without manual resizing.
                    var staged = new CanvasCardViewModel
                    {
                        CardType = "Sensor",
                        CardTitle = sensorName,
                        Sensor = sensor,
                        Width = 200,
                        Height = 150,
                    };
                    staged.RequestPinToggle = () => TogglePinToHome(staged);
                    StagedCards.Add(staged);

                    // Restore every persisted card for this sensor.
                    foreach (var saved in (_profile.Cards ?? Enumerable.Empty<CardState>()).Where(c => c.CardType == "Sensor" && c.SensorId == sensorName))
                    {
                        if (Cards.Any(c => c.CardId == saved.CardId))
                        {
                            continue;
                        }

                        var restored = CanvasCardViewModel.FromCardState(saved);
                        restored.CardTitle = sensorName;
                        restored.Sensor = sensor;
                        ApplyPersistedSensorState(sensor, saved);
                        var pinnedIds = _profile.PinnedSensorIds ?? Enumerable.Empty<string>();
                        restored.IsPinnedToHome = pinnedIds.Contains(sensorName);
                        restored.RequestPinToggle = () => TogglePinToHome(restored);
                        Cards.Add(restored);
                        TrackZIndex(saved.ZIndex);
                    }

                    sensorVms.Add(sensor);
                }

                foreach (var sensorVm in sensorVms)
                {
                    sensorVm.Update(reading);
                }
            }
        });
    }

    // ═══════════════ Persistence ═══════════════

    /// <summary>
    /// Applies a persisted card's per-sensor customization (graph type, custom title, value-overlay
    /// visibility, and color theme) onto the shared <see cref="SensorViewModel"/> during layout restore.
    /// One VM is shared across all cards for a sensor name, so this is per-sensor by design.
    /// </summary>
    private static void ApplyPersistedSensorState(SensorViewModel sensor, CardState state)
    {
        sensor.SelectedGraphType = state.DisplayMode;
        sensor.SecondarySensorId = state.SecondarySensorId;
        if (!string.IsNullOrWhiteSpace(state.CustomTitle))
            sensor.CustomTitle = state.CustomTitle;
        sensor.ShowValueOverlay = state.ShowValueOverlay;
        if (state.CardTheme is not null)
            sensor.Theme = state.CardTheme;
    }

    /// <summary>
    /// Persists the layout when a user changes a sensor's customization (color theme, custom title,
    /// value-overlay visibility, or graph type). Telemetry updates (Name/Value/Unit) are ignored so
    /// the incoming reading stream does not spam saves.
    /// </summary>
    private void OnSensorCustomizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SensorViewModel.Theme)
            or nameof(SensorViewModel.CustomTitle)
            or nameof(SensorViewModel.ShowValueOverlay)
            or nameof(SensorViewModel.SelectedGraphType)
            or nameof(SensorViewModel.SecondarySensorId))
        {
            TriggerSave();
        }
    }

    private void TriggerSave()
    {
        // Use the current profile from the layout service as the base to avoid overwriting
        // global flags (like HasCompletedTutorial) set by other components.
        var baseProfile = _layoutService.CurrentProfile ?? _profile;

        // Sensor cards are restored lazily as telemetry arrives, so the live canvas is only a full
        // picture once every sensor has reported. Saving the raw live set would delete every sensor
        // card whose sensor hasn't reported yet (RemEx-jwvg). "Materialized" = sensors that currently
        // have a SensorViewModel on the canvas or in staging (created on first sighting, never dropped
        // on disconnect) — for those the live canvas is authoritative; the rest are preserved from the
        // persisted profile so an early/partial save can't erode the layout.
        var materializedSensorNames = new HashSet<string>(
            Cards.Concat(StagedCards)
                .Where(c => c.CardType == "Sensor" && c.Sensor != null)
                .Select(c => c.Sensor!.Name),
            StringComparer.OrdinalIgnoreCase);

        var liveCardStates = Cards.Select(c => c.ToCardState()).ToList();
        var livePinned = Cards
            .Where(c => c.CardType == "Sensor" && c.IsPinnedToHome && c.Sensor != null)
            .Select(c => c.Sensor!.Name);

        var profile = baseProfile with
        {
            IsSnapToGridEnabled = IsSnapToGridEnabled,
            GridSize = GridSize,
            HostAddress = Connection.HostAddress,
            Cards = CanvasLayoutMerge.MergeCards(baseProfile.Cards, liveCardStates, materializedSensorNames),
            PinnedSensorIds = CanvasLayoutMerge.MergePinnedSensors(baseProfile.PinnedSensorIds, livePinned, materializedSensorNames),
            SensorAlerts = _sensorAlerts.Values.ToList(),
        };

        _profile = profile;

        _layoutService.RequestSave(profile);

        if (Connection.IsConnected)
        {
            _ = Connection.SendLayoutUpdateAsync(profile);
        }
    }

    private CanvasCardViewModel? GetPrimarySensorCard(string sensorName)
    {
        return Cards
            .Where(c => c.CardType == "Sensor" && string.Equals(c.Sensor?.Name, sensorName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.PositionY)
            .ThenBy(c => c.PositionX)
            .FirstOrDefault();
    }

    private void SwapPrimarySensorCardY(string firstSensorName, string secondSensorName)
    {
        var first = GetPrimarySensorCard(firstSensorName);
        var second = GetPrimarySensorCard(secondSensorName);
        if (first is null || second is null) return;

        (first.PositionY, second.PositionY) = (second.PositionY, first.PositionY);
    }

    private void RefreshSensorActivationItems()
    {
        var activeSensors = Cards
            .Where(c => c.CardType == "Sensor" && !string.IsNullOrWhiteSpace(c.Sensor?.Name))
            .GroupBy(c => c.Sensor!.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { SensorName = g.First().Sensor!.Name, SortY = g.Min(c => c.PositionY) })
            .OrderBy(x => x.SortY)
            .ThenBy(x => x.SensorName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var activeNameSet = activeSensors
            .Select(x => x.SensorName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var knownNames = activeSensors
            .Select(x => x.SensorName)
            .Concat(
                StagedCards
                    .Where(c => c.CardType == "Sensor" && !string.IsNullOrWhiteSpace(c.Sensor?.Name))
                    .Select(c => c.Sensor!.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _isRefreshingSensorActivation = true;
        try
        {
            foreach (var existing in SensorActivationItems)
            {
                existing.ActiveChanged -= OnSensorActivationChanged;
            }

            SensorActivationItems.Clear();

            foreach (var name in knownNames)
            {
                var item = new SensorActivationItem(name, activeNameSet.Contains(name));
                item.ActiveChanged += OnSensorActivationChanged;
                SensorActivationItems.Add(item);
            }
        }
        finally
        {
            _isRefreshingSensorActivation = false;
        }

        RefreshSecondaryWiring();
    }

    /// <summary>Maps every live sensor name to its (shared) view-model, for DualMetric resolution.</summary>
    private Dictionary<string, SensorViewModel> LiveSensorsByName() =>
        Cards.Concat(StagedCards)
            .Where(c => c.CardType == "Sensor" && c.Sensor != null && !string.IsNullOrWhiteSpace(c.Sensor!.Name))
            .GroupBy(c => c.Sensor!.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Sensor!, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves each sensor card's persisted <see cref="SensorViewModel.SecondarySensorId"/> to a live
    /// <see cref="SensorViewModel"/> so the DualMetric overlay series has data. Cheap and idempotent —
    /// safe to call on any card/staging change.
    /// </summary>
    private void RefreshSecondaryWiring()
    {
        var sensorByName = LiveSensorsByName();
        foreach (var card in Cards.Where(c => c.CardType == "Sensor" && c.Sensor != null))
        {
            var secId = card.Sensor!.SecondarySensorId;
            card.Sensor.SecondarySensor =
                (!string.IsNullOrWhiteSpace(secId) && sensorByName.TryGetValue(secId!, out var sec))
                    ? sec
                    : null;
        }
    }

    /// <summary>Raised when a card wants to choose its DualMetric second sensor (opens a picker dialog).</summary>
    public event Action<CanvasCardViewModel>? ShowSecondMetricRequested;

    [RelayCommand]
    private void ChooseSecondMetric(CanvasCardViewModel? card)
    {
        if (card?.Sensor is not null)
            ShowSecondMetricRequested?.Invoke(card);
    }

    /// <summary>Every OTHER live sensor name, offered as a second-metric choice for the given card.</summary>
    public IReadOnlyList<string> SecondaryCandidatesFor(CanvasCardViewModel card)
    {
        var self = card.Sensor?.Name;
        return LiveSensorsByName().Keys
            .Where(n => !string.Equals(n, self, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Applies (or clears, when null) a card's DualMetric second sensor and resolves it live.</summary>
    public void ApplySecondMetric(CanvasCardViewModel card, string? sensorName)
    {
        if (card.Sensor is null) return;
        card.Sensor.SecondarySensorId = string.IsNullOrEmpty(sensorName) ? null : sensorName;
        RefreshSecondaryWiring(); // resolve the live reference immediately (save fires via customization hook)
    }

    private void OnSensorActivationChanged(object? sender, bool isActive)
    {
        if (_isRefreshingSensorActivation) return;
        if (sender is not SensorActivationItem item) return;

        var sensorName = item.SensorName;
        if (string.IsNullOrWhiteSpace(sensorName)) return;

        if (isActive)
        {
            var alreadyActive = Cards.Any(c => c.CardType == "Sensor" && string.Equals(c.Sensor?.Name, sensorName, StringComparison.OrdinalIgnoreCase));
            if (!alreadyActive)
            {
                var staged = StagedCards.FirstOrDefault(c => c.CardType == "Sensor" && string.Equals(c.Sensor?.Name, sensorName, StringComparison.OrdinalIgnoreCase));
                if (staged is not null)
                {
                    PlaceFromStaging(staged);
                }
            }
        }
        else
        {
            var sensorCards = Cards
                .Where(c => c.CardType == "Sensor" && string.Equals(c.Sensor?.Name, sensorName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sensorCards.Count > 0)
            {
                foreach (var card in sensorCards)
                {
                    Cards.Remove(card);
                    SelectedCards.Remove(card);
                }
                TriggerSave();
            }
        }

        RefreshSensorActivationItems();
    }

    private void TrackZIndex(int z)
    {
        if (z >= _nextZIndex)
            _nextZIndex = z + 1;
    }

    /// <summary>
    /// Re-applies the layout currently persisted by <see cref="_layoutService"/> to the live canvas.
    /// Called after a savefile import replaces <c>dashboard_layout.json</c> so the canvas updates in
    /// place instead of only on the next app restart (RemEx-83c4). Non-sensor cards refresh
    /// immediately; sensor cards for sensors that aren't live yet re-materialize as their telemetry
    /// arrives (the restore path reads the freshly-applied <c>_profile</c>). A no-op if the canvas
    /// hasn't finished its own first load — it will pick the imported layout up from disk then.
    /// </summary>
    public void ReloadFromPersistedLayout()
    {
        if (!_isInitialized) return;

        var profile = _layoutService.CurrentProfile;
        if (profile is null) return;

        if (Dispatcher.UIThread.CheckAccess())
            ApplyProfile(profile);
        else
            Dispatcher.UIThread.Post(() => ApplyProfile(profile));
    }

    private void ApplyProfile(DashboardProfile profile)
    {
        _profile = profile;
        IsSnapToGridEnabled = profile.IsSnapToGridEnabled;
        GridSize = profile.GridSize;

        var profileCardIds = (profile.Cards ?? Enumerable.Empty<CardState>())
            .Select(c => c.CardId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var existing in Cards.Where(c => !profileCardIds.Contains(c.CardId)).ToList())
        {
            Cards.Remove(existing);
        }

        foreach (var state in profile.Cards ?? Enumerable.Empty<CardState>())
        {
            var existing = Cards.FirstOrDefault(c => c.CardId == state.CardId);

            if (existing != null)
            {
                existing.PositionX = state.PositionX;
                existing.PositionY = state.PositionY;
                existing.Width = state.Width;
                existing.Height = state.Height;
                existing.ZIndex = state.ZIndex;
                var pinnedIds = profile.PinnedSensorIds ?? Enumerable.Empty<string>();
                existing.IsPinnedToHome = pinnedIds.Contains(existing.Sensor?.Name ?? state.SensorId ?? "");
                existing.RequestPinToggle = () => TogglePinToHome(existing);
                continue;
            }

            if (state.CardType != "Sensor")
            {
                var card = CanvasCardViewModel.FromCardState(state);
                card.CardTitle = state.CardType;
                card.Connection = Connection;
                card.RequestPinToggle = () => TogglePinToHome(card);
                Cards.Add(card);
                TrackZIndex(card.ZIndex);
                continue;
            }

            // Restore sensor cards only when the sensor model is known.
            var sensor = StagedCards.FirstOrDefault(c => c.CardType == "Sensor" && c.Sensor?.Name == state.SensorId)?.Sensor
                ?? Cards.FirstOrDefault(c => c.CardType == "Sensor" && c.Sensor?.Name == state.SensorId)?.Sensor;

            if (sensor is null)
            {
                continue;
            }

            var restored = CanvasCardViewModel.FromCardState(state);
            restored.CardTitle = state.SensorId ?? "Sensor";
            restored.Sensor = sensor;
            ApplyPersistedSensorState(sensor, state);
            var pinnedIds2 = profile.PinnedSensorIds ?? Enumerable.Empty<string>();
            restored.IsPinnedToHome = pinnedIds2.Contains(state.SensorId ?? "");
            restored.RequestPinToggle = () => TogglePinToHome(restored);
            Cards.Add(restored);
            TrackZIndex(restored.ZIndex);
        }

        foreach (var c in Cards) TrackZIndex(c.ZIndex);

        // Also update local storage so they stay in sync even when offline next time
        _layoutService.RequestSave(profile);

        // Refresh home pinned sensors if the home view is cached, but do NOT
        // forcibly navigate — the user may be on a different screen.
        if (_shell.CurrentView is HomeViewModel home)
            home.RefreshPinnedSensors();

        RefreshSensorActivationItems();
    }

    public void CleanupSensorSubscriptions()
    {
        var sensorVms = Cards.Concat(StagedCards)
            .Where(c => c.Sensor is not null)
            .Select(c => c.Sensor!)
            .Distinct()
            .ToList();

        foreach (var sensor in sensorVms)
            sensor.AlertTriggered -= OnSensorAlertTriggered;

        _subscribedSensorNames.Clear();
    }

    public void Dispose()
    {
        CleanupSensorSubscriptions();
        Connection.PropertyChanged -= OnConnectionPropertyChanged;
        Connection.LayoutProfileReceived -= OnLayoutProfileReceived;
        Cards.CollectionChanged -= OnCardsCollectionChanged;
        StagedCards.CollectionChanged -= OnStagedCardsCollectionChanged;
        SelectedCards.CollectionChanged -= OnSelectedCardsChanged;
    }
}

public partial class SensorActivationItem : ObservableObject
{
    public string SensorName { get; }

    [ObservableProperty]
    private bool _isActive;

    public event Action<object?, bool>? ActiveChanged;

    public SensorActivationItem(string sensorName, bool isActive)
    {
        SensorName = sensorName;
        _isActive = isActive;
    }

    partial void OnIsActiveChanged(bool value) => ActiveChanged?.Invoke(this, value);
}

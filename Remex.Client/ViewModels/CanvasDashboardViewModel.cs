using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Client.Services;
using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Client.ViewModels;

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

    public ConnectionViewModel Connection { get; }

    /// <summary>Raised when the view should reset the canvas pan/zoom to origin.</summary>
    public event EventHandler? ResetViewRequested;

    [RelayCommand]
    private void ResetView() => ResetViewRequested?.Invoke(this, EventArgs.Empty);

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
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Connection.Telemetry) && Connection.Telemetry != null)
        {
            ProcessTelemetry(Connection.Telemetry);
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

            // Restore non-sensor cards from profile.
            foreach (var state in _profile.Cards.Where(c => c.CardType != "Sensor"))
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
                CardTitle = "Connection",
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
                CardTitle = "Actions",
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
                CardTitle = "Latency",
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
        TriggerSave();
    }

    /// <summary>
    /// Toggles a sensor card's pinned state on the Home overview.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTogglePinToHome))]
    private void TogglePinToHome(CanvasCardViewModel card)
    {
        if (card.CardType != "Sensor" || card.Sensor is null) return;

        var sensorName = card.Sensor.Name;
        if (string.IsNullOrWhiteSpace(sensorName)) return;

        card.IsPinnedToHome = !card.IsPinnedToHome;

        if (card.IsPinnedToHome)
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
            var ws = Connection.GetWebSocket();
            if (ws != null)
            {
                var msg = new RemexMessage
                {
                    Type = MessageTypes.LayoutRequest,
                };
                await MessageSerializer.SendAsync(ws, msg);
                LayoutStatus = "Sync requested…";
            }
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

    private void ProcessTelemetry(TelemetryPayload payload)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (payload.Sensors == null) return;

            foreach (var reading in payload.Sensors)
            {
                var sensorName = string.IsNullOrWhiteSpace(reading.Name) ? "Unknown" : reading.Name;

                var sensorVms = Cards
                    .Where(c => c.CardType == "Sensor" && c.Sensor?.Name == sensorName)
                    .Select(c => c.Sensor)
                    .Concat(
                        StagedCards
                            .Where(c => c.CardType == "Sensor" && c.Sensor?.Name == sensorName)
                            .Select(c => c.Sensor))
                    .Where(s => s is not null)
                    .Select(s => s!)
                    .Distinct()
                    .ToList();

                // First time seeing this sensor in the current session.
                if (sensorVms.Count == 0)
                {
                    var sensor = new SensorViewModel();

                    // Keep one reusable template in staging so users can add more cards.
                    var staged = new CanvasCardViewModel
                    {
                        CardType = "Sensor",
                        CardTitle = sensorName,
                        Sensor = sensor,
                        Width = 200,
                        Height = 120,
                    };
                    staged.RequestPinToggle = () => TogglePinToHome(staged);
                    StagedCards.Add(staged);

                    // Restore every persisted card for this sensor.
                    foreach (var saved in _profile.Cards.Where(c => c.CardType == "Sensor" && c.SensorId == sensorName))
                    {
                        if (Cards.Any(c => c.CardId == saved.CardId))
                        {
                            continue;
                        }

                        var restored = CanvasCardViewModel.FromCardState(saved);
                        restored.CardTitle = sensorName;
                        restored.Sensor = sensor;
                        restored.IsPinnedToHome = _profile.PinnedSensorIds.Contains(sensorName);
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

    private void TriggerSave()
    {
        // Use the current profile from the layout service as the base to avoid overwriting
        // global flags (like HasCompletedTutorial) set by other components.
        var baseProfile = _layoutService.CurrentProfile ?? _profile;

        var profile = baseProfile with
        {
            IsSnapToGridEnabled = IsSnapToGridEnabled,
            GridSize = GridSize,
            HostAddress = Connection.HostAddress,
            Cards = Cards.Select(c => c.ToCardState()).ToList(),
            // Rebuild pinned sensors from the actual card states to ensure consistency
            PinnedSensorIds = Cards
                .Where(c => c.CardType == "Sensor" && c.IsPinnedToHome && c.Sensor != null)
                .Select(c => c.Sensor!.Name)
                .Distinct()
                .ToList(),
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

    private void ApplyProfile(DashboardProfile profile)
    {
        _profile = profile;
        IsSnapToGridEnabled = profile.IsSnapToGridEnabled;
        GridSize = profile.GridSize;

        var profileCardIds = profile.Cards
            .Select(c => c.CardId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var existing in Cards.Where(c => !profileCardIds.Contains(c.CardId)).ToList())
        {
            Cards.Remove(existing);
        }

        foreach (var state in profile.Cards)
        {
            var existing = Cards.FirstOrDefault(c => c.CardId == state.CardId);

            if (existing != null)
            {
                existing.PositionX = state.PositionX;
                existing.PositionY = state.PositionY;
                existing.Width = state.Width;
                existing.Height = state.Height;
                existing.ZIndex = state.ZIndex;
                existing.IsPinnedToHome = profile.PinnedSensorIds.Contains(existing.Sensor?.Name ?? state.SensorId ?? "");
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
            restored.IsPinnedToHome = profile.PinnedSensorIds.Contains(state.SensorId ?? "");
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

    public void Dispose()
    {
        Connection.PropertyChanged -= OnConnectionPropertyChanged;
        Connection.LayoutProfileReceived -= OnLayoutProfileReceived;
        Cards.CollectionChanged -= OnCardsCollectionChanged;
        StagedCards.CollectionChanged -= OnStagedCardsCollectionChanged;
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

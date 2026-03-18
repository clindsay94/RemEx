using System;
using System.Collections.ObjectModel;
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
public partial class CanvasDashboardViewModel : ObservableObject
{
    private readonly DashboardLayoutService _layoutService;
    private readonly ShellViewModel _shell;
    private DashboardProfile _profile = new();
    private int _nextZIndex = 1;
    private bool _isInitialized;
    private bool _hasSyncedWithHost;
    private DashboardProfile? _pendingSyncProfile;

    public ConnectionViewModel Connection { get; }

    /// <summary>Cards currently placed on the canvas.</summary>
    public ObservableCollection<CanvasCardViewModel> Cards { get; } = new();

    /// <summary>Newly discovered sensors waiting to be placed by the user.</summary>
    public ObservableCollection<CanvasCardViewModel> StagedCards { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<CanvasCardViewModel> SelectedCards { get; } = new();

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
        Connection.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Connection.Telemetry) && Connection.Telemetry != null)
            {
                ProcessTelemetry(Connection.Telemetry);
            }
        };

        Connection.LayoutProfileReceived += async profile =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_isInitialized)
                {
                    _pendingSyncProfile = profile;
                    return;
                }
                _hasSyncedWithHost = true;
                ApplyProfile(profile);
            });
        };

        StagedCards.CollectionChanged += (_, _) =>
            HasStagedCards = StagedCards.Count > 0;
    }

    /// <summary>
    /// Loads the persisted profile and restores card positions.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        var localProfile = await _layoutService.LoadAsync().ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _isInitialized = true;

            // If a host sync arrived before we finished local load, prioritize the host.
            if (_pendingSyncProfile != null)
            {
                _hasSyncedWithHost = true;
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
                Cards.Add(card);
                TrackZIndex(card.ZIndex);
            }

            // Create default cards if this is a fresh profile.
            EnsureDefaultCards();

            // If we're already connected, maybe the host is empty? 
            // We should probably push our local layout if the host didn't send anything.
            // But for now, let's just ensure we stay in sync.
        });
    }

    private void EnsureDefaultCards()
    {
        if (!Cards.Any(c => c.CardType == "Connection"))
        {
            Cards.Add(new CanvasCardViewModel
            {
                CardType = "Connection",
                CardTitle = "Connection",
                Connection = Connection,
                PositionX = 20, PositionY = 20,
                Width = 240, Height = 180,
                ZIndex = _nextZIndex++,
            });
        }

        if (!Cards.Any(c => c.CardType == "Actions"))
        {
            Cards.Add(new CanvasCardViewModel
            {
                CardType = "Actions",
                CardTitle = "Actions",
                Connection = Connection,
                PositionX = 280, PositionY = 20,
                Width = 240, Height = 180,
                ZIndex = _nextZIndex++,
            });
        }

        if (!Cards.Any(c => c.CardType == "Latency"))
        {
            Cards.Add(new CanvasCardViewModel
            {
                CardType = "Latency",
                CardTitle = "Latency",
                Connection = Connection,
                PositionX = 540, PositionY = 20,
                Width = 360, Height = 220,
                ZIndex = _nextZIndex++,
            });
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
    [RelayCommand]
    private void TogglePinToHome(CanvasCardViewModel card)
    {
        if (card.CardType != "Sensor" || card.Sensor is null) return;

        card.IsPinnedToHome = !card.IsPinnedToHome;
        TriggerSave();
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
            var profile = await _layoutService.LoadAsync().ConfigureAwait(false);
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

                // Check if we already have a card for this sensor.
                var existing = Cards.FirstOrDefault(c =>
                    c.CardType == "Sensor" && c.Sensor?.Name == sensorName);

                if (existing != null)
                {
                    existing.Sensor!.Update(reading);
                    continue;
                }

                // Check staging drawer.
                var staged = StagedCards.FirstOrDefault(c =>
                    c.CardType == "Sensor" && c.Sensor?.Name == sensorName);

                if (staged != null)
                {
                    staged.Sensor!.Update(reading);
                    continue;
                }

                // New sensor — check for a persisted CardState.
                var saved = _profile.Cards.FirstOrDefault(c =>
                    c.CardType == "Sensor" && c.SensorId == sensorName);

                var sensor = new SensorViewModel();
                sensor.Update(reading);

                var card = new CanvasCardViewModel
                {
                    CardType = "Sensor",
                    CardTitle = sensorName,
                    Sensor = sensor,
                };

                if (saved != null)
                {
                    // Restore position from profile → place directly on canvas.
                    card.PositionX = saved.PositionX;
                    card.PositionY = saved.PositionY;
                    card.Width = saved.Width;
                    card.Height = saved.Height;
                    card.ZIndex = saved.ZIndex;
                    card.IsPinnedToHome = _profile.PinnedSensorIds.Contains(sensorName);
                    TrackZIndex(saved.ZIndex);
                    Cards.Add(card);
                }
                else
                {
                    // No saved state → goes to staging drawer.
                    card.Width = 200;
                    card.Height = 120;
                    StagedCards.Add(card);
                }
            }
        });
    }

    // ═══════════════ Persistence ═══════════════

    private void TriggerSave()
    {
        var profile = new DashboardProfile
        {
            ProfileName = _profile.ProfileName,
            IsSnapToGridEnabled = IsSnapToGridEnabled,
            GridSize = GridSize,
            HostAddress = Connection.HostAddress,
            Cards = Cards.Select(c => c.ToCardState()).ToList(),
            PinnedSensorIds = Cards
                .Where(c => c.IsPinnedToHome && c.Sensor != null)
                .Select(c => c.Sensor!.Name)
                .ToList(),
        };

        _layoutService.RequestSave(profile);

        if (Connection.IsConnected)
        {
            _ = Connection.SendLayoutUpdateAsync(profile);
        }
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

        // Sync cards
        var toRemove = Cards.ToList();
        foreach (var state in profile.Cards)
        {
            var existing = Cards.FirstOrDefault(c => 
                (c.CardType == state.CardType && state.CardType != "Sensor") ||
                (c.CardType == "Sensor" && c.Sensor?.Name == state.SensorId));

            if (existing != null)
            {
                existing.PositionX = state.PositionX;
                existing.PositionY = state.PositionY;
                existing.Width = state.Width;
                existing.Height = state.Height;
                existing.ZIndex = state.ZIndex;
                existing.IsPinnedToHome = profile.PinnedSensorIds.Contains(state.SensorId ?? "");
                toRemove.Remove(existing);
            }
            else if (state.CardType != "Sensor")
            {
                var card = CanvasCardViewModel.FromCardState(state);
                card.CardTitle = state.CardType;
                card.Connection = Connection;
                Cards.Add(card);
            }
            // Sensors will be handled by ProcessTelemetry if they aren't on canvas yet
        }

        foreach (var r in toRemove)
        {
            // If it's a sensor, move it to staging instead of just removing?
            // Actually, if it's not in the profile, it should be in staging.
            if (r.CardType == "Sensor")
            {
                Cards.Remove(r);
                r.PositionX = 0;
                r.PositionY = 0;
                StagedCards.Add(r);
            }
            else
            {
                Cards.Remove(r);
            }
        }

        foreach (var c in Cards) TrackZIndex(c.ZIndex);
        
        // Also update local storage so they stay in sync even when offline next time
        _layoutService.RequestSave(profile);

        // Refresh home pinned sensors if it's currently showing or cached
        _shell.NavigateToHomeCommand.Execute(null);
    }
}

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

    public ConnectionViewModel Connection { get; }

    /// <summary>Cards currently placed on the canvas.</summary>
    public ObservableCollection<CanvasCardViewModel> Cards { get; } = new();

    /// <summary>Newly discovered sensors waiting to be placed by the user.</summary>
    public ObservableCollection<CanvasCardViewModel> StagedCards { get; } = new();

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

        StagedCards.CollectionChanged += (_, _) =>
            HasStagedCards = StagedCards.Count > 0;
    }

    /// <summary>
    /// Loads the persisted profile and restores card positions.
    /// </summary>
    public async Task InitializeAsync()
    {
        _profile = await _layoutService.LoadAsync().ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
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
    }

    private void TrackZIndex(int z)
    {
        if (z >= _nextZIndex)
            _nextZIndex = z + 1;
    }
}

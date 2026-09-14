using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// Snap-to-grid, grid size and pinned sensors — moved off <c>SettingsViewModel</c> onto the
/// Personalize sheet as their own view model (RemEx-4kv0g.4.2, temporarily surfaced in a LAYOUT
/// section on today's sheet; drop 3 gives it a real tab). Constructed by <c>ShellViewModel</c>
/// alongside <c>CustomizationViewModel</c>, which exposes it as <see cref="CustomizationViewModel.Layout"/>.
/// </summary>
/// <remarks>
/// EVERY SAVE BUILDS FROM <see cref="DashboardLayoutService.CurrentProfile"/>, NEVER A PRIVATE STALE
/// COPY. <c>DashboardLayoutService.RequestSave</c>'s own contract comment (:623) documents why: every
/// caller is expected to hand in <c>CurrentProfile with { ... }</c>, because <c>RequestSave</c> both
/// refuses a fallback profile as a save base and treats <c>CurrentProfile</c> as the one place a
/// concurrent writer's change would already be visible. Today's <c>SettingsViewModel.Save</c> (still,
/// for the fields it kept — out of scope here) builds from its own private <c>_profile</c> field
/// instead; that field goes stale the moment this VM's <see cref="SaveLayout"/> writes a newer
/// <c>CurrentProfile</c>, which is exactly why <c>SettingsViewModel.Save</c> was switched in this same
/// change to build its own `with` from <c>CurrentProfile</c> too (see its comment).
/// </remarks>
public sealed partial class LayoutSettingsViewModel : ObservableObject
{
    private readonly DashboardLayoutService _layoutService;
    private readonly CanvasDashboardViewModel? _canvas;
    private readonly HomeViewModel? _home;

    // GUARDS THE LOAD-TIME ASSIGNMENTS BELOW, the same shape CustomizationViewModel's
    // _suppressPersist/_isApplyingPreset guard the equivalent window with (CustomizationViewModel.cs
    // :34-45). That VM can get away with writing its [ObservableProperty] backing fields directly
    // because its whole snapshot happens in the constructor, where CommunityToolkit.Mvvm's generator
    // still allows it (MVVMTK0034 forbids it everywhere else). InitializeAsync here is async and runs
    // AFTER construction, so the properties' real setters are the only way in — which means their
    // change handlers WILL fire for the initial assignment unless this flag short-circuits the save.
    private bool _loading;

    [ObservableProperty]
    private bool _isSnapToGridEnabled;

    [ObservableProperty]
    private int _gridSize = 50;

    /// <summary>Sensors with checkboxes for pinning to Home, moved verbatim off SettingsViewModel.</summary>
    public ObservableCollection<SensorPinItem> PinnedSensors { get; } = new();

    /// <param name="canvas">
    /// The live canvas, taken directly rather than through <c>ShellViewModel</c> (deviation from the
    /// handoff brief's ctor shape, called out in the task report). <c>ShellViewModel</c> cannot be
    /// constructed headless — every existing Settings VM test passes it as <c>null!</c>, and the one
    /// test class in the suite that builds a real one documents at length how painful that is — so
    /// this VM takes the two collaborators it actually needs (canvas, home) instead, the same
    /// "small interface instead of the shell" escape hatch the brief names for exactly this case.
    /// <c>ShellViewModel.CanvasViewModel</c> is set once in its constructor before
    /// <c>EnsureCustomizationVm</c> can ever run, so the reference handed in here never goes stale.
    /// </param>
    public LayoutSettingsViewModel(
        DashboardLayoutService layoutService, CanvasDashboardViewModel? canvas, HomeViewModel? home)
    {
        _layoutService = layoutService;
        _canvas = canvas;
        _home = home;
    }

    /// <summary>Loads current values from the persisted profile. Does not queue a save.</summary>
    public async Task InitializeAsync()
    {
        var profile = await _layoutService.LoadAsync();
        ApplyFromProfile(profile);
    }

    /// <summary>
    /// Re-reads snap/grid/pinned sensors from <see cref="DashboardLayoutService.CurrentProfile"/>
    /// after something else replaced it wholesale (a savefile import) — <c>SettingsViewModel</c> calls
    /// this instead of touching this VM's members directly now that it no longer owns them.
    /// </summary>
    public Task ReloadFromProfileAsync()
    {
        ApplyFromProfile(_layoutService.CurrentProfile);
        return Task.CompletedTask;
    }

    private void ApplyFromProfile(DashboardProfile profile)
    {
        _loading = true;
        try
        {
            IsSnapToGridEnabled = profile.IsSnapToGridEnabled;
            GridSize = profile.GridSize;
            RefreshSensors();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Rebuilds the pinned-sensor list from the canvas VM's current cards.</summary>
    public void RefreshSensors()
    {
        foreach (var item in PinnedSensors)
            item.PinChanged -= OnSensorPinChanged;
        PinnedSensors.Clear();

        if (_canvas is null) return;

        var sensorCards = _canvas.Cards
            .Where(c => c.CardType == "Sensor" && c.Sensor != null)
            .OrderBy(c => c.Sensor!.Name);

        foreach (var card in sensorCards)
        {
            var name = card.Sensor!.Name;
            var pinnedIds = _layoutService.CurrentProfile.PinnedSensorIds ?? Enumerable.Empty<string>();
            var isPinned = pinnedIds.Contains(name);
            var source = card.Sensor.RawReading?.Source ?? "Unknown";
            var item = new SensorPinItem(name, isPinned, source);
            item.PinChanged += OnSensorPinChanged;
            PinnedSensors.Add(item);
        }
    }

    private void OnSensorPinChanged(object? sender, bool isPinned)
    {
        if (sender is not SensorPinItem item) return;

        var card = _canvas?.Cards.FirstOrDefault(c => c.Sensor?.Name == item.SensorName);
        if (card != null)
            card.IsPinnedToHome = isPinned;

        // The stored list ± this one name, NOT this VM's checklist re-serialised (drop-2 review,
        // MEDIUM): a card pinned from the canvas's own menu (SetCardPinned → TriggerSave's merge) is
        // absent from a checklist built before that click, so re-serialising the checklist would
        // silently unpin it. Case-insensitive to match CanvasDashboardViewModel.SetCardPinned.
        SaveLayout(profile =>
        {
            var ids = new List<string>(profile.PinnedSensorIds ?? new List<string>());
            ids.RemoveAll(id => string.Equals(id, item.SensorName, StringComparison.OrdinalIgnoreCase));
            if (isPinned)
                ids.Add(item.SensorName);
            return profile with { PinnedSensorIds = ids };
        });

        // Not gated behind navigate-back any more (RemEx-4kv0g.4.2) — the Layout section lives on the
        // always-open Personalize sheet, which has no "leaving the page" moment to hang this off.
        _home?.RefreshPinnedSensors();
    }

    partial void OnIsSnapToGridEnabledChanged(bool value)
    {
        if (_canvas is not null)
            _canvas.IsSnapToGridEnabled = value;
        if (_loading) return;
        SaveLayout(profile => profile with { IsSnapToGridEnabled = value });
    }

    partial void OnGridSizeChanged(int value)
    {
        if (_canvas is not null)
            _canvas.GridSize = value;
        if (_loading) return;
        SaveLayout(profile => profile with { GridSize = value });
    }

    /// <summary>
    /// Always builds from <see cref="DashboardLayoutService.CurrentProfile"/>, never a private copy —
    /// see this class's remarks for why. <paramref name="mutate"/> applies only the field this call
    /// owns; every other field carries forward from whatever is live right now.
    /// </summary>
    private void SaveLayout(Func<DashboardProfile, DashboardProfile> mutate) =>
        _layoutService.RequestSave(mutate(_layoutService.CurrentProfile));
}

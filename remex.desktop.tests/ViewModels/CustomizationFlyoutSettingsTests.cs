using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Flyout D2 .2 (RemEx-4kv0g.18.6): the Flyout section's view-model half — toggling a card, a tile or
/// an app writes the right <see cref="CustomizationSettings"/> list and persists, a newly pinned
/// sensor appears shown by default, <c>FlyoutOpacity</c> clamps, and <c>ApplyAndSave</c> round-trips
/// all four fields (<see cref="CustomizationSettingsRoundTripTests"/> stays green independently — it
/// only checks every field is ASSIGNED, not what it is assigned to).
/// </summary>
/// <remarks>
/// Fix round 1 (review HIGH 1) added the persisted-lists-are-the-source-of-truth tests: a save with
/// no <c>HomeViewModel</c>/<c>ILauncherStorageService</c> collaborator, a save with a throwing
/// launcher, and a hidden sensor absent from the current pins all have to survive an UNRELATED save
/// unchanged — the checklists (<c>FlyoutCards</c>/<c>FlyoutTiles</c>/<c>FlyoutApps</c>) are a view,
/// never the thing <c>BuildCurrentSettings</c> reads from.
/// </remarks>
/// <remarks>
/// Harness is <see cref="CustomizationTypographyTests"/>'s: a redirected, unsaved-to-disk
/// <c>DashboardLayoutService</c> seeded synchronously by <c>RequestSave</c>, <c>ThemeService</c>
/// posting inline. <c>shell</c> stays <c>null!</c> the same way it does there — nothing this file
/// exercises (<c>BuildCurrentSettings</c>, <c>ApplyAndSave</c>, the three Flyout rebuild methods)
/// dereferences it. <c>HomeViewModel</c> is real (its own <c>shell</c> also stays <c>null!</c> — its
/// constructor only stores the reference, the same way <c>TrayFlyoutViewModelToolbarTests</c>' fake
/// launcher storage returns an already-completed <c>Task</c>, so the fire-and-forget
/// <c>RefreshFlyoutApps</c> has finished by the time the constructor returns).
/// </remarks>
public sealed class CustomizationFlyoutSettingsTests : IDisposable
{
    private DashboardLayoutService? _layoutService;

    public void Dispose() => _layoutService?.Dispose();

    private sealed class FakeLauncherStorage : ILauncherStorageService
    {
        public List<AppEntry> Entries { get; set; } = new();
        public Task<List<AppEntry>> LoadEntriesAsync() => Task.FromResult(new List<AppEntry>(Entries));
        public Task SaveEntriesAsync(IEnumerable<AppEntry> entries) => Task.CompletedTask;
    }

    private sealed class ThrowingLauncherStorage : ILauncherStorageService
    {
        public Task<List<AppEntry>> LoadEntriesAsync() => throw new InvalidOperationException("boom");
        public Task SaveEntriesAsync(IEnumerable<AppEntry> entries) => Task.CompletedTask;
    }

    private (CustomizationViewModel Vm, DashboardLayoutService Layout, HomeViewModel Home, FakeLauncherStorage Launcher) MakeVm(
        CustomizationSettings? seed = null,
        IEnumerable<SensorViewModel>? pins = null,
        IEnumerable<AppEntry>? launcherEntries = null)
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = _layoutService = new DashboardLayoutService(theme);
        layout.RequestSave(new DashboardProfile { Customization = seed ?? new CustomizationSettings() });

        var connection = new ConnectionViewModel();
        var home = new HomeViewModel(connection, null!);
        foreach (var pin in pins ?? Enumerable.Empty<SensorViewModel>()) home.PinnedSensors.Add(pin);

        var launcher = new FakeLauncherStorage { Entries = launcherEntries?.ToList() ?? new() };

        var vm = new CustomizationViewModel(null!, layout, theme, home, launcher);
        return (vm, layout, home, launcher);
    }

    [Fact]
    public void TogglingACardChoiceOff_WritesItsIdToFlyoutHiddenSensorIds_AndSaves()
    {
        var cpu = new SensorViewModel { Name = "CPU Package Temp" };
        var gpu = new SensorViewModel { Name = "GPU Temp" };
        var (vm, layout, _, _) = MakeVm(pins: [cpu, gpu]);

        vm.FlyoutCards.Select(c => c.SensorId).Should().BeEquivalentTo("CPU Package Temp", "GPU Temp");
        vm.FlyoutCards.Should().OnlyContain(c => c.IsShown, "every pin is shown by default");

        vm.FlyoutCards.Single(c => c.SensorId == "CPU Package Temp").IsShown = false;

        layout.CurrentProfile.Customization.FlyoutHiddenSensorIds.Should().Contain("CPU Package Temp");
        layout.CurrentProfile.Customization.FlyoutHiddenSensorIds.Should().NotContain("GPU Temp");
    }

    [Fact]
    public void ANewlyPinnedSensor_AppearsShownByDefault()
    {
        var existing = new SensorViewModel { Name = "CPU Package Temp" };
        var (vm, _, home, _) = MakeVm(pins: [existing]);

        var pinnedLater = new SensorViewModel { Name = "Fan RPM" };
        home.PinnedSensors.Add(pinnedLater);

        vm.FlyoutCards.Should().Contain(c => c.SensorId == "Fan RPM" && c.IsShown);
    }

    [Fact]
    public void APreviouslyHiddenSensor_StaysHiddenAcrossARebuild()
    {
        var hidden = new SensorViewModel { Name = "CPU Package Temp" };
        var (vm, _, home, _) = MakeVm(
            seed: new CustomizationSettings { FlyoutHiddenSensorIds = new List<string> { "CPU Package Temp" } },
            pins: [hidden]);

        vm.FlyoutCards.Single(c => c.SensorId == "CPU Package Temp").IsShown.Should().BeFalse();

        // Triggers RebuildFlyoutCards via PinnedSensors.CollectionChanged without touching the
        // already-hidden entry - it must not silently flip back to shown.
        home.PinnedSensors.Add(new SensorViewModel { Name = "Fan RPM" });

        vm.FlyoutCards.Single(c => c.SensorId == "CPU Package Temp").IsShown.Should().BeFalse();
    }

    [Fact]
    public void TogglingATile_WritesFlyoutHiddenTileIds()
    {
        var (vm, layout, _, _) = MakeVm();

        vm.FlyoutTiles.Select(t => t.Id).Should().Equal("lock", "sleep", "remote", "send", "pair", "power");
        vm.FlyoutTiles.Should().OnlyContain(t => t.IsShown);

        vm.FlyoutTiles.Single(t => t.Id == "sleep").IsShown = false;

        layout.CurrentProfile.Customization.FlyoutHiddenTileIds.Should().Contain("sleep");
        layout.CurrentProfile.Customization.FlyoutHiddenTileIds.Should().HaveCount(1);
    }

    [Fact]
    public void TickingAnApp_WritesFlyoutAppIds_ShownListNotHiddenList()
    {
        var kept = new AppEntry(Guid.NewGuid(), "Notepad", @"C:\Windows\notepad.exe", "#000000", null);
        var untouched = new AppEntry(Guid.NewGuid(), "Calculator", @"C:\Windows\System32\calc.exe", "#000000", null);
        var (vm, layout, _, _) = MakeVm(launcherEntries: [kept, untouched]);

        vm.FlyoutApps.Should().OnlyContain(a => !a.IsShown, "apps are opt-in — unticked is the default");

        vm.FlyoutApps.Single(a => a.Id == kept.Id).IsShown = true;

        layout.CurrentProfile.Customization.FlyoutAppIds.Should().Equal(kept.Id);
    }

    [Fact]
    public void FlyoutOpacity_ClampsOnSave_AboveAndBelowRange()
    {
        var (vm, layout, _, _) = MakeVm();

        vm.FlyoutOpacity = 5.0;
        layout.CurrentProfile.Customization.FlyoutOpacity.Should().Be(1.0);

        vm.FlyoutOpacity = -3.0;
        layout.CurrentProfile.Customization.FlyoutOpacity.Should().Be(0.0);
    }

    [Fact]
    public void FlyoutOpacity_ClampsOnLoad()
    {
        var (vm, _, _, _) = MakeVm(seed: new CustomizationSettings { FlyoutOpacity = 7.0 });

        vm.FlyoutOpacity.Should().Be(1.0);
    }

    [Fact]
    public void ASaveWithNoCollaborators_PreservesAllFiveExistingFlyoutValues()
    {
        // Fix round 1 (RemEx-4kv0g.18.6 review, HIGH 1). home/launcherStorage are both optional
        // (ShellViewModel resolves them with GetService, not GetRequiredService) so both null here is
        // a real production shape, not a test-only contrivance - and with both null, FlyoutCards and
        // FlyoutApps never populate at all. An unrelated save (GlassOpacity) must still carry every
        // existing Flyout value forward unchanged; deriving the save from the (empty) checklists is
        // exactly the preset-wipe shape this fix closes.
        var seed = new CustomizationSettings
        {
            FlyoutOpacity = 0.7,
            FlyoutHiddenSensorIds = new List<string> { "CPU Package Temp", "GPU Temp" },
            FlyoutHiddenTileIds = new List<string> { "pair" },
            FlyoutAppIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() },
        };
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = _layoutService = new DashboardLayoutService(theme);
        layout.RequestSave(new DashboardProfile { Customization = seed });

        var vm = new CustomizationViewModel(null!, layout, theme, home: null, launcherStorage: null);

        vm.FlyoutCards.Should().BeEmpty("no HomeViewModel collaborator");
        vm.FlyoutApps.Should().BeEmpty("no launcher collaborator");

        vm.GlassOpacity = 0.33; // an entirely unrelated slider

        var saved = layout.CurrentProfile.Customization;
        saved.FlyoutOpacity.Should().Be(0.7);
        saved.FlyoutHiddenSensorIds.Should().BeEquivalentTo(seed.FlyoutHiddenSensorIds);
        saved.FlyoutHiddenTileIds.Should().BeEquivalentTo(seed.FlyoutHiddenTileIds);
        saved.FlyoutAppIds.Should().BeEquivalentTo(seed.FlyoutAppIds);
    }

    [Fact]
    public void ASaveWithAThrowingLauncher_PreservesTheAppIds()
    {
        // Fix round 1 (RemEx-4kv0g.18.6 review, HIGH 1): RefreshFlyoutAppsAsync's catch produces an
        // empty entry list on a launcher failure - FlyoutApps ends up empty, but _flyoutAppIds (and
        // therefore the next save) must not.
        var seed = new CustomizationSettings { FlyoutAppIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() } };
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = _layoutService = new DashboardLayoutService(theme);
        layout.RequestSave(new DashboardProfile { Customization = seed });

        var connection = new ConnectionViewModel();
        var home = new HomeViewModel(connection, null!);
        var vm = new CustomizationViewModel(null!, layout, theme, home, new ThrowingLauncherStorage());

        vm.FlyoutApps.Should().BeEmpty("the throwing launcher's catch produced an empty entry list");

        vm.GlassOpacity = 0.5;

        layout.CurrentProfile.Customization.FlyoutAppIds.Should().BeEquivalentTo(seed.FlyoutAppIds);
    }

    [Fact]
    public void AHiddenSensorNotAmongTheCurrentPins_SurvivesASave()
    {
        // The hidden id belongs to a sensor that is not pinned at all right now (offline this
        // session, or simply not staged on the canvas) - RebuildFlyoutCards has no row for it, but
        // the field must still carry it forward through an unrelated save rather than pruning it.
        var seed = new CustomizationSettings { FlyoutHiddenSensorIds = new List<string> { "Offline Sensor" } };
        var (vm, layout, _, _) = MakeVm(seed: seed, pins: [new SensorViewModel { Name = "GPU Temp" }]);

        vm.FlyoutCards.Should().NotContain(c => c.SensorId == "Offline Sensor");

        vm.GlassOpacity = 0.61;

        layout.CurrentProfile.Customization.FlyoutHiddenSensorIds.Should().Contain("Offline Sensor");
    }

    [Fact]
    public void UnpinningAHiddenSensor_PrunesOnlyThatIdAndSaves()
    {
        // The one legitimate way a hidden-sensor id stops meaning anything: the user unpins that
        // exact sensor. A second, still-hidden-but-unpinned sensor must survive untouched.
        var toUnpin = new SensorViewModel { Name = "CPU Package Temp" };
        var staysHidden = new SensorViewModel { Name = "GPU Temp" };
        var seed = new CustomizationSettings
        {
            FlyoutHiddenSensorIds = new List<string> { "CPU Package Temp", "GPU Temp", "Offline Sensor" },
        };
        var (vm, layout, home, _) = MakeVm(seed: seed, pins: [toUnpin, staysHidden]);

        home.PinnedSensors.Remove(toUnpin);

        var saved = layout.CurrentProfile.Customization;
        saved.FlyoutHiddenSensorIds.Should().NotContain("CPU Package Temp");
        saved.FlyoutHiddenSensorIds.Should().Contain("GPU Temp").And.Contain("Offline Sensor");
        vm.FlyoutCards.Should().NotContain(c => c.SensorId == "CPU Package Temp");
    }

    [Fact]
    public void ApplyAndSave_RoundTripsAllFourFlyoutFields()
    {
        var sensor = new SensorViewModel { Name = "CPU Package Temp" };
        var app = new AppEntry(Guid.NewGuid(), "Notepad", @"C:\Windows\notepad.exe", "#000000", null);
        var (vm, layout, _, _) = MakeVm(pins: [sensor], launcherEntries: [app]);

        vm.FlyoutOpacity = 0.42;
        vm.FlyoutCards.Single().IsShown = false;
        vm.FlyoutTiles.Single(t => t.Id == "pair").IsShown = false;
        vm.FlyoutApps.Single().IsShown = true;

        var saved = layout.CurrentProfile.Customization;
        saved.FlyoutOpacity.Should().Be(0.42);
        saved.FlyoutHiddenSensorIds.Should().Equal("CPU Package Temp");
        saved.FlyoutHiddenTileIds.Should().Equal("pair");
        saved.FlyoutAppIds.Should().Equal(app.Id);
    }
}

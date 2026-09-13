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

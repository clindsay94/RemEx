using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-4kv0g.4.2: snap-to-grid, grid size and pinned sensors moved off <c>SettingsViewModel</c>
/// onto their own <see cref="LayoutSettingsViewModel"/>. Every save here must build from
/// <see cref="DashboardLayoutService.CurrentProfile"/> at save time, never a private snapshot — see
/// <see cref="DashboardLayoutService.RequestSave"/>'s own contract comment and this class's remarks.
/// </summary>
/// <remarks>
/// SHELL IS NOT CONSTRUCTIBLE HEADLESS (deviation from the handoff brief's literal ctor shape,
/// reported in the task's Deviations). Every existing Settings VM test in this suite passes
/// <c>ShellViewModel</c> as <c>null!</c> (see <c>PairedDeviceCardTests.NewSettingsViewModel</c>), and
/// the one test class that constructs a real one (<c>ProfileReplacementInvalidatesCustomizationVmTests</c>)
/// documents at length how much of the DI graph that drags in. <see cref="LayoutSettingsViewModel"/>
/// therefore takes <c>CanvasDashboardViewModel?</c> directly instead of <c>ShellViewModel</c> — the
/// small-interface escape hatch the brief names for exactly this case — which is what makes every
/// test below constructible without a shell at all: <c>CanvasDashboardViewModel</c> itself accepts a
/// <c>null!</c> shell/layoutService when a test does not exercise canvas save behaviour (same
/// precedent as <c>CanvasAlertStateTests.NewDashboard</c>).
/// </remarks>
public sealed class LayoutSettingsViewModelTests
{
    private static readonly FieldInfo PendingProfileField =
        typeof(DashboardLayoutService).GetField("_pendingProfile", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Whether a debounced write is currently queued — a save was requested, disk or not.</summary>
    private static bool HasPendingSave(DashboardLayoutService service) =>
        PendingProfileField.GetValue(service) is not null;

    private static CanvasCardViewModel SensorCard(string name) =>
        new() { CardType = "Sensor", Sensor = new SensorViewModel { Name = name } };

    /// <summary>
    /// Builds a temp-rooted layout service, a canvas seeded with the given sensor cards, and an
    /// initialised <see cref="LayoutSettingsViewModel"/> over both. <paramref name="tempDir"/> is the
    /// caller's to clean up.
    /// </summary>
    private static async Task<(DashboardLayoutService Layout, CanvasDashboardViewModel Canvas, LayoutSettingsViewModel Vm)>
        NewInitializedAsync(string tempDir, params CanvasCardViewModel[] sensorCards)
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = new DashboardLayoutService(Path.Combine(tempDir, "dashboard_layout.json"), theme);
        await layout.LoadAsync();

        // null! shell/layoutService: this canvas is only ever touched for its Cards collection and
        // its own IsSnapToGridEnabled/GridSize properties here, neither of which reaches shell or
        // layoutService (CanvasAlertStateTests.NewDashboard is the same shape).
        var canvas = new CanvasDashboardViewModel(
            new ConnectionViewModel(), null!, null!, new SensorAlertStore(), new SensorAlertTracker());
        foreach (var card in sensorCards)
            canvas.Cards.Add(card);

        var vm = new LayoutSettingsViewModel(layout, canvas, home: null);
        await vm.InitializeAsync();
        return (layout, canvas, vm);
    }

    [Fact]
    public async Task InitialLoadDoesNotQueueASave()
    {
        var tempDir = Directory.CreateTempSubdirectory("remex-4kv0g42-").FullName;
        try
        {
            var (layout, _, _) = await NewInitializedAsync(tempDir);

            HasPendingSave(layout).Should().BeFalse(
                "seeding IsSnapToGridEnabled/GridSize from the just-loaded profile must not itself " +
                "queue a write — only a real user edit should");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task TogglingSnapSavesCurrentProfileWithOnlyThatFieldChanged()
    {
        var tempDir = Directory.CreateTempSubdirectory("remex-4kv0g42-").FullName;
        try
        {
            var (layout, canvas, vm) = await NewInitializedAsync(tempDir);

            // Seed a marker field directly onto CurrentProfile, AFTER InitializeAsync, so the save
            // below can only have picked it up by reading CurrentProfile at save time — never a
            // private snapshot taken earlier (the exact clobber DashboardLayoutService.RequestSave's
            // contract comment warns every caller against).
            layout.RequestSave(layout.CurrentProfile with { HostAddress = "wss://marker-host/" });

            vm.IsSnapToGridEnabled.Should().BeFalse("default");
            vm.IsSnapToGridEnabled = true;

            canvas.IsSnapToGridEnabled.Should().BeTrue("the canvas must receive the live push too");
            layout.CurrentProfile.IsSnapToGridEnabled.Should().BeTrue();
            layout.CurrentProfile.HostAddress.Should().Be("wss://marker-host/",
                "the save must build from CurrentProfile, carrying every other field forward");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ChangingGridSizeSavesCurrentProfileWithOnlyThatFieldChanged()
    {
        var tempDir = Directory.CreateTempSubdirectory("remex-4kv0g42-").FullName;
        try
        {
            var (layout, canvas, vm) = await NewInitializedAsync(tempDir);

            layout.RequestSave(layout.CurrentProfile with { HostAddress = "wss://marker-host/" });

            vm.GridSize = 30;

            canvas.GridSize.Should().Be(30, "the canvas must receive the live push too");
            layout.CurrentProfile.GridSize.Should().Be(30);
            layout.CurrentProfile.HostAddress.Should().Be("wss://marker-host/",
                "the save must build from CurrentProfile, carrying every other field forward");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RefreshSensorsRebuildsFromCanvasCardsPreservingPinnedState()
    {
        var tempDir = Directory.CreateTempSubdirectory("remex-4kv0g42-").FullName;
        try
        {
            var (layout, canvas, vm) = await NewInitializedAsync(
                tempDir, SensorCard("GPU Hotspot"), SensorCard("CPU Package"));

            // Seed the pin AFTER construction so RefreshSensors (called again below) has to read it
            // from CurrentProfile fresh, the same as RefreshSensors's own contract requires.
            layout.RequestSave(layout.CurrentProfile with { PinnedSensorIds = new() { "GPU Hotspot" } });

            vm.RefreshSensors();

            vm.PinnedSensors.Should().HaveCount(2);
            vm.PinnedSensors.Single(s => s.SensorName == "GPU Hotspot").IsPinned.Should().BeTrue();
            vm.PinnedSensors.Single(s => s.SensorName == "CPU Package").IsPinned.Should().BeFalse();

            // A card added after the fact is picked up on the next refresh too.
            canvas.Cards.Add(SensorCard("RAM"));
            vm.RefreshSensors();
            vm.PinnedSensors.Should().HaveCount(3);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task PinningASensorWritesItsNameAndUnpinningRemovesIt()
    {
        var tempDir = Directory.CreateTempSubdirectory("remex-4kv0g42-").FullName;
        try
        {
            var (layout, canvas, vm) = await NewInitializedAsync(tempDir, SensorCard("GPU Hotspot"));

            var item = vm.PinnedSensors.Single(s => s.SensorName == "GPU Hotspot");

            item.IsPinned = true;

            layout.CurrentProfile.PinnedSensorIds.Should().Contain("GPU Hotspot");
            canvas.Cards.Single().IsPinnedToHome.Should().BeTrue(
                "the matching canvas card's pinned flag must follow the checkbox too");

            item.IsPinned = false;

            layout.CurrentProfile.PinnedSensorIds.Should().NotContain("GPU Hotspot");
            canvas.Cards.Single().IsPinnedToHome.Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ReloadFromProfileAsyncReReadsCurrentProfile()
    {
        var tempDir = Directory.CreateTempSubdirectory("remex-4kv0g42-").FullName;
        try
        {
            var (layout, canvas, vm) = await NewInitializedAsync(tempDir, SensorCard("GPU Hotspot"));

            vm.IsSnapToGridEnabled.Should().BeFalse();
            vm.GridSize.Should().Be(50);
            vm.PinnedSensors.Single().IsPinned.Should().BeFalse();

            // Simulate something else (a savefile import) replacing CurrentProfile wholesale, without
            // going through this VM at all.
            layout.RequestSave(layout.CurrentProfile with
            {
                IsSnapToGridEnabled = true,
                GridSize = 77,
                PinnedSensorIds = new() { "GPU Hotspot" },
            });
            // Captured AFTER that seeding RequestSave (which necessarily leaves one queued) so the
            // check below proves ReloadFromProfileAsync added no save of its own, not merely that
            // none exists at all.
            var pendingBeforeReload = PendingProfileField.GetValue(layout);

            await vm.ReloadFromProfileAsync();

            vm.IsSnapToGridEnabled.Should().BeTrue();
            vm.GridSize.Should().Be(77);
            vm.PinnedSensors.Single().IsPinned.Should().BeTrue();

            // And reloading must not itself queue a NEW save (same guard as the initial load) -
            // RequestSave replaces _pendingProfile with a new instance, so reference equality here
            // proves nothing new was queued.
            PendingProfileField.GetValue(layout).Should().BeSameAs(pendingBeforeReload,
                "ReloadFromProfileAsync seeds from CurrentProfile; it must not immediately write it back");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}

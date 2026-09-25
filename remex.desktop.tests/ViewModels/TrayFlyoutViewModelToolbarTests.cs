using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Flyout toolbar drop 1 (RemEx-4kv0g.18.5): the toolbar row (six action tiles, hideable by id,
/// followed by launcher shortcuts after a divider) and the cards row's hidden-sensor filter.
/// Setup mirrors <see cref="TrayFlyoutOnlineDeviceCountTests"/> - real collaborators against a temp
/// profile, no mocking framework - except settings are driven directly through
/// <c>ShellViewModel.Customization</c> (the same property <c>ThemeService.CustomizationApplied</c>
/// writes in production) rather than round-tripped through a profile file, since that is the exact
/// signal <c>TrayFlyoutViewModel.OnShellPropertyChanged</c> reacts to.
/// </summary>
public sealed class TrayFlyoutViewModelToolbarTests : IAsyncLifetime
{
    private readonly IServiceProvider? _savedHost = App.EmbeddedHostServices;
    private readonly FakeLauncherStorage _launcherStorage = new();
    private readonly FakeAppLauncherService _appLauncher = new();
    private string _tempDir = null!;
    private ShellViewModel _shell = null!;
    private HomeViewModel _home = null!;
    private TrayFlyoutViewModel _tray = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-4kv0g185-toolbar-").FullName;
        App.EmbeddedHostServices = new HostServiceProvider(_appLauncher);

        var theme = new ThemeService { PostToUiThread = action => action() };
        var layoutService = new DashboardLayoutService(
            Path.Combine(_tempDir, "dashboard_layout.json"), theme);
        await layoutService.LoadAsync();

        var connection = new ConnectionViewModel();
        _shell = new ShellViewModel(
            layoutService,
            theme,
            connection,
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider());
        _shell.ProfileReplacedDispatch = run => run();
        _shell.DiagnosticsLogDispatch = run => run();

        _home = new HomeViewModel(connection, _shell);
        _tray = new TrayFlyoutViewModel(_shell, _home, _launcherStorage);
    }

    public Task DisposeAsync()
    {
        _tray.Dispose();
        _shell.Dispose();
        App.EmbeddedHostServices = _savedHost;
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public void SixTilesInOrderByDefault()
    {
        _tray.Tiles.Select(t => t.Id).Should().Equal("lock", "sleep", "remote", "send", "pair", "power");
        _tray.ToolbarItems.Should().OnlyContain(item => item is TrayTile,
            "no divider or shortcuts until an app is ticked in FlyoutAppIds");
    }

    [Fact]
    public void HiddenTileIdsAreExcluded()
    {
        _shell.Customization = new CustomizationSettings
        {
            FlyoutHiddenTileIds = new List<string> { "sleep", "pair" },
        };

        _tray.Tiles.Select(t => t.Id).Should().Equal("lock", "remote", "send", "power");
    }

    [Fact]
    public void TickedAppAppendsADividerThenTheShortcut()
    {
        var kept = new AppEntry(Guid.NewGuid(), "Notepad", @"C:\Windows\notepad.exe", "#000000", null);
        var untouched = new AppEntry(Guid.NewGuid(), "Calculator", @"C:\Windows\System32\calc.exe", "#000000", null);
        _launcherStorage.Entries = [kept, untouched];

        _shell.Customization = new CustomizationSettings { FlyoutAppIds = new List<Guid> { kept.Id } };
        _tray.Refresh();

        var items = _tray.ToolbarItems;
        items.Should().HaveCount(8, "six tiles, one divider, one shortcut - the untouched entry is not ticked");
        items[6].Should().BeOfType<TrayToolbarDivider>();
        var shortcut = items[7].Should().BeOfType<TrayShortcut>().Subject;
        shortcut.EntryId.Should().Be(kept.Id);
        shortcut.DisplayName.Should().Be("Notepad");
    }

    [Fact]
    public void TickedAppsRenderInLauncherOrderNotTickedOrder()
    {
        // .18.5 review carry-forward: FlyoutAppIds used to be walked in ticked order. Order=1/0/2
        // here, ticked in the reverse of launcher order (third, first, second) - the row must still
        // come out first/third/second by Order, not third/first/second by tick order.
        var first = new AppEntry(Guid.NewGuid(), "Notepad", @"C:\Windows\notepad.exe", "#000000", null, Order: 0);
        var second = new AppEntry(Guid.NewGuid(), "Calculator", @"C:\Windows\System32\calc.exe", "#000000", null, Order: 1);
        var third = new AppEntry(Guid.NewGuid(), "Paint", @"C:\Windows\System32\mspaint.exe", "#000000", null, Order: 2);
        _launcherStorage.Entries = [first, second, third];

        _shell.Customization = new CustomizationSettings
        {
            FlyoutAppIds = new List<Guid> { third.Id, first.Id, second.Id },
        };
        _tray.Refresh();

        var shortcuts = _tray.ToolbarItems.OfType<TrayShortcut>().Select(s => s.EntryId).ToArray();
        shortcuts.Should().Equal(new[] { first.Id, second.Id, third.Id },
            "spec §2: shortcut order follows the launcher, not the order ids were ticked in Personalize");
    }

    [Fact]
    public void AppIdNotInTheLauncherIsSkippedWithNoDividerOrShortcut()
    {
        _launcherStorage.Entries = [];
        _shell.Customization = new CustomizationSettings { FlyoutAppIds = new List<Guid> { Guid.NewGuid() } };
        _tray.Refresh();

        _tray.ToolbarItems.Should().HaveCount(6,
            "an id with no matching launcher entry must be skipped, not leave a dangling divider");
        _tray.ToolbarItems.Should().OnlyContain(item => item is TrayTile);
    }

    [Fact]
    public void FlyoutSensorsExcludesAHiddenIdAndIncludesANewlyPinnedOne()
    {
        var hidden = new SensorViewModel { Name = "CPU Package Temp" };
        var shown = new SensorViewModel { Name = "GPU Temp" };
        _home.PinnedSensors.Add(hidden);
        _home.PinnedSensors.Add(shown);

        _shell.Customization = new CustomizationSettings
        {
            FlyoutHiddenSensorIds = new List<string> { "CPU Package Temp" },
        };

        _tray.FlyoutSensors.Should().ContainSingle().Which.Should().BeSameAs(shown);

        var pinnedLater = new SensorViewModel { Name = "Fan RPM" };
        _home.PinnedSensors.Add(pinnedLater);

        _tray.FlyoutSensors.Should().HaveCount(2);
        _tray.FlyoutSensors.Should().Contain(pinnedLater);
        _tray.FlyoutSensors.Should().NotContain(hidden);
    }

    /// <summary>
    /// Pins real sensors the way the app does: the canvas knows them (staged from a telemetry reading)
    /// and the profile lists their ids, so <c>HomeViewModel.RefreshPinnedSensors</c> - which every
    /// flyout show runs - resolves them instead of clearing hand-added ones.
    /// </summary>
    private void PinRealSensors(params string[] names)
    {
        var canvas = _shell.CanvasViewModel;
        canvas.Should().NotBeNull("the shell builds its canvas up front");
        canvas!.ApplyTelemetry(new TelemetryPayload
        {
            Sensors = names.Select(n => new SensorReading { Id = n, Name = n, Value = 1, Unit = "°C" }).ToList(),
        });
        _shell.LayoutService.RequestSave(_shell.LayoutService.CurrentProfile! with { PinnedSensorIds = names.ToList() });
    }

    [Fact]
    public void AHiddenFlyoutDropsItsSensorTilesAndTheNextShowRebindsThem()
    {
        // Perf audit P3-66: a hidden flyout must not stay bound to live, ticking SensorViewModels.
        PinRealSensors("GPU Temp");
        _tray.Refresh();
        _tray.FlyoutSensors.Should().ContainSingle();

        _tray.OnHidden();
        _tray.FlyoutSensors.Should().BeEmpty();

        _home.PinnedSensors.Add(new SensorViewModel { Name = "Fan RPM" });
        _tray.FlyoutSensors.Should().BeEmpty("a pin change while hidden must not rebind the tiles");

        PinRealSensors("GPU Temp", "Fan RPM");
        _tray.Refresh();
        _tray.FlyoutSensors.Select(s => s.Name).Should().Equal("GPU Temp", "Fan RPM");
    }

    [Fact]
    public void AnUnchangedShowLeavesTheRealizedTilesAlone()
    {
        // Perf audit P3-67: every show used to Clear and re-Add the whole row even when nothing moved.
        PinRealSensors("GPU Temp", "CPU Temp");
        _tray.Refresh();
        _tray.FlyoutSensors.Should().HaveCount(2);
        var changes = 0;
        _tray.FlyoutSensors.CollectionChanged += (_, _) => changes++;

        _tray.Refresh();
        _tray.Refresh();

        changes.Should().Be(0);
        _tray.FlyoutSensors.Should().HaveCount(2);
    }

    [Fact]
    public async Task ShortcutsLaunchCommandCallsTheLauncherServiceWithTheEntrysTargetPath()
    {
        var entry = new AppEntry(Guid.NewGuid(), "Notepad", @"C:\Windows\notepad.exe", "#000000", null);
        _launcherStorage.Entries = [entry];
        _shell.Customization = new CustomizationSettings { FlyoutAppIds = new List<Guid> { entry.Id } };
        _tray.Refresh();

        var shortcut = _tray.ToolbarItems.OfType<TrayShortcut>().Single();
        await ((IAsyncRelayCommand)shortcut.LaunchCommand).ExecuteAsync(null);

        _appLauncher.LaunchedPaths.Should().ContainSingle().Which.Should().Be(entry.TargetPath);
    }

    /// <summary>
    /// Fix round (whole-branch review, RemEx-4kv0g.18.8, MEDIUM): <c>LaunchShortcutAsync</c> used to
    /// let <c>IAppLauncherService.LaunchAppAsync</c> throw straight through the dispatcher.
    /// <see cref="InvalidOperationException"/> stands in for the real service's Win32Exception here -
    /// the fake doesn't need the Win32-specific type to prove the <c>try/catch</c> swallows it
    /// instead of the command's <c>ExecuteAsync</c> propagating it.
    /// </summary>
    [Fact]
    public async Task ShortcutsLaunchCommandDoesNotThrowWhenTheLauncherServiceFails()
    {
        var entry = new AppEntry(Guid.NewGuid(), "Notepad", @"C:\Windows\notepad.exe", "#000000", null);
        _launcherStorage.Entries = [entry];
        _appLauncher.ThrowOnLaunch = new InvalidOperationException("target missing");
        _shell.Customization = new CustomizationSettings { FlyoutAppIds = new List<Guid> { entry.Id } };
        _tray.Refresh();

        var shortcut = _tray.ToolbarItems.OfType<TrayShortcut>().Single();

        var act = async () => await ((IAsyncRelayCommand)shortcut.LaunchCommand).ExecuteAsync(null);

        await act.Should().NotThrowAsync(
            "a failed launch should be logged, not crash the command that fired it");
    }

    private sealed class FakeLauncherStorage : ILauncherStorageService
    {
        public List<AppEntry> Entries { get; set; } = new();
        public Task<List<AppEntry>> LoadEntriesAsync() => Task.FromResult(new List<AppEntry>(Entries));
        public Task SaveEntriesAsync(IEnumerable<AppEntry> entries) => Task.CompletedTask;
    }

    private sealed class FakeAppLauncherService : IAppLauncherService
    {
        public List<string> LaunchedPaths { get; } = new();
        public Exception? ThrowOnLaunch { get; set; }

        public Task LaunchAppAsync(string targetPath)
        {
            if (ThrowOnLaunch is { } ex)
                throw ex;

            LaunchedPaths.Add(targetPath);
            return Task.CompletedTask;
        }
    }

    private sealed class HostServiceProvider(IAppLauncherService appLauncher) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType switch
        {
            _ when serviceType == typeof(IAppLauncherService) => appLauncher,
            _ => null,
        };
    }
}

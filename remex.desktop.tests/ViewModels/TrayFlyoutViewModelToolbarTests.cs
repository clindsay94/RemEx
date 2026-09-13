using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    private sealed class FakeLauncherStorage : ILauncherStorageService
    {
        public List<AppEntry> Entries { get; set; } = new();
        public Task<List<AppEntry>> LoadEntriesAsync() => Task.FromResult(new List<AppEntry>(Entries));
        public Task SaveEntriesAsync(IEnumerable<AppEntry> entries) => Task.CompletedTask;
    }

    private sealed class FakeAppLauncherService : IAppLauncherService
    {
        public List<string> LaunchedPaths { get; } = new();

        public Task LaunchAppAsync(string targetPath)
        {
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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-8wpvr.2. Round 2 (HIGH) moved the alert-store seed into <c>ApplyProfile</c> itself so every
/// call site seeds, not just <c>FinishInitialize</c>'s local-load branch — see the two facts still
/// named for that round below.
///
/// Round 3 (HIGH) covered the host layout sync, which handed <c>ApplyProfile</c> a host profile with no
/// alerts. That sync was removed in RemEx-sydzo, and its two facts with it; the seed still reads
/// <c>_layoutService.CurrentProfile ?? profile</c>, the same source the trailing save resolves to.
/// </summary>
public sealed class CanvasDashboardViewModelApplyProfileAlertSeedTests : IAsyncLifetime
{
    private static readonly MethodInfo FinishInitializeMethod =
        typeof(CanvasDashboardViewModel).GetMethod("FinishInitialize", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo ApplyProfileMethod =
        typeof(CanvasDashboardViewModel).GetMethod("ApplyProfile", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-8wpvr2-applyprofile-").FullName;
    private ThemeService _theme = null!;
    private DashboardLayoutService _layoutService = null!;
    private ShellViewModel _shell = null!;
    private ConnectionViewModel _connection = null!;
    private SensorAlertStore _alertStore = null!;
    private SensorAlertTracker _alertTracker = null!;
    private CanvasDashboardViewModel _vm = null!;

    private static TelemetryPayload Reading(string id, double value) => new()
    {
        Sensors = new List<SensorReading> { new() { Id = id, Name = id, Value = value, Unit = "°C" } },
    };

    public async Task InitializeAsync()
    {
        // SYNCHRONOUS DISPATCH — same reason as CanvasDashboardViewModelAlertLoadSaveTests: this
        // assembly has no Avalonia.Headless reference, so nothing ever drains a real posted callback.
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
        await _layoutService.LoadAsync();

        // Local (this device's) profile carries ONE alert from the start — round 3's fix means this
        // is now the alert every test below expects to SURVIVE a host sync, not the alert a host sync
        // supplies.
        var localProfile = _layoutService.CurrentProfile with
        {
            Cards = new List<CardState>
            {
                new() { CardId = "connection-card", CardType = "Connection", PositionX = 5, PositionY = 5 },
            },
            SensorAlerts = new List<SensorAlert>
            {
                new() { SensorName = "CPU Package", Threshold = 90, Direction = AlertDirection.Above, Severity = AlertSeverity.Warning },
            },
        };
        await _layoutService.SaveAsync(localProfile);
        await _layoutService.ReloadAsync();

        _connection = new ConnectionViewModel();
        _alertStore = new SensorAlertStore();
        _alertTracker = new SensorAlertTracker();
        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            _connection,
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider());
        _shell.ProfileReplacedDispatch = run => run();

        _vm = new CanvasDashboardViewModel(_connection, _layoutService, _shell, _alertStore, _alertTracker);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ApplyProfileAfterInitReseedsTheStoreWithoutAnExtraSave()
    {
        var localProfile = await _layoutService.LoadAsync();
        FinishInitializeMethod.Invoke(_vm, new object[] { localProfile });

        // Init subscribes OnAlertStoreChanged; from here on, ReplaceAll would fire an extra
        // TriggerSave unless ApplyProfile's own seed suppresses it.
        var saveCountBefore = _vm.AlertStoreSaveCount;

        // A LOCAL reload (e.g. ReloadFromPersistedLayout, which passes _layoutService.CurrentProfile
        // itself) — persist the new alert set first so _layoutService.CurrentProfile is genuinely the
        // profile being applied, exercising the same `_layoutService.CurrentProfile ?? profile` source
        // the round-3 fix reads.
        var newProfile = _layoutService.CurrentProfile with
        {
            SensorAlerts = new List<SensorAlert>
            {
                new() { SensorName = "GPU Hotspot", Threshold = 85, Direction = AlertDirection.Above, Severity = AlertSeverity.Critical },
            },
        };
        await _layoutService.SaveAsync(newProfile);
        await _layoutService.ReloadAsync();

        ApplyProfileMethod.Invoke(_vm, new object[] { _layoutService.CurrentProfile });

        _alertStore.TryGet("GPU Hotspot", out var reseeded).Should().BeTrue(
            "a later ApplyProfile call must reseed the store from the newly-applied local profile");
        reseeded!.Threshold.Should().Be(85);

        _vm.AlertStoreSaveCount.Should().Be(saveCountBefore,
            "ApplyProfile's alert-store reseed must not fire an extra save on top of the layout-sync " +
            "save ApplyProfile already issues unconditionally (RemEx-8wpvr.2 review round 2, HIGH)");
    }
}

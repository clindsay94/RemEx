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
/// Round 3 (HIGH) found the seed was still reading the wrong profile: <c>ApplyProfile</c> seeded from
/// its <c>profile</c> parameter, but on two of its five call sites (<c>OnLayoutProfileReceivedAsync</c>
/// and <c>FinishInitialize</c>'s pending-sync branch) that parameter is the CONNECTED HOST's own
/// <c>DashboardProfile</c> — a different file from this device's per-user profile, and one that never
/// carries alerts (RemEx-hmigd). <c>ApplyProfile</c>'s own trailing save layers only
/// Cards/PinnedSensorIds/IsSnapToGridEnabled/GridSize onto the LOCAL profile and never carries
/// SensorAlerts, so alerts are device-local by design — but the seed did not match that, so connecting
/// to any host nulled every configured alert and the next card drag persisted the empty list to this
/// device's own disk. The fix seeds from <c>_layoutService.CurrentProfile ?? profile</c> — the same
/// source the trailing save resolves to — so a host sync can no longer touch this device's alerts.
/// </summary>
public sealed class CanvasDashboardViewModelApplyProfileAlertSeedTests : IAsyncLifetime
{
    private static readonly MethodInfo FinishInitializeMethod =
        typeof(CanvasDashboardViewModel).GetMethod("FinishInitialize", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo ApplyProfileMethod =
        typeof(CanvasDashboardViewModel).GetMethod("ApplyProfile", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo PendingSyncProfileField =
        typeof(CanvasDashboardViewModel).GetField("_pendingSyncProfile", BindingFlags.NonPublic | BindingFlags.Instance)!;

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
            new HardwareThemeService(_theme),
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
    public async Task ApplyProfileFromAConnectedHostKeepsTheLocalAlertInsteadOfTheHostsEmptyOne()
    {
        // Finish local init the normal way, then materialize the sensor the local alert targets so
        // HasAlert can actually be observed on a card, not just in the store.
        var localProfile = await _layoutService.LoadAsync();
        FinishInitializeMethod.Invoke(_vm, new object[] { localProfile });
        _vm.ApplyTelemetry(Reading("CPU Package", 10));

        var card = _vm.StagedCards.Single(c => c.Sensor?.Name == "CPU Package");
        card.Sensor!.HasAlert.Should().BeTrue("the local profile's alert applies as soon as the sensor is seen");

        // Simulate OnLayoutProfileReceivedAsync (CanvasDashboardViewModel.cs:508): the connected
        // HOST's own DashboardProfile, whose host_dashboard_layout.json never carries alerts.
        var hostProfile = _layoutService.CurrentProfile with { SensorAlerts = new List<SensorAlert>() };
        ApplyProfileMethod.Invoke(_vm, new object[] { hostProfile });

        // THE REGRESSION (round 3, HIGH): before the fix this ReplaceAll(empty) nulled every alert.
        _alertStore.TryGet("CPU Package", out var seeded).Should().BeTrue(
            "a host profile must never overwrite this device's own alerts — they are device-local by design");
        seeded!.Threshold.Should().Be(90);
        card.Sensor.HasAlert.Should().BeTrue("the card must keep reflecting the surviving local alert");

        // A subsequent save (e.g. the next card drag) must still persist the local alert.
        _vm.ApplySensorAlert("GPU Hotspot", new SensorAlert { SensorName = "GPU Hotspot", Threshold = 80, Direction = AlertDirection.Above });

        _layoutService.CurrentProfile.SensorAlerts.Should().Contain(a => a.SensorName == "CPU Package",
            "a save after a host sync must not have lost the device-local alert");
    }

    [Fact]
    public async Task PendingHostSyncKeepsTheLocalAlertInsteadOfTheHostsEmptyOne()
    {
        // Simulate the race exactly the way OnLayoutProfileReceivedAsync/FinishInitialize handle it: a
        // host profile lands in _pendingSyncProfile before _isInitialized flips true. The host profile
        // carries no alerts, same as any real host_dashboard_layout.json.
        var hostProfile = _layoutService.CurrentProfile with { SensorAlerts = new List<SensorAlert>() };
        PendingSyncProfileField.SetValue(_vm, hostProfile);

        var localProfile = await _layoutService.LoadAsync();
        FinishInitializeMethod.Invoke(_vm, new object[] { localProfile });

        // THE REGRESSION (round 3, HIGH): before the fix, the pending-sync branch's ApplyProfile call
        // seeded from the pending HOST profile, nulling the local alert for the rest of the session.
        _alertStore.TryGet("CPU Package", out var seeded).Should().BeTrue(
            "FinishInitialize's pending-sync branch must keep the local alert, not the host's empty one");
        seeded!.Threshold.Should().Be(90);

        // A subsequent save (e.g. the next card drag) must not silently drop the local alert.
        _vm.ApplySensorAlert("GPU Hotspot", new SensorAlert { SensorName = "GPU Hotspot", Threshold = 80, Direction = AlertDirection.Above });

        _layoutService.CurrentProfile.SensorAlerts.Should().Contain(a => a.SensorName == "CPU Package",
            "a save after a pending-sync init must not have lost the local alert");
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
